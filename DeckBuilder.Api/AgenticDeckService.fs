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
    Format: DeckBuilder.Shared.DeckFormat
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
    | DeckBuilder.Shared.DeckFormat.Core ->
        sb.AppendLine("FORMAT: Core") |> ignore
        sb.AppendLine("- Only cards legal in Core format will be returned in searches") |> ignore
        sb.AppendLine("- Newer sets may not be legal in Core format") |> ignore
        sb.AppendLine() |> ignore
    | DeckBuilder.Shared.DeckFormat.Infinity ->
        sb.AppendLine("FORMAT: Infinity") |> ignore
        sb.AppendLine("- Only cards legal in Infinity format will be returned in searches") |> ignore
        sb.AppendLine("- Some cards are banned or restricted in Infinity format") |> ignore
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
        sb.AppendLine("  → Color identities (general tendencies, not prescriptive):") |> ignore
        sb.AppendLine("    • Amber: supportive effects, healing, defensive play") |> ignore
        sb.AppendLine("    • Amethyst: evasive strategies, card draw/filtering, control/bounce") |> ignore
        sb.AppendLine("    • Emerald: protection, challenge pressure, go-wide strategies") |> ignore
        sb.AppendLine("    • Ruby: aggressive tempo, direct interaction, pressure") |> ignore
        sb.AppendLine("    • Sapphire: economy and cost reduction, ready/exert manipulation, defensive") |> ignore
        sb.AppendLine("    • Steel: removal/banishment, durability, challenge dominance") |> ignore
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
        sb.AppendLine("- Balance characters (lore generation) with support/interaction effects (removal/utility)") |> ignore
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
    
    // Build Qdrant native filter (colors, cost, inkable)
    let baseFilter = buildQdrantFilter filters
    
    // Add format filter (now required)
    let format = 
        match filters with
        | Some f when f.Format.IsSome -> f.Format.Value
        | _ -> DeckBuilder.Shared.DeckFormat.Core
    
    logger.LogDebug("Using format: {Format}", format)
    let formatFilter = QdrantHelpers.buildFormatFilter format
    
    // Combine base filter with format filter
    let combinedFilter = 
        match formatFilter with
        | Some ff ->
            // Merge filters: copy all Must conditions from both
            let combined = Qdrant.Client.Grpc.Filter()
            for cond in baseFilter.Must do
                combined.Must.Add(cond)
            for cond in ff.Must do
                combined.Must.Add(cond)
            combined
        | None -> baseFilter
    
    let hasFilters = 
        match filters with
        | Some f -> f.Colors.IsSome || f.CostMin.IsSome || f.CostMax.IsSome || f.Inkable.IsSome || f.Format.IsSome
        | None -> false
    
    if hasFilters then
        logger.LogInformation("Using Qdrant NATIVE filtering: colors={Colors}, cost={CostRange}, inkable={Inkable}, format={Format}", 
            (match filters with Some f -> f.Colors |> Option.map (String.concat ",") | _ -> None) |> Option.defaultValue "any",
            (match filters with Some f -> sprintf "%A-%A" f.CostMin f.CostMax | _ -> "any"),
            (match filters with Some f -> f.Inkable |> Option.map string | _ -> None) |> Option.defaultValue "any",
            format)
    
    // Search with native filtering (including format)
    logger.LogDebug("Searching Qdrant with native filter (including format), limit: {Limit}", limit)
    let searchLimit = uint64 (limit * 2)
    let! results = qdrant.SearchAsync("lorcana_cards", vector, filter = combinedFilter, limit = searchLimit)
    
    logger.LogInformation("Qdrant search returned {Count} results (AFTER native filtering including format)", Seq.length results)
    
    // No post-filtering needed - format is now handled by Qdrant
    let filteredResults = results |> Seq.truncate limit
    
    logger.LogDebug("After format filtering: {Count} results", Seq.length filteredResults)
    
    // Format results as JSON array for LLM
    let cards = 
        filteredResults
        |> Seq.map (fun point ->
            let fullName = Payload.fullName point.Payload
            let cardType = Payload.cardType point.Payload
            let cost = Payload.cost point.Payload
            let inkable = Payload.inkable point.Payload |> Option.defaultValue false
            let colors = Payload.colors point.Payload
            let fullText = Payload.fullText point.Payload
            let subtypes = Payload.subtypes point.Payload
            let maxCopies = Payload.maxCopiesInDeck point.Payload |> Option.defaultValue 4
            let cardUrl = Payload.cardMarketUrl point.Payload |> Option.defaultValue ""
            
            let colorStr = colors |> String.concat ","
            let subtypesJson =
                if subtypes.IsEmpty then "[]"
                else
                    let esc (s:string) = "\"" + (s.Replace("\"","\\\"")) + "\""
                    "[" + (subtypes |> List.map esc |> String.concat ",") + "]"
            let costStr = cost |> Option.map string |> Option.defaultValue "?"
            let inkableStr = if inkable then "Y" else "N"
            let textBrief = if fullText.Length > 100 then fullText.Substring(0, 97) + "..." else fullText
            
            sprintf """{"name":"%s","type":"%s","cost":%s,"ink":"%s","colors":"%s","subtypes":%s,"text":"%s","max":%d,"cardMarketUrl":"%s"}"""
                (fullName.Replace("\"", "\\\""))
                cardType
                costStr
                inkableStr
                colorStr
                subtypesJson
                (textBrief.Replace("\"", "\\\"").Replace("\n", " "))
                maxCopies
                (cardUrl.Replace("\"", "\\\"")))
        |> String.concat ","
    
    let json = sprintf "[%s]" cards
    
    logger.LogInformation("Search completed: JSON length={Length}, card count={Count}", json.Length, Seq.length filteredResults)
    return json
}

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

// ===== LLM-BASED COLOR SELECTION =====

let selectOptimalColors
    (ollama: IOllamaApiClient)
    (userRequest: string)
    (searchResults: JsonElement array)
    (logger: Microsoft.Extensions.Logging.ILogger)
    : Task<string list> = task {
    
    logger.LogInformation("Asking LLM to select optimal 2 colors for deck theme: {Request}", userRequest)
    
    // Analyze color distribution in search results
    let colorCounts = 
        searchResults
        |> Array.collect (fun elem ->
            let mutable colorsProp = Unchecked.defaultof<JsonElement>
            if elem.TryGetProperty("colors", &colorsProp) then
                let colorsStr = colorsProp.GetString()
                if System.String.IsNullOrWhiteSpace(colorsStr) then
                    Array.empty
                else
                    colorsStr.Split(',') 
                    |> Array.map (fun s -> s.Trim())
                    |> Array.filter (fun s -> not (System.String.IsNullOrEmpty(s)))
            else
                Array.empty)
        |> Array.countBy id
        |> Array.sortByDescending snd
    
    let colorDistribution = 
        colorCounts
        |> Array.map (fun (color, count) -> sprintf "%s: %d cards" color count)
        |> String.concat ", "
    
    // Build sample card list for LLM (top 10 cards from each color)
    let cardsByColor = 
        searchResults
        |> Array.groupBy (fun elem ->
            let mutable colorsProp = Unchecked.defaultof<JsonElement>
            if elem.TryGetProperty("colors", &colorsProp) then
                colorsProp.GetString()
            else
                "Unknown")
        |> Array.filter (fun (colors, _) -> not (System.String.IsNullOrWhiteSpace colors))
    
    let cardSampleText = 
        cardsByColor
        |> Array.collect (fun (colors, cards) ->
            let colorLabel = colors.Split(',') |> Array.head
            let samples = 
                cards 
                |> Array.truncate 10
                |> Array.choose (fun elem ->
                    let mutable nameProp = Unchecked.defaultof<JsonElement>
                    let mutable textProp = Unchecked.defaultof<JsonElement>
                    if elem.TryGetProperty("name", &nameProp) && elem.TryGetProperty("text", &textProp) then
                        Some (sprintf "  - %s: %s" (nameProp.GetString()) (textProp.GetString().Substring(0, Math.Min(60, textProp.GetString().Length))))
                    else
                        None)
            Array.append [|sprintf "\n%s cards:" colorLabel|] samples)
        |> String.concat "\n"
    
    let prompt = 
        sprintf 
            """You are an expert Disney Lorcana deck builder. Choose the BEST 2 INK COLORS for a deck based on the following request:

USER REQUEST: %s

AVAILABLE CARDS BY COLOR:
Color distribution: %s

Sample cards from search results:
%s

RULES:
- Lorcana decks MUST contain exactly 1-2 ink colors (never more)
- Choose colors that have the most synergistic cards for this theme
- Consider:
  • Card availability (how many cards in each color)
  • Thematic fit (which colors match the request best)
  • Gameplay synergy (do the colors work well together)
  • Color identities:
    - Amber: support effects, healing, defensive support
    - Amethyst: Evasive, card draw, bounce/control
    - Emerald: Ward, aggressive challenges, go-wide
    - Ruby: Aggressive (Rush/Reckless), direct damage
    - Sapphire: tools/economy, cost reduction, defensive
    - Steel: Removal/banishment, Resist, challenges

RESPOND WITH EXACTLY 2 COLORS in this JSON format:
{"color1": "ColorName", "color2": "ColorName", "reasoning": "brief explanation"}

Choose colors that will create the strongest, most synergistic deck for this request."""
            userRequest 
            colorDistribution 
            cardSampleText
    
    let genReq = OllamaSharp.Models.GenerateRequest()
    genReq.Model <- "qwen2.5:14b-instruct"
    genReq.Prompt <- prompt
    
    logger.LogDebug("Sending color selection prompt to LLM (length: {Length})", prompt.Length)
    
    let! llmResponse = task {
        let stream = ollama.GenerateAsync(genReq)
        let sb = StringBuilder()
        let e = stream.GetAsyncEnumerator()
        let rec loop () = task {
            let! moved = e.MoveNextAsync().AsTask()
            if moved then
                let chunk = e.Current
                if not (isNull chunk) && not (String.IsNullOrWhiteSpace chunk.Response) then
                    sb.Append(chunk.Response) |> ignore
                return! loop()
            else
                return sb.ToString()
        }
        return! loop()
    }

// (synergy selection function defined below after color selection to avoid offside issues)
    
    logger.LogDebug("LLM color selection response received (length: {Length})", llmResponse.Length)
    
    // Helper: normalize LLM color names to canonical Lorcana ink colors
    let canonicalColors = [ "Amber"; "Amethyst"; "Emerald"; "Ruby"; "Sapphire"; "Steel" ]
    let normalizeColor (s:string) : string option =
        if String.IsNullOrWhiteSpace s then None else
        let t = s.Trim().ToLowerInvariant()
        canonicalColors
        |> List.tryFind (fun c -> c.ToLowerInvariant() = t)
    
    // Parse JSON response
    try
        let trimmed = llmResponse.Trim()
        let firstBrace = trimmed.IndexOf('{')
        if firstBrace = -1 then
            logger.LogWarning("LLM didn't return JSON, falling back to auto-detection")
            return colorCounts |> Array.truncate 2 |> Array.map fst |> Array.toList
        else
            let jsonStart = trimmed.Substring(firstBrace)
            let lastBrace = jsonStart.LastIndexOf('}')
            let jsonStr = if lastBrace > 0 then jsonStart.Substring(0, lastBrace + 1) else jsonStart
            
            let doc = JsonDocument.Parse(jsonStr)
            let root = doc.RootElement
            
            let mutable color1Prop = Unchecked.defaultof<JsonElement>
            let mutable color2Prop = Unchecked.defaultof<JsonElement>
            let mutable reasoningProp = Unchecked.defaultof<JsonElement>
            
            if root.TryGetProperty("color1", &color1Prop) && root.TryGetProperty("color2", &color2Prop) then
                let c1 = color1Prop.GetString()
                let c2 = color2Prop.GetString()
                let reasoning = 
                    if root.TryGetProperty("reasoning", &reasoningProp) then
                        reasoningProp.GetString()
                    else
                        "No reasoning provided"
                
                let chosen = [c1; c2] |> List.choose normalizeColor |> List.distinct
                match chosen with
                | [a; b] ->
                    logger.LogInformation("LLM selected colors (normalized): {Color1} + {Color2}", a, b)
                    logger.LogInformation("LLM reasoning: {Reasoning}", reasoning)
                    return [a; b]
                | _ ->
                    logger.LogWarning("LLM color selection invalid or not canonical. Falling back to data-driven detection.")
                    return colorCounts |> Array.truncate 2 |> Array.map fst |> Array.toList
            else
                logger.LogWarning("LLM response missing color1/color2 fields, falling back to auto-detection")
                return colorCounts |> Array.truncate 2 |> Array.map fst |> Array.toList
    with ex ->
        logger.LogError("Failed to parse LLM color selection: {Error}, falling back to auto-detection", ex.Message)
        return colorCounts |> Array.truncate 2 |> Array.map fst |> Array.toList
}

// ===== LLM-BASED SYNERGY SELECTION =====

let recommendSynergy
    (ollama: IOllamaApiClient)
    (userRequest: string)
    (filteredCards: JsonElement array)
    (style: string option)
    (preferredSubtypes: string list)
    (logger: Microsoft.Extensions.Logging.ILogger)
    : Task<Set<string>> = task {
    try
        // Prepare compact candidate list with name, cost, subtypes, text
        let candidates =
            filteredCards
            |> Array.truncate 120
            |> Array.choose (fun e ->
                let mutable n = Unchecked.defaultof<JsonElement>
                if e.TryGetProperty("name", &n) then
                    let name = n.GetString()
                    let mutable costProp = Unchecked.defaultof<JsonElement>
                    let cost = if e.TryGetProperty("cost", &costProp) && costProp.ValueKind = JsonValueKind.Number then costProp.GetInt32() else -1
                    let mutable subsProp = Unchecked.defaultof<JsonElement>
                    let subs =
                        if e.TryGetProperty("subtypes", &subsProp) && subsProp.ValueKind = JsonValueKind.Array then
                            subsProp.EnumerateArray() |> Seq.choose (fun it -> if it.ValueKind = JsonValueKind.String then Some(it.GetString()) else None) |> String.concat "/"
                        else ""
                    let mutable textProp = Unchecked.defaultof<JsonElement>
                    let txt = if e.TryGetProperty("text", &textProp) then textProp.GetString() else ""
                    Some (sprintf "%s | cost:%d | subtypes:%s | %s" name cost subs (if String.IsNullOrWhiteSpace txt then "" else txt.Substring(0, Math.Min(80, txt.Length))))
                else None)
        let sample = String.Join("\n", candidates)
        let styleLine =
            match style with
            | Some s when not (String.IsNullOrWhiteSpace s) -> sprintf "Deck style: %s" s
            | _ -> "Deck style: Auto"
        let subtypeLine =
            if preferredSubtypes.IsEmpty then "Preferred subtypes: (none)" else sprintf "Preferred subtypes: %s" (String.Join(", ", preferredSubtypes))
        let prompt = $"""
    You are optimizing a Disney Lorcana deck for: {userRequest}
    {styleLine}
    {subtypeLine}

    From the following cards (name | cost | subtypes | brief text), pick up to 15 that BEST synergize with the theme and style.
    Guidance:
    - Favor cards that align with the preferred subtypes identified from the request and reinforce the declared style.
    - Respect Lorcana constraints (2 colors, 4-copy max per unique card).
    - Prefer cards that enable cohesive interactions, tempo advantages, efficient resource use, and consistent win paths without relying on any specific named examples.

    Return ONLY a JSON array of objects with a single field "name" (no counts), like:
    [{{"name":"Card A"}}, {{"name":"Card B"}}]

    Candidates:
    {sample}
    """
        let req = OllamaSharp.Models.GenerateRequest()
        req.Model <- "qwen2.5:14b-instruct"
        req.Prompt <- prompt
        let stream = ollama.GenerateAsync(req)
        let sb = StringBuilder()
        let e = stream.GetAsyncEnumerator()
        let rec loop () = task {
            let! moved = e.MoveNextAsync().AsTask()
            if moved then
                let chunk = e.Current
                if not (isNull chunk) && not (String.IsNullOrWhiteSpace chunk.Response) then
                    sb.Append(chunk.Response) |> ignore
                return! loop()
            else
                return sb.ToString()
        }
        let! text = loop()
        let text = text.Trim()
        let synergy =
            try
                let doc = JsonDocument.Parse(text)
                if doc.RootElement.ValueKind = JsonValueKind.Array then
                    doc.RootElement.EnumerateArray()
                    |> Seq.choose (fun el ->
                        let mutable p = Unchecked.defaultof<JsonElement>
                        if el.TryGetProperty("name", &p) then Some (p.GetString()) else None)
                    |> Set.ofSeq
                else Set.empty
            with _ -> Set.empty
        logger.LogInformation("Synergy selection returned {Count} names", synergy.Count)
        return synergy
    with ex ->
        logger.LogWarning("Synergy selection failed: {Error}", ex.Message)
        return Set.empty
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
        genReq.Model <- "qwen2.5:14b-instruct"
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
                        | Some f -> Some { f with Format = Some state.Format }
                        | None -> Some { Colors = None; CostMin = None; CostMax = None; Inkable = None; Format = Some state.Format }
                    
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
                    cost = None
                    subtypes = [||]
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

// ===== DETERMINISTIC PIPELINE =====
// Replaces unreliable agentic loop with predictable 3-phase workflow

// Deck style detection and scoring helpers
let private detectDeckStyle (request: string) : string * (string * int) list =
    let r = request.ToLowerInvariant()
    let hasAny (xs:string list) = xs |> List.exists (fun k -> r.Contains(k))
    // Style → weighted ability keywords (lowercase keyword, weight)
    if hasAny ["aggro"; "rush"; "fast"; "swarm"; "go-wide"; "beatdown"; "tempo"] then
        "Aggro",
        [ "rush",4; "reckless",3; "challenger",3; "evasive",2; "support",2 ]
    elif hasAny ["control"; "stall"; "removal"; "lock"; "counter"; "defensive" ] then
        "Control",
        [ "banish",4; "ward",3; "resist",3; "draw",3; "bounce",3; "bodyguard",2 ]
    elif hasAny ["combo"; "engine"; "loop"; "synergy"; "tutor" ] then
        "Combo",
        [ "draw",3; "shift",2; "reduce cost",3; "ready",2 ]
    elif hasAny ["lore"; "evasive"; "race"; "race-to-lore" ] then
        "Lore",
        [ "evasive",4; "lore",3; "ward",2; "support",2 ]
    elif hasAny ["midrange"; "value"; "curved" ] then
        "Midrange",
        [ "resist",3; "support",3; "bodyguard",2; "challenger",2 ]
    else
        "Tempo",
        [ "rush",3; "support",2; "draw",2 ]

let buildDeckDeterministic
    (ollama: IOllamaApiClient)
    (qdrant: QdrantClient)
    (embeddingGen: Func<string, Task<float32 array>>)
    (query: DeckBuilder.Shared.DeckQuery)
    (logger: Microsoft.Extensions.Logging.ILogger)
    : Task<Result<DeckBuilder.Shared.DeckResponse, string>> = task {
    
    logger.LogInformation("Starting deterministic deck building for request: {Request}", query.request)
    let startTime = System.Diagnostics.Stopwatch.StartNew()
    
    let targetSize = query.deckSize
    let format = query.format
    logger.LogInformation("Building deck with format: {Format}, target size: {TargetSize}", format, targetSize)
    
    // ===== PHASE 1: SEARCH & DISCOVERY =====
    logger.LogInformation("Phase 1: Search & Discovery")
    
    // Detect deck style from request to guide synergy
    let style, abilityWeights = detectDeckStyle query.request
    logger.LogInformation("Detected deck style: {Style}", style)

    // Extract key search terms from request
    let searchTerms = 
        let tokens =
            query.request.Split([|' '; ','; ';'|], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun t -> t.Length > 3)
            |> Array.distinct
        Array.append [| query.request |] tokens
        |> Array.distinct
        |> Array.truncate 8
    
    logger.LogDebug("Search terms: {Terms}", System.String.Join(", ", searchTerms))
    
    // Execute multiple searches in parallel (respect requested format)
    let! searchResults = task {
        let formatFilters = Some { Colors = None; CostMin = None; CostMax = None; Inkable = None; Format = Some format }
        let searchTasks =
            searchTerms
            |> Array.map (fun term -> task {
                try
                    let! json = searchCardsInQdrant qdrant embeddingGen term formatFilters 100 logger
                    let doc = JsonDocument.Parse(json)
                    return doc.RootElement.EnumerateArray() |> Seq.toArray
                with ex ->
                    logger.LogWarning("Search for '{Term}' failed: {Error}", term, ex.Message)
                    return Array.empty
            })
        
        let! results = Task.WhenAll(searchTasks)
        return results |> Array.concat
    }
    
    // Deduplicate by fullName (name field in the JSON)
    let uniqueCards = 
        searchResults
        |> Array.distinctBy (fun elem -> 
            let mutable prop = Unchecked.defaultof<JsonElement>
            if elem.TryGetProperty("name", &prop) then
                prop.GetString()
            else
                System.Guid.NewGuid().ToString())
    
    // Subtype-driven second-wave searches (derive from initial results)
    let reqLower = query.request.ToLowerInvariant()
    let subtypeCounts =
        uniqueCards
        |> Array.collect (fun elem ->
            let mutable subsProp = Unchecked.defaultof<JsonElement>
            if elem.TryGetProperty("subtypes", &subsProp) && subsProp.ValueKind = JsonValueKind.Array then
                subsProp.EnumerateArray()
                |> Seq.choose (fun it -> if it.ValueKind = JsonValueKind.String then Some(it.GetString()) else None)
                |> Seq.toArray
            else [||])
        |> Array.choose (fun s -> if System.String.IsNullOrWhiteSpace s then None else Some s)
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.countBy id
        |> Array.sortByDescending snd

    let requestedSubtypes = subtypeCounts |> Array.filter (fun (s,_) -> reqLower.Contains(s.ToLowerInvariant())) |> Array.map fst |> Array.truncate 4
    let fallbackSubtypes = subtypeCounts |> Array.map fst |> Array.truncate 3
    let preferredSubtypes = Array.append requestedSubtypes fallbackSubtypes |> Array.distinct |> Array.truncate 5 |> Array.toList

    let! secondWave = task {
        if preferredSubtypes.IsEmpty then return [||] else
        let formatFilters = Some { Colors = None; CostMin = None; CostMax = None; Inkable = None; Format = Some format }
        let tasks =
            preferredSubtypes
            |> List.toArray
            |> Array.map (fun st -> task {
                try
                    let! json = searchCardsInQdrant qdrant embeddingGen st formatFilters 80 logger
                    let doc = JsonDocument.Parse(json)
                    return doc.RootElement.EnumerateArray() |> Seq.toArray
                with ex ->
                    logger.LogWarning("Subtype search for '{Subtype}' failed: {Error}", st, ex.Message)
                    return Array.empty
            })
        let! res = Task.WhenAll(tasks)
        return res |> Array.concat
    }

    let expandedCards =
        Array.append uniqueCards secondWave
        |> Array.distinctBy (fun elem ->
            let mutable prop = Unchecked.defaultof<JsonElement>
            if elem.TryGetProperty("name", &prop) then prop.GetString() else System.Guid.NewGuid().ToString())

    logger.LogInformation("Phase 1 complete: Found {Initial} unique cards (terms={Terms}); after subtype wave (+{Added}) total {Total}", uniqueCards.Length, searchTerms.Length, Math.Max(0, expandedCards.Length - uniqueCards.Length), expandedCards.Length)
    
    if expandedCards.Length = 0 then
        logger.LogWarning("Phase 1 failed: Zero cards returned for search terms: {Terms}, format: {Format}", System.String.Join(", ", searchTerms), format)
        return Error "No cards found matching the search criteria"
    else
    
    // ===== PHASE 2: FILTERING & VALIDATION =====
    logger.LogInformation("Phase 2: Color & Format Filtering")
    
    // Determine colors to use
    let! targetColors = task {
        match query.selectedColors with
        | Some colors when colors.Length > 0 ->
            logger.LogInformation("Using user-selected colors: {Colors}", System.String.Join(", ", colors))
            return colors |> Array.toList
        | _ ->
            // Ask LLM to select optimal 2 colors based on search results
            logger.LogInformation("No colors specified, asking LLM to select optimal colors")
            let! selectedColors = selectOptimalColors ollama query.request expandedCards logger
            return selectedColors
    }
    
    // Filter cards by color (format already filtered by searchCardsInQdrant)
    let filteredCards = 
        if targetColors.IsEmpty then
            uniqueCards // No color filtering if auto-detect found nothing
        else
            expandedCards
            |> Array.filter (fun elem ->
                let mutable colorsProp = Unchecked.defaultof<JsonElement>
                if elem.TryGetProperty("colors", &colorsProp) then
                    let colorsStr = colorsProp.GetString()
                    let cardColors = 
                        if System.String.IsNullOrWhiteSpace(colorsStr) then []
                        else colorsStr.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList
                    
                    // CRITICAL FIX: Card must have ALL its colors in targetColors
                    // This prevents a 3-color deck (e.g., Steel/Emerald/Amber)
                    not cardColors.IsEmpty && cardColors |> List.forall (fun c -> targetColors |> List.contains c)
                else
                    false)
    
    logger.LogInformation("Phase 2 complete: {Count} cards after filtering (format={Format}, colors={Colors})", 
        filteredCards.Length, format, System.String.Join("/", targetColors))
    
    if filteredCards.Length = 0 then
        return Error (sprintf "No cards found matching colors %s and format %A" (System.String.Join("/", targetColors)) format)
    else
    
    // ===== PHASE 3: DECK ASSEMBLY =====
    logger.LogInformation("Phase 3: Deck Assembly (target size: {Size})", targetSize)
    
    // Bias towards cards matching the user's request terms, then inkable desc, cost asc
    let reqLower = query.request.ToLowerInvariant()
    let termsLower = searchTerms |> Array.map (fun t -> t.ToLowerInvariant())
    let subtypePrefLower = preferredSubtypes |> List.map (fun s -> s.ToLowerInvariant()) |> Set.ofList
    // Do not bias toward any specific named examples or fixed types
    let abilityWeightsMap = abilityWeights |> dict
    let scoreFor (elem: JsonElement) =
        let mutable nameProp = Unchecked.defaultof<JsonElement>
        let mutable textProp = Unchecked.defaultof<JsonElement>
        let nameLower = if elem.TryGetProperty("name", &nameProp) then (nameProp.GetString()).ToLowerInvariant() else ""
        let textLower = if elem.TryGetProperty("text", &textProp) then (textProp.GetString()).ToLowerInvariant() else ""
        let phraseHit = if nameLower.Contains(reqLower) then 10 else 0
        let nameHits = termsLower |> Array.sumBy (fun t -> if t.Length > 0 && nameLower.Contains(t) then 1 else 0)
        let textHits = termsLower |> Array.sumBy (fun t -> if t.Length > 0 && textLower.Contains(t) then 1 else 0)
        let subtypeBoost =
            let mutable subsProp = Unchecked.defaultof<JsonElement>
            if elem.TryGetProperty("subtypes", &subsProp) && subsProp.ValueKind = JsonValueKind.Array then
                subsProp.EnumerateArray()
                |> Seq.choose (fun it -> if it.ValueKind = JsonValueKind.String then Some(it.GetString().ToLowerInvariant()) else None)
                |> Seq.filter (fun s -> subtypePrefLower.Contains(s))
                |> Seq.length
            else 0
        let abilityBoost =
            abilityWeights
            |> List.sumBy (fun (kw, w) -> if textLower.Contains(kw) || nameLower.Contains(kw) then w else 0)
        phraseHit + (nameHits * 3) + textHits + (subtypeBoost * 5) + abilityBoost
    
    // LLM synergy pass (within budget) to bias card choices
    let! synergyPreferredLLM = recommendSynergy ollama query.request filteredCards (Some style) preferredSubtypes logger
    // Fallback synergy: if LLM returned none, use top-scoring names as soft-synergy
    let fallbackSynergy =
        if synergyPreferredLLM.Count > 0 then synergyPreferredLLM else
        filteredCards
        |> Array.sortByDescending scoreFor
        |> Array.truncate 12
        |> Array.choose (fun e -> let mutable n = Unchecked.defaultof<JsonElement> in if e.TryGetProperty("name", &n) then Some(n.GetString()) else None)
        |> Set.ofArray
    let synergyPreferred = fallbackSynergy
    
    let sortedCards =
        filteredCards
        |> Array.sortByDescending (fun elem ->
            let mutable inkProp = Unchecked.defaultof<JsonElement>
            let isInkable = if elem.TryGetProperty("ink", &inkProp) then (inkProp.GetString() = "Y") else false
            let mutable costProp = Unchecked.defaultof<JsonElement>
            let costKey =
                if elem.TryGetProperty("cost", &costProp) then
                    if costProp.ValueKind = JsonValueKind.Number then -costProp.GetDouble() else 0.0
                else 0.0
            let pref = scoreFor elem
            let mutable nameProp = Unchecked.defaultof<JsonElement>
            let nameLower = if elem.TryGetProperty("name", &nameProp) then (nameProp.GetString()) else ""
            let synergyBoost = if synergyPreferred.Contains(nameLower) then 50 else 0
            let inkKey = if isInkable then 1 else 0
            (pref + synergyBoost, inkKey, costKey))
    
    // Style-based desired mana curve (bucketed: 1,2,3,4,5+)
    let desiredCurve =
        let mk a b c d e = dict [ (1,a); (2,b); (3,c); (4,d); (5,e) ]
        match style with
        | "Aggro" -> mk 12 16 14 10 8
        | "Tempo" -> mk 10 14 14 12 10
        | "Midrange" -> mk 8 12 14 14 12
        | "Control" -> mk 6 10 12 14 18
        | "Combo" -> mk 8 12 14 12 14
        | "Lore" -> mk 10 14 14 12 10
        | _ -> mk 10 14 14 12 10
    let bucketOf (costOpt:int option) =
        match costOpt with
        | Some c when c <= 1 -> 1
        | Some c when c = 2 -> 2
        | Some c when c = 3 -> 3
        | Some c when c = 4 -> 4
        | _ -> 5
    let curveCounts = System.Collections.Generic.Dictionary<int,int>()
    curveCounts.[1] <- 0; curveCounts.[2] <- 0; curveCounts.[3] <- 0; curveCounts.[4] <- 0; curveCounts.[5] <- 0

    // Build deck respecting max copies, curve, and inkable ratio
    let mutable deck = []
    let mutable totalCards = 0
    let mutable cardCounts = Map.empty<string, int>
    let mutable inkableAdded = 0
    
    for cardElem in sortedCards do
        if totalCards < targetSize then
            let mutable nameProp = Unchecked.defaultof<JsonElement>
            let fullName = 
                if cardElem.TryGetProperty("name", &nameProp) then
                    nameProp.GetString()
                else
                    ""
            
            let mutable maxProp = Unchecked.defaultof<JsonElement>
            let maxCopies = 
                if cardElem.TryGetProperty("max", &maxProp) then
                    maxProp.GetInt32()
                else
                    4
            
            let currentCount = cardCounts |> Map.tryFind fullName |> Option.defaultValue 0
            
            // Card attributes
            let mutable costProp2 = Unchecked.defaultof<JsonElement>
            let costOpt = if cardElem.TryGetProperty("cost", &costProp2) && costProp2.ValueKind = JsonValueKind.Number then Some (costProp2.GetInt32()) else None
            let bucket = bucketOf costOpt
            let mutable inkProp = Unchecked.defaultof<JsonElement>
            let isInkable = if cardElem.TryGetProperty("ink", &inkProp) then inkProp.GetString() = "Y" else false

            // Determine caps and desired copies based on style, synergy, cost, curve deficits, and ink ratio
            let isSynergy = synergyPreferred.Contains(fullName)
            let baseCap =
                if isSynergy then Math.Min(maxCopies, 4)
                else
                    match costOpt with
                    | Some c when c <= 2 -> Math.Min(maxCopies, 3)
                    | Some c when c >= 5 -> Math.Min(maxCopies, 1)
                    | _ -> Math.Min(maxCopies, 2)
            let curveDeficit = Math.Max(0, desiredCurve.[bucket] - curveCounts.[bucket])
            let remaining = targetSize - totalCards
            let mutable desiredAdd = Math.Min(baseCap - currentCount, Math.Min(remaining, if curveDeficit > 0 then curveDeficit else if isSynergy then 1 else 0))
            // Inkable ratio steering
            let currentInkablePct = if totalCards = 0 then 1.0 else (float inkableAdded / float totalCards)
            if currentInkablePct < 0.7 && (not isInkable) && (not isSynergy) then
                // Avoid adding too many non-inkable early
                desiredAdd <- Math.Min(desiredAdd, 1)
            elif currentInkablePct > 0.85 && isInkable && (not isSynergy) then
                // Avoid overshooting inkable ratio
                desiredAdd <- Math.Min(desiredAdd, 1)

            if currentCount < baseCap && desiredAdd > 0 then
                
                let inkable = isInkable
                
                let mutable colorsProp = Unchecked.defaultof<JsonElement>
                let inkColor = 
                    if cardElem.TryGetProperty("colors", &colorsProp) then
                        let colorsStr = colorsProp.GetString()
                        if System.String.IsNullOrWhiteSpace(colorsStr) then 
                            "" 
                        else 
                            colorsStr.Split(',').[0].Trim()
                    else
                        ""
                
                let mutable urlProp = Unchecked.defaultof<JsonElement>
                let cardUrl = 
                    if cardElem.TryGetProperty("cardMarketUrl", &urlProp) then
                        urlProp.GetString()
                    else ""
                
                let mutable subsProp2 = Unchecked.defaultof<JsonElement>
                let subtypesArr = 
                    if cardElem.TryGetProperty("subtypes", &subsProp2) && subsProp2.ValueKind = JsonValueKind.Array then
                        subsProp2.EnumerateArray() |> Seq.choose (fun it -> if it.ValueKind = JsonValueKind.String then Some(it.GetString()) else None) |> Seq.toArray
                    else Array.empty

                let entry : DeckBuilder.Shared.CardEntry = {
                    count = desiredAdd
                    fullName = fullName
                    inkable = inkable
                    cardMarketUrl = (if System.String.IsNullOrWhiteSpace(cardUrl) then "" else cardUrl)
                    inkColor = inkColor
                    cost = costOpt
                    subtypes = subtypesArr
                }
                
                deck <- entry :: deck
                totalCards <- totalCards + desiredAdd
                if inkable then inkableAdded <- inkableAdded + desiredAdd
                curveCounts.[bucket] <- curveCounts.[bucket] + desiredAdd
                cardCounts <- cardCounts |> Map.add fullName (currentCount + desiredAdd)
    
    let finalDeck = deck |> List.rev |> List.toArray
    
    logger.LogInformation("Phase 3 complete: Built deck with {Count}/{Target} cards", totalCards, targetSize)
    
    if totalCards < targetSize then
        logger.LogWarning("Deck incomplete: only {Count}/{Target} cards available", totalCards, targetSize)
        return Error (sprintf "Insufficient cards: only %d/%d cards available matching criteria" totalCards targetSize)
    else
    
    // Calculate statistics
    let inkableCount = finalDeck |> Array.filter (fun e -> e.inkable) |> Array.sumBy (fun e -> e.count)
    let inkablePercent = (float inkableCount / float totalCards) * 100.0
    
    startTime.Stop()
    
    let explanation = 
        sprintf "Built %d-card %s deck in %dms using deterministic 3-phase pipeline:\n" 
            totalCards 
            (System.String.Join("/", targetColors))
            startTime.ElapsedMilliseconds +
        sprintf "Phase 1: Found %d unique cards from %d searches; expanded %d additional thematic terms\n" expandedCards.Length searchTerms.Length preferredSubtypes.Length +
        sprintf "Phase 2: Filtered to %d legal %A cards\n" filteredCards.Length format +
        sprintf "Phase 3: Assembled deck with %d inkable cards (%.1f%%) | Style: %s\n" inkableCount inkablePercent style +
        (let b1 = finalDeck |> Array.filter (fun e -> defaultArg e.cost 0 <= 1) |> Array.sumBy (fun e -> e.count)
         let b2 = finalDeck |> Array.filter (fun e -> defaultArg e.cost 0 = 2) |> Array.sumBy (fun e -> e.count)
         let b3 = finalDeck |> Array.filter (fun e -> defaultArg e.cost 0 = 3) |> Array.sumBy (fun e -> e.count)
         let b4 = finalDeck |> Array.filter (fun e -> defaultArg e.cost 0 = 4) |> Array.sumBy (fun e -> e.count)
         let b5 = finalDeck |> Array.filter (fun e -> defaultArg e.cost 5 >= 5) |> Array.sumBy (fun e -> e.count)
         sprintf "Curve: 1:%d, 2:%d, 3:%d, 4:%d, 5+:%d\n" b1 b2 b3 b4 b5) +
        "Tip: You can include specific characters or themes in your request to further bias selection."
    
    logger.LogInformation("Deterministic deck building completed successfully in {Ms}ms", startTime.ElapsedMilliseconds)
    
    return Ok ({
        cards = finalDeck
        explanation = explanation
    } : DeckBuilder.Shared.DeckResponse)
}
