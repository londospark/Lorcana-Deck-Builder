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
    Iteration: int
    TargetSize: int
    AllowedColors: string list
    Format: DeckBuilder.Shared.DeckFormat option
    Complete: bool
    Reasoning: string list
}

type AgentResponse = {
    Action: string
    Query: string option
    Filters: SearchFilters option
    Cards: (string * int) list option
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
        sb.AppendLine("(CSV format: fullName,type,cost,inkable,colors,strength,willpower,lore,fullText,subtypes,rarity,story,maxCopies)") |> ignore
        sb.AppendLine(results) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("HOW TO USE THIS DATA:") |> ignore
        sb.AppendLine("- COLORS column shows which ink colors these cards belong to") |> ignore
        if state.AllowedColors.Length = 0 then
            sb.AppendLine("- ⚠️ IMPORTANT: Count which colors appear most frequently in results!") |> ignore
            sb.AppendLine("- Choose 1-2 colors that have the most synergistic cards for the theme") |> ignore
        sb.AppendLine("- fullText shows abilities and effects - READ THIS to understand synergies") |> ignore
        sb.AppendLine("- strength/willpower/lore are Character stats (empty for Actions/Items/Songs)") |> ignore
        sb.AppendLine("- subtypes show tribal types (Princess, Hero, Villain, Broom, etc.) for synergy building") |> ignore
        sb.AppendLine("- inkable Y means can be played as ink for resource generation") |> ignore
        sb.AppendLine("- maxCopies is usually 4, but some cards have special limits") |> ignore
        sb.AppendLine() |> ignore
    | None -> 
        ()
    
    // Instructions
    sb.AppendLine("AVAILABLE ACTIONS:") |> ignore
    sb.AppendLine("1. search_cards - Search Qdrant for cards matching criteria") |> ignore
    sb.AppendLine("2. add_cards - Add specific cards from search results to deck") |> ignore
    sb.AppendLine("3. finalize - Complete deck building (only when target size reached)") |> ignore
    sb.AppendLine() |> ignore
    
    sb.AppendLine("RESPOND WITH JSON ONLY (no markdown, no code fences):") |> ignore
    sb.AppendLine("{") |> ignore
    sb.AppendLine("  \"action\": \"search_cards\" | \"add_cards\" | \"finalize\",") |> ignore
    sb.AppendLine("  \"query\": \"search text\" (required for search_cards),") |> ignore
    sb.AppendLine("  \"filters\": { \"colors\": [\"Amber\"], \"costMin\": 1, \"costMax\": 3, \"inkable\": true } (optional),") |> ignore
    sb.AppendLine("  \"cards\": [[\"Card Name\", 4]] (required for add_cards - use EXACT names from search),") |> ignore
    sb.AppendLine("  \"reasoning\": \"brief explanation of decision\"") |> ignore
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
            sb.AppendLine("- Example: 'magic brooms' → search finds Amethyst has most brooms → use Amethyst") |> ignore
            sb.AppendLine("- Do NOT default to Amber+Emerald - let the search results guide color choice!") |> ignore
        else
            sb.AppendLine("- Colors are pre-selected by user, search for synergies in those colors") |> ignore
        sb.AppendLine("- Read fullText carefully to identify powerful abilities and synergies") |> ignore
        sb.AppendLine("- Look for characters with high lore (quest value) or strong effects") |> ignore
    elif currentCount < state.TargetSize then
        let remaining = state.TargetSize - currentCount
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
        let options = JsonSerializerOptions()
        options.PropertyNameCaseInsensitive <- true
        let response = JsonSerializer.Deserialize<AgentResponse>(json, options)
        Ok response
    with ex ->
        Error $"Failed to parse agent response: {ex.Message}"

// ===== SEARCH & RETRIEVAL =====

let searchCardsInQdrant (qdrant: QdrantClient) (embeddingGen: Func<string, Task<float32 array>>) (query: string) (filters: SearchFilters option) (limit: int) (logger: Microsoft.Extensions.Logging.ILogger) = task {
    logger.LogDebug("searchCardsInQdrant called with query: {Query}, limit: {Limit}", query.Substring(0, Math.Min(50, query.Length)), limit)
    // Generate embedding for query
    let! vector = embeddingGen.Invoke(query)
    logger.LogDebug("Embedding generated, vector length: {Length}", vector.Length)
    
    // For now, metadata filtering is done post-search
    // TODO: Implement Qdrant native filtering for colors/cost/inkable
    // The Qdrant Grpc types are complex and need more research
    
    // Search with semantic similarity - get more results to account for filtering
    let searchLimit = 
        match filters with
        | Some f when f.Format.IsSome || f.Colors.IsSome || f.Inkable.IsSome -> limit * 3
        | _ -> limit
    
    logger.LogDebug("Searching Qdrant with limit: {Limit}", searchLimit)
    // No limit - return all matching cards since we only show metadata to LLM
    let! results = qdrant.SearchAsync("lorcana_cards", vector, limit = 1000uL)
    logger.LogDebug("Qdrant search returned {Count} results", Seq.length results)
    
    // Filter results by all specified criteria
    let filteredResults = 
        results
        |> Seq.filter (fun point ->
            let payload = point.Payload
            
            // Check format legality
            let formatOk = 
                match filters with
                | Some f when f.Format.IsSome -> Payload.isAllowedInFormat payload f.Format.Value
                | _ -> true
            
            // Check colors (card must have at least one of the specified colors)
            let colorsOk =
                match filters with
                | Some f when f.Colors.IsSome && not (List.isEmpty f.Colors.Value) ->
                    let cardColors = Payload.colors payload
                    let allowedColors = f.Colors.Value
                    cardColors |> List.exists (fun cc -> allowedColors |> List.contains cc)
                | _ -> true
            
            // Check cost range
            let costOk =
                match filters with
                | Some f ->
                    match Payload.cost payload with
                    | Some cardCost ->
                        let minOk = f.CostMin |> Option.map (fun min -> cardCost >= min) |> Option.defaultValue true
                        let maxOk = f.CostMax |> Option.map (fun max -> cardCost <= max) |> Option.defaultValue true
                        minOk && maxOk
                    | None -> true
                | _ -> true
            
            // Check inkable
            let inkableOk =
                match filters with
                | Some f when f.Inkable.IsSome ->
                    Payload.inkable payload |> Option.map (fun ink -> ink = f.Inkable.Value) |> Option.defaultValue false
                | _ -> true
            
            formatOk && colorsOk && costOk && inkableOk
        )
        |> Seq.truncate limit
    
    logger.LogDebug("After filtering: {Count} results", Seq.length filteredResults)
    
    // Format results as CSV for LLM with full card data
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
    return sb.ToString()
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
        genReq.Model <- "qwen2.5:7b"
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
        logger.LogDebug("Parsing agent response...")
        match parseAgentResponse llmResponse with
        | Error err ->
            logger.LogError("Agent response parse error: {Error}", err)
            return Error (sprintf "Agent response parse error at iteration %d: %s" state.Iteration err)
        
        | Ok response ->
            logger.LogInformation("Agent action: {Action}, reasoning: {Reasoning}", response.Action, response.Reasoning.Substring(0, Math.Min(100, response.Reasoning.Length)))
            let newState = { state with 
                                Iteration = state.Iteration + 1
                                Reasoning = response.Reasoning :: state.Reasoning }
            
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
                    
                    // Execute search - get more cards per search to reduce iterations
                    let! results = searchCardsInQdrant qdrant embeddingGen query mergedFilters 50 logger
                    logger.LogDebug("Search completed, results length: {Length}", results.Length)
                    // Continue loop with results
                    return! agentLoop ollama qdrant embeddingGen userRequest rulesExcerpt newState (Some results) maxIterations logger
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
                        |> List.fold (fun (deck: Map<string, int>) (name, count) ->
                            let current = deck |> Map.tryFind name |> Option.defaultValue 0
                            deck |> Map.add name (current + count)
                        ) newState.CurrentDeck
                    
                    let totalCards = updatedDeck |> Map.fold (fun acc _ count -> acc + count) 0
                    logger.LogInformation("Deck updated to {TotalCards} cards", totalCards)
                    let newState2 = { newState with CurrentDeck = updatedDeck }
                    
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
        Iteration = 0
        TargetSize = query.deckSize
        AllowedColors = allowedColors
        Format = query.format
        Complete = false
        Reasoning = []
    }
    
    logger.LogInformation("Starting agent loop with max 5 iterations...")
    // Reduce max iterations to 5 for faster response (each iteration = LLM call)
    let! result = agentLoop ollama qdrant embeddingGen query.request rulesExcerpt initialState None 5 logger
    
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
