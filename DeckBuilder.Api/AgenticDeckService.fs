module DeckBuilder.Api.AgenticDeckService

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
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
}

type AgentState = {
    CurrentDeck: Map<string, int>
    SearchHistory: string list
    Iteration: int
    TargetSize: int
    AllowedColors: string list
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
    
    // Current state
    let currentCount = state.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
    sb.AppendLine("CURRENT STATE:") |> ignore
    sb.AppendLine(sprintf "- Cards in deck: %d/%d" currentCount state.TargetSize) |> ignore
    sb.AppendLine(sprintf "- Iteration: %d" state.Iteration) |> ignore
    
    if state.AllowedColors.Length > 0 then
        let colorsStr = String.Join(", ", state.AllowedColors)
        sb.AppendLine(sprintf "- Required colors: %s" colorsStr) |> ignore
    
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
        sb.AppendLine(results) |> ignore
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
        sb.AppendLine("- START: Search for core cards that define the deck strategy") |> ignore
        sb.AppendLine("- Focus on key synergies and win conditions") |> ignore
    elif currentCount < state.TargetSize then
        let remaining = state.TargetSize - currentCount
        sb.AppendLine(sprintf "- CONTINUE: Need AT LEAST %d more cards (current: %d, target: %d)" remaining currentCount state.TargetSize) |> ignore
        sb.AppendLine("- DO NOT use 'finalize' action yet - deck is incomplete!") |> ignore
        sb.AppendLine("- Fill gaps in mana curve (aim for smooth 1-6 cost distribution)") |> ignore
        sb.AppendLine("- Ensure ~70-80%% of cards are inkable for resource consistency") |> ignore
        sb.AppendLine("- Add support cards that synergize with existing choices") |> ignore
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

let searchCardsInQdrant (qdrant: QdrantClient) (embeddingGen: Func<string, Task<float32 array>>) (query: string) (filters: SearchFilters option) (limit: int) = task {
    // Generate embedding for query
    let! vector = embeddingGen.Invoke(query)
    
    // Build Qdrant filter (basic implementation)
    // Note: Advanced filtering (cost range, etc.) can be added via Qdrant filter syntax
    let filter = Qdrant.Client.Grpc.Filter()
    
    // Search with semantic similarity
    let! results = qdrant.SearchAsync("lorcana_cards", vector, limit = uint64 limit, filter = filter)
    
    // Format results as CSV for LLM
    let sb = StringBuilder()
    sb.AppendLine("fullName,cost,inkable,colors,maxCopies") |> ignore
    
    for point in results do
        let fullName = Payload.fullName point.Payload
        let cost = Payload.cost point.Payload |> Option.map string |> Option.defaultValue "?"
        let inkable = Payload.inkable point.Payload |> Option.map (fun b -> if b then "true" else "false") |> Option.defaultValue "?"
        let colors = Payload.colors point.Payload |> String.concat "|"
        let maxCopies = Payload.maxCopiesInDeck point.Payload |> Option.map string |> Option.defaultValue "4"
        
        sb.AppendLine(sprintf "\"%s\",%s,%s,\"%s\",%s" fullName cost inkable colors maxCopies) |> ignore
    
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
    (maxIterations: int) = task {
    
    if state.Complete || state.Iteration >= maxIterations then
        return Ok state
    else
        // Build prompt
        let prompt = buildAgentPrompt state userRequest rulesExcerpt searchResults
        
        // Get LLM decision
        let genReq = OllamaSharp.Models.GenerateRequest()
        genReq.Model <- "qwen2.5:7b"
        genReq.Prompt <- prompt
        
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
        
        // Parse response
        match parseAgentResponse llmResponse with
        | Error err ->
            return Error (sprintf "Agent response parse error at iteration %d: %s" state.Iteration err)
        
        | Ok response ->
            let newState = { state with 
                                Iteration = state.Iteration + 1
                                Reasoning = response.Reasoning :: state.Reasoning }
            
            match response.Action.ToLowerInvariant() with
            | "search_cards" ->
                match response.Query with
                | Some query ->
                    // Execute search
                    let! results = searchCardsInQdrant qdrant embeddingGen query response.Filters 20
                    // Continue loop with results
                    return! agentLoop ollama qdrant embeddingGen userRequest rulesExcerpt newState (Some results) maxIterations
                | None ->
                    return Error "search_cards action requires query"
            
            | "add_cards" ->
                match response.Cards with
                | Some cards ->
                    // Add cards to deck
                    let updatedDeck = 
                        cards 
                        |> List.fold (fun (deck: Map<string, int>) (name, count) ->
                            let current = deck |> Map.tryFind name |> Option.defaultValue 0
                            deck |> Map.add name (current + count)
                        ) newState.CurrentDeck
                    
                    let newState2 = { newState with CurrentDeck = updatedDeck }
                    
                    // Continue loop
                    return! agentLoop ollama qdrant embeddingGen userRequest rulesExcerpt newState2 None maxIterations
                | None ->
                    return Error "add_cards action requires cards"
            
            | "finalize" ->
                // Strict validation: deck MUST be at least target size
                let totalCards = newState.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
                if totalCards >= state.TargetSize then
                    return Ok { newState with Complete = true }
                else
                    let shortage = state.TargetSize - totalCards
                    // Reject finalize, force agent to add more cards
                    return Error (sprintf "Cannot finalize: deck has only %d cards, need at least %d (short by %d cards). Continue searching and adding cards." totalCards state.TargetSize shortage)
            
            | _ ->
                return Error (sprintf "Unknown action: %s" response.Action)
}

// ===== PUBLIC API =====

let buildDeckAgentic 
    (ollama: IOllamaApiClient)
    (qdrant: QdrantClient)
    (embeddingGen: Func<string, Task<float32 array>>)
    (query: DeckBuilder.Shared.DeckQuery) = task {
    
    let allowedColors = 
        query.selectedColors 
        |> Option.defaultValue [||]
        |> Array.toList
    
    // Fetch relevant rules excerpts using RAG
    let! rulesExcerpt = getRulesForPrompt qdrant embeddingGen query.request
    
    let initialState = {
        CurrentDeck = Map.empty
        SearchHistory = []
        Iteration = 0
        TargetSize = query.deckSize
        AllowedColors = allowedColors
        Complete = false
        Reasoning = []
    }
    
    let! result = agentLoop ollama qdrant embeddingGen query.request rulesExcerpt initialState None 10
    
    match result with
    | Ok finalState ->
        // CRITICAL VALIDATION: Ensure deck meets minimum size requirement
        let totalCards = finalState.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
        
        if totalCards < query.deckSize then
            // Deck is too small - this should never happen due to finalize validation
            return Error (sprintf "Deck building failed: only %d cards built, required at least %d. Agent completed prematurely." totalCards query.deckSize)
        else
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
            
            return Ok ({
                cards = cards
                explanation = explanation
            } : DeckBuilder.Shared.DeckResponse)
    
    | Error err ->
        return Error err
}
