module DeckBuilder.Api.OpenAIProvider

open System
open System.Text
open System.Threading.Tasks
open OpenAI.Chat
open OpenAI.Embeddings
open DeckBuilder.Api.LlmAbstraction

type OpenAILlmService(apiKey: string, modelName: string) =
    let client = ChatClient(modelName, apiKey)
    
    interface ILlmService with
        member _.GenerateAsync(prompt: string) = task {
            let! completion = client.CompleteChatAsync(prompt)
            let response = completion.Value
            return 
                if response.Content.Count > 0 then
                    response.Content.[0].Text
                else
                    ""
        }
        
        member _.GenerateStreamAsync(prompt: string) =
            // Not implemented for now - just throw
            raise (NotImplementedException("Streaming not implemented for OpenAI provider"))

type OpenAIEmbeddingService(apiKey: string, modelName: string) =
    let client = EmbeddingClient(modelName, apiKey)
    
    interface IEmbeddingService with
        member _.GenerateEmbeddingAsync(text: string) = task {
            let! response = client.GenerateEmbeddingAsync(text)
            let embedding = response.Value
            return embedding.ToFloats().ToArray()
        }
