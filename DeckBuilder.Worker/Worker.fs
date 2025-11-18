module DeckBuilder.Worker.Worker

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Qdrant.Client
open OllamaSharp

[<Literal>]
let embedModel = "all-minilm"

type DataIngestionWorker(
    logger: ILogger<DataIngestionWorker>,
    qdrant: QdrantClient,
    ollama: IOllamaApiClient,
    hostApplicationLifetime: IHostApplicationLifetime) =
    
    let rec toQdrantValue (el: JsonElement) : Qdrant.Client.Grpc.Value =
        let v = Qdrant.Client.Grpc.Value()
        match el.ValueKind with
        | JsonValueKind.String -> v.StringValue <- el.GetString(); v
        | JsonValueKind.Number ->
            let mutable i64 = 0L
            if el.TryGetInt64(&i64) then v.DoubleValue <- float i64; v
            else v.DoubleValue <- el.GetDouble(); v
        | JsonValueKind.True -> v.BoolValue <- true; v
        | JsonValueKind.False -> v.BoolValue <- false; v
        | JsonValueKind.Null -> v.NullValue <- Qdrant.Client.Grpc.NullValue.NullValue; v
        | JsonValueKind.Array ->
            let lv = Qdrant.Client.Grpc.ListValue()
            for item in el.EnumerateArray() do
                lv.Values.Add(toQdrantValue item)
            v.ListValue <- lv; v
        | JsonValueKind.Object ->
            let s = Qdrant.Client.Grpc.Struct()
            for p in el.EnumerateObject() do
                s.Fields[p.Name] <- toQdrantValue p.Value
            v.StructValue <- s; v
        | _ -> v.NullValue <- Qdrant.Client.Grpc.NullValue.NullValue; v
    
    let embeddingText (card: JsonElement) =
        let sb = StringBuilder()
        let tryAppend (key: string) =
            let mutable el = Unchecked.defaultof<JsonElement>
            if card.TryGetProperty(key, &el) && el.ValueKind = JsonValueKind.String then
                let s = el.GetString()
                if not (String.IsNullOrWhiteSpace s) then
                    sb.Append(s).Append(" ") |> ignore
        tryAppend "name"
        tryAppend "type"
        tryAppend "text"
        tryAppend "flavorText"
        sb.ToString().Trim()
    
    interface IHostedService with
        member _.StartAsync(cancellationToken: CancellationToken) = task {
            try
                logger.LogInformation("Data Ingestion Worker starting...")
                
                let collectionName = "lorcana_cards"
                let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
                
                if exists then
                    logger.LogInformation("Collection '{CollectionName}' already exists. Skipping ingestion.", collectionName)
                    hostApplicationLifetime.StopApplication()
                    return ()
                
                logger.LogInformation("Collection not found. Starting data ingestion...")
                
                let dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "allCards.json")
                if not (File.Exists dataPath) then
                    logger.LogError("allCards.json not found at {Path}", dataPath)
                    hostApplicationLifetime.StopApplication()
                    return ()
                
                logger.LogInformation("Reading card data from {Path}...", dataPath)
                let txt = File.ReadAllText(dataPath)
                use doc = JsonDocument.Parse(txt)
                let root = doc.RootElement
                
                let mutable cardsEl = Unchecked.defaultof<JsonElement>
                if not (root.TryGetProperty("cards", &cardsEl)) || cardsEl.ValueKind <> JsonValueKind.Array then
                    logger.LogError("Invalid allCards.json format: missing 'cards' array")
                    hostApplicationLifetime.StopApplication()
                    return ()
                
                // Create collection
                logger.LogInformation("Creating Qdrant collection '{CollectionName}'...", collectionName)
                let vectorParams = Qdrant.Client.Grpc.VectorParams(Size = 384uL, Distance = Qdrant.Client.Grpc.Distance.Cosine)
                try
                    do! qdrant.CreateCollectionAsync(collectionName, vectorParams, cancellationToken = cancellationToken)
                with
                | :? Grpc.Core.RpcException as ex when ex.StatusCode = Grpc.Core.StatusCode.AlreadyExists -> 
                    logger.LogInformation("Collection already exists (race condition)")
                
                // Generate embeddings and upsert
                let pointsBuffer = System.Collections.Generic.List<Qdrant.Client.Grpc.PointStruct>()
                let mutable idx = 0
                let cardArray = cardsEl.EnumerateArray() |> Seq.toArray
                logger.LogInformation("Processing {CardCount} cards...", cardArray.Length)
                
                for card in cardArray do
                    idx <- idx + 1
                    let inputText = embeddingText card
                    
                    if not (String.IsNullOrWhiteSpace inputText) then
                        try
                            let! embRes =
                                let req = OllamaSharp.Models.EmbedRequest()
                                req.Model <- embedModel
                                req.Input <- System.Collections.Generic.List<string>()
                                let maxLen = 4000
                                let trimmed = inputText.Trim()
                                let safeInput = if trimmed.Length > maxLen then trimmed.Substring(0, maxLen) else trimmed
                                req.Input.Add(safeInput)
                                ollama.EmbedAsync(req)
                            
                            let vecDoubles =
                                if not (isNull embRes) && not (isNull embRes.Embeddings) && embRes.Embeddings.Count > 0 then
                                    embRes.Embeddings[0] |> Seq.map float32 |> Seq.toArray
                                else
                                    Array.zeroCreate<float32> 384
                            
                            let point = Qdrant.Client.Grpc.PointStruct()
                            point.Id <- Qdrant.Client.Grpc.PointId(Num = uint64 idx)
                            let vec = Qdrant.Client.Grpc.Vector()
                            vec.Data.AddRange(vecDoubles)
                            point.Vectors <- Qdrant.Client.Grpc.Vectors(Vector = vec)
                            
                            // Convert card JSON to payload
                            for prop in card.EnumerateObject() do
                                point.Payload[prop.Name] <- toQdrantValue prop.Value
                            
                            pointsBuffer.Add(point)
                            
                            if idx % 50 = 0 then
                                logger.LogInformation("Processed {Count}/{Total} cards...", idx, cardArray.Length)
                        with ex ->
                            logger.LogWarning(ex, "Failed to process card {Index}", idx)
                
                // Upsert all points
                logger.LogInformation("Upserting {PointCount} points to Qdrant...", pointsBuffer.Count)
                let! _ = qdrant.UpsertAsync(collectionName, pointsBuffer, cancellationToken = cancellationToken)
                
                logger.LogInformation("Data ingestion completed successfully! Processed {Count} cards.", pointsBuffer.Count)
                
                // Stop the worker (success)
                hostApplicationLifetime.StopApplication()
                
            with ex ->
                logger.LogError(ex, "Fatal error during data ingestion")
                hostApplicationLifetime.StopApplication()
        }
        
        member _.StopAsync(cancellationToken: CancellationToken) =
            logger.LogInformation("Data Ingestion Worker stopping...")
            Task.CompletedTask
