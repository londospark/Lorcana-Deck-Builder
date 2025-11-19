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

let tryParseDateToUnix (s: string) : float option =
    if String.IsNullOrWhiteSpace s then None else
    let formats = [| "yyyy-MM-dd"; "yyyy-MM-ddTHH:mm:ssZ"; "yyyy-MM-ddTHH:mm:ss.fffZ" |]
    let mutable dt = DateTime.MinValue
    if System.DateTime.TryParseExact(s.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal ||| System.Globalization.DateTimeStyles.AdjustToUniversal, &dt) then
        let dto = DateTimeOffset(dt.ToUniversalTime())
        Some (float (dto.ToUnixTimeSeconds()))
    else None

let getStoredHash (qdrant: QdrantClient) (collectionName: string) = task {
    try
        let! exists = qdrant.CollectionExistsAsync(collectionName)
        if not exists then return None else
        
        let! scrollResult = qdrant.ScrollAsync(collectionName, limit = 1u)
        let points = scrollResult.Result |> Seq.toList
        
        return
            points
            |> List.tryHead
            |> Option.bind (fun point ->
                let mutable hashValue = Unchecked.defaultof<Qdrant.Client.Grpc.Value>
                if point.Payload.TryGetValue("__file_hash__", &hashValue) then
                    Some hashValue
                else
                    None)
            |> Option.bind (fun hashValue ->
                if not (isNull (box hashValue)) && not (String.IsNullOrWhiteSpace hashValue.StringValue) then
                    Some hashValue.StringValue
                else
                    None)
    with ex ->
        System.Console.WriteLine($"Error retrieving stored hash: {ex.Message}")
        return None
}

type DataIngestionWorker(
    logger: ILogger<DataIngestionWorker>,
    qdrant: QdrantClient,
    ollama: IOllamaApiClient,
    hostApplicationLifetime: IHostApplicationLifetime) =
    
    let mutable isRunning = false
    
    // Define helper functions first (before they're used)
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
    
    let checkForceReimport (triggerPath: string) =
        if File.Exists triggerPath then
            logger.LogInformation("Force reimport trigger detected at {Path}", triggerPath)
            File.Delete(triggerPath)
            true
        else
            false
    
    let handleHashComparison (currentHash: string) (storedHash: string option) (collectionName: string) (cancellationToken: CancellationToken) = task {
        match storedHash with
        | Some hash when hash = currentHash ->
            logger.LogInformation("Data unchanged (hash match). Skipping ingestion.")
            return true // Skip ingestion
        | Some hash ->
            logger.LogInformation("Data changed (old hash: {OldHash}, new hash: {NewHash}). Re-importing...", hash, currentHash)
            let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
            if exists then
                logger.LogInformation("Deleting existing collection '{CollectionName}'...", collectionName)
                do! qdrant.DeleteCollectionAsync(collectionName, cancellationToken = cancellationToken)
            return false
        | None ->
            logger.LogInformation("No hash found in existing data. Checking if collection has points...")
            let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
            if exists then
                try
                    let! scrollResult = qdrant.ScrollAsync(collectionName, limit = 1u, cancellationToken = cancellationToken)
                    let pointCount = scrollResult.Result |> Seq.length
                    if pointCount > 0 then
                        logger.LogInformation("Collection has {Count} points but no hash. Assuming data is stale, will re-import with hash.", pointCount)
                        logger.LogInformation("Deleting existing collection '{CollectionName}'...", collectionName)
                        do! qdrant.DeleteCollectionAsync(collectionName, cancellationToken = cancellationToken)
                    else
                        logger.LogInformation("Collection exists but is empty. Will create fresh.")
                with ex ->
                    logger.LogWarning(ex, "Error checking collection points, will recreate collection")
                    do! qdrant.DeleteCollectionAsync(collectionName, cancellationToken = cancellationToken)
            else
                logger.LogInformation("No existing collection found. Starting fresh import...")
            return false
    }
    
    let createEmbedding (inputText: string | null) = task {
        let req = OllamaSharp.Models.EmbedRequest()
        req.Model <- embedModel
        req.Input <- System.Collections.Generic.List<string>()
        let maxLen = 4000
        let trimmed = if isNull inputText then "" else inputText.Trim()
        let safeInput = if trimmed.Length > maxLen then trimmed.Substring(0, maxLen) else trimmed
        req.Input.Add(safeInput)
        let! embRes = ollama.EmbedAsync(req)
        
        return
            try
                if box embRes |> isNull then 
                    Array.zeroCreate<float32> 384
                elif box embRes.Embeddings |> isNull || embRes.Embeddings.Count = 0 then 
                    Array.zeroCreate<float32> 384
                else 
                    embRes.Embeddings[0] |> Seq.map float32 |> Seq.toArray
            with _ ->
                Array.zeroCreate<float32> 384
    }
    
    let createPoint (idx: int) (card: JsonElement) (vecDoubles: float32[]) (currentHash: string) =
        let point = Qdrant.Client.Grpc.PointStruct()
        point.Id <- Qdrant.Client.Grpc.PointId(Num = uint64 idx)
        let vec = Qdrant.Client.Grpc.Vector()
        vec.Data.AddRange(vecDoubles)
        point.Vectors <- Qdrant.Client.Grpc.Vectors(Vector = vec)
        
        // Convert card JSON to payload
        for prop in card.EnumerateObject() do
            point.Payload[prop.Name] <- toQdrantValue prop.Value
        
        // Add UNIX timestamps for format date fields if present
        let mutable allowedInFormatsEl = Unchecked.defaultof<JsonElement>
        if card.TryGetProperty("allowedInFormats", &allowedInFormatsEl) && allowedInFormatsEl.ValueKind = JsonValueKind.Object then
            let mutable coreEl = Unchecked.defaultof<JsonElement>
            if allowedInFormatsEl.TryGetProperty("Core", &coreEl) && coreEl.ValueKind = JsonValueKind.Object then
                // allowedFromDate -> allowedFromTs
                let mutable fromEl = Unchecked.defaultof<JsonElement>
                if coreEl.TryGetProperty("allowedFromDate", &fromEl) && fromEl.ValueKind = JsonValueKind.String then
                    let s = fromEl.GetString()
                    match s with
                    | null | "" -> ()
                    | _ ->
                        match tryParseDateToUnix s with
                        | Some ts ->
                            let v = Qdrant.Client.Grpc.Value()
                            v.DoubleValue <- ts
                            point.Payload["allowedInFormats.Core.allowedFromTs"] <- v
                        | None -> ()
                // allowedUntilDate -> allowedUntilTs
                let mutable untilEl = Unchecked.defaultof<JsonElement>
                if coreEl.TryGetProperty("allowedUntilDate", &untilEl) && untilEl.ValueKind = JsonValueKind.String then
                    let s = untilEl.GetString()
                    match s with
                    | null | "" -> ()
                    | _ ->
                        match tryParseDateToUnix s with
                        | Some ts ->
                            let v = Qdrant.Client.Grpc.Value()
                            v.DoubleValue <- ts
                            point.Payload["allowedInFormats.Core.allowedUntilTs"] <- v
                        | None -> ()
        
        // Store file hash in every point for future comparison
        if not (String.IsNullOrWhiteSpace currentHash) then
            let hashValue = Qdrant.Client.Grpc.Value()
            hashValue.StringValue <- currentHash
            point.Payload["__file_hash__"] <- hashValue
        else
            logger.LogWarning("Current hash is empty, cannot store hash in point {Index}", idx)
        
        point
    
    let processCards (cardArray: JsonElement[]) (currentHash: string) = task {
        let pointsBuffer = System.Collections.Generic.List<Qdrant.Client.Grpc.PointStruct>()
        let mutable idx = 0
        
        for card in cardArray do
            idx <- idx + 1
            let inputText = embeddingText card
            
            if not (String.IsNullOrWhiteSpace inputText) then
                try
                    let! vecDoubles = createEmbedding inputText
                    let point = createPoint idx card vecDoubles currentHash
                    pointsBuffer.Add(point)
                    
                    if idx % 50 = 0 then
                        logger.LogInformation("Processed {Count}/{Total} cards...", idx, cardArray.Length)
                with ex ->
                    logger.LogWarning(ex, "Failed to process card {Index}", idx)
        
        return pointsBuffer
    }
    
    let ensureCollection (collectionName: string) (cancellationToken: CancellationToken) = task {
        logger.LogInformation("Creating Qdrant collection '{CollectionName}'...", collectionName)
        let vectorParams = Qdrant.Client.Grpc.VectorParams(Size = vectorSize, Distance = Qdrant.Client.Grpc.Distance.Cosine)
        try
            do! qdrant.CreateCollectionAsync(collectionName, vectorParams, cancellationToken = cancellationToken)
        with
        | :? Grpc.Core.RpcException as ex when ex.StatusCode = Grpc.Core.StatusCode.AlreadyExists -> 
            logger.LogInformation("Collection already exists (race condition)")
    }
    
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
                
                let forceReimport = checkForceReimport triggerPath
                
                logger.LogInformation("Computing hash of {Path}...", dataPath)
                let currentHash = computeFileHash dataPath
                logger.LogInformation("Current file hash: {Hash}", currentHash)
                
                logger.LogInformation("Checking for existing collection and stored hash...")
                let! storedHash = getStoredHash qdrant collectionName
                
                match storedHash with
                | Some hash -> logger.LogInformation("Found stored hash in collection: {Hash}", hash)
                | None -> logger.LogInformation("No stored hash found in collection")
                
                if forceReimport then
                    logger.LogInformation("Forcing reimport regardless of hash...")
                    let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
                    if exists then
                        logger.LogInformation("Deleting existing collection '{CollectionName}'...", collectionName)
                        do! qdrant.DeleteCollectionAsync(collectionName, cancellationToken = cancellationToken)
                else
                    let! shouldSkip = handleHashComparison currentHash storedHash collectionName cancellationToken
                    if shouldSkip then
                        hostApplicationLifetime.StopApplication()
                        return ()
                
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
                
                do! ensureCollection collectionName cancellationToken
                
                let cardArray = cardsEl.EnumerateArray() |> Seq.toArray
                logger.LogInformation("Processing {CardCount} cards...", cardArray.Length)
                
                let! pointsBuffer = processCards cardArray currentHash
                
                logger.LogInformation("Upserting {PointCount} points to Qdrant...", pointsBuffer.Count)
                let! _ = qdrant.UpsertAsync(collectionName, pointsBuffer, cancellationToken = cancellationToken)
                
                logger.LogInformation("Data ingestion completed successfully! Processed {Count} cards.", pointsBuffer.Count)
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
