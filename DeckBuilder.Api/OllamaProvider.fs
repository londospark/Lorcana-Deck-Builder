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
            // Not implemented for now - just throw
            raise (NotImplementedException("Streaming not implemented for Ollama provider"))

type OllamaEmbeddingService(client: IOllamaApiClient, modelName: string) =
    interface IEmbeddingService with
        member _.GenerateEmbeddingAsync(text: string) = task {
            let embedReq = OllamaSharp.Models.EmbedRequest()
            embedReq.Model <- modelName
            embedReq.Input <- System.Collections.Generic.List<string>()
            embedReq.Input.Add(text)
            let! embedResp = client.EmbedAsync(embedReq)
            return embedResp.Embeddings |> Seq.head |> Seq.toArray
        }
