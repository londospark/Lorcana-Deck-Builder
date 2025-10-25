module RulesProvider

open System
open System.IO
open UglyToad.PdfPig
open UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor

/// Loads and caches the Lorcana comprehensive rules from the Data PDF.
/// If the file is missing or can't be parsed, it falls back to an empty string so the API still runs.
/// Consumers can check IsLoaded and Text properties.
type IRulesProvider =
    abstract member Text : string
    abstract member IsLoaded : bool

/// Implementation using PdfPig to extract text. It lazily loads on first access
/// and searches for a matching rules PDF in the Data directory to be resilient
/// to filename changes.
type PdfRulesProvider(dataDir:string) =
    let syncRoot = obj()
    let mutable loaded = false
    let mutable text = ""

    let tryPickRulesFile (dir:string) =
        try
            if not (Directory.Exists dir) then None else
            // Prefer known naming pattern, else any PDF in Data
            let patternPreferred = Directory.EnumerateFiles(dir, "*Quick_Start*.pdf")
            let candidates =
                let arr = patternPreferred |> Seq.toArray
                if arr.Length > 0 then arr
                else Directory.EnumerateFiles(dir, "*.pdf") |> Seq.toArray
            if candidates.Length = 0 then None
            else
                // Choose the most recently modified file
                candidates
                |> Array.sortByDescending (fun p -> File.GetLastWriteTimeUtc p)
                |> Array.tryHead
        with _ -> None

    let tryPickFromMultipleDirs (dirs:string list) =
        dirs
        |> List.tryPick (fun d -> tryPickRulesFile d)

    // Advanced text extraction using PdfPig segmentation and reading order
    let extractPageTextAdvanced (page: UglyToad.PdfPig.Content.Page) =
        try
            let letters = page.Letters
            let wordExtractor = NearestNeighbourWordExtractor.Instance
            let words = wordExtractor.GetWords(letters) |> Seq.toArray
            if words.Length = 0 then "" else
            // Sort words top-to-bottom (descending Y), then left-to-right (ascending X)
            let sorted =
                words
                |> Array.sortWith (fun a b ->
                    let ya = a.BoundingBox.Bottom
                    let yb = b.BoundingBox.Bottom
                    if Math.Abs(ya - yb) > 0.5 then compare -ya -yb // higher Y first
                    else compare a.BoundingBox.Left b.BoundingBox.Left)
            // Group into lines by Y proximity
            let sbp = System.Text.StringBuilder()
            let mutable currentY = Double.NaN
            let mutable firstWordInLine = true
            let mutable i = 0
            while i < sorted.Length do
                let w = sorted[i]
                let y = w.BoundingBox.Bottom
                let h = w.BoundingBox.Height
                let tol = Math.Max(0.5, h * 0.4)
                if Double.IsNaN(currentY) || Math.Abs(y - currentY) > tol then
                    // New line
                    if not (Double.IsNaN(currentY)) then sbp.AppendLine() |> ignore
                    currentY <- y
                    firstWordInLine <- true
                let t = w.Text
                if not (String.IsNullOrWhiteSpace t) then
                    if not firstWordInLine then sbp.Append(' ') |> ignore else firstWordInLine <- false
                    sbp.Append(t) |> ignore
                i <- i + 1
            sbp.AppendLine() |> ignore
            sbp.ToString()
        with _ -> ""

    let loadIfNeeded () =
        if not loaded then
            lock syncRoot (fun () ->
                if not loaded then
                    try
                        let dirsToSearch = [
                            dataDir
                            Path.Combine(AppContext.BaseDirectory, "Data")
                            Path.Combine(Directory.GetCurrentDirectory(), "Data")
                        ]
                        Console.WriteLine("[DEBUG_LOG] RulesProvider: attempting to load rules PDF. Dirs searched:")
                        for d in dirsToSearch do Console.WriteLine($"[DEBUG_LOG]  - {d}")
                        match tryPickFromMultipleDirs dirsToSearch with
                        | None ->
                            Console.WriteLine("[DEBUG_LOG] RulesProvider: no rules PDF found in any directory.")
                            text <- ""
                            loaded <- false
                        | Some path ->
                            Console.WriteLine($"[DEBUG_LOG] RulesProvider: loading rules from '{path}'")
                            if File.Exists path then
                                use doc = PdfDocument.Open(path)
                                let sb = System.Text.StringBuilder()
                                for i in 1 .. int doc.NumberOfPages do
                                    let page = doc.GetPage(int i)
                                    if not (isNull page) then
                                        // Prefer advanced extraction; fall back to simple page.Text
                                        let adv = extractPageTextAdvanced page
                                        let pageText = if String.IsNullOrWhiteSpace adv then page.Text else adv
                                        if not (String.IsNullOrWhiteSpace pageText) then
                                            sb.AppendLine(pageText) |> ignore
                                text <- sb.ToString()
                                loaded <- not (String.IsNullOrWhiteSpace text)
                                Console.WriteLine($"[DEBUG_LOG] RulesProvider: loaded={loaded} length={text.Length}")
                            else
                                Console.WriteLine($"[DEBUG_LOG] RulesProvider: path resolved but file does not exist: {path}")
                                text <- ""
                                loaded <- false
                    with ex ->
                        Console.WriteLine($"[DEBUG_LOG] RulesProvider: exception while loading rules: {ex.Message}")
                        text <- ""
                        loaded <- false)

    interface IRulesProvider with
        member _.Text =
            loadIfNeeded()
            text
        member _.IsLoaded =
            loadIfNeeded()
            loaded

/// Helper to build the provider using AppContext.BaseDirectory/Data
let createDefault () : IRulesProvider =
    let dataDir = Path.Combine(AppContext.BaseDirectory, "Data")
    PdfRulesProvider(dataDir) :> IRulesProvider

/// Helper to build the provider using a specific content root (it will look into <root>/Data)
let createWithDir (rootDir:string) : IRulesProvider =
    let dataDir = Path.Combine(rootDir, "Data")
    PdfRulesProvider(dataDir) :> IRulesProvider
