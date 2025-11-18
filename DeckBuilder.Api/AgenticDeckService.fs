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

type AgentAction =
    | SearchCards of query: string * filters: SearchFilters * limit: int
    | GetSynergies of cardName: string * limit: int
    | ValidateDeck
    | AddCards of cards: (string * int) list
    | Finalize

type AgentState = {
    CurrentDeck: Map<string, int>  // cardName -> count
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
    Complete: bool
}

// ===== PROMPTS =====

let buildAgentPrompt (state: AgentState) (searchResults: string option) =
    let sb = StringBuilder()
    
    sb.AppendLine("You are an expert Lorcana deck builder using an agentic approach.") |> ignore
    sb.AppendLine("Goal: Build a complete 60-card deck through iterative searches and card selection.") |> ignore
    sb.AppendLine() |> ignore
    
    // Current state
    let currentCount = state.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
    sb.AppendLine("CURRENT STATE:") |> ignore
    sb.AppendLine(sprintf "- Cards in deck: %d/%d" currentCount state.TargetSize) |> ignore
    sb.AppendLine(sprintf "- Iteration: %d" state.Iteration) |> ignore
    
    if state.AllowedColors.Length > 0 then
        let colorsStr = String.Join(", ", state.AllowedColors)
        sb.AppendLine(sprintf "- Allowed colors: %s" colorsStr) |> ignore
    
    if not state.CurrentDeck.IsEmpty then
        sb.AppendLine() |> ignore
        sb.AppendLine("Current deck:") |> ignore
        state.CurrentDeck 
        |> Map.toSeq
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
    sb.AppendLine("1. search_cards - Search for specific cards by criteria") |> ignore
    sb.AppendLine("2. add_cards - Add selected cards to the deck") |> ignore
    sb.AppendLine("3. finalize - Complete the deck (use when target size reached)") |> ignore
    sb.AppendLine() |> ignore
    
    sb.AppendLine("RESPOND WITH JSON ONLY (no markdown, no code fences):") |> ignore
    sb.AppendLine("""{""") |> ignore
    sb.AppendLine("""  "action": "search_cards" | "add_cards" | "finalize",""") |> ignore
    sb.AppendLine("""  "query": "search text" (if search_cards),""") |> ignore
    sb.AppendLine("""  "filters": { "colors": ["Amber"], "costMin": 1, "costMax": 3, "inkable": true } (optional),""") |> ignore
    sb.AppendLine("""  "cards": [["Card Name", 4], ...] (if add_cards),""") |> ignore
    sb.AppendLine("""  "reasoning": "explanation of decision",""") |> ignore
    sb.AppendLine("""  "complete": false""") |> ignore
    sb.AppendLine("""}""") |> ignore
    sb.AppendLine() |> ignore
    
    if currentCount = 0 then
        sb.AppendLine("START: Begin by searching for core cards that match the strategy.") |> ignore
    elif currentCount < state.TargetSize then
        let remaining = state.TargetSize - currentCount
        sb.AppendLine(sprintf "CONTINUE: Add %d more cards. Consider curve, synergies, and balance." remaining) |> ignore
    else
        sb.AppendLine("FINALIZE: Deck is complete. Use 'finalize' action.") |> ignore
    
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
    
    // Build Qdrant filter
    let filter = Qdrant.Client.Grpc.Filter()
    
    match filters with
    | Some f ->
        // Add color filter
        match f.Colors with
        | Some colors when colors.Length > 0 ->
            // TODO: Add color filtering logic
            ()
        | _ -> ()
        
        // Add cost range filter  
        // TODO: Add cost filtering logic
        ()
    | None -> ()
    
    // Search
    let! results = qdrant.SearchAsync("lorcana_cards", vector, limit = uint64 limit, filter = filter)
    
    // Format results as CSV
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

// ===== AGENT LOOP =====

let rec agentLoop 
    (ollama: IOllamaApiClient) 
    (qdrant: QdrantClient)
    (embeddingGen: Func<string, Task<float32 array>>)
    (state: AgentState) 
    (searchResults: string option)
    (maxIterations: int) = task {
    
    if state.Complete || state.Iteration >= maxIterations then
        return Ok state
    else
        // Build prompt
        let prompt = buildAgentPrompt state searchResults
        
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
                    return! agentLoop ollama qdrant embeddingGen newState (Some results) maxIterations
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
                    return! agentLoop ollama qdrant embeddingGen newState2 None maxIterations
                | None ->
                    return Error "add_cards action requires cards"
            
            | "finalize" ->
                // Check if deck is complete
                let totalCards = newState.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
                if totalCards >= state.TargetSize then
                    return Ok { newState with Complete = true }
                else
                    // Not done yet, continue
                    return! agentLoop ollama qdrant embeddingGen newState None maxIterations
            
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
    
    let initialState = {
        CurrentDeck = Map.empty
        SearchHistory = []
        Iteration = 0
        TargetSize = query.deckSize
        AllowedColors = allowedColors
        Complete = false
        Reasoning = []
    }
    
    let! result = agentLoop ollama qdrant embeddingGen initialState None 10
    
    match result with
    | Ok finalState ->
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
            sb.AppendLine(sprintf "Built deck in %d iterations using agentic RAG." finalState.Iteration) |> ignore
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
