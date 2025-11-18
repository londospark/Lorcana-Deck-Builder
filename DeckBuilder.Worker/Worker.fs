module DeckBuilder.Worker.Worker

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Qdrant.Client
open OllamaSharp

[<Literal>]
let embedModel = "nomic-embed-text"

// Vector dimensions for nomic-embed-text model
[<Literal>]
let vectorSize = 768uL

let computeFileHash (filePath: string) =
    use sha256 = SHA256.Create()
    use stream = File.OpenRead(filePath)
    let hashBytes = sha256.ComputeHash(stream)
    BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant()

let getStoredHash (qdrant: QdrantClient) (collectionName: string) = task {
    try
        let! exists = qdrant.CollectionExistsAsync(collectionName)
        if not exists then
            return None
        else
            // Try to get first point to check for hash
            let! scrollResult = qdrant.ScrollAsync(collectionName, limit = 1u)
            let points = scrollResult.Result |> Seq.toList
            if points.Length > 0 then
                let firstPoint = points.[0]
                let mutable hashValue = Unchecked.defaultof<Qdrant.Client.Grpc.Value>
                if firstPoint.Payload.TryGetValue("__file_hash__", &hashValue) then
                    return Some hashValue.StringValue
                else
                    return None
            else
                return None
    with _ ->
        return None
}

type DataIngestionWorker(
    logger: ILogger<DataIngestionWorker>,
    qdrant: QdrantClient,
    ollama: IOllamaApiClient,
    hostApplicationLifetime: IHostApplicationLifetime) =
    
    let mutable isRunning = false
    
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
        
        let tryGetInt (key: string) =
            let mutable el = Unchecked.defaultof<JsonElement>
            if card.TryGetProperty(key, &el) && el.ValueKind = JsonValueKind.Number then
                let mutable i = 0
                if el.TryGetInt32(&i) then Some i else None
            else None
        
        let tryGetArray (key: string) =
            let mutable el = Unchecked.defaultof<JsonElement>
            if card.TryGetProperty(key, &el) && el.ValueKind = JsonValueKind.Array then
                el.EnumerateArray()
                |> Seq.choose (fun x -> 
                    if x.ValueKind = JsonValueKind.String then 
                        let s = x.GetString()
                        if isNull s then None else Some s
                    else None)
                |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                |> Seq.map (fun s -> s.Trim())
                |> String.concat " "
                |> Some
            else None
        
        let tryGetBool (key: string) =
            let mutable el = Unchecked.defaultof<JsonElement>
            if card.TryGetProperty(key, &el) && (el.ValueKind = JsonValueKind.True || el.ValueKind = JsonValueKind.False) then
                Some (el.GetBoolean())
            else None
        
        // Core identity (NO STORY - it's flavor text that misleads mechanics)
        tryAppend "name"
        tryAppend "type"
        
        // Subtypes (for tribal synergies)
        match tryGetArray "subtypes" with
        | Some s -> sb.Append(s).Append(" ") |> ignore
        | None -> ()
        
        // Full rules text (most important!)
        tryAppend "fullText"
        
        // Extract and add keyword abilities explicitly
        let mutable fullTextEl = Unchecked.defaultof<JsonElement>
        if card.TryGetProperty("fullText", &fullTextEl) && fullTextEl.ValueKind = JsonValueKind.String then
            let fullText = fullTextEl.GetString() |> Option.ofObj |> Option.defaultValue ""
            if not (String.IsNullOrWhiteSpace fullText) then
                let keywords = [|
                    "Evasive"; "Challenger"; "Bodyguard"; "Ward"; "Singer"; "Shift"
                    "Reckless"; "Support"; "Rush"; "Resist"
                |]
                for keyword in keywords do
                    if fullText.Contains(keyword, StringComparison.OrdinalIgnoreCase) then
                        sb.Append($" {keyword} ") |> ignore
        
        // Inkable status
        match tryGetBool "inkable" with
        | Some true -> sb.Append(" inkable flexible ") |> ignore
        | Some false -> sb.Append(" uninkable must-play ") |> ignore
        | None -> ()
        
        // Add contextual hints based on stats (for Characters)
        let mutable typeEl = Unchecked.defaultof<JsonElement>
        if card.TryGetProperty("type", &typeEl) && typeEl.ValueKind = JsonValueKind.String then
            let cardType = typeEl.GetString() |> Option.ofObj |> Option.defaultValue ""
            if cardType.Trim().ToLowerInvariant() = "character" then
                match tryGetInt "lore" with
                | Some l when l >= 3 -> sb.Append(" high-lore questing valuable ") |> ignore
                | Some l when l = 0 -> sb.Append(" zero-lore utility ") |> ignore
                | _ -> ()
                
                match tryGetInt "willpower" with
                | Some w when w >= 5 -> sb.Append(" defensive tank durable ") |> ignore
                | Some w when w <= 2 -> sb.Append(" fragile vulnerable ") |> ignore
                | _ -> ()
                
                match tryGetInt "strength" with
                | Some s when s >= 4 -> sb.Append(" aggressive attacker removal ") |> ignore
                | Some s when s = 0 -> sb.Append(" passive non-combat ") |> ignore
                | _ -> ()
        
        // Cost context
        match tryGetInt "cost" with
        | Some c when c <= 2 -> sb.Append(" early-game cheap fast ") |> ignore
        | Some c when c >= 7 -> sb.Append(" late-game expensive finisher ") |> ignore
        | Some c when c >= 4 && c <= 6 -> sb.Append(" midrange ") |> ignore
        | _ -> ()
        
        sb.ToString().Trim()
    
    interface IHostedService with
        member _.StartAsync(cancellationToken: CancellationToken) = task {
            try
                logger.LogInformation("Data Ingestion Worker starting...")
                
                let collectionName = "lorcana_cards"
                let dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "allCards.json")
                let triggerPath = Path.Combine(AppContext.BaseDirectory, "Data", ".force_reimport")
                
                if not (File.Exists dataPath) then
                    logger.LogError("allCards.json not found at {Path}", dataPath)
                    hostApplicationLifetime.StopApplication()
                    return ()
                
                // Check for force reimport trigger
                let forceReimport = 
                    if File.Exists triggerPath then
                        logger.LogInformation("Force reimport trigger detected at {Path}", triggerPath)
                        File.Delete(triggerPath)
                        true
                    else
                        false
                
                // Compute hash of current file
                logger.LogInformation("Computing hash of {Path}...", dataPath)
                let currentHash = computeFileHash dataPath
                logger.LogInformation("File hash: {Hash}", currentHash)
                
                // Check if collection exists and get stored hash
                let! storedHash = getStoredHash qdrant collectionName
                
                if forceReimport then
                    logger.LogInformation("Forcing reimport regardless of hash...")
                    let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
                    if exists then
                        logger.LogInformation("Deleting existing collection '{CollectionName}'...", collectionName)
                        do! qdrant.DeleteCollectionAsync(collectionName, cancellationToken = cancellationToken)
                else
                    match storedHash with
                    | Some hash when hash = currentHash ->
                        logger.LogInformation("Data unchanged (hash match). Skipping ingestion.")
                        hostApplicationLifetime.StopApplication()
                        return ()
                    | Some hash ->
                        logger.LogInformation("Data changed (old hash: {OldHash}, new hash: {NewHash}). Re-importing...", hash, currentHash)
                        let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
                        if exists then
                            logger.LogInformation("Deleting existing collection '{CollectionName}'...", collectionName)
                            do! qdrant.DeleteCollectionAsync(collectionName, cancellationToken = cancellationToken)
                    | None ->
                        logger.LogInformation("No existing data found. Starting fresh import...")
                        let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
                        if exists then
                            logger.LogInformation("Deleting existing collection '{CollectionName}'...", collectionName)
                            do! qdrant.DeleteCollectionAsync(collectionName, cancellationToken = cancellationToken)
                
                logger.LogInformation("Starting data ingestion...")
                
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
                let vectorParams = Qdrant.Client.Grpc.VectorParams(Size = vectorSize, Distance = Qdrant.Client.Grpc.Distance.Cosine)
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
                                try
                                    if box embRes |> isNull then 
                                        Array.zeroCreate<float32> 384
                                    elif box embRes.Embeddings |> isNull || embRes.Embeddings.Count = 0 then 
                                        Array.zeroCreate<float32> 384
                                    else 
                                        embRes.Embeddings[0] |> Seq.map float32 |> Seq.toArray
                                with _ ->
                                    Array.zeroCreate<float32> 384
                            
                            let point = Qdrant.Client.Grpc.PointStruct()
                            point.Id <- Qdrant.Client.Grpc.PointId(Num = uint64 idx)
                            let vec = Qdrant.Client.Grpc.Vector()
                            vec.Data.AddRange(vecDoubles)
                            point.Vectors <- Qdrant.Client.Grpc.Vectors(Vector = vec)
                            
                            // Convert card JSON to payload
                            for prop in card.EnumerateObject() do
                                point.Payload[prop.Name] <- toQdrantValue prop.Value
                            
                            // Store file hash in every point for future comparison
                            let hashValue = Qdrant.Client.Grpc.Value()
                            hashValue.StringValue <- currentHash
                            point.Payload["__file_hash__"] <- hashValue
                            
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
    
    member this.ForceReimport() = task {
        if isRunning then
            logger.LogWarning("Reimport already in progress, skipping...")
            return ()
        
        isRunning <- true
        try
            logger.LogInformation("Force reimport triggered via API endpoint")
            let triggerPath = Path.Combine(AppContext.BaseDirectory, "Data", ".force_reimport")
            File.WriteAllText(triggerPath, DateTime.UtcNow.ToString("O"))
            logger.LogInformation("Trigger file created at {Path}", triggerPath)
            
            // Call StartAsync to re-run the ingestion
            let! _ = (this :> IHostedService).StartAsync(CancellationToken.None)
            ()
        finally
            isRunning <- false
    }
