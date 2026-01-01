module DeckBuilder.Api.AnthropicProvider

open System
open System.Threading.Tasks
open DeckBuilder.Api.LlmAbstraction

// Simplified Anthropic provider - will implement later when needed
type AnthropicLlmService(apiKey: string, modelName: string) =
    interface ILlmService with
        member _.GenerateAsync(prompt: string) = task {
            // TODO: Implement Anthropic API calls
            return failwith "Anthropic provider not yet implemented. Use OpenAI or Local (Ollama) provider."
        }
        
        member _.GenerateStreamAsync(prompt: string) =
            raise (NotImplementedException("Anthropic provider not yet implemented"))

type AnthropicEmbeddingService() =
    interface IEmbeddingService with
        member _.GenerateEmbeddingAsync(text: string) = task {
            return failwith "Anthropic does not provide embedding services. Use OpenAI embeddings with Anthropic generation."
        }
