namespace DeckBuilder.Api

open System
open System.Text.Json
open Microsoft.Extensions.Logging
open Microsoft.Extensions.AI
open Qdrant.Client
open Qdrant.Client.Grpc

module RagDeckService =
    
    type RagDeckBuilder(
        logger: ILogger<RagDeckBuilder>,
        qdrantClient: QdrantClient,
        embedder: IEmbeddingGenerator<string, Embedding<float32>>,
        chatClient: IChatClient,
        rulesProvider: RulesProvider.RulesProvider) =
        
        let collectionName = "lorcana_cards"
        let maxCards = 60
        
        // Step 1: Theme Search
        let searchThemeCards (userRequest: string) (limit: uint32) = task {
            logger.LogInformation("RAG Step 1: Searching for theme cards with query: {Query}", userRequest)
            
            let! embedding = embedder.GenerateEmbeddingVectorAsync(userRequest)
            
            let! results = qdrantClient.SearchAsync(
                collectionName,
                embedding,
                limit = limit
            )
            
            let cards = 
                results
                |> Seq.map (fun r -> 
                    let payload = r.Payload
                    {| 
                        FullName = payload.["fullName"].StringValue
                        InkColor = payload.["inkColor"].StringValue
                        Cost = payload.["cost"].IntegerValue
                        Inkable = payload.["inkable"].BoolValue
                        FullText = payload.["fullText"].StringValue
                        Score = r.Score
                    |})
                |> Seq.toList
            
            logger.LogInformation("Found {Count} theme cards", cards.Length)
            return cards
        }
        
        // Step 2: Choose Colors
        let chooseColors themeCards = 
            logger.LogInformation("RAG Step 2: Analyzing color distribution")
            
            let colorCounts =
                themeCards
                |> List.groupBy (fun c -> c.InkColor)
                |> List.map (fun (color, cards) -> color, cards.Length)
                |> List.sortByDescending snd
            
            logger.LogInformation("Color distribution: {Colors}", String.Join(", ", colorCounts |> List.map (fun (c, n) -> $"{c}={n}")))
            
            let chosenColors = 
                colorCounts 
                |> List.truncate 2 
                |> List.map fst
                |> String.concat ","
            
            logger.LogInformation("Chosen colors: {Colors}", chosenColors)
            chosenColors
        
        // Step 3: Synergy Search
        let searchSynergyCards (themeCards: {| FullName: string; InkColor: string; Cost: int64; Inkable: bool; FullText: string; Score: float32 |} list) (colors: string) (limit: uint32) = task {
            logger.LogInformation("RAG Step 3: Searching for synergy cards in colors: {Colors}", colors)
            
            // Build synergy query from top theme cards
            let topCards = themeCards |> List.truncate 5
            let synergyQuery = sprintf "cards that synergize with %s" (String.Join(", ", topCards |> List.map (fun c -> c.FullName)))
            
            let! embedding = embedder.GenerateEmbeddingVectorAsync(synergyQuery)
            
            // Create color filter
            let colorList = colors.Split(',') |> Array.toList
            let colorFilter = 
                {| must = 
                    [| {| key = "inkColor"; ``match`` = {| any = colorList |} |} |]
                |} |> JsonSerializer.Serialize
                |> Conditions.op_Implicit
            
            let! results = qdrantClient.SearchAsync(
                collectionName,
                embedding,
                filter = colorFilter,
                limit = limit
            )
            
            let cards = 
                results
                |> Seq.map (fun r -> 
                    let payload = r.Payload
                    {| 
                        FullName = payload.["fullName"].StringValue
                        InkColor = payload.["inkColor"].StringValue
                        Cost = payload.["cost"].IntegerValue
                        Inkable = payload.["inkable"].BoolValue
                        FullText = payload.["fullText"].StringValue
                        Score = r.Score
                    |})
                |> Seq.toList
            
            logger.LogInformation("Found {Count} synergy cards", cards.Length)
            return cards
        }
        
        // Step 4: LLM Selection
        let selectBestCards (allCandidates: {| FullName: string; InkColor: string; Cost: int64; Inkable: bool; FullText: string; Score: float32 |} list) (deckSize: int) = task {
            logger.LogInformation("RAG Step 4: Using LLM to select best {Count} cards from {Total} candidates", deckSize, allCandidates.Length)
            
            // Build concise card list for LLM
            let inkableStr c = if c then "Inkable" else "Not Inkable"
            let cardSummaries = 
                allCandidates
                |> List.mapi (fun i (c: {| FullName: string; InkColor: string; Cost: int64; Inkable: bool; FullText: string |}) ->
                    sprintf "%d. %s (%s, %d cost, %s)" i c.FullName c.InkColor (int c.Cost) (inkableStr c.Inkable))
                |> String.concat "\n"
            
            let prompt = 
                sprintf """You are building a %d-card Lorcana deck.

AVAILABLE CARDS:
%s

TASK: Select exactly %d cards by index to create a competitive deck. Consider:
- Mana curve (mix of low, medium, high cost cards)
- Color synergy (focus on 1-2 colors)
- Card synergies and combos
- Inkable cards for ink generation

RULES:
- Each card can appear up to 4 times
- Minimum deck size is %d

OUTPUT FORMAT (JSON only, no explanation):
{{
  "selections": [
    {{"index": 0, "count": 4}},
    {{"index": 3, "count": 4}},
    ...
  ],
  "reasoning": "Brief explanation"
}}""" deckSize cardSummaries deckSize deckSize
            
            let messages = [
                ChatMessage(ChatRole.System, "You are a competitive Lorcana deck builder. Return ONLY valid JSON.")
                ChatMessage(ChatRole.User, prompt)
            ]
            
            logger.LogInformation("Calling LLM for card selection...")
            let! response = chatClient.CompleteAsync(messages)
            let responseText = response.Message.Text
            
            logger.LogInformation("LLM response received: {Length} chars", responseText.Length)
            
            // Parse JSON response
            try
                let json = JsonDocument.Parse(responseText)
                let selections = json.RootElement.GetProperty("selections")
                
                let selectedCards = [
                    for selection in selections.EnumerateArray() do
                        let idx = selection.GetProperty("index").GetInt32()
                        let count = selection.GetProperty("count").GetInt32()
                        if idx < allCandidates.Length then
                            let card = allCandidates.[idx]
                            yield (card, count)
                ]
                
                logger.LogInformation("LLM selected {Count} unique cards, total {Total} cards", selectedCards.Length, selectedCards |> List.sumBy snd)
                return selectedCards
            with ex ->
                logger.LogError(ex, "Failed to parse LLM response, falling back to top cards")
                // Fallback: take top cards by score
                return allCandidates |> List.truncate (deckSize / 4) |> List.map (fun c -> (c, 4))
        }
        
        // Main workflow
        member _.BuildDeckAsync(userRequest: string, deckSize: int) = task {
            logger.LogInformation("Starting RAG deck building workflow for: {Request}, size: {Size}", userRequest, deckSize)
            
            try
                // Step 1: Theme search
                let! themeCards = searchThemeCards userRequest 50u
                
                if themeCards.IsEmpty then
                    logger.LogWarning("No theme cards found!")
                    return Error "No cards found matching your request"
                else
                    // Step 2: Choose colors
                    let colors = chooseColors themeCards
                    
                    // Step 3: Synergy search
                    let! synergyCards = searchSynergyCards themeCards colors 50u
                    
                    // Combine and deduplicate
                    let allCandidates =
                        (themeCards @ synergyCards)
                        |> List.distinctBy (fun c -> c.FullName)
                        |> List.sortByDescending (fun c -> c.Score)
                    
                    logger.LogInformation("Total unique candidates: {Count}", allCandidates.Length)
                    
                    // Step 4: LLM selection
                    let! selectedCards = selectBestCards allCandidates deckSize
                    
                    // Build final deck
                    let deck = [
                        for (card, count) in selectedCards do
                            {|
                                Count = count
                                FullName = card.FullName
                                Inkable = card.Inkable
                                InkColor = card.InkColor
                            |}
                    ]
                    
                    let totalCards = deck |> List.sumBy (fun c -> c.Count)
                    logger.LogInformation("RAG workflow complete! Built deck with {Total} cards", totalCards)
                    
                    return Ok (deck, colors)
            with ex ->
                logger.LogError(ex, "RAG workflow failed")
                return Error $"Deck building failed: {ex.Message}"
        }
