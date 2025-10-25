module DeckService

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open OllamaSharp
open Qdrant.Client
open ApiModels
open Card
open Inkable
open DeckHelpers
open RulesProvider
open DeckEndpointLogic

// Service interface to encapsulate the /api/deck pipeline
type IDeckBuilder =
    abstract member BuildDeck : DeckQuery -> Task<Result<DeckResponse,string>>

// Internal prep record shared across helpers
type Prep = {
    InkMap: System.Collections.Generic.Dictionary<string,bool>
    ColorsMap: System.Collections.Generic.Dictionary<string,string list>
    MaxCopiesMap: System.Collections.Generic.Dictionary<string,int>
    ResolveInk: string -> bool
    ResolveMaxCopies: string -> int
    RulesText: string
    AllowedColors: string list
    KnownColors: string list
    CardColors: string -> string list
    AllowedColorsForCandidates: string list
}

[<Sealed>]
type DeckBuilderService(qdrant: QdrantClient, ollama: IOllamaApiClient, rulesProvider: IRulesProvider) =
    // Shared model names (can be made configurable later)
    [<Literal>]
    let embedModel = "all-minilm"
    [<Literal>]
    let genModel = "llama3"

    // ---- helpers migrated from Endpoints.fs ----
    let embedRequest (text:string) = task {
        let req = OllamaSharp.Models.EmbedRequest()
        req.Model <- embedModel
        // Truncate to a safe max length to avoid Ollama embeddings warning about token marking
        let maxLen = 4000
        let inputText =
            let t = text.Trim()
            if t.Length > maxLen then t.Substring(0, maxLen) else t
        req.Input <- System.Collections.Generic.List<string>()
        req.Input.Add(inputText)
        let! embRes = ollama.EmbedAsync(req)
        let emb =
            if not (isNull embRes) && not (isNull embRes.Embeddings) && embRes.Embeddings.Count > 0 && not (isNull embRes.Embeddings[0]) then
                embRes.Embeddings[0] |> Seq.toArray
            else [||]
        return emb
    }

    let searchCandidates (vector: float32 array) =
        qdrant.SearchAsync("lorcana_cards", vector, limit = 40uL)

    // Build a Qdrant payload filter that excludes points containing any disallowed colors
    let knownInks = [ "Amber"; "Amethyst"; "Emerald"; "Ruby"; "Sapphire"; "Steel" ]

    let colorExclusionFilter (allowed:string list) =
        let f = Qdrant.Client.Grpc.Filter()
        if not (isNull (box allowed)) && allowed.Length > 0 then
            for c in knownInks do
                if not (allowed |> List.exists (fun a -> a.Equals(c, StringComparison.OrdinalIgnoreCase))) then
                    let cond = Qdrant.Client.Grpc.Condition()
                    let fc = Qdrant.Client.Grpc.FieldCondition()
                    fc.Key <- "colors"
                    let m = Qdrant.Client.Grpc.Match()
                    m.Keyword <- c
                    fc.Match <- m
                    cond.Field <- fc
                    f.MustNot.Add(cond)
        f

    let searchCandidatesFiltered (vector: float32 array) (allowed:string list) =
        let filter = colorExclusionFilter allowed
        // Increase limit since we're narrowing by colors; gives server more pool for filling
        qdrant.SearchAsync("lorcana_cards", vector, limit = 120uL, filter = filter)

    // RAG: search rules collection using the same embedding space
    let searchRules (vector: float32 array) =
        qdrant.SearchAsync("lorcana_rules", vector, limit = 6uL)

    let buildInkMap (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) =
        let d = System.Collections.Generic.Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase)
        for p in candidates do
            let fn = Payload.fullName p.Payload
            match Payload.inkable p.Payload with
            | Some b -> d[Inkable.normalizeName fn] <- b
            | None -> ()
        d

    let buildColorsMap (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) =
        let d = System.Collections.Generic.Dictionary<string,string list>(StringComparer.OrdinalIgnoreCase)
        for p in candidates do
            let fn = Payload.fullName p.Payload
            let cols = Payload.colors p.Payload
            if cols.Length > 0 then d[Inkable.normalizeName fn] <- cols
        d

    let buildMaxCopiesMap (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) =
        let d = System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase)
        for p in candidates do
            let fn = Payload.fullName p.Payload
            let maxOpt = Card.Payload.maxCopiesInDeck p.Payload
            let maxVal = match maxOpt with | Some n when n > 0 -> n | _ -> 4
            d[Inkable.normalizeName fn] <- maxVal
        d

    let resolveInkWith (inkMap: System.Collections.Generic.IDictionary<string,bool>) (name:string) =
        let key = Inkable.normalizeName name
        if inkMap.ContainsKey(key) then inkMap[key] else Inkable.isInkable key

    let resolveMaxCopiesWith (maxMap: System.Collections.Generic.IDictionary<string,int>) (name:string) =
        let key = Inkable.normalizeName name
        if maxMap.ContainsKey(key) then maxMap[key] else 4

    let getRulesAndAllowedColors (query: DeckQuery) =
        // Use server-side rules provider; do not accept rules from the client
        let rulesText = rulesProvider.Text
        let allowedColors : string list =
            if not (isNull (box query.selectedColors)) then
                let arr = defaultArg query.selectedColors [||]
                let arr2 = arr |> Array.choose (fun s -> if String.IsNullOrWhiteSpace s then None else Some (s.Trim())) |> Array.distinct
                if arr2.Length = 2 then arr2 |> Array.toList else DeckEndpointLogic.parseAllowedColors rulesText
            else DeckEndpointLogic.parseAllowedColors rulesText
        rulesText, allowedColors

    let cardColorsFromMaps (colorsMap: System.Collections.Generic.IDictionary<string,string list>) (name:string) =
        let key = Inkable.normalizeName name
        if colorsMap.ContainsKey(key) then colorsMap[key] else []

    let buildPrompt (query: DeckQuery) (rulesText:string) (_:string list) (legalCandidates:string) =
        let prompt = StringBuilder()
        prompt.AppendLine("You are an expert Lorcana deck builder.") |> ignore
        prompt.AppendLine("The rules text below are the official Disney Lorcana Rules. Treat them as authoritative.") |> ignore
        prompt.AppendLine("Game objective: the goal is to reach 20 lore before your opponent; build and choose cards to advance this plan.") |> ignore
        prompt.AppendLine("RULES:") |> ignore
        prompt.AppendLine(rulesText) |> ignore
        prompt.AppendLine() |> ignore
        prompt.AppendLine("CANDIDATE CARDS (CSV columns: fullName, cost, inkable, effects, maxCopies). Values may be quoted.") |> ignore
        prompt.AppendLine(legalCandidates : string) |> ignore
        prompt.AppendLine() |> ignore
        prompt.AppendLine($"TASK: Build a legal {query.deckSize}-card deck for this request:") |> ignore
        prompt.AppendLine(query.request) |> ignore
        prompt.AppendLine() |> ignore
        prompt.AppendLine("STRICT SOURCE CONSTRAINTS:") |> ignore
        prompt.AppendLine("- Use ONLY cards from the CANDIDATE CARDS list above.") |> ignore
        prompt.AppendLine("- Do NOT invent or suggest any card name that is not present in the CANDIDATE CARDS list.") |> ignore
        prompt.AppendLine() |> ignore
        prompt.AppendLine($"OUTPUT: Return EXACTLY {query.deckSize} lines, one per card copy, using the card fullName only (no counts in the string, duplicates allowed). Do not include headers or commentary.") |> ignore
        // STRICT OUTPUT RULES
        prompt.AppendLine("CRITICAL: ONLY return valid Disney Lorcana card fullNames from the candidate list. One card name per line. No bullets, numbering, quotes, JSON, code fences, headers, or explanations.") |> ignore
        prompt.AppendLine("Do not output any text that is not a card name. Do not add counts in the string. Do not add trailing punctuation.") |> ignore
        // New deck construction approach per requirements
        prompt.AppendLine("- Build process: First, choose the set of cards you want for the strategy and imagine each at its legal maxCopies (see CSV; typically 4), ignoring the deck size limit initially.") |> ignore
        prompt.AppendLine("  Then remove entire playsets that don't synergize well enough with the core plan. If the total is still above the target size,") |> ignore
        prompt.AppendLine("  remove individual copies of the least important cards until you reach the target count. Cards with effects that work with other cards in the deck should be a high priority.") |> ignore
        prompt.AppendLine("- Playset strategy: Strongly prefer 3–4 copies (up to the card's maxCopies) of key cards; minimize singletons and 2-ofs unless justified by legend rule, curve risks, or role-specific tech.") |> ignore
        prompt.AppendLine("- Target playsets: Aim for 5–12 distinct full playsets (at each card's maxCopies; typically 4) in the final deck.") |> ignore
        prompt.AppendLine("- Inkable balance: Prefer ~70–85% inkable overall where the plan allows; select inkable options when choices are close.") |> ignore
        prompt.AppendLine("- Cost curve: Ensure a smooth curve with ample 1–3 cost plays, moderate 4–5, and light 6+ unless the plan requires otherwise.") |> ignore
        prompt.AppendLine("- Use each card's fullName to distinguish versions/printings. No markdown, no code fences, no JSON.") |> ignore
        prompt.ToString()

    let generateText (prompt:string) = task {
        let genReq = OllamaSharp.Models.GenerateRequest()
        genReq.Model <- genModel
        genReq.Prompt <- prompt
        let stream = ollama.GenerateAsync(genReq)
        let sb = StringBuilder()
        let e = stream.GetAsyncEnumerator()
        let rec loop () = task {
            let! moved = e.MoveNextAsync().AsTask()
            if moved then
                let chunk = e.Current
                if not (isNull chunk) && not (isNull chunk.Response) then
                    sb.Append(chunk.Response) |> ignore
                return! loop ()
            else
                return ()
        }
        do! loop ()
        do! e.DisposeAsync().AsTask()
        return sb.ToString()
    }

    let buildUrlMap (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) =
        let d = System.Collections.Generic.Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        for p in candidates do
            let fn = Payload.fullName p.Payload
            match Payload.cardMarketUrl p.Payload with
            | Some u when not (String.IsNullOrWhiteSpace u) -> d[Inkable.normalizeName fn] <- u
            | _ -> ()
        d

    // --- Split BuildDeck into small composable helpers ---
    let validateQuery (q: DeckQuery) =
        if isNull (box q) then Error "Invalid request body."
        elif String.IsNullOrWhiteSpace q.request then Error "Missing request text."
        elif q.deckSize <= 0 then Error "Deck size must be positive."
        else Ok ()

    let embedAndSearch (request:string) (allowedColors:string list) = task {
        let! emb = embedRequest request
        if emb.Length = 0 then return Error "No embedding returned"
        else
            let vec = emb |> Array.map float32
            let! results =
                if not (isNull (box allowedColors)) && allowedColors.Length = 2 then
                    searchCandidatesFiltered vec allowedColors
                else
                    searchCandidates vec
            return Ok (emb, results :> seq<Qdrant.Client.Grpc.ScoredPoint>)
    }

    // Build a compact rules excerpt block from the rules RAG collection using the request embedding
    let getRulesForPrompt (requestEmbedding: float32 array) = task {
        try
            let! hits = searchRules requestEmbedding
            let texts =
                hits
                |> Seq.choose (fun p ->
                    let ok, v = p.Payload.TryGetValue("text")
                    if ok && not (isNull v) then
                        let s = v.StringValue
                        if String.IsNullOrWhiteSpace s then None else Some s
                    else None)
                |> Seq.truncate 8
                |> Seq.toArray
            if texts.Length = 0 then return "[No relevant rules excerpts found; using general constraints: Core format; two ink colors; objective is to reach 20 lore before your opponent.]" else
            let joined = String.Join("\n\n---\n\n", texts)
            // Cap to 1800 chars for prompt safety
            let maxLen = 1800
            return if joined.Length > maxLen then joined.Substring(0, maxLen) else joined
        with _ ->
            return "[Rules excerpts unavailable. Core format; two ink colors; objective is to reach 20 lore before your opponent.]"
    }

    let prepare (q:DeckQuery) (cands: seq<Qdrant.Client.Grpc.ScoredPoint>) =
        let inkMap = buildInkMap cands
        let colorsMap = buildColorsMap cands
        let maxCopiesMap = buildMaxCopiesMap cands
        let resolveInk = resolveInkWith inkMap
        let resolveMaxCopies = resolveMaxCopiesWith maxCopiesMap
        let rulesText, allowedColors = getRulesAndAllowedColors q
        let knownColorsList = [ "Amber"; "Amethyst"; "Emerald"; "Ruby"; "Sapphire"; "Steel" ]
        let cardColors = cardColorsFromMaps colorsMap
        let allowedForCand = if allowedColors.Length = 2 then allowedColors else []
        { InkMap = inkMap; ColorsMap = colorsMap; MaxCopiesMap = maxCopiesMap; ResolveInk = resolveInk; ResolveMaxCopies = resolveMaxCopies; RulesText = rulesText; AllowedColors = allowedColors; KnownColors = knownColorsList; CardColors = cardColors; AllowedColorsForCandidates = allowedForCand }

    let legalCandidatesAndPrompt (q:DeckQuery) (rulesForPrompt:string) (prep:Prep) (cands: seq<Qdrant.Client.Grpc.ScoredPoint>) =
        let legalCands = DeckEndpointLogic.buildLegalCandidatesText cands q prep.AllowedColorsForCandidates prep.ResolveInk
        let prompt = buildPrompt q rulesForPrompt prep.AllowedColorsForCandidates legalCands
        legalCands, prompt

    let generateAndTopUp (q:DeckQuery) (rulesText:string) (legalCandidates:string) (prompt:string) = task {
        let! genText = generateText prompt
        if String.IsNullOrWhiteSpace(genText) then return Error "Ollama generate failed" else
        let! toppedUp = ensureDeckSize genModel ollama rulesText q.request legalCandidates q.deckSize genText
        return Ok toppedUp
    }

    let toFlat (deckSize:int) (maxCopies:int) (toppedUp:string) =
        let rawLines = splitLines toppedUp
        let _, flat = buildCountedDeck rawLines deckSize maxCopies
        rawLines, flat

    let cleanAndValidate (inkMap:System.Collections.Generic.IDictionary<string,bool>) (flat:string array) =
        let isGenuineName (name:string) =
            let key = normalizeName name
            // Restrict to names present in current Qdrant candidates only
            inkMap.ContainsKey(key)
        let cleaned =
            flat
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        let nonCardLines = cleaned |> Array.filter (fun s -> not (isGenuineName s))
        let filtered = cleaned |> Array.filter isGenuineName
        filtered, nonCardLines

    let chooseColorsAndFilter (knownColors:string list) (cardColors:string->string list) (allowedColors:string list) (names:string array) =
        let chosen = DeckEndpointLogic.chooseDeckColors knownColors cardColors allowedColors names
        let isLegalByColor (n:string) =
            let cols = cardColors n
            DeckEndpointLogic.isCardColorLegal cols chosen
        let colorIllegal = names |> Array.filter (fun n -> not (isLegalByColor n))
        let filteredByColor = names |> Array.filter isLegalByColor
        chosen, filteredByColor, colorIllegal, isLegalByColor

    let buildInitialCounts (maxOf:string->int) (filteredByColor:string array) =
        let counts = System.Collections.Generic.Dictionary<string,int>(StringComparer.Ordinal)
        for name in filteredByColor do
            let cur = if counts.ContainsKey(name) then counts[name] else 0
            let cap = maxOf name
            if cur < cap then counts[name] <- cur + 1
        counts, (counts.Values |> Seq.sum)

    let fillToSize (deckSize:int) (maxOf:string->int) (isLegalNameByColor:string->bool) (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) (filteredByColor:string array) (counts:System.Collections.Generic.Dictionary<string,int>) (isGenuineName:string->bool) =
        let canInc name = counts[name] < maxOf name
        let sumCounts () = counts.Values |> Seq.sum
        let rec raiseExisting total =
            if total >= deckSize then total
            else
                // Try to increment lower-count cards first
                let order = counts |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.sortBy snd |> Seq.toArray
                let totalBefore = total
                let totalAfter =
                    [3;2;1]
                    |> List.fold (fun acc i ->
                        if acc >= deckSize then acc else
                        order
                        |> Array.fold (fun acc2 (n,v) ->
                            if acc2 < deckSize && v = i && canInc n && isLegalNameByColor n then
                                counts[n] <- v + 1
                                acc2 + 1
                            else acc2) acc) total
                if totalAfter > totalBefore then raiseExisting totalAfter else totalAfter
        let total0 = sumCounts()
        let total1 = raiseExisting total0
        let total2 =
            if total1 < deckSize then
                let legalPool = filteredByColor |> Array.distinct |> Array.sortBy id |> Array.toList
                let rec addFromPool total names =
                    match names with
                    | _ when total >= deckSize -> total
                    | [] -> total
                    | n::rest ->
                        let cur = if counts.ContainsKey(n) then counts[n] else 0
                        if cur < maxOf n then counts[n] <- cur + 1; addFromPool (total + 1) rest
                        else addFromPool total rest
                addFromPool total1 legalPool
            else total1
        let total3 =
            if total2 < deckSize then
                let candArr : Qdrant.Client.Grpc.ScoredPoint list = candidates |> Seq.toArray |> Array.toList
                let rec addFromCandidates total (xs: Qdrant.Client.Grpc.ScoredPoint list) =
                    match xs with
                    | _ when total >= deckSize -> total
                    | [] -> total
                    | c::rest ->
                        let n = Payload.fullName c.Payload
                        if isGenuineName n && isLegalNameByColor n then
                            let cur = if counts.ContainsKey(n) then counts[n] else 0
                            if cur < maxOf n then counts[n] <- cur + 1; addFromCandidates (total + 1) rest
                            else addFromCandidates total rest
                        else addFromCandidates total rest
                addFromCandidates total2 candArr
            else total2
        total3

    let trimOversize (deckSize:int) (counts:System.Collections.Generic.Dictionary<string,int>) (totalCards:int) =
        if totalCards <= deckSize then totalCards else
        let mutable over = totalCards - deckSize
        // First reduce any counts > 1 by as much as needed, starting from highest counts
        let keys = counts.Keys |> Seq.toArray
        while over > 0 do
            // sort by count descending so we shrink the largest piles first
            let ordered = keys |> Array.map (fun k -> k, counts[k]) |> Array.sortByDescending snd
            let mutable progressed = false
            for (n,v) in ordered do
                if over > 0 && v > 1 then
                    counts[n] <- v - 1
                    over <- over - 1
                    progressed <- true
            if not progressed then over <- 0
        deckSize

    let adjustPlaysets (counts:System.Collections.Generic.Dictionary<string,int>) =
        let getPlaysetNames () = counts |> Seq.choose (fun kv -> if kv.Value = 4 then Some kv.Key else None)
        let playsetCount () = getPlaysetNames() |> Seq.length
        let promotions = System.Collections.Generic.List<string>()
        let reductions = System.Collections.Generic.List<string>()
        let tryFindDonor exclude4Preferred =
            let seq = counts |> Seq.map (fun kv -> kv.Key, kv.Value)
            let ordered = if exclude4Preferred then seq |> Seq.sortByDescending (fun (_,v) -> if v = 4 then -1 else v) else seq |> Seq.sortBy (fun (_,v) -> v)
            ordered |> Seq.tryFind (fun (_,v) -> v > 1) |> Option.map fst
        let canInc name = counts[name] < 4
        let adjust targetMin targetMax =
            let rec promote curPl =
                if curPl >= targetMin then curPl else
                let triples = counts |> Seq.choose (fun kv -> if kv.Value = 3 then Some kv.Key else None) |> Seq.toList
                let rec loop pl xs =
                    match xs with
                    | [] -> pl
                    | n::rest ->
                        if pl >= targetMin then pl
                        elif canInc n then
                            match tryFindDonor true with
                            | Some d -> counts[d] <- counts[d] - 1; counts[n] <- 4; promotions.Add(n); reductions.Add(d); loop (pl + 1) rest
                            | None -> loop pl rest
                        else loop pl rest
                promote (loop curPl triples)
            let rec demote curPl =
                if curPl <= targetMax then curPl else
                let fours = getPlaysetNames() |> Seq.toList
                let rec loop pl xs =
                    match xs with
                    | [] -> pl
                    | n::rest ->
                        if pl <= targetMax then pl
                        else counts[n] <- 3; reductions.Add(n); loop (pl - 1) rest
                demote (loop curPl fours)
            let start = playsetCount()
            let afterPromotions = promote start
            let _ = demote afterPromotions
            ()
        adjust 5 12
        promotions, reductions

    // Final safety: ensure the deck has AT LEAST the requested size by topping up counts (respecting maxCopies and color legality)
    let ensureMinimumSize (deckSize:int) (maxOf:string->int) (isLegalNameByColor:string->bool) (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) (filteredByColor:string array) (counts:System.Collections.Generic.Dictionary<string,int>) (isGenuineName:string->bool) =
        let sumCounts () = counts.Values |> Seq.sum
        let canInc name =
            let cur = if counts.ContainsKey(name) then counts[name] else 0
            cur < maxOf name
        let rec fillExisting total =
            if total >= deckSize then total else
            // Try to bring all existing names up to their per-card caps in round-robin order of low counts first
            let ordered = counts |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.sortBy snd |> Seq.toArray
            let mutable grew = false
            let mutable acc = total
            for (n,v) in ordered do
                if acc < deckSize && v < maxOf n && isLegalNameByColor n then
                    counts[n] <- v + 1
                    acc <- acc + 1
                    grew <- true
            if grew then fillExisting acc else acc
        let afterExisting = fillExisting (sumCounts())
        let afterPool =
            if afterExisting >= deckSize then afterExisting else
            let pool = filteredByColor |> Array.distinct |> Array.sortBy id |> Array.toList
            let rec addFromPool total xs =
                match xs with
                | [] -> total
                | n::rest ->
                    if total >= deckSize then total else
                    let cur = if counts.ContainsKey(n) then counts[n] else 0
                    if cur < maxOf n then counts[n] <- cur + 1; addFromPool (total + 1) rest
                    else addFromPool total rest
            addFromPool afterExisting pool
        let afterCandidates =
            if afterPool >= deckSize then afterPool else
            let candList : Qdrant.Client.Grpc.ScoredPoint list = candidates |> Seq.toList
            let rec addFromCandidates total (xs: Qdrant.Client.Grpc.ScoredPoint list) =
                match xs with
                | [] -> total
                | c::rest ->
                    if total >= deckSize then total else
                    let n = Payload.fullName c.Payload
                    if isGenuineName n && isLegalNameByColor n then
                        let cur = if counts.ContainsKey(n) then counts[n] else 0
                        if cur < maxOf n then counts[n] <- cur + 1; addFromCandidates (total + 1) rest
                        else addFromCandidates total rest
                    else addFromCandidates total rest
            addFromCandidates afterPool candList
        afterCandidates

    let buildResponse (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) (inkMap:System.Collections.Generic.IDictionary<string,bool>) (counts:System.Collections.Generic.Dictionary<string,int>) (removed:int) (colorIllegal:string array) (promotions:System.Collections.Generic.List<string>) (reductions:System.Collections.Generic.List<string>) : DeckResponse =
        let urlMap = buildUrlMap candidates
        let resolveInkFinal = resolveInkWith inkMap
        let entries : ApiModels.CardEntry array =
            counts
            |> Seq.map (fun kv ->
                let name = kv.Key
                let count = kv.Value
                let key = normalizeName name
                let cm = if urlMap.ContainsKey(key) then urlMap[key] else ""
                let ink = resolveInkFinal name
                ({ count = count; fullName = name; inkable = ink; cardMarketUrl = cm } : ApiModels.CardEntry))
            |> Seq.sortBy (fun e -> e.fullName)
            |> Seq.toArray
        let explanation =
            let sb = StringBuilder()
            if removed > 0 then sb.AppendLine($"Removed {removed} non-card lines from model output.") |> ignore
            if colorIllegal.Length > 0 then
                let removedList = String.Join(", ", colorIllegal |> Array.truncate 12)
                let suffix = if colorIllegal.Length > 12 then ", …" else ""
                sb.AppendLine($"Removed {colorIllegal.Length} cards not matching chosen colors: {removedList}{suffix}.") |> ignore
            if promotions.Count > 0 || reductions.Count > 0 then
                let pro = String.Join(", ", promotions)
                let red = String.Join(", ", reductions)
                sb.AppendLine($"Adjusted playsets. Promotions: {pro}. Reductions: {red}.") |> ignore
            sb.ToString()
        { cards = entries; explanation = explanation }

    interface IDeckBuilder with
        member _.BuildDeck(query: DeckQuery) = task {
            match validateQuery query with
            | Error e -> return Error e
            | Ok () ->
                let _rulesTextEarly, allowedColorsEarly = getRulesAndAllowedColors query
                let! candRes = embedAndSearch query.request allowedColorsEarly
                match candRes with
                | Error e -> return Error e
                | Ok (emb, candidates) ->
                    let coreLegalCandidates = QdrantHelpers.filterLegalCardsPoints query candidates |> Seq.toArray :> seq<Qdrant.Client.Grpc.ScoredPoint>
                    let prep = prepare query coreLegalCandidates
                    let! rulesForPrompt = getRulesForPrompt (emb |> Array.map float32)
                    let legalCandidates, prompt = legalCandidatesAndPrompt query rulesForPrompt prep coreLegalCandidates
                    let! topupRes = generateAndTopUp query prep.RulesText legalCandidates prompt
                    match topupRes with
                    | Error e -> return Error e
                    | Ok toppedUp ->
                        // Use deckSize as a generous per-name cap during initial flattening; real per-card caps enforced later
                        let _raw, flat = toFlat query.deckSize query.deckSize toppedUp
                        let filtered, nonCardLines = cleanAndValidate prep.InkMap flat
                        let removed = nonCardLines.Length
                        let chosenColors, filteredByColor, colorIllegal, isLegalNameByColor = chooseColorsAndFilter prep.KnownColors prep.CardColors prep.AllowedColors filtered
                        let counts, _ = buildInitialCounts prep.ResolveMaxCopies filteredByColor
                        // Only consider names present in current Core-legal Qdrant candidates
                        let isGenuineName (name:string) =
                            let key = normalizeName name
                            prep.InkMap.ContainsKey(key)
                        let totalCards = fillToSize query.deckSize prep.ResolveMaxCopies isLegalNameByColor coreLegalCandidates filteredByColor counts isGenuineName
                        let totalCards = trimOversize query.deckSize counts totalCards
                        let promotions, reductions = adjustPlaysets counts
                        // Safety net: after adjustments, ensure we still meet at least the requested size
                        let _ = ensureMinimumSize query.deckSize prep.ResolveMaxCopies isLegalNameByColor coreLegalCandidates filteredByColor counts isGenuineName
                        let response = buildResponse coreLegalCandidates prep.InkMap counts removed colorIllegal promotions reductions
                        return Ok response
        }
