module DeckBuilder.Api.LlmAbstraction

open System.Threading.Tasks

/// Abstraction for Large Language Model services
type ILlmService =
    abstract member GenerateAsync: prompt:string -> Task<string>
    abstract member GenerateStreamAsync: prompt:string -> System.Collections.Generic.IAsyncEnumerable<string>

/// Abstraction for embedding generation services
type IEmbeddingService =
    abstract member GenerateEmbeddingAsync: text:string -> Task<float32 array>
