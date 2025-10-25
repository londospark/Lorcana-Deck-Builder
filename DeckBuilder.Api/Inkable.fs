module Inkable

open System
open System.IO
open System.Text
open System.Text.Json

// Name normalization to improve matching across data sources and Unicode variants
let normalizeName (s:string) =
    if String.IsNullOrWhiteSpace s then "" else
    let trimmed = s.Trim()
    let sb = StringBuilder(trimmed.Length)
    let mutable i = 0
    while i < trimmed.Length do
        let ch = trimmed[i]
        let repl =
            match ch with
            | '\u2019' // right single quote ’
            | '\u2018' // left single quote ‘
            | '\u02BC' // modifier letter apostrophe ʼ
            | '\uFF07' // fullwidth '
                -> '\''
            | '\u2013' // en dash –
            | '\u2014' // em dash —
            | '\u2212' // minus sign −
                -> '-'
            | c when Char.IsWhiteSpace c -> ' '
            | c -> c
        // skip zero-width characters
        if repl <> '\u200B' && repl <> '\u200C' && repl <> '\u200D' && repl <> '\uFEFF' then
            sb.Append(repl) |> ignore
        i <- i + 1
    // collapse multiple spaces
    let collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "\s+", " ")
    collapsed.Trim()

// Lazily load an index of inkable cards from Data/allCards.json
let private inkableIndexLazy : Lazy<System.Collections.Generic.Dictionary<string,bool>> =
    lazy
        let dict = System.Collections.Generic.Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase)
        try
            let dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "allCards.json")
            if File.Exists(dataPath) then
                let txt = File.ReadAllText(dataPath)
                use doc = JsonDocument.Parse(txt)
                let root = doc.RootElement
                let mutable cardsEl = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("cards", &cardsEl) && cardsEl.ValueKind = JsonValueKind.Array then
                    for c in cardsEl.EnumerateArray() do
                        let mutable nameEl = Unchecked.defaultof<JsonElement>
                        let name = if c.TryGetProperty("fullName", &nameEl) && nameEl.ValueKind = JsonValueKind.String then nameEl.GetString() else if c.TryGetProperty("name", &nameEl) && nameEl.ValueKind = JsonValueKind.String then nameEl.GetString() else null
                        let mutable inkEl = Unchecked.defaultof<JsonElement>
                        let mutable inkable = false
                        // Prefer 'inkwell' boolean if present, else fall back to legacy keys.
                        if c.TryGetProperty("inkwell", &inkEl) then
                            inkable <-
                                match inkEl.ValueKind with
                                | JsonValueKind.True -> true
                                | JsonValueKind.False -> false
                                | JsonValueKind.String ->
                                    let s = inkEl.GetString()
                                    if isNull s then false else
                                    let t = s.Trim().ToLowerInvariant()
                                    t = "true" || t = "yes" || t = "1"
                                | JsonValueKind.Number ->
                                    let mutable n = 0
                                    if inkEl.TryGetInt32(&n) then n <> 0 else
                                        try inkEl.GetDouble() <> 0.0 with _ -> false
                                | _ -> false
                        else
                            let mutable alt = Unchecked.defaultof<JsonElement>
                            if c.TryGetProperty("inkWell", &alt) || c.TryGetProperty("inkable", &alt) || c.TryGetProperty("playableAsInk", &alt) then
                                inkable <-
                                    match alt.ValueKind with
                                    | JsonValueKind.True -> true
                                    | JsonValueKind.False -> false
                                    | JsonValueKind.String ->
                                        let s = alt.GetString()
                                        if isNull s then false else
                                        let t = s.Trim().ToLowerInvariant()
                                        t = "true" || t = "yes" || t = "1"
                                    | JsonValueKind.Number ->
                                        let mutable n = 0
                                        if alt.TryGetInt32(&n) then n <> 0 else
                                            try alt.GetDouble() <> 0.0 with _ -> false
                                    | _ -> false
                            else ()
                        if not (isNull name) && not (String.IsNullOrWhiteSpace name) then
                            dict[normalizeName name] <- inkable
        with _ -> ()
        dict

// Lazily load a colors index: fullName -> list of colors (1 or 2). Uses 'colors' array; else splits 'color' string.
let private colorsIndexLazy : Lazy<System.Collections.Generic.Dictionary<string,string list>> =
    lazy
        let dict = System.Collections.Generic.Dictionary<string,string list>(StringComparer.OrdinalIgnoreCase)
        try
            let dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "allCards.json")
            if File.Exists(dataPath) then
                let txt = File.ReadAllText(dataPath)
                use doc = JsonDocument.Parse(txt)
                let root = doc.RootElement
                let mutable cardsEl = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("cards", &cardsEl) && cardsEl.ValueKind = JsonValueKind.Array then
                    for c in cardsEl.EnumerateArray() do
                        let mutable nameEl = Unchecked.defaultof<JsonElement>
                        let name = if c.TryGetProperty("fullName", &nameEl) && nameEl.ValueKind = JsonValueKind.String then nameEl.GetString() else if c.TryGetProperty("name", &nameEl) && nameEl.ValueKind = JsonValueKind.String then nameEl.GetString() else null
                        if not (isNull name) && not (String.IsNullOrWhiteSpace name) then
                            let key = normalizeName name
                            let mutable cols : string list = []
                            let mutable arrEl = Unchecked.defaultof<JsonElement>
                            if c.TryGetProperty("colors", &arrEl) && arrEl.ValueKind = JsonValueKind.Array then
                                cols <-
                                    arrEl.EnumerateArray()
                                    |> Seq.choose (fun e -> if e.ValueKind = JsonValueKind.String then Some (e.GetString()) else None)
                                    |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                                    |> Seq.map (fun s -> s.Trim())
                                    |> Seq.toList
                            else
                                let mutable colEl = Unchecked.defaultof<JsonElement>
                                if c.TryGetProperty("color", &colEl) && colEl.ValueKind = JsonValueKind.String then
                                    let s = colEl.GetString()
                                    if not (isNull s) then
                                        cols <- s.Split([|','; '/'; '-'; '|'; '+'|], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun x -> x.Trim()) |> Array.toList
                            if cols.Length > 0 then dict[key] <- cols
        with _ -> ()
        dict

let isInkable (name:string) =
    let idx = inkableIndexLazy.Value
    let key = normalizeName name
    if idx.ContainsKey(key) then idx[key] else false

// Try to get colors list for a given card name
let tryGetColors (name:string) : string list =
    let idx = colorsIndexLazy.Value
    let key = normalizeName name
    if idx.ContainsKey(key) then idx[key] else []

// Check if a given name corresponds to a genuine card present in the index
let existsCardName (name:string) =
    let idx = inkableIndexLazy.Value
    let key = normalizeName name
    idx.ContainsKey(key)

// Enumerate all known card names from the static index
let allKnownCardNames () : string array =
    let idx = inkableIndexLazy.Value
    idx.Keys |> Seq.toArray

// Enumerate all names that have color data along with their colors
let allKnownWithColors () : (string * string list) array =
    let idx = colorsIndexLazy.Value
    idx |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.toArray
