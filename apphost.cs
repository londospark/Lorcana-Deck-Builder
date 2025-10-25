#:sdk Aspire.AppHost.Sdk@13.0.0-preview.1.25524.7

#:package Aspire.Hosting.AppHost@13.0.0-preview.1.25524.7
#:package CommunityToolkit.Aspire.Hosting.Ollama@9.8.1-beta.413
#:package Aspire.Hosting.Qdrant@13.0.0-preview.1.25524.7
#:project ./DeckBuilder.Api
#:project ./DeckBuilder.Ui

var builder = DistributedApplication.CreateBuilder(args);

// Ollama container
var ollama = builder.AddOllama("ollama").WithOpenWebUI().WithDataVolume().WithContainerRuntimeArgs("--gpus=all");

var llama3 = ollama.AddModel("llama3");
var allMinilm = ollama.AddModel("all-minilm");

var qdrant = builder.AddQdrant("qdrant")
                    .WithLifetime(ContainerLifetime.Persistent);

// DeckBuilder API (F# project)
var deckApi = builder.AddProject<Projects.DeckBuilder_Api>("deck-api")
    .WithReference(ollama)
    .WithReference(llama3)
    .WithReference(allMinilm)
    .WithReference(qdrant)
    .WithExternalHttpEndpoints();

var deckUi = builder.AddProject<Projects.DeckBuilder_Ui>("deck-ui").WithReference(deckApi).WithExternalHttpEndpoints();

builder.Build().Run();

