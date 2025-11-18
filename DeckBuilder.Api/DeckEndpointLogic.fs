module DeckEndpointLogic

open System
open ApiModels

// Parse up to two allowed colors from a rules text blob
let parseAllowedColors (rules:string) : string list =
    let knownColors = set [ "Amber"; "Amethyst"; "Emerald"; "Ruby"; "Sapphire"; "Steel" ]
    if String.IsNullOrWhiteSpace rules then [] else
    rules.Split([|'\n'; '\r'; ','; '/'; '|'; '-'; ';'; ':'; ' '|], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun t -> let x = t.Trim() in if knownColors.Contains(x) then Some x else None)
    |> Array.distinct
    |> Array.truncate 2
    |> Array.toList

// Check if a card with colors `cols` is legal for exactly-two `allowed` deck colors
let isCardColorLegal (cols:string list) (allowed:string list) : bool =
    match allowed with
    | [] -> true
    | [a; b] ->
        let s = set [a; b]
        let cs = cols |> List.map (fun c -> c.Trim()) |> set
        cs.IsSubsetOf s && cs.Count <= 2 && (if cs.Count = 2 then cs = s else true)
    | _ -> false

// Build the candidate list text with basic legality filtering and copy caps
// Output format: CSV without header. Columns: fullName,cost,inkable,effects,maxCopies
// - fullName: string (may include commas; will be quoted)
// - cost: integer or ?
// - inkable: true/false (resolved)
// - effects: semicolon-separated list (quoted)
// - maxCopies: integer (per-card legal maximum; default 4 when absent)
let buildLegalCandidatesText
    (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>)
    (query: DeckQuery)
    (allowedColorsForCandidates: string list)
    (resolveInk: string -> bool)
    : string =
    let quote (s:string) =
        if String.IsNullOrWhiteSpace s then "" else
        let s2 = s.Replace("\"", "\"\"")
        $"\"{s2}\""
    candidates
    |> QdrantHelpers.filterLegalCardsPoints query
    |> Seq.filter (fun p ->
        if allowedColorsForCandidates.IsEmpty then true else
        let cols = Card.Payload.colors p.Payload
        isCardColorLegal cols allowedColorsForCandidates)
    |> QdrantHelpers.applyMaxCopiesPointsPerCard
    |> Seq.map (fun p ->
        let info = Card.Payload.toInfo p.Payload
        let costStr = info.Cost |> Option.map (fun c -> sprintf "%.0f" c) |> Option.defaultValue "?"
        let inkableBool = match info.Inkable with | Some b -> b | None -> resolveInk info.FullName
        let effects = Card.Payload.effects p.Payload |> String.concat "; "
        let maxCopies = Card.Payload.maxCopiesInDeck p.Payload |> Option.defaultValue 4
        String.Join(",",
            [ quote info.FullName
              costStr
              if inkableBool then "true" else "false"
              quote effects
              string maxCopies ]))
    |> Seq.truncate 40
    |> String.concat "\n"

// Infer best color pair from a bag of names, optionally anchoring to one color
let inferBestColorPair (cardColors: string -> string list) (knownColors: string list) (names:string array) (anchor:string option) : string list =
    let allColors = knownColors |> List.toArray
    let combos =
        seq {
            for i = 0 to allColors.Length - 2 do
                for j = i + 1 to allColors.Length - 1 do
                    let pair = [ allColors[i]; allColors[j] ]
                    match anchor with
                    | Some a when not ((set pair).Contains a) -> ()
                    | _ -> yield pair
        } |> Seq.toArray
    let score (allowed:string list) =
        names |> Array.sumBy (fun n -> let cols = cardColors n in if isCardColorLegal cols allowed then 1 else 0)
    let mutable best : string list = if combos.Length > 0 then combos[0] else []
    let mutable bestScore = -1
    
    // Score all combinations and track best
    let scores = 
        combos 
        |> Array.map (fun combo -> 
            let sc = score combo
            (combo, sc))
        |> Array.sortByDescending snd
    
    if scores.Length > 0 then
        best <- fst scores.[0]
        bestScore <- snd scores.[0]
    
    best

let chooseDeckColors (knownColors: string list) (cardColors: string -> string list) (allowedColors: string list) (filteredNames:string array) : string list =
    // NOTE: This function should choose colors based on the actual card data (filteredNames),
    // but if allowedColors already has 2+ colors (e.g., from query.selectedColors), it just returns those.
    if allowedColors.Length >= 2 then 
        // User specified 2+ colors, use them as-is
        allowedColors |> List.truncate 2
    elif allowedColors.Length = 1 then 
        // User specified 1 color, infer best pair that includes it
        inferBestColorPair cardColors knownColors filteredNames (Some allowedColors.Head)
    else 
        // No colors specified, infer best pair from data
        inferBestColorPair cardColors knownColors filteredNames None
