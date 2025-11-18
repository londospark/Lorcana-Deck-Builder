# Aspire Worker for Automatic Data Ingestion

## Problem

The Qdrant vector database collection `lorcana_cards` doesn't exist, causing 404 errors when building decks. Currently, data must be manually ingested via `/ingest` endpoint.

## Solution

Create an **Aspire Background Worker** that automatically:
1. Checks if the `lorcana_cards` collection exists in Qdrant
2. If not, reads `allCards.json` and the rules PDF
3. Generates embeddings using Ollama
4. Populates Qdrant with card data
5. Runs once at startup, then exits successfully

## Implementation Steps

### 1. Create Worker Project

```bash
cd X:\Code\Lorcana-Deck-Builder
dotnet new worker -n DeckBuilder.Worker -lang F#
cd DeckBuilder.Worker
```

### 2. Add Dependencies to `.fsproj`

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
  <PackageReference Include="CommunityToolkit.Aspire.OllamaSharp" Version="9.8.1-beta.414" />
  <PackageReference Include="Qdrant.Client" Version="1.15.1" />
  <PackageReference Include="Aspire.Qdrant.Client" Version="13.0.0-preview.1.25524.7" />
  <PackageReference Include="PdfPig" Version="0.1.11" />
  <PackageReference Include="FSharp.SystemTextJson" Version="1.3.13" />
</ItemGroup>

<ItemGroup>
  <ProjectReference Include="..\DeckBuilder.Api\DeckBuilder.Api.fsproj" />
</ItemGroup>

<ItemGroup>
  <None Include="Data\allCards.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="Data\Shimmering-Skies_Quick_Start_Book_EN.pdf">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

### 3. Copy Data Files

```bash
copy ..\DeckBuilder.Api\Data\allCards.json Data\
copy "..\DeckBuilder.Api\Data\Shimmering-Skies_Quick_Start_Book_EN.pdf" Data\
```

### 4. Create `Worker.fs`

```fsharp
module DeckBuilder.Worker.Worker

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Qdrant.Client
open OllamaSharp
// Reuse ingestion logic from API
open System.IO
open System.Text.Json

type DataIngestionWorker(
    logger: ILogger<DataIngestionWorker>,
    qdrant: QdrantClient,
    ollama: IOllamaApiClient,
    hostApplicationLifetime: IHostApplicationLifetime) =
    
    interface IHostedService with
        member _.StartAsync(cancellationToken: CancellationToken) = task {
            try
                logger.LogInformation("Checking if lorcana_cards collection exists...")
                
                let collectionName = "lorcana_cards"
                let! exists = qdrant.CollectionExistsAsync(collectionName, cancellationToken)
                
                if exists then
                    logger.LogInformation("Collection already exists. Skipping ingestion.")
                else
                    logger.LogInformation("Collection not found. Starting data ingestion...")
                    
                    // Call the ingestion logic (refactor from Endpoints.fs)
                    let dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "allCards.json")
                    if not (File.Exists dataPath) then
                        logger.LogError("allCards.json not found at {Path}", dataPath)
                    else
                        logger.LogInformation("Reading card data from {Path}...", dataPath)
                        // Implement ingestion logic here (copy from Endpoints.fs)
                        logger.LogInformation("Data ingestion completed successfully!")
                
                // Stop the application after ingestion
                hostApplicationLifetime.StopApplication()
                
            with ex ->
                logger.LogError(ex, "Error during data ingestion")
                hostApplicationLifetime.StopApplication()
        }
        
        member _.StopAsync(cancellationToken: CancellationToken) =
            logger.LogInformation("Worker stopping...")
            Task.CompletedTask
```

### 5. Create `Program.fs`

```fsharp
module DeckBuilder.Worker.Program

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

[<EntryPoint>]
let main args =
    let builder = Host.CreateApplicationBuilder(args)
    
    // Add Aspire service defaults
    builder.AddServiceDefaults() |> ignore
    
    // Register Qdrant and Ollama clients
    builder.AddQdrantClient("qdrant") |> ignore
    let ollama = builder.AddOllamaApiClient("ollama")
    ollama.AddEmbeddingGenerator() |> ignore
    
    // Register the worker
    builder.Services.AddHostedService<Worker.DataIngestionWorker>() |> ignore
    
    let host = builder.Build()
    host.Run()
    0
```

### 6. Update `DeckBuilder.AppHost/Program.cs`

```csharp
// Add worker before API
var worker = builder.AddProject<Projects.DeckBuilder_Worker>("data-worker")
    .WithReference(ollama)
    .WithReference(nomic)  
    .WithReference(qdrant)
    .WaitFor(qdrant)
    .WaitFor(ollama);

// Make API wait for worker to complete
var deckApi = builder.AddProject<Projects.DeckBuilder_Api>("deck-api")
    .WithReference(ollama)
    .WithReference(qwen)
    .WithReference(nomic)
    .WithReference(qdrant)
    .WaitFor(worker)  // Wait for data ingestion
    .WithExternalHttpEndpoints();
```

## Alternative: Simpler Approach

Since the ingestion logic is complex, a simpler approach is to:

1. Make the API check if collection exists on startup
2. If not, trigger ingestion automatically
3. Add a startup probe/health check

### Update API `Program.fs`

```fsharp
// After app.Build(), before app.Run()
task {
    let qdrant = app.Services.GetRequiredService<QdrantClient>()
    let! exists = qdrant.CollectionExistsAsync("lorcana_cards")
    if not exists then
        let logger = app.Services.GetRequiredService<ILogger<_>>()
        logger.LogInformation("Collection not found. Triggering auto-ingestion...")
        // Call ingestion logic here
}.Wait()

app.Run()
```

## Recommendation

Use the **API auto-ingestion approach** since:
- ✅ Simpler - no new project needed
- ✅ Runs on API startup automatically  
- ✅ Checks collection existence each time
- ✅ Reuses existing ingestion code

I can implement either approach - which would you prefer?

1. **Full Worker Service** (separate project, more Aspire-native)
2. **API Auto-Ingestion** (simpler, runs in existing API)

Let me know and I'll implement it!
