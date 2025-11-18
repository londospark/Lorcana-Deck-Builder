#:sdk Aspire.AppHost.Sdk@13.0.0

#:package Aspire.Hosting.AppHost@13.0.0
#:package CommunityToolkit.Aspire.Hosting.Ollama@9.9.0
#:package Aspire.Hosting.Qdrant@13.0.0
#:project ./DeckBuilder.Api
#:project ./DeckBuilder.Server
#:project ./DeckBuilder.Worker

var builder = DistributedApplication.CreateBuilder(args);

// Ollama container
var ollama = builder.AddOllama("ollama").WithOpenWebUI().WithDataVolume().WithContainerRuntimeArgs("--gpus=all");

var gemma3 = ollama.AddModel("gemma3:12b");
var nomicEmbed = ollama.AddModel("nomic-embed-text");

var qdrant = builder.AddQdrant("qdrant")
                    .WithLifetime(ContainerLifetime.Persistent);

// Data ingestion worker - runs first to populate Qdrant
var worker = builder.AddProject<Projects.DeckBuilder_Worker>("data-worker")
    .WithReference(ollama)
    .WithReference(nomicEmbed)
    .WithReference(qdrant)
    .WithHttpEndpoint(name: "force-reimport", targetPort: 8080)
    .WithExternalHttpEndpoints();

// DeckBuilder API (F# project) - waits for worker to complete
var deckApi = builder.AddProject<Projects.DeckBuilder_Api>("deck-api")
    .WithReference(ollama)
    .WithReference(gemma3)
    .WithReference(nomicEmbed)
    .WithReference(qdrant)
    .WaitForCompletion(worker);

// Server host for Blazor WASM - uses service discovery to proxy to API
var server = builder.AddProject<Projects.DeckBuilder_Server>("server")
    .WithReference(deckApi) // Service discovery for API
    .WithExternalHttpEndpoints();

builder.Build().Run();


