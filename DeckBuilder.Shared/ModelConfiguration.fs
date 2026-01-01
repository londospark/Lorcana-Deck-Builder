namespace DeckBuilder.Shared

/// Model provider types
type ModelProvider =
    | Local      // Ollama
    | OpenAI
    | Anthropic

/// Configuration for model selection
[<CLIMutable>]
type ModelConfig = {
    provider: ModelProvider
    apiKey: string option
    generationModel: string option  // e.g., "gpt-4", "claude-3-opus", "qwen2.5:14b-instruct"
    embeddingModel: string option   // e.g., "text-embedding-3-small", "nomic-embed-text"
}

/// Default configurations for each provider
module ModelDefaults =
    let local = {
        provider = Local
        apiKey = None
        generationModel = Some "qwen2.5:14b-instruct"
        embeddingModel = Some "nomic-embed-text"
    }
    
    let openAI apiKey = {
        provider = OpenAI
        apiKey = Some apiKey
        generationModel = Some "gpt-4o"
        embeddingModel = Some "text-embedding-3-small"
    }
    
    let anthropic apiKey = {
        provider = Anthropic
        apiKey = Some apiKey
        generationModel = Some "claude-3-5-sonnet-20241022"
        embeddingModel = Some "text-embedding-3-small"  // Anthropic doesn't have embeddings, use OpenAI
    }
