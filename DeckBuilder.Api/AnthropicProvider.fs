module DeckBuilder.Api.AnthropicProvider

open System
open System.Threading.Tasks
open Anthropic.SDK
open Anthropic.SDK.Messaging
open DeckBuilder.Api.LlmAbstraction

type AnthropicLlmService(apiKey: string, modelName: string) =
    let client = AnthropicClient(apiKey)
    
    interface ILlmService with
        member _.GenerateAsync(prompt: string) = task {
            let messages = [
                Message(Role = RoleType.User, Content = prompt)
            ]
            
            let! response = client.Messages.GetClaudeMessageAsync(
                messages,
                modelName,
                maxTokens = 4096
            )
            
            return 
                if response.Content.Count > 0 then
                    response.Content.[0].Text
                else
                    ""
        }
        
        member _.GenerateStreamAsync(prompt: string) =
            asyncSeq {
                let messages = [
                    Message(Role = RoleType.User, Content = prompt)
                ]
                
                let stream = client.Messages.StreamClaudeMessageAsync(
                    messages,
                    modelName,
                    maxTokens = 4096
                )
                
                let e = stream.GetAsyncEnumerator()
                
                let rec loop () = asyncSeq {
                    let! moved = e.MoveNextAsync().AsTask() |> Async.AwaitTask
                    if moved then
                        let update = e.Current
                        match update with
                        | :? ContentBlockDelta as delta ->
                            if not (isNull delta.Delta) && not (String.IsNullOrEmpty delta.Delta.Text) then
                                yield delta.Delta.Text
                        | _ -> ()
                        yield! loop ()
                    else
                        do! e.DisposeAsync().AsTask() |> Async.AwaitTask
                }
                
                yield! loop ()
            }

/// Anthropic doesn't provide embeddings, so we fall back to OpenAI for embeddings
/// when using Anthropic for generation
type AnthropicEmbeddingService() =
    interface IEmbeddingService with
        member _.GenerateEmbeddingAsync(text: string) = task {
            // This should never be called - we use OpenAI embeddings with Anthropic
            return failwith "Anthropic does not provide embedding services. Use OpenAI embeddings with Anthropic generation."
        }
