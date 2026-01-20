module DeckBuilder.Api.LLMProvider

open System
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open OllamaSharp

// ===== LLM PROVIDER ABSTRACTION =====

type LLMProviderType =
    | Ollama
    | FoundryLocal

type LLMConfig = {
    ProviderType: LLMProviderType
    BaseUrl: string option
    ModelName: string
    EmbeddingModelName: string option
}

type ChatMessage = {
    Role: string
    Content: string
}

type ToolDefinition = {
    Name: string
    Description: string
    Parameters: string  // JSON schema as string
}

type GenerateRequest = {
    Messages: ChatMessage list
    Tools: ToolDefinition list option
}

type GenerateResponse = {
    Content: string
    ToolCalls: (string * string) list option  // (toolName, argumentsJson)
}

type ILLMProvider =
    abstract member GenerateAsync: prompt:string -> Task<string>
    abstract member GenerateWithToolsAsync: request:GenerateRequest -> Task<GenerateResponse>
    abstract member GenerateEmbeddingAsync: text:string -> Task<float32 array>

// ===== OLLAMA PROVIDER =====

type OllamaProvider(ollamaClient: IOllamaApiClient, modelName: string, embeddingModel: string, logger: ILogger) =
    interface ILLMProvider with
        member _.GenerateAsync(prompt: string) = task {
            let genReq = OllamaSharp.Models.GenerateRequest()
            genReq.Model <- modelName
            genReq.Prompt <- prompt
            
            logger.LogDebug("Calling Ollama GenerateAsync with model: {Model}", modelName)
            let stream = ollamaClient.GenerateAsync(genReq)
            let sb = StringBuilder()
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
        
        member _.GenerateWithToolsAsync(request: GenerateRequest) = task {
            // Ollama doesn't natively support tool calling in the same way as OpenAI
            // For now, we'll use a prompt-based approach
            logger.LogWarning("Ollama provider does not support native tool calling. Using prompt-based approach.")
            
            let promptBuilder = StringBuilder()
            for msg in request.Messages do
                promptBuilder.AppendLine(sprintf "%s: %s" msg.Role msg.Content) |> ignore
            
            match request.Tools with
            | Some tools when tools.Length > 0 ->
                promptBuilder.AppendLine() |> ignore
                promptBuilder.AppendLine("Available tools:") |> ignore
                for tool in tools do
                    promptBuilder.AppendLine(sprintf "- %s: %s" tool.Name tool.Description) |> ignore
                    promptBuilder.AppendLine(sprintf "  Parameters: %s" tool.Parameters) |> ignore
            | _ -> ()
            
            let genReq = OllamaSharp.Models.GenerateRequest()
            genReq.Model <- modelName
            genReq.Prompt <- promptBuilder.ToString()
            
            let stream = ollamaClient.GenerateAsync(genReq)
            let sb = StringBuilder()
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
            let! content = loop()
            
            return {
                Content = content
                ToolCalls = None  // Ollama doesn't return structured tool calls
            }
        }
        
        member _.GenerateEmbeddingAsync(text: string) = task {
            logger.LogDebug("Generating embedding with model: {Model}", embeddingModel)
            let embedReq = OllamaSharp.Models.EmbedRequest()
            embedReq.Model <- embeddingModel
            embedReq.Input <- System.Collections.Generic.List<string>()
            embedReq.Input.Add(text)
            let! embedResp = ollamaClient.EmbedAsync(embedReq)
            return embedResp.Embeddings |> Seq.head |> Seq.toArray
        }

// ===== FOUNDRY LOCAL PROVIDER =====

open Azure.AI.OpenAI
open Azure
open System.Collections.Generic

type FoundryLocalProvider(baseUrl: string, modelName: string, embeddingModel: string, logger: ILogger) =
    let createClient() =
        let endpoint = Uri(baseUrl)
        // FoundryLocal doesn't need real API key but OpenAI client requires one
        let credential = AzureKeyCredential("sk-local")
        let options = AzureOpenAIClientOptions()
        new AzureOpenAIClient(endpoint, credential, options)
    
    interface ILLMProvider with
        member _.GenerateAsync(prompt: string) = task {
            let client = createClient()
            let chatClient = client.GetChatClient(modelName)
            
            logger.LogDebug("Calling FoundryLocal chat completion with model: {Model}", modelName)
            
            let messages = List<OpenAI.Chat.ChatMessage>()
            messages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(prompt))
            
            let! completion = chatClient.CompleteChatAsync(messages)
            
            let response = completion.Value
            return 
                if response.Content.Count > 0 then
                    response.Content.[0].Text
                else
                    ""
        }
        
        member _.GenerateWithToolsAsync(request: GenerateRequest) = task {
            let client = createClient()
            let chatClient = client.GetChatClient(modelName)
            
            logger.LogDebug("Calling FoundryLocal with tools, model: {Model}, tools count: {Count}", 
                modelName, (request.Tools |> Option.map List.length |> Option.defaultValue 0))
            
            let messages = List<OpenAI.Chat.ChatMessage>()
            for msg in request.Messages do
                if msg.Role.ToLower() = "user" then
                    messages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(msg.Content))
                elif msg.Role.ToLower() = "assistant" then
                    messages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(msg.Content))
                else
                    messages.Add(OpenAI.Chat.ChatMessage.CreateSystemMessage(msg.Content))
            
            let options = OpenAI.Chat.ChatCompletionOptions()
            
            // Add tools if provided
            match request.Tools with
            | Some tools when tools.Length > 0 ->
                for tool in tools do
                    try
                        let chatTool = OpenAI.Chat.ChatTool.CreateFunctionTool(tool.Name, tool.Description)
                        // Note: Parameters schema would need to be parsed from JSON string
                        // For now, we'll use a simplified approach
                        options.Tools.Add(chatTool)
                    with ex ->
                        logger.LogWarning("Failed to add tool {ToolName}: {Error}", tool.Name, ex.Message)
            | _ -> ()
            
            let! completion = chatClient.CompleteChatAsync(messages, options)
            let response = completion.Value
            
            // Check for tool calls
            let toolCalls =
                if response.ToolCalls.Count > 0 then
                    let calls =
                        response.ToolCalls
                        |> Seq.choose (fun (tc: OpenAI.Chat.ChatToolCall) ->
                            // Extract function name and arguments
                            Some (tc.FunctionName, tc.FunctionArguments.ToString())
                        )
                        |> Seq.toList
                    Some calls
                else
                    None
            
            let content =
                if response.Content.Count > 0 then
                    response.Content.[0].Text
                else
                    ""
            
            return {
                Content = content
                ToolCalls = toolCalls
            }
        }
        
        member _.GenerateEmbeddingAsync(text: string) = task {
            let client = createClient()
            let embeddingClient = client.GetEmbeddingClient(embeddingModel)
            
            logger.LogDebug("Generating embedding with FoundryLocal, model: {Model}", embeddingModel)
            
            let! embeddings = embeddingClient.GenerateEmbeddingAsync(text)
            
            return 
                embeddings.Value.ToFloats().ToArray()
        }

// ===== PROVIDER FACTORY =====

let createProvider (config: LLMConfig) (ollamaClient: IOllamaApiClient option) (logger: ILogger) : ILLMProvider =
    match config.ProviderType with
    | Ollama ->
        match ollamaClient with
        | Some client ->
            let embeddingModel = config.EmbeddingModelName |> Option.defaultValue "nomic-embed-text"
            OllamaProvider(client, config.ModelName, embeddingModel, logger) :> ILLMProvider
        | None ->
            failwith "Ollama client required for Ollama provider type"
    
    | FoundryLocal ->
        let baseUrl = config.BaseUrl |> Option.defaultValue "http://localhost:5272"
        let embeddingModel = config.EmbeddingModelName |> Option.defaultValue "nomic-embed-text"
        FoundryLocalProvider(baseUrl, config.ModelName, embeddingModel, logger) :> ILLMProvider
