namespace DeckBuilder.Api

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.AI
open Qdrant.Client
open Qdrant.Client.Grpc
open DeckBuilder.Shared
open System.Linq

type RagWorkflowService(
    logger: ILogger<RagWorkflowService>,
    qdrant: QdrantClient,
    embedder: IEmbeddingGenerator<string, Embedding<float32>>,
    chatClient: IChatClient) =
    
    let collectionName = "lorcana-cards"
    let mutable requestEmbedding: float32 array = [||]
    
    // Step 1: Embed user request
    member this.EmbedRequest(userRequest: string) : Task<float32 array> = task {
        logger.LogInformation("Step 1: Embedding user request")
        let! response = embedder.GenerateEmbeddingAsync(userRequest)
        requestEmbedding <- response.Vector.ToArray()
        logger.LogInformation($"Embedded request into {requestEmbedding.Length} dimensions")
        return requestEmbedding
    }
    
    // Step 2: RAG search for theme cards
    member this.SearchThemeCards(embedding: float32 array, topN: uint64) : Task<CardData list> = task {
        logger.LogInformation($"Step 2: Searching for top {topN} theme cards")
        
        let! results = qdrant.SearchAsync(
            collectionName,
            embedding,
            limit = topN,
            scoreThreshold = Nullable<float32>(0.3f)
        )
        
        let cards = 
            results
            |> Seq.map (fun result ->
                let payload = result.Payload
                {
                    FullName = payload["fullName"].StringValue
                    Artist = payload["artist"].StringValue
                    SetName = payload["setName"].StringValue
                    Classifications = payload["classifications"].ListValue.Values |> Seq.map (_.StringValue) |> List.ofSeq
                    SetId = int payload["setId"].IntegerValue
                    CardNum = int payload["cardNum"].IntegerValue
                    InkColor = payload["inkColor"].StringValue
                    CardMarketUrl = payload["cardMarketUrl"].StringValue
                    Rarity = payload["rarity"].StringValue
                    InkCost = int payload["inkCost"].IntegerValue
                    Inkwell = payload["inkwell"].BoolValue
                    Strength = if payload.ContainsKey("strength") then Some(int payload["strength"].IntegerValue) else None
                    Willpower = if payload.ContainsKey("willpower") then Some(int payload["willpower"].IntegerValue) else None
                    LoreValue = if payload.ContainsKey("loreValue") then Some(int payload["loreValue"].IntegerValue) else None
                    FullText = payload["fullText"].StringValue
                    Set_Num = payload["set_Num"].StringValue
                }
            )
            |> List.ofSeq
        
        logger.LogInformation($"Found {cards.Length} theme cards")
        return cards
    }
    
    // Step 3: Analyze colors using LLM
    member this.AnalyzeColors(themeCards: CardData list) : Task<string list> = task {
        logger.LogInformation("Step 3: Analyzing color distribution with LLM")
        
        let colorCounts = 
            themeCards
            |> List.groupBy (_.InkColor)
            |> List.map (fun (color, cards) -> $"{color}: {cards.Length}")
            |> String.concat ", "
        
        let cardSummary = 
            themeCards
            |> List.take (Math.Min(10, themeCards.Length))
            |> List.map (fun c -> $"- {c.FullName} ({c.InkColor}, {c.InkCost} cost)")
            |> String.concat "\n"
        
        let prompt = $"""Analyze these Lorcana cards and choose the best 1-2 ink colors for a competitive deck.

Color distribution:
{colorCounts}

Sample cards:
{cardSummary}

Respond with ONLY a JSON object in this exact format (no extra text):
{{
  "colors": ["Color1", "Color2"],
  "reasoning": "Brief explanation"
}}"""

        let! response = chatClient.CompleteAsync(prompt)
        let responseText = response.Message.Text
        
        logger.LogInformation($"LLM response: {responseText}")
        
        // Parse JSON response
        let json = System.Text.Json.JsonDocument.Parse(responseText)
        let colors = 
            json.RootElement.GetProperty("colors").EnumerateArray()
            |> Seq.map (_.GetString())
            |> Seq.choose id
            |> List.ofSeq
        
        logger.LogInformation($"Chosen colors: {String.Join(", ", colors)}")
        return colors
    }
    
    // Step 4: RAG search for synergy cards
    member this.SearchSynergyCards(embedding: float32 array, colors: string list, topN: uint64) : Task<CardData list> = task {
        logger.LogInformation($"Step 4: Searching for synergy cards in colors: {String.Join(", ", colors)}")
        
        // Build color filter
        let colorFilter = 
            if colors.IsEmpty then None
            else
                Some(Filter(
                    Should = ResizeArray([
                        for color in colors do
                            Condition(
                                Field = FieldCondition(
                                    Key = "inkColor",
                                    Match = MatchValue(Value = MatchValueValue.StringValue color)
                                )
                            )
                    ])
                ))
        
        let! results = qdrant.SearchAsync(
            collectionName,
            embedding,
            filter = (if colorFilter.IsSome then colorFilter.Value else null),
            limit = topN,
            scoreThreshold = Nullable<float32>(0.25f)
        )
        
        let cards = 
            results
            |> Seq.map (fun result ->
                let payload = result.Payload
                {
                    FullName = payload["fullName"].StringValue
                    Artist = payload["artist"].StringValue
                    SetName = payload["setName"].StringValue
                    Classifications = payload["classifications"].ListValue.Values |> Seq.map (_.StringValue) |> List.ofSeq
                    SetId = int payload["setId"].IntegerValue
                    CardNum = int payload["cardNum"].IntegerValue
                    InkColor = payload["inkColor"].StringValue
                    CardMarketUrl = payload["cardMarketUrl"].StringValue
                    Rarity = payload["rarity"].StringValue
                    InkCost = int payload["inkCost"].IntegerValue
                    Inkwell = payload["inkwell"].BoolValue
                    Strength = if payload.ContainsKey("strength") then Some(int payload["strength"].IntegerValue) else None
                    Willpower = if payload.ContainsKey("willpower") then Some(int payload["willpower"].IntegerValue) else None
                    LoreValue = if payload.ContainsKey("loreValue") then Some(int payload["loreValue"].IntegerValue) else None
                    FullText = payload["fullText"].StringValue
                    Set_Num = payload["set_Num"].StringValue
                }
            )
            |> List.ofSeq
        
        logger.LogInformation($"Found {cards.Length} synergy cards")
        return cards
    }
    
    // Step 5: Build deck using LLM
    member this.BuildDeck(themeCards: CardData list, synergyCards: CardData list, deckSize: int) : Task<(string * int) list> = task {
        logger.LogInformation($"Step 5: Building {deckSize}-card deck with LLM")
        
        let allCards = (themeCards @ synergyCards) |> List.distinctBy (_.FullName)
        
        let cardList =
            allCards
            |> List.map (fun c -> $"- {c.FullName} ({c.InkColor}, {c.InkCost} cost, {if c.Inkwell then "inkable" else "not inkable"})")
            |> String.concat "\n"
        
        let prompt = $"""Build a competitive {deckSize}-card Lorcana deck from these cards.

Available cards:
{cardList}

Rules:
- Deck must have exactly {deckSize} cards
- Maximum 4 copies of any card
- Prioritize cards that work well together
- Include a good mana curve (mix of costs)
- Include enough inkable cards (~50%)

Respond with ONLY a JSON object (no extra text):
{{
  "deck": [
    {{"card": "Card Name", "count": 4}},
    {{"card": "Another Card", "count": 3}}
  ],
  "reasoning": "Brief deck strategy"
}}"""

        let! response = chatClient.CompleteAsync(prompt)
        let responseText = response.Message.Text
        
        logger.LogInformation($"LLM deck response length: {responseText.Length}")
        
        // Parse JSON response
        let json = System.Text.Json.JsonDocument.Parse(responseText)
        let deck = 
            json.RootElement.GetProperty("deck").EnumerateArray()
            |> Seq.map (fun elem ->
                let card = elem.GetProperty("card").GetString()
                let count = elem.GetProperty("count").GetInt32()
                (card, count)
            )
            |> List.ofSeq
        
        let totalCards = deck |> List.sumBy snd
        logger.LogInformation($"Built deck with {deck.Length} unique cards, {totalCards} total cards")
        
        return deck
    }
    
    // Complete workflow
    member this.BuildDeckWorkflow(userRequest: string, deckSize: int) : Task<DeckResult> = task {
        logger.LogInformation($"Starting RAG workflow for: {userRequest}")
        
        try
            // Step 1: Embed
            let! embedding = this.EmbedRequest(userRequest)
            
            // Step 2: Search theme cards
            let! themeCards = this.SearchThemeCards(embedding, 30UL)
            
            if themeCards.IsEmpty then
                logger.LogWarning("No theme cards found")
                return { Cards = []; Explanation = "No cards found matching your request" }
            else
                // Step 3: Analyze colors
                let! colors = this.AnalyzeColors(themeCards)
                
                // Step 4: Search synergy cards
                let! synergyCards = this.SearchSynergyCards(embedding, colors, 40UL)
                
                // Step 5: Build deck
                let! deckList = this.BuildDeck(themeCards, synergyCards, deckSize)
                
                // Convert to DeckCard format
                let allCards = (themeCards @ synergyCards) |> List.distinctBy (_.FullName)
                let cardMap = allCards |> List.map (fun c -> (c.FullName, c)) |> Map.ofList
                
                let deckCards =
                    deckList
                    |> List.choose (fun (name, count) ->
                        match Map.tryFind name cardMap with
                        | Some card ->
                            Some {
                                Count = count
                                FullName = card.FullName
                                Inkable = card.Inkwell
                                CardMarketUrl = card.CardMarketUrl
                                InkColor = card.InkColor
                            }
                        | None ->
                            logger.LogWarning($"Card not found in results: {name}")
                            None
                    )
                
                let totalCards = deckCards |> List.sumBy (_.Count)
                let explanation = $"Built {totalCards}-card deck with {deckCards.Length} unique cards using {String.Join("/", colors)} colors"
                
                logger.LogInformation($"Workflow complete: {explanation}")
                return { Cards = deckCards; Explanation = explanation }
                
        with ex ->
            logger.LogError(ex, "Workflow failed")
            return { Cards = []; Explanation = $"Error: {ex.Message}" }
    }
