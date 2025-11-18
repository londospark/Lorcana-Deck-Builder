module DeckBuilder.Api.AgenticDeckService

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open OllamaSharp
open Qdrant.Client
open Card
open Inkable

// ===== TYPES =====

type SearchFilters = {
    Colors: string list option
    CostMin: int option
    CostMax: int option
    Inkable: bool option
    Format: DeckBuilder.Shared.DeckFormat option
}

type AgentState = {
    CurrentDeck: Map<string, int>
    SearchHistory: string list
    LastSearchResults: string option  // CSV string from last search
    Iteration: int
    TargetSize: int
    AllowedColors: string list
    Format: DeckBuilder.Shared.DeckFormat option
    Complete: bool
    Reasoning: string list
}

open System.Text.Json.Serialization

type CardEntry = {
    [<JsonPropertyName("name")>]
    Name: string
    [<JsonPropertyName("count")>]
    Count: int
}

// Custom converter to handle array format [["name", count]]
type CardEntryListConverter() =
    inherit JsonConverter<CardEntry list option>()
    
    override _.Read(reader: byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) =
        if reader.TokenType = JsonTokenType.Null then
            None
        elif reader.TokenType = JsonTokenType.StartArray then
            let mutable cards = []
            reader.Read() |> ignore // consume start array
            while reader.TokenType <> JsonTokenType.EndArray do
                if reader.TokenType = JsonTokenType.StartArray then
                    reader.Read() |> ignore // consume start of inner array
                    let name = reader.GetString()
                    reader.Read() |> ignore
                    let count = reader.GetInt32()
                    reader.Read() |> ignore // consume end of inner array
                    cards <- { Name = name; Count = count } :: cards
                reader.Read() |> ignore
            Some (List.rev cards)
        else
            raise (JsonException("Expected array for cards"))
    
    override _.Write(writer: Utf8JsonWriter, value: CardEntry list option, options: JsonSerializerOptions) =
        match value with
        | None -> writer.WriteNullValue()
        | Some cards ->
            writer.WriteStartArray()
            cards |> List.iter (fun card ->
                writer.WriteStartArray()
                writer.WriteStringValue(card.Name)
                writer.WriteNumberValue(card.Count)
                writer.WriteEndArray())
            writer.WriteEndArray()

type AgentResponse = {
    Action: string
    Query: string option
    Filters: SearchFilters option
    [<JsonConverter(typeof<CardEntryListConverter>)>]
    Cards: CardEntry list option
    Reasoning: string
}

// ===== PROMPTS =====

let buildAgentPrompt (state: AgentState) (userRequest: string) (rulesExcerpt: string option) (searchResults: string option) =
    let sb = StringBuilder()
    
    sb.AppendLine("You are an expert Disney Lorcana deck builder using an agentic approach.") |> ignore
    sb.AppendLine(sprintf "USER REQUEST: %s" userRequest) |> ignore
    sb.AppendLine(sprintf "GOAL: Build a complete %d-card deck that fulfills this request." state.TargetSize) |> ignore
    sb.AppendLine() |> ignore
    
    // Include relevant rules if available
    match rulesExcerpt with
    | Some rules when not (String.IsNullOrWhiteSpace rules) ->
        sb.AppendLine("LORCANA RULES (relevant excerpts from official comprehensive rules):") |> ignore
        sb.AppendLine(rules) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("Key deck building rules:") |> ignore
        sb.AppendLine("- Maximum 4 copies of any card (except cards with specific limits)") |> ignore
        sb.AppendLine("- 1-2 ink colors per deck") |> ignore
        sb.AppendLine("- Game objective: Reach 20 lore before your opponent") |> ignore
        sb.AppendLine() |> ignore
    | _ ->
        sb.AppendLine("Core deck building rules:") |> ignore
        sb.AppendLine("- Maximum 4 copies per card") |> ignore
        sb.AppendLine("- 1-2 ink colors only") |> ignore
        sb.AppendLine("- Objective: Reach 20 lore first") |> ignore
        sb.AppendLine() |> ignore
    
    sb.AppendLine("CRITICAL RULE: The deck MUST contain AT LEAST the target size.") |> ignore
    sb.AppendLine(sprintf "- You CANNOT finalize until you have AT LEAST %d cards total" state.TargetSize) |> ignore
    sb.AppendLine(sprintf "- Target: %d cards minimum (a few extra cards %d-%d is acceptable if needed)" state.TargetSize state.TargetSize (state.TargetSize + 2)) |> ignore
    sb.AppendLine() |> ignore
    
    // Format restrictions
    match state.Format with
    | Some DeckBuilder.Shared.DeckFormat.Core ->
        sb.AppendLine("FORMAT: Core") |> ignore
        sb.AppendLine("- Only cards legal in Core format will be returned in searches") |> ignore
        sb.AppendLine("- Newer sets may not be legal in Core format") |> ignore
        sb.AppendLine() |> ignore
    | Some DeckBuilder.Shared.DeckFormat.Infinity ->
        sb.AppendLine("FORMAT: Infinity") |> ignore
        sb.AppendLine("- Only cards legal in Infinity format will be returned in searches") |> ignore
        sb.AppendLine("- Some cards are banned or restricted in Infinity format") |> ignore
        sb.AppendLine() |> ignore
    | None ->
        sb.AppendLine("FORMAT: All cards (no format restrictions)") |> ignore
        sb.AppendLine() |> ignore
    
    // Current state
    let currentCount = state.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
    sb.AppendLine("CURRENT STATE:") |> ignore
    sb.AppendLine(sprintf "- Cards in deck: %d/%d" currentCount state.TargetSize) |> ignore
    sb.AppendLine(sprintf "- Iteration: %d" state.Iteration) |> ignore
    
    if state.AllowedColors.Length > 0 then
        let colorsStr = String.Join(", ", state.AllowedColors)
        sb.AppendLine(sprintf "- Required colors: %s (user-specified - must use these)" colorsStr) |> ignore
    else
        sb.AppendLine("- Color selection: FLEXIBLE - analyze search results to pick optimal colors") |> ignore
        sb.AppendLine("  → Search for theme-related cards FIRST, then pick colors with most support") |> ignore
        sb.AppendLine("  → Look at which colors appear most in your search results") |> ignore
        sb.AppendLine("  → Color identities based on actual Lorcana card data:") |> ignore
        sb.AppendLine("    • Amber: Songs/singing (231 mentions), Bodyguard/Support defensive play, healing") |> ignore
        sb.AppendLine("    • Amethyst: Evasive creatures, card draw/filtering (102 mentions), bounce/control") |> ignore
        sb.AppendLine("    • Emerald: Ward protection, aggressive challenges/damage, go-wide strategies") |> ignore
        sb.AppendLine("    • Ruby: Aggressive (Rush/Reckless), direct damage/banishment (140 mentions), lore drain") |> ignore
        sb.AppendLine("    • Sapphire: Items (105 mentions), cost reduction, ready/exert manipulation, defensive") |> ignore
        sb.AppendLine("    • Steel: Removal/banishment (236 mentions!), Resist durability, Bodyguard, challenges") |> ignore
        sb.AppendLine("  → If struggling to reach target size, try different color combinations") |> ignore
    
    if not state.CurrentDeck.IsEmpty then
        sb.AppendLine() |> ignore
        sb.AppendLine("DECK SO FAR:") |> ignore
        state.CurrentDeck 
        |> Map.toSeq
        |> Seq.sortByDescending snd
        |> Seq.iter (fun (name, count) -> 
            sb.AppendLine(sprintf "  %dx %s" count name) |> ignore)
    
    sb.AppendLine() |> ignore
    
    // Search results if any
    match searchResults with
    | Some results ->
        sb.AppendLine("SEARCH RESULTS FROM LAST QUERY:") |> ignore
        sb.AppendLine("(JSON array of cards with essential fields)") |> ignore
        sb.AppendLine(results) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("HOW TO USE THIS DATA:") |> ignore
        sb.AppendLine("- 'colors' field shows which ink colors these cards belong to") |> ignore
        if state.AllowedColors.Length = 0 then
            sb.AppendLine("- ⚠️ IMPORTANT: Count which colors appear most frequently in results!") |> ignore
            sb.AppendLine("- Choose 1-2 colors that have the most synergistic cards for the theme") |> ignore
        sb.AppendLine("- 'text' shows abilities and effects - READ THIS to understand synergies") |> ignore
        sb.AppendLine("- 'cost' is mana cost (aim for smooth curve 1-6)") |> ignore
        sb.AppendLine("- 'ink' Y means can be played as ink for resource generation (~75% should be Y)") |> ignore
        sb.AppendLine("- 'max' is usually 4, but some cards have special limits") |> ignore
        sb.AppendLine() |> ignore
    | None -> 
        ()
    
    // Instructions
    sb.AppendLine("AVAILABLE ACTIONS:") |> ignore
    sb.AppendLine("1. search_cards - Search Qdrant for cards matching criteria") |> ignore
    sb.AppendLine("2. add_cards - Add specific cards from search results to deck") |> ignore
    sb.AppendLine("3. finalize - Complete deck building (only when target size reached)") |> ignore
    sb.AppendLine() |> ignore
    
    sb.AppendLine("RESPOND WITH VALID JSON ONLY:") |> ignore
    sb.AppendLine("{") |> ignore
    sb.AppendLine("  \"action\": \"search_cards\" | \"add_cards\" | \"finalize\",") |> ignore
    sb.AppendLine("  \"query\": \"search text\" (required for search_cards),") |> ignore
    sb.AppendLine("  \"filters\": { \"colors\": [\"Amber\"], \"costMin\": 1, \"costMax\": 3, \"inkable\": true } (optional),") |> ignore
    sb.AppendLine("  \"cards\": [[\"Card Name\", 4]] (required for add_cards - use EXACT names from search),") |> ignore
    sb.AppendLine("  \"reasoning\": \"brief explanation\"") |> ignore
    sb.AppendLine("}") |> ignore
    sb.AppendLine() |> ignore
    
    sb.AppendLine("STRATEGY GUIDANCE:") |> ignore
    if currentCount = 0 then
        sb.AppendLine("- START: You MUST search for cards related to the user's request FIRST") |> ignore
        if state.AllowedColors.Length = 0 then
            sb.AppendLine("- ⚠️ CRITICAL: DO NOT apply color filters in your first search!") |> ignore
            sb.AppendLine("- First search should have NO filters to discover which colors support this theme") |> ignore
            sb.AppendLine("- WORKFLOW:") |> ignore
            sb.AppendLine("  1. search_cards with NO color filters → see all matching cards") |> ignore
            sb.AppendLine("  2. Count which colors appear most in the COLORS column") |> ignore
            sb.AppendLine("  3. add_cards from the 1-2 colors with most/best support") |> ignore
            sb.AppendLine("  4. Continue building with those colors only") |> ignore
            sb.AppendLine("- Do NOT default to Amber+Emerald - let the search results guide color choice!") |> ignore
        else
            sb.AppendLine("- Colors are pre-selected by user, search for synergies in those colors") |> ignore
        sb.AppendLine("- Read fullText carefully to identify powerful abilities and synergies") |> ignore
        sb.AppendLine("- Look for characters with high lore (quest value) or strong effects") |> ignore
    elif currentCount < state.TargetSize then
        let remaining = state.TargetSize - currentCount
        
        // CRITICAL: Force add_cards if we have search results
        match state.LastSearchResults with
        | Some csv when not (String.IsNullOrWhiteSpace csv) ->
            sb.AppendLine() |> ignore
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━") |> ignore
            sb.AppendLine("⚠️⚠️⚠️ YOU JUST DID A SEARCH! ⚠️⚠️⚠️") |> ignore
            sb.AppendLine("Your NEXT action MUST be 'add_cards'!") |> ignore
            sb.AppendLine("DO NOT search again - pick cards from the results above and add them.") |> ignore
            sb.AppendLine(sprintf "Need to add at least %d cards total to reach %d." remaining state.TargetSize) |> ignore
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━") |> ignore
            sb.AppendLine() |> ignore
        | _ -> ()
        
        sb.AppendLine(sprintf "- CONTINUE: Need AT LEAST %d more cards (current: %d, target: %d)" remaining currentCount state.TargetSize) |> ignore
        sb.AppendLine("- DO NOT use 'finalize' action yet - deck is incomplete!") |> ignore
        
        // Add color flexibility guidance if user didn't specify colors AND deck is struggling
        if state.AllowedColors.Length = 0 && state.Iteration > 5 && remaining > 10 then
            sb.AppendLine("⚠️ IMPORTANT: If struggling to find enough cards:") |> ignore
            sb.AppendLine("  → Consider switching to different color combinations") |> ignore
            sb.AppendLine("  → Some colors (e.g., Amber/Steel, Sapphire/Steel) have more card options") |> ignore
            sb.AppendLine("  → Try broader searches or pivot to colors with better availability") |> ignore
        
        sb.AppendLine("- Build synergies: Look for cards with complementary abilities in fullText") |> ignore
        sb.AppendLine("- Fill gaps in mana curve (aim for smooth 1-6 cost distribution)") |> ignore
        sb.AppendLine("- Ensure ~70-80%% of cards are inkable for resource consistency") |> ignore
        sb.AppendLine("- Balance characters (lore generation) with actions/items (removal/utility)") |> ignore
    else
        sb.AppendLine(sprintf "- READY TO FINALIZE: Deck has %d cards (target: %d)" currentCount state.TargetSize) |> ignore
        sb.AppendLine("- Use 'finalize' action to complete deck building") |> ignore
    
    sb.ToString()

let parseAgentResponse (json: string) : Result<AgentResponse, string> =
    try
        let trimmed = json.Trim()
        
        // Find first '{' - gemma3 might add preamble
        let firstBrace = trimmed.IndexOf('{')
        if firstBrace = -1 then
            Error "No JSON object found in response"
        else
            let cleanJson = 
                if firstBrace > 0 then
                    // Extract from first { to end, then find matching }
                    let startFrom = trimmed.Substring(firstBrace)
                    let mutable braceCount = 0
                    let mutable inString = false
                    let mutable escaped = false
                    let mutable jsonEnd = -1
                    
                    for i = 0 to startFrom.Length - 1 do
                        let c = startFrom.[i]
                        if escaped then escaped <- false
                        elif c = '\\' && inString then escaped <- true
                        elif c = '"' then inString <- not inString
                        elif not inString then
                            if c = '{' then braceCount <- braceCount + 1
                            elif c = '}' then
                                braceCount <- braceCount - 1
                                if braceCount = 0 then jsonEnd <- i + 1
                    
                    if jsonEnd > 0 then startFrom.Substring(0, jsonEnd) else startFrom
                else
                    trimmed
            
            let options = JsonSerializerOptions()
            options.PropertyNameCaseInsensitive <- true
            let response = JsonSerializer.Deserialize<AgentResponse>(cleanJson, options)
            Ok response
    with ex ->
        Error $"Failed to parse agent response: {ex.Message}"

// ===== QDRANT NATIVE FILTERING HELPERS =====

let buildQdrantFilter (filters: SearchFilters option) =
    let filter = Qdrant.Client.Grpc.Filter()
    
    match filters with
    | None -> filter // Empty filter
    | Some f ->
        // Color filtering - card must have at least one of the specified colors
        match f.Colors with
        | Some colors when not (List.isEmpty colors) ->
            // Build "should" clause: color1 OR color2 OR color3
            let shouldClause = Qdrant.Client.Grpc.Filter()
            for color in colors do
                let cond = Qdrant.Client.Grpc.Condition()
                let fc = Qdrant.Client.Grpc.FieldCondition()
                fc.Key <- "colors"
                let m = Qdrant.Client.Grpc.Match()
                m.Keyword <- color
                fc.Match <- m
                cond.Field <- fc
                shouldClause.Should.Add(cond)
            
            // Add should clause to main filter
            if shouldClause.Should.Count > 0 then
                let outerCond = Qdrant.Client.Grpc.Condition()
                outerCond.Filter <- shouldClause
                filter.Must.Add(outerCond)
        | _ -> ()
        
        // Cost range filtering
        match f.CostMin, f.CostMax with
        | Some minCost, Some maxCost ->
            // Range filter: cost >= minCost AND cost <= maxCost
            let cond = Qdrant.Client.Grpc.Condition()
            let fc = Qdrant.Client.Grpc.FieldCondition()
            fc.Key <- "cost"
            let range = Qdrant.Client.Grpc.Range()
            range.Gte <- float minCost
            range.Lte <- float maxCost
            fc.Range <- range
            cond.Field <- fc
            filter.Must.Add(cond)
        | Some minCost, None ->
            // Just minimum: cost >= minCost
            let cond = Qdrant.Client.Grpc.Condition()
            let fc = Qdrant.Client.Grpc.FieldCondition()
            fc.Key <- "cost"
            let range = Qdrant.Client.Grpc.Range()
            range.Gte <- float minCost
            fc.Range <- range
            cond.Field <- fc
            filter.Must.Add(cond)
        | None, Some maxCost ->
            // Just maximum: cost <= maxCost
            let cond = Qdrant.Client.Grpc.Condition()
            let fc = Qdrant.Client.Grpc.FieldCondition()
            fc.Key <- "cost"
            let range = Qdrant.Client.Grpc.Range()
            range.Lte <- float maxCost
            fc.Range <- range
            cond.Field <- fc
            filter.Must.Add(cond)
        | None, None -> ()
        
        // Inkable filtering
        match f.Inkable with
        | Some inkable ->
            let cond = Qdrant.Client.Grpc.Condition()
            let fc = Qdrant.Client.Grpc.FieldCondition()
            fc.Key <- "inkable"
            let m = Qdrant.Client.Grpc.Match()
            m.Boolean <- inkable
            fc.Match <- m
            cond.Field <- fc
            filter.Must.Add(cond)
        | None -> ()
        
        filter

// ===== SEARCH & RETRIEVAL =====

let searchCardsInQdrant (qdrant: QdrantClient) (embeddingGen: Func<string, Task<float32 array>>) (query: string) (filters: SearchFilters option) (limit: int) (logger: Microsoft.Extensions.Logging.ILogger) = task {
    logger.LogDebug("searchCardsInQdrant called with query: {Query}, limit: {Limit}", query.Substring(0, Math.Min(50, query.Length)), limit)
    
    // Generate embedding for query
    let! vector = embeddingGen.Invoke(query)
    logger.LogDebug("Embedding generated, vector length: {Length}", vector.Length)
    
    // Build Qdrant native filter
    let qdrantFilter = buildQdrantFilter filters
    let hasFilters = 
        match filters with
        | Some f -> f.Colors.IsSome || f.CostMin.IsSome || f.CostMax.IsSome || f.Inkable.IsSome
        | None -> false
    
    if hasFilters then
        logger.LogInformation("Using Qdrant NATIVE filtering: colors={Colors}, cost={CostRange}, inkable={Inkable}", 
            (match filters with Some f -> f.Colors |> Option.map (String.concat ",") | _ -> None) |> Option.defaultValue "any",
            (match filters with Some f -> sprintf "%A-%A" f.CostMin f.CostMax | _ -> "any"),
            (match filters with Some f -> f.Inkable |> Option.map string | _ -> None) |> Option.defaultValue "any")
    
    // Search with native filtering - much faster!
    logger.LogDebug("Searching Qdrant with native filter, limit: {Limit}", limit)
    let searchLimit = uint64 (limit * 2) // Get 2x to account for format filtering post-search
    let! results = 
        if hasFilters then
            qdrant.SearchAsync("lorcana_cards", vector, filter = qdrantFilter, limit = searchLimit)
        else
            qdrant.SearchAsync("lorcana_cards", vector, limit = searchLimit)
    
    logger.LogInformation("Qdrant search returned {Count} results (AFTER native filtering)", Seq.length results)
    
    // Post-filter only for format legality (can't do this in Qdrant easily)
    let filteredResults = 
        results
        |> Seq.filter (fun point ->
            let payload = point.Payload
            match filters with
            | Some f when f.Format.IsSome -> Payload.isAllowedInFormat payload f.Format.Value
            | _ -> true)
        |> Seq.truncate limit
    
    logger.LogDebug("After format filtering: {Count} results", Seq.length filteredResults)
    
    // Format results as JSON array for LLM - much more compact and LLM-friendly
    let cards = 
        filteredResults
        |> Seq.map (fun point ->
            let fullName = Payload.fullName point.Payload
            let cardType = Payload.cardType point.Payload
            let cost = Payload.cost point.Payload
            let inkable = Payload.inkable point.Payload |> Option.defaultValue false
            let colors = Payload.colors point.Payload
            let fullText = Payload.fullText point.Payload
            let maxCopies = Payload.maxCopiesInDeck point.Payload |> Option.defaultValue 4
            
            // Create concise JSON object with only essential fields
            let colorStr = colors |> String.concat ","
            let costStr = cost |> Option.map string |> Option.defaultValue "?"
            let inkableStr = if inkable then "Y" else "N"
            let textBrief = if fullText.Length > 100 then fullText.Substring(0, 97) + "..." else fullText
            
            sprintf """{"name":"%s","type":"%s","cost":%s,"ink":"%s","colors":"%s","text":"%s","max":%d}"""
                (fullName.Replace("\"", "\\\""))
                cardType
                costStr
                inkableStr
                colorStr
                (textBrief.Replace("\"", "\\\"").Replace("\n", " "))
                maxCopies)
        |> String.concat ","
    
    let json = sprintf "[%s]" cards
    
    logger.LogInformation("Search completed: JSON length={Length}, card count={Count}", json.Length, Seq.length filteredResults)
    return json
}

// Legacy CSV formatting (keeping for now but unused)
let formatSearchResultsAsCSV (filteredResults: seq<Qdrant.Client.Grpc.ScoredPoint>) (logger: Microsoft.Extensions.Logging.ILogger) =
    let sb = StringBuilder()
    sb.AppendLine("fullName,type,cost,inkable,colors,strength,willpower,lore,fullText,subtypes,rarity,story,maxCopies") |> ignore
    
    for point in filteredResults do
        let fullName = Payload.fullName point.Payload
        let cardType = Payload.cardType point.Payload
        let cost = Payload.cost point.Payload |> Option.map string |> Option.defaultValue ""
        let inkable = Payload.inkable point.Payload |> Option.map (fun b -> if b then "Y" else "N") |> Option.defaultValue ""
        let colors = Payload.colors point.Payload |> String.concat "|"
        let strength = Payload.strength point.Payload |> Option.map string |> Option.defaultValue ""
        let willpower = Payload.willpower point.Payload |> Option.map string |> Option.defaultValue ""
        let lore = Payload.lore point.Payload |> Option.map string |> Option.defaultValue ""
        let fullText = Payload.fullText point.Payload
        let subtypes = Payload.subtypes point.Payload |> String.concat "|"
        let rarity = Payload.rarity point.Payload
        let story = Payload.story point.Payload
        let maxCopies = Payload.maxCopiesInDeck point.Payload |> Option.map string |> Option.defaultValue "4"
        
        // Escape quotes in text fields
        let escapeCSV (s: string) = 
            if String.IsNullOrEmpty s then ""
            else s.Replace("\"", "\"\"")
        
        sb.AppendLine(sprintf "\"%s\",\"%s\",%s,%s,\"%s\",%s,%s,%s,\"%s\",\"%s\",\"%s\",\"%s\",%s"
            (escapeCSV fullName) 
            (escapeCSV cardType) 
            cost 
            inkable 
            (escapeCSV colors)
            strength
            willpower
            lore
            (escapeCSV fullText) 
            (escapeCSV subtypes)
            (escapeCSV rarity)
            (escapeCSV story)
            maxCopies) |> ignore
    
    logger.LogDebug("Formatted CSV response length: {Length}", sb.Length)
    sb.ToString()

// ===== RULES FETCHING =====

let getRulesForPrompt (qdrant: QdrantClient) (embeddingGen: Func<string, Task<float32 array>>) (userRequest: string) = task {
    try
        // Generate embedding for user request
        let! vector = embeddingGen.Invoke(userRequest)
        
        // Search rules collection for relevant excerpts
        let! hits = qdrant.SearchAsync("lorcana_rules", vector, limit = 6uL)
        
        let texts =
            hits
            |> Seq.choose (fun p ->
                let ok, v = p.Payload.TryGetValue("text")
                if ok && not (isNull v) then
                    let s = v.StringValue
                    if String.IsNullOrWhiteSpace s then None else Some s
                else None)
            |> Seq.truncate 6
            |> Seq.toArray
        
        if texts.Length = 0 then 
            return None
        else
            let joined = String.Join("\n\n---\n\n", texts)
            // Cap to 1500 chars for prompt efficiency
            let maxLen = 1500
            return Some (if joined.Length > maxLen then joined.Substring(0, maxLen) else joined)
    with _ ->
        return None
}

// ===== AGENT LOOP =====

let rec agentLoop 
    (ollama: IOllamaApiClient) 
    (qdrant: QdrantClient)
    (embeddingGen: Func<string, Task<float32 array>>)
    (userRequest: string)
    (rulesExcerpt: string option)
    (state: AgentState) 
    (searchResults: string option)
    (maxIterations: int)
    (logger: Microsoft.Extensions.Logging.ILogger) = task {
    
    logger.LogInformation("AgentLoop iteration {Iteration}, complete: {Complete}, cards: {CardCount}", state.Iteration, state.Complete, state.CurrentDeck.Count)
    
    if state.Complete || state.Iteration >= maxIterations then
        logger.LogInformation("Agent loop finished: complete={Complete}, maxIterations={MaxIterations}", state.Complete, state.Iteration >= maxIterations)
        return Ok state
    else
        // Build prompt
        logger.LogDebug("Building agent prompt...")
        let prompt = buildAgentPrompt state userRequest rulesExcerpt searchResults
        logger.LogDebug("Agent prompt length: {Length}", prompt.Length)
        
        // Get LLM decision
        let genReq = OllamaSharp.Models.GenerateRequest()
        genReq.Model <- "gemma3:12b"
        genReq.Prompt <- prompt
        
        logger.LogInformation("Calling Ollama GenerateAsync for agent decision...")
        let! llmResponse = task {
            let stream = ollama.GenerateAsync(genReq)
            let sb = StringBuilder()
            let e = stream.GetAsyncEnumerator()
            let mutable chunkCount = 0
            let rec loop () = task {
                let! moved = e.MoveNextAsync().AsTask()
                if moved then
                    let chunk = e.Current
                    if not (isNull chunk) && not (String.IsNullOrWhiteSpace chunk.Response) then
                        sb.Append(chunk.Response) |> ignore
                        chunkCount <- chunkCount + 1
                    return! loop()
                else
                    return sb.ToString()
            }
            let! result = loop()
            logger.LogDebug("Agent LLM response received, {ChunkCount} chunks, length: {Length}", chunkCount, result.Length)
            return result
        }
        
        // Parse response
        match parseAgentResponse llmResponse with
        | Error err ->
            logger.LogError("Agent response parse error: {Error}", err)
            logger.LogError("Failed response: {Response}", llmResponse.Substring(0, Math.Min(500, llmResponse.Length)))
            return Error (sprintf "Agent response parse error at iteration %d: %s" state.Iteration err)
        
        | Ok response ->
            let reasoningPreview = 
                if isNull response.Reasoning then "[no reasoning provided]"
                else response.Reasoning.Substring(0, Math.Min(100, response.Reasoning.Length))
            logger.LogInformation("Agent action: {Action}, reasoning: {Reasoning}", response.Action, reasoningPreview)
            let reasoning = if isNull response.Reasoning then "No reasoning provided" else response.Reasoning
            let newState = { state with 
                                Iteration = state.Iteration + 1
                                Reasoning = reasoning :: state.Reasoning }
            
            match response.Action.ToLowerInvariant() with
            | "search_cards" ->
                match response.Query with
                | Some query ->
                    logger.LogInformation("Executing search_cards with query: {Query}", query.Substring(0, Math.Min(50, query.Length)))
                    // Merge response filters with format from state
                    let mergedFilters = 
                        match response.Filters with
                        | Some f -> Some { f with Format = state.Format }
                        | None -> Some { Colors = None; CostMin = None; CostMax = None; Inkable = None; Format = state.Format }
                    
                    // Execute search - return TOP 20 cards only to avoid overwhelming LLM
                    let! results = searchCardsInQdrant qdrant embeddingGen query mergedFilters 20 logger
                    // Count cards in JSON array
                    let cardCount = 
                        if results.StartsWith("[") && results.EndsWith("]") then
                            results.Split([|'{'|], StringSplitOptions.RemoveEmptyEntries).Length - 1
                        else 0
                    logger.LogInformation("Search completed: JSON length={Length}, card count={CardCount}", results.Length, cardCount)
                    
                    if cardCount = 0 then
                        logger.LogWarning("Search returned 0 cards - prompt will ask agent to try different search")
                        let stateWithResults = { newState with LastSearchResults = None }
                        return! agentLoop ollama qdrant embeddingGen userRequest rulesExcerpt stateWithResults None maxIterations logger
                    else
                        // Update state with search results so next prompt can reference them
                        let stateWithResults = { newState with LastSearchResults = Some results }
                        // Continue loop with results
                        return! agentLoop ollama qdrant embeddingGen userRequest rulesExcerpt stateWithResults (Some results) maxIterations logger
                | None ->
                    logger.LogWarning("search_cards action missing query")
                    return Error "search_cards action requires query"
            
            | "add_cards" ->
                match response.Cards with
                | Some cards ->
                    logger.LogInformation("Executing add_cards with {Count} card entries", cards.Length)
                    // Add cards to deck
                    let updatedDeck = 
                        cards 
                        |> List.fold (fun (deck: Map<string, int>) card ->
                            let current = deck |> Map.tryFind card.Name |> Option.defaultValue 0
                            deck |> Map.add card.Name (current + card.Count)
                        ) newState.CurrentDeck
                    
                    let totalCards = updatedDeck |> Map.fold (fun acc _ count -> acc + count) 0
                    logger.LogInformation("Deck updated to {TotalCards} cards", totalCards)
                    // Clear LastSearchResults after adding cards
                    let newState2 = { newState with CurrentDeck = updatedDeck; LastSearchResults = None }
                    
                    // Continue loop
                    return! agentLoop ollama qdrant embeddingGen userRequest rulesExcerpt newState2 None maxIterations logger
                | None ->
                    logger.LogWarning("add_cards action missing cards")
                    return Error "add_cards action requires cards"
            
            | "finalize" ->
                // Strict validation: deck MUST be at least target size
                let totalCards = newState.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
                logger.LogInformation("Finalize requested with {TotalCards} cards, target: {TargetSize}", totalCards, state.TargetSize)
                if totalCards >= state.TargetSize then
                    logger.LogInformation("Deck finalized successfully")
                    return Ok { newState with Complete = true }
                else
                    let shortage = state.TargetSize - totalCards
                    logger.LogWarning("Cannot finalize: deck too small by {Shortage} cards", shortage)
                    // Reject finalize, force agent to add more cards
                    return Error (sprintf "Cannot finalize: deck has only %d cards, need at least %d (short by %d cards). Continue searching and adding cards." totalCards state.TargetSize shortage)
            
            | _ ->
                logger.LogError("Unknown action: {Action}", response.Action)
                return Error (sprintf "Unknown action: %s" response.Action)
}

// ===== PUBLIC API =====

let buildDeckAgentic 
    (ollama: IOllamaApiClient)
    (qdrant: QdrantClient)
    (embeddingGen: Func<string, Task<float32 array>>)
    (query: DeckBuilder.Shared.DeckQuery)
    (logger: Microsoft.Extensions.Logging.ILogger) = task {
    
    logger.LogInformation("buildDeckAgentic started: deckSize={DeckSize}, request={Request}", query.deckSize, query.request)
    
    let allowedColors = 
        query.selectedColors 
        |> Option.defaultValue [||]
        |> Array.toList
    
    logger.LogInformation("Allowed colors: {Colors}", String.Join(",", allowedColors))
    
    // Fetch relevant rules excerpts using RAG
    logger.LogDebug("Fetching rules excerpts...")
    let! rulesExcerpt = getRulesForPrompt qdrant embeddingGen query.request
    logger.LogDebug("Rules excerpt fetched, length: {Length}", match rulesExcerpt with Some r -> r.Length | None -> 0)
    
    let initialState = {
        CurrentDeck = Map.empty
        SearchHistory = []
        LastSearchResults = None
        Iteration = 0
        TargetSize = query.deckSize
        AllowedColors = allowedColors
        Format = query.format
        Complete = false
        Reasoning = []
    }
    
    logger.LogInformation("Starting agent loop with max 30 iterations...")
    // Increased max iterations to allow deck completion
    let! result = agentLoop ollama qdrant embeddingGen query.request rulesExcerpt initialState None 30 logger
    
    match result with
    | Ok finalState ->
        // CRITICAL VALIDATION: Ensure deck meets minimum size requirement
        let totalCards = finalState.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
        
        logger.LogInformation("Agent loop completed: {TotalCards} cards built", totalCards)
        
        if totalCards < query.deckSize then
            // Deck is too small - this should never happen due to finalize validation
            logger.LogError("Deck building failed: insufficient cards {TotalCards}/{DeckSize}", totalCards, query.deckSize)
            return Error (sprintf "Deck building failed: only %d cards built, required at least %d. Agent completed prematurely." totalCards query.deckSize)
        else
            logger.LogDebug("Building response...")
            let cards = 
                finalState.CurrentDeck 
                |> Map.toArray
                |> Array.map (fun (name, cnt) -> ({
                    count = cnt
                    fullName = name
                    inkable = false  // TODO: Look up actual value
                    cardMarketUrl = ""
                    inkColor = ""
                } : DeckBuilder.Shared.CardEntry))
            
            let explanation = 
                let sb = StringBuilder()
                sb.AppendLine(sprintf "Built %d-card deck in %d iterations using agentic RAG." totalCards finalState.Iteration) |> ignore
                sb.AppendLine() |> ignore
                sb.AppendLine("Reasoning history:") |> ignore
                finalState.Reasoning 
                |> List.rev
                |> List.iteri (fun i reasoning -> 
                    sb.AppendLine(sprintf "%d. %s" (i + 1) reasoning) |> ignore)
                sb.ToString()
            
            logger.LogInformation("buildDeckAgentic completed successfully with {Count} cards", cards.Length)
            return Ok ({
                cards = cards
                explanation = explanation
            } : DeckBuilder.Shared.DeckResponse)
    
    | Error err ->
        logger.LogError("Agent loop failed: {Error}", err)
        return Error err
}
