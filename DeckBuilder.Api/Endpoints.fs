module Endpoints

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System.Threading.Tasks
open OllamaSharp
open Qdrant.Client
open ApiModels
open Card
open Inkable
open DeckHelpers
open DeckBuilder.Api
open DeckBuilder.Shared

/// Shared model names (can be made configurable later)
[<Literal>]
let embedModel = "nomic-embed-text"

// Vector dimensions for nomic-embed-text model
[<Literal>]
let vectorSize = 768uL

let computeTextHash (text: string) =
    use sha256 = SHA256.Create()
    let bytes = Text.Encoding.UTF8.GetBytes(text)
    let hashBytes = sha256.ComputeHash(bytes)
    BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant()

let getStoredRulesHash (qdrant: QdrantClient) = task {
    try
        let! exists = qdrant.CollectionExistsAsync("lorcana_rules")
        if not exists then
            return None
        else
            let! scrollResult = qdrant.ScrollAsync("lorcana_rules", limit = 1u)
            let points = scrollResult.Result |> Seq.toList
            if points.Length > 0 then
                let firstPoint = points.[0]
                let mutable hashValue = Unchecked.defaultof<Qdrant.Client.Grpc.Value>
                if firstPoint.Payload.TryGetValue("__rules_hash__", &hashValue) then
                    return Some hashValue.StringValue
                else
                    return None
            else
                return None
    with _ ->
        return None
}

let registerIngest (app: WebApplication) =
    // Ingest endpoint - will create collection and generate embeddings per card, upsert to Qdrant
    app.MapPost("/ingest", Func<HttpContext, QdrantClient, IOllamaApiClient, Task>(fun ctx qdrant ollamaApiClient ->
        task {
            let dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "allCards.json")
            if not (File.Exists dataPath) then
                ctx.Response.StatusCode <- 500
                do! ctx.Response.WriteAsync("Data/allCards.json not found. Place LorcanaJSON allCards.json in Data folder.")
            else
            // Read json document
                let txt = File.ReadAllText(dataPath)
                use doc = JsonDocument.Parse(txt)
                let root = doc.RootElement
                // Build a map of set number -> set name (if available)
                let setNameByNumber =
                    let dict = System.Collections.Generic.Dictionary<string,string>()
                    let mutable setsEl = Unchecked.defaultof<JsonElement>
                    if root.TryGetProperty("sets", &setsEl) && setsEl.ValueKind = JsonValueKind.Object then
                        for prop in setsEl.EnumerateObject() do
                            let mutable n = Unchecked.defaultof<JsonElement>
                            let name = if prop.Value.TryGetProperty("name", &n) && n.ValueKind = JsonValueKind.String then n.GetString() else null
                            if not (isNull name) then dict[prop.Name] <- name
                    dict
                // Helper: convert JsonElement to Qdrant.Value recursively
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
                // Extract cards array
                let mutable cardsEl = Unchecked.defaultof<JsonElement>
                if not (root.TryGetProperty("cards", &cardsEl)) || cardsEl.ValueKind <> JsonValueKind.Array then
                    ctx.Response.StatusCode <- 500
                    do! ctx.Response.WriteAsync("Invalid allCards.json format: missing 'cards' array.")
                else
                // Create collection using QdrantClient (gRPC)
                    let vectorParams = Qdrant.Client.Grpc.VectorParams(Size = vectorSize, Distance = Qdrant.Client.Grpc.Distance.Cosine)
                    try
                        do! qdrant.CreateCollectionAsync("lorcana_cards", vectorParams)
                    with
                    | :? Grpc.Core.RpcException as ex when ex.StatusCode = Grpc.Core.StatusCode.AlreadyExists -> ()
                    // Generate embeddings and prepare points
                    let pointsBuffer = System.Collections.Generic.List<Qdrant.Client.Grpc.PointStruct>()
                    let mutable idx = 0
                    for card in cardsEl.EnumerateArray() do
                        let c = ofJson card
                        let inputText = embeddingText c
                        let nameSafe = c.Name |> Option.defaultValue ""
                        try
                            if String.IsNullOrWhiteSpace(inputText) then
                                do! ctx.Response.WriteAsync("Skipping card with no usable text.\n")
                            else
                                let! embRes =
                                    let req = OllamaSharp.Models.EmbedRequest()
                                    req.Model <- embedModel
                                    req.Input <- System.Collections.Generic.List<string>()
                                    let maxLen = 4000
                                    let trimmed = inputText.Trim()
                                    let safeInput = if trimmed.Length > maxLen then trimmed.Substring(0, maxLen) else trimmed
                                    req.Input.Add(safeInput)
                                    ollamaApiClient.EmbedAsync(req)
                                let vecDoubles =
                                    if not (isNull embRes) && not (isNull embRes.Embeddings) && embRes.Embeddings.Count > 0 && not (isNull embRes.Embeddings[0]) then
                                        embRes.Embeddings[0] |> Seq.toArray
                                    else [||]
                                if vecDoubles.Length > 0 then
                                    let vec = vecDoubles |> Array.map float32
                                    let p = toPoint c setNameByNumber vec idx
                                    pointsBuffer.Add(p)
                                    idx <- idx + 1
                        with ex ->
                            do! ctx.Response.WriteAsync $"Embedding request failed for {nameSafe}: {ex.ToString()}\n"
                    if pointsBuffer.Count > 0 then
                        let! _ = qdrant.UpsertAsync("lorcana_cards", pointsBuffer.ToArray())
                        ()
                    do! ctx.Response.WriteAsync $"Ingested {pointsBuffer.Count} cards into Qdrant"
        } :> Task
    )) |> ignore

let registerRules (app: WebApplication) =
    app.MapGet("/rules", Func<RulesProvider.IRulesProvider, IResult>(fun rp ->
        // Trigger lazy load by accessing Text; then decide based on content
        let txt = rp.Text
        if not (String.IsNullOrWhiteSpace txt) then Results.Text(txt, "text/plain", Encoding.UTF8)
        else Results.Problem("Rules PDF not loaded. Place the PDF in the Data folder.")
    )) |> ignore

// (moved to DeckService)

let ingestRulesAsync (qdrant: QdrantClient) (ollamaApiClient: IOllamaApiClient) (rulesProvider: RulesProvider.IRulesProvider) = task {
    // Load full rules text from provider
    let rulesText = rulesProvider.Text
    if String.IsNullOrWhiteSpace rulesText then
        return Error "Rules PDF not loaded. Place the PDF in the Data folder."
    else
        // Compute hash of current rules text
        let currentHash = computeTextHash rulesText
        
        // Check if we need to re-import
        let! storedHash = getStoredRulesHash qdrant
        
        let shouldSkip =
            match storedHash with
            | Some hash when hash = currentHash -> true
            | _ -> false
        
        if shouldSkip then
            return Ok 0  // Return 0 to indicate skip
        else
            // Delete existing collection if it exists
            match storedHash with
            | Some _ | None ->
                let! exists = qdrant.CollectionExistsAsync("lorcana_rules")
                if exists then
                    do! qdrant.DeleteCollectionAsync("lorcana_rules")
            
            // Create collection for rules chunks
            let vectorParams = Qdrant.Client.Grpc.VectorParams(Size = vectorSize, Distance = Qdrant.Client.Grpc.Distance.Cosine)
            do! qdrant.CreateCollectionAsync("lorcana_rules", vectorParams)

            // Chunk rules text into manageable segments
            let maxChunk = 500
            let overlap = 80
            let paras =
                rulesText.Split([|"\n\n"|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (fun s -> s.Length >= 100)
            let chunks =
                paras
                |> Array.collect (fun para ->
                    let p = para
                    let rec go acc i =
                        if i >= p.Length then List.rev acc |> List.toArray
                        else
                            let len = Math.Min(maxChunk, p.Length - i)
                            let chunk = p.Substring(i, len)
                            go (chunk::acc) (i + (maxChunk - overlap))
                    go [] 0)

            // Embed and upsert chunks
            let points = System.Collections.Generic.List<Qdrant.Client.Grpc.PointStruct>()
            let mutable idx = 0UL
            for ch in chunks do
                let req = OllamaSharp.Models.EmbedRequest()
                req.Model <- embedModel
                req.Input <- System.Collections.Generic.List<string>()
                let t = ch.Trim()
                let safe = if t.Length > 4000 then t.Substring(0, 4000) else t
                req.Input.Add(safe)
                try
                    let! emb = ollamaApiClient.EmbedAsync(req)
                    if emb.Embeddings.Count > 0 && not (isNull emb.Embeddings[0]) then
                        let vec = emb.Embeddings[0] |> Seq.toArray |> Array.map float32
                        if vec.Length = int vectorSize then
                            let v = Qdrant.Client.Grpc.Vector()
                            v.Data.AddRange(vec)
                            let vs = Qdrant.Client.Grpc.Vectors()
                            vs.Vector <- v
                            let value = Qdrant.Client.Grpc.Value()
                            value.StringValue <- safe
                            let p = Qdrant.Client.Grpc.PointStruct()
                            p.Id <- Qdrant.Client.Grpc.PointId(Num = idx)
                            p.Vectors <- vs
                            // Payload is a MapField<string, Value>
                            p.Payload.Add("text", value)
                            
                            // Store rules hash in every point for future comparison
                            let hashValue = Qdrant.Client.Grpc.Value()
                            hashValue.StringValue <- currentHash
                            p.Payload.Add("__rules_hash__", hashValue)
                            
                            points.Add(p)
                            idx <- idx + 1UL
                with _ -> ()
            if points.Count > 0 then
                let! _ = qdrant.UpsertAsync("lorcana_rules", points.ToArray())
                ()
            return Ok points.Count
}

let registerIngestRules (app: WebApplication) =
    app.MapPost("/ingest-rules", Func<HttpContext, QdrantClient, IOllamaApiClient, RulesProvider.IRulesProvider, Task>(fun ctx qdrant ollamaApiClient rulesProvider ->
        task {
            let! res = ingestRulesAsync qdrant ollamaApiClient rulesProvider
            match res with
            | Ok count -> do! ctx.Response.WriteAsync($"Ingested {count} rules chunks into Qdrant")
            | Error msg -> ctx.Response.StatusCode <- 500; do! ctx.Response.WriteAsync(msg)
        } :> Task
    )) |> ignore

let registerDeck (app: WebApplication) =
    // Using agentic approach for all deck building (iterative search)
    app.MapPost("/api/deck", Func<HttpContext, IOllamaApiClient, QdrantClient, Microsoft.Extensions.Logging.ILogger<obj>, Task>(fun ctx ollama qdrant logger ->
        task {
            logger.LogInformation("Received /api/deck request (agentic mode)")
            use sr = new StreamReader(ctx.Request.Body, Encoding.UTF8)
            let! body = sr.ReadToEndAsync()
            logger.LogInformation("Request body read, length: {Length}", body.Length)
            try
                let options = JsonSerializerOptions()
                options.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
                let query = JsonSerializer.Deserialize<DeckQuery>(body, options)
                logger.LogInformation("Query deserialized: deckSize={DeckSize}, request={Request}", query.deckSize, query.request)
                
                // Create embedding generator function
                let embeddingGen = Func<string, Task<float32 array>>(fun text -> task {
                    logger.LogDebug("Generating embedding for text of length {Length}", text.Length)
                    let embedReq = OllamaSharp.Models.EmbedRequest()
                    embedReq.Model <- embedModel
                    embedReq.Input <- System.Collections.Generic.List<string>()
                    embedReq.Input.Add(text)
                    let! embedResp = ollama.EmbedAsync(embedReq)
                    logger.LogDebug("Embedding generated")
                    return embedResp.Embeddings |> Seq.head |> Seq.toArray
                })
                
                logger.LogInformation("Starting agentic deck building")
                let! res = DeckBuilder.Api.AgenticDeckService.buildDeckAgentic ollama qdrant embeddingGen query logger
                logger.LogInformation("Agentic deck building completed")
                match res with
                | Ok response ->
                    logger.LogInformation("Agentic deck built successfully with {Count} cards", response.cards.Length)
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(JsonSerializer.Serialize(response, options))
                | Error msg ->
                    logger.LogWarning("Agentic deck building failed: {Message}", msg)
                    ctx.Response.StatusCode <- 500
                    do! ctx.Response.WriteAsync(msg)
            with ex ->
                logger.LogError(ex, "Error processing /api/deck request")
                ctx.Response.StatusCode <- 400
                do! ctx.Response.WriteAsync($"Invalid request body: {ex.Message}")
        } :> Task
    )) |> ignore

let registerRagDeck (app: WebApplication) =
    // RAG-enhanced workflow endpoint disabled temporarily
    // Will be re-enabled after completing RAG workflow design
    ()


let registerForceReimport (app: WebApplication) =
    app.MapPost("/api/admin/force-reimport", Func<HttpContext, Task>(fun ctx ->
        task {
            // This endpoint triggers a Worker restart which forces reimport
            // by touching a special trigger file
            try
                let triggerPath = Path.Combine(AppContext.BaseDirectory, "..", "DeckBuilder.Worker", "Data", ".force_reimport")
                let dir = Path.GetDirectoryName(triggerPath)
                if not (Directory.Exists(dir)) then
                    Directory.CreateDirectory(dir) |> ignore
                File.WriteAllText(triggerPath, DateTime.UtcNow.ToString("o"))
                
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync("""{"status":"ok","message":"Force reimport triggered. The Worker will reimport on next run."}""")
            with ex ->
                ctx.Response.StatusCode <- 500
                do! ctx.Response.WriteAsync($"Failed to trigger reimport: {ex.Message}")
        } :> Task
    )) |> ignore
