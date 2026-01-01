module DeckBuilder.Api.OnlineModelDeckService

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Qdrant.Client
open Card
open Inkable
open DeckBuilder.Api.LlmAbstraction
open DeckBuilder.Shared

/// Build a Lorcana deck using RAG with flexible model providers (online or local)
let buildDeckWithOnlineModels
    (llmService: ILlmService)
    (embeddingService: IEmbeddingService)
    (qdrant: QdrantClient)
    (query: DeckQuery)
    (logger: ILogger)
    : Task<Result<DeckResponse, string>> = task {
    
    logger.LogInformation("Starting online model deck building for request: {Request}", query.request)
    let startTime = System.Diagnostics.Stopwatch.StartNew()
    
    let targetSize = query.deckSize
    let format = query.format
    logger.LogInformation("Building deck with format: {Format}, target size: {TargetSize}", format, targetSize)
    
    // ===== PHASE 1: SEARCH & DISCOVERY =====
    logger.LogInformation("Phase 1: Search & Discovery")
    
    // Generate embedding for user request
    let! queryEmbedding = embeddingService.GenerateEmbeddingAsync(query.request)
    logger.LogDebug("Query embedding generated, vector length: {Length}", queryEmbedding.Length)
    
    // Build format filter
    let formatFilter = QdrantHelpers.buildFormatFilter format
    
    // Search Qdrant for relevant cards
    let searchLimit = 120uL
    let! searchResults = task {
        match formatFilter with
        | Some ff ->
            return! qdrant.SearchAsync("lorcana_cards", queryEmbedding, filter = ff, limit = searchLimit)
        | None ->
            return! qdrant.SearchAsync("lorcana_cards", queryEmbedding, limit = searchLimit)
    }
    
    logger.LogInformation("Search returned {Count} results", Seq.length searchResults)
    
    if Seq.isEmpty searchResults then
        return Error "No cards found matching the search criteria"
    else
        // ===== PHASE 2: LLM-BASED DECK BUILDING =====
        logger.LogInformation("Phase 2: LLM-based Deck Construction")
        
        // Format search results as structured data for LLM
        let candidateCards = 
            searchResults
            |> Seq.truncate 80  // Limit to avoid overwhelming the LLM
            |> Seq.map (fun point ->
                let fullName = Payload.fullName point.Payload
                let cost = Payload.cost point.Payload |> Option.map string |> Option.defaultValue "?"
                let inkable = Payload.inkable point.Payload |> Option.defaultValue false
                let colors = Payload.colors point.Payload |> String.concat ","
                let fullText = Payload.fullText point.Payload
                let maxCopies = Payload.maxCopiesInDeck point.Payload |> Option.defaultValue 4
                
                sprintf "%s | cost:%s | ink:%s | colors:%s | max:%d | text:%s" 
                    fullName cost (if inkable then "Y" else "N") colors maxCopies
                    (if fullText.Length > 80 then fullText.Substring(0, 77) + "..." else fullText))
            |> String.concat "\n"
        
        // Get user-selected colors or let LLM choose
        let colorConstraint =
            match query.selectedColors with
            | Some colors when colors.Length > 0 ->
                sprintf "REQUIRED COLORS: %s (user-specified, must use these colors only)" (String.Join(", ", colors))
            | _ ->
                "COLOR SELECTION: Choose 1-2 optimal ink colors based on the available cards and user's request"
        
        // Build LLM prompt
        let promptTemplate = """You are an expert Disney Lorcana deck builder. Build a competitive %d-card deck for the following request:

USER REQUEST: %s

FORMAT: %A (only cards legal in this format are included in candidates)

%s

CANDIDATE CARDS (from vector search):
%s

DECK BUILDING RULES:
- Exactly %d cards total
- Maximum 4 copies per card (check 'max' field - some cards have different limits)
- 1-2 ink colors only
- ~70-80%% inkable cards for resource consistency
- Smooth mana curve (mix of costs 1-6)
- Win condition: Reach 20 lore before opponent

RESPOND WITH VALID JSON ONLY:
{
  "colors": ["Color1", "Color2"],
  "cards": [
    ["Card Full Name 1", 4],
    ["Card Full Name 2", 3],
    ["Card Full Name 3", 2]
  ],
  "reasoning": "Brief explanation of deck strategy"
}

CRITICAL:
- Use EXACT card names from CANDIDATE CARDS list
- Total card count from all entries MUST equal %d
- Respect max copies limit for each card
- ONLY valid Lorcana card names, no made-up cards
- JSON format only, no extra text"""
        let prompt = sprintf promptTemplate targetSize query.request format colorConstraint candidateCards targetSize
        
        logger.LogDebug("Sending prompt to LLM (length: {Length})", prompt.Length)
        
        // Call LLM
        let! llmResponse = llmService.GenerateAsync(prompt)
        logger.LogDebug("LLM response received (length: {Length})", llmResponse.Length)
        
        // Parse LLM response
        try
            let trimmed = llmResponse.Trim()
            let firstBrace = trimmed.IndexOf('{')
            if firstBrace = -1 then
                logger.LogWarning("LLM didn't return JSON")
                return Error "LLM response was not in expected JSON format"
            else
                let jsonStart = trimmed.Substring(firstBrace)
                // Find matching closing brace
                let mutable braceCount = 0
                let mutable inString = false
                let mutable escaped = false
                let mutable jsonEnd = -1
                
                for i = 0 to jsonStart.Length - 1 do
                    let c = jsonStart.[i]
                    if escaped then 
                        escaped <- false
                    elif c = '\\' && inString then 
                        escaped <- true
                    elif c = '"' then 
                        inString <- not inString
                    elif not inString then
                        if c = '{' then 
                            braceCount <- braceCount + 1
                        elif c = '}' then
                            braceCount <- braceCount - 1
                            if braceCount = 0 then 
                                jsonEnd <- i + 1
                
                let jsonStr = if jsonEnd > 0 then jsonStart.Substring(0, jsonEnd) else jsonStart
                
                logger.LogDebug("Parsing JSON: {Json}", jsonStr.Substring(0, Math.Min(200, jsonStr.Length)))
                
                let doc = JsonDocument.Parse(jsonStr)
                let root = doc.RootElement
                
                // Extract colors
                let mutable colorsProp = Unchecked.defaultof<JsonElement>
                let selectedColors = 
                    if root.TryGetProperty("colors", &colorsProp) && colorsProp.ValueKind = JsonValueKind.Array then
                        colorsProp.EnumerateArray() 
                        |> Seq.choose (fun e -> if e.ValueKind = JsonValueKind.String then Some (e.GetString()) else None)
                        |> Seq.toList
                    else
                        []
                
                // Extract cards
                let mutable cardsProp = Unchecked.defaultof<JsonElement>
                let cards =
                    if root.TryGetProperty("cards", &cardsProp) && cardsProp.ValueKind = JsonValueKind.Array then
                        cardsProp.EnumerateArray()
                        |> Seq.choose (fun entry ->
                            if entry.ValueKind = JsonValueKind.Array && entry.GetArrayLength() >= 2 then
                                let arr = entry.EnumerateArray() |> Seq.toArray
                                if arr.Length >= 2 && arr.[0].ValueKind = JsonValueKind.String then
                                    let name = arr.[0].GetString()
                                    let count = 
                                        if arr.[1].ValueKind = JsonValueKind.Number then 
                                            arr.[1].GetInt32() 
                                        else 1
                                    Some (name, count)
                                else None
                            else None)
                        |> Seq.toList
                    else
                        []
                
                // Extract reasoning
                let mutable reasoningProp = Unchecked.defaultof<JsonElement>
                let reasoning = 
                    if root.TryGetProperty("reasoning", &reasoningProp) && reasoningProp.ValueKind = JsonValueKind.String then
                        reasoningProp.GetString()
                    else
                        "No reasoning provided"
                
                logger.LogInformation("LLM selected {ColorCount} colors, {CardCount} unique cards", selectedColors.Length, cards.Length)
                
                // Validate and build response
                let totalCards = cards |> List.sumBy snd
                if totalCards <> targetSize then
                    logger.LogWarning("LLM returned {Count} cards, expected {Target}", totalCards, targetSize)
                
                // Look up card details from Qdrant for each card in the deck
                let! cardEntries = task {
                    let entries = System.Collections.Generic.List<CardEntry>()
                    for (cardName, count) in cards do
                        // Search for exact card by name
                        let! cardEmbedding = embeddingService.GenerateEmbeddingAsync(cardName)
                        let! cardResults = qdrant.SearchAsync("lorcana_cards", cardEmbedding, limit = 1uL)
                        
                        let entry : CardEntry =
                            if Seq.isEmpty cardResults then
                                // Card not found, use minimal info
                                {
                                    count = count
                                    fullName = cardName
                                    inkable = false
                                    cardMarketUrl = ""
                                    inkColor = ""
                                    cost = None
                                    subtypes = [||]
                                }
                            else
                                let point = Seq.head cardResults
                                let payload = point.Payload
                                {
                                    count = count
                                    fullName = Payload.fullName payload
                                    inkable = Payload.inkable payload |> Option.defaultValue false
                                    cardMarketUrl = Payload.cardMarketUrl payload |> Option.defaultValue ""
                                    inkColor = Payload.colors payload |> List.tryHead |> Option.defaultValue ""
                                    cost = Payload.cost payload
                                    subtypes = Payload.subtypes payload |> List.toArray
                                }
                        
                        entries.Add(entry)
                    
                    return entries |> Seq.toArray
                }
                
                startTime.Stop()
                
                let explanationTemplate = """Built %d-card %s deck in %dms using online model RAG.

Selected Colors: %s

Deck Strategy:
%s

Note: This deck was built using an AI model with enhanced reasoning capabilities."""
                let explanation = sprintf explanationTemplate totalCards (String.Join("/", selectedColors)) startTime.ElapsedMilliseconds (String.Join(", ", selectedColors)) reasoning
                
                logger.LogInformation("Online model deck building completed successfully in {Ms}ms", startTime.ElapsedMilliseconds)
                
                return Ok ({
                    cards = cardEntries
                    explanation = explanation
                } : DeckResponse)
                
        with ex ->
            logger.LogError(ex, "Failed to parse LLM response")
            return Error (sprintf "Failed to parse LLM response: %s" ex.Message)
}
