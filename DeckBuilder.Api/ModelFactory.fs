module DeckBuilder.Api.ModelFactory

open System
open Microsoft.Extensions.Logging
open DeckBuilder.Shared
open DeckBuilder.Api.LlmAbstraction
open DeckBuilder.Api.OllamaProvider
open DeckBuilder.Api.OpenAIProvider
open DeckBuilder.Api.AnthropicProvider

/// Create LLM service based on model configuration
let createLlmService 
    (config: ModelConfig) 
    (ollamaClient: OllamaSharp.IOllamaApiClient option)
    (logger: ILogger) 
    : ILlmService =
    
    match config.provider with
    | ModelProvider.Local ->
        match ollamaClient with
        | Some client ->
            let modelName = config.generationModel |> Option.defaultValue "qwen2.5:14b-instruct"
            logger.LogInformation("Using Ollama (local) model: {Model}", modelName)
            OllamaLlmService(client, modelName) :> ILlmService
        | None ->
            failwith "Ollama client not available for local model provider"
    
    | ModelProvider.OpenAI ->
        match config.apiKey with
        | Some apiKey ->
            let modelName = config.generationModel |> Option.defaultValue "gpt-4o"
            logger.LogInformation("Using OpenAI model: {Model}", modelName)
            OpenAILlmService(apiKey, modelName) :> ILlmService
        | None ->
            failwith "API key required for OpenAI provider"
    
    | ModelProvider.Anthropic ->
        match config.apiKey with
        | Some apiKey ->
            let modelName = config.generationModel |> Option.defaultValue "claude-3-5-sonnet-20241022"
            logger.LogInformation("Using Anthropic model: {Model}", modelName)
            AnthropicLlmService(apiKey, modelName) :> ILlmService
        | None ->
            failwith "API key required for Anthropic provider"

/// Create embedding service based on model configuration
let createEmbeddingService
    (config: ModelConfig)
    (ollamaClient: OllamaSharp.IOllamaApiClient option)
    (logger: ILogger)
    : IEmbeddingService =
    
    match config.provider with
    | ModelProvider.Local ->
        match ollamaClient with
        | Some client ->
            let modelName = config.embeddingModel |> Option.defaultValue "nomic-embed-text"
            logger.LogInformation("Using Ollama (local) embedding model: {Model}", modelName)
            OllamaEmbeddingService(client, modelName) :> IEmbeddingService
        | None ->
            failwith "Ollama client not available for local embedding provider"
    
    | ModelProvider.OpenAI ->
        match config.apiKey with
        | Some apiKey ->
            let modelName = config.embeddingModel |> Option.defaultValue "text-embedding-3-small"
            logger.LogInformation("Using OpenAI embedding model: {Model}", modelName)
            OpenAIEmbeddingService(apiKey, modelName) :> IEmbeddingService
        | None ->
            failwith "API key required for OpenAI embedding provider"
    
    | ModelProvider.Anthropic ->
        // Anthropic doesn't provide embeddings, fall back to OpenAI
        match config.apiKey with
        | Some apiKey ->
            let modelName = config.embeddingModel |> Option.defaultValue "text-embedding-3-small"
            logger.LogWarning("Anthropic does not provide embeddings, using OpenAI embedding model: {Model}", modelName)
            OpenAIEmbeddingService(apiKey, modelName) :> IEmbeddingService
        | None ->
            failwith "API key required for embedding provider when using Anthropic"
