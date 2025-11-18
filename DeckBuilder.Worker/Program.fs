module DeckBuilder.Worker.Program

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

[<EntryPoint>]
let main args =
    let builder = Host.CreateApplicationBuilder(args)
    
    // Register Qdrant client via Aspire integration
    builder.AddQdrantClient("qdrant") |> ignore
    
    // Register Ollama client and embedding generator
    let ollama = builder.AddOllamaApiClient("ollama")
    ollama.AddEmbeddingGenerator() |> ignore
    
    // Register the worker
    builder.Services.AddHostedService<Worker.DataIngestionWorker>() |> ignore
    
    // Build and run
    let host = builder.Build()
    host.Run()
    0
