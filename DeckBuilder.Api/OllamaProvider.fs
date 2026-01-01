module DeckBuilder.Api.OllamaProvider

open System
open System.Text
open System.Threading.Tasks
open OllamaSharp
open DeckBuilder.Api.LlmAbstraction

type OllamaLlmService(client: IOllamaApiClient, modelName: string) =
    interface ILlmService with
        member _.GenerateAsync(prompt: string) = task {
            let request = OllamaSharp.Models.GenerateRequest()
            request.Model <- modelName
            request.Prompt <- prompt
            
            let sb = StringBuilder()
            let stream = client.GenerateAsync(request)
            let e = stream.GetAsyncEnumerator()
            
            let rec loop () = task {
                let! moved = e.MoveNextAsync().AsTask()
                if moved then
                    let chunk = e.Current
                    if not (isNull chunk) && not (String.IsNullOrWhiteSpace chunk.Response) then
                        sb.Append(chunk.Response) |> ignore
                    return! loop()
                else
                    return sb.ToString()
            }
            
            return! loop()
        }
        
        member _.GenerateStreamAsync(prompt: string) = 
            let request = OllamaSharp.Models.GenerateRequest()
            request.Model <- modelName
            request.Prompt <- prompt
            
            let stream = client.GenerateAsync(request)
            
            AsyncSeq.ofAsyncEnum {
                let e = stream.GetAsyncEnumerator()
                let rec loop () = task {
                    let! moved = e.MoveNextAsync().AsTask()
                    if moved then
                        let chunk = e.Current
                        if not (isNull chunk) && not (String.IsNullOrWhiteSpace chunk.Response) then
                            yield chunk.Response
                        yield! loop()
                }
                yield! loop()
            }

type OllamaEmbeddingService(client: IOllamaApiClient, modelName: string) =
    interface IEmbeddingService with
        member _.GenerateEmbeddingAsync(text: string) = task {
            let! response = client.GetEmbeddings(modelName, text)
            return response.ToArray()
        }

/// Helper for async sequence conversion
module AsyncSeq =
    let ofAsyncEnum (source: System.Collections.Generic.IAsyncEnumerable<'T>) =
        let rec loop (e: System.Collections.Generic.IAsyncEnumerator<'T>) = asyncSeq {
            let! moved = e.MoveNextAsync().AsTask() |> Async.AwaitTask
            if moved then
                yield e.Current
                yield! loop e
            else
                do! e.DisposeAsync().AsTask() |> Async.AwaitTask
        }
        asyncSeq {
            let e = source.GetAsyncEnumerator()
            yield! loop e
        }
