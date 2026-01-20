# FoundryLocal Support

## Overview

The Lorcana Deck Builder now supports using Azure AI Foundry Local as an alternative LLM provider to Ollama. FoundryLocal provides OpenAI API-compatible local AI inference with support for tool calling and function calling capabilities.

## Configuration

Configure the LLM provider via `appsettings.json`:

```json
{
  "LLM": {
    "ProviderType": "Ollama",  // or "FoundryLocal"
    "FoundryLocalBaseUrl": "http://localhost:5272",
    "ModelName": "qwen2.5:14b-instruct",
    "EmbeddingModelName": "nomic-embed-text",
    "UseAgenticMode": false  // Feature flag for agentic workflow
  }
}
```

### Configuration Options

- **ProviderType**: Choose between `"Ollama"` (default) or `"FoundryLocal"`
- **FoundryLocalBaseUrl**: Base URL for FoundryLocal service (default: `http://localhost:5272`)
- **ModelName**: Model name for text generation (default: `qwen2.5:14b-instruct`)
- **EmbeddingModelName**: Model name for embeddings (default: `nomic-embed-text`)
- **UseAgenticMode**: Enable agentic workflow with tool calling (default: `false`)

### Environment Variables

You can also configure via environment variables:

```bash
export LLM__ProviderType=FoundryLocal
export LLM__FoundryLocalBaseUrl=http://localhost:5272
export LLM__ModelName=qwen2.5:14b-instruct
export LLM__EmbeddingModelName=nomic-embed-text
export LLM__UseAgenticMode=true
```

## Setting Up FoundryLocal

### Option 1: Using .NET Aspire (Recommended)

FoundryLocal can be managed directly by .NET Aspire as a container resource:

1. **Enable FoundryLocal in AppHost**:
   Edit `DeckBuilder.AppHost/Program.cs` and uncomment the FoundryLocal section:
   ```csharp
   var foundry = builder.AddAzureAIFoundry("foundry")
       .RunAsFoundryLocal();
   var foundryChat = foundry.AddDeployment("chat", "qwen2.5:14b-instruct", "1", "Ollama");
   var foundryEmbed = foundry.AddDeployment("embed", "nomic-embed-text", "1", "Ollama");
   ```

2. **Update API configuration**:
   Edit `DeckBuilder.Api/appsettings.json`:
   ```json
   {
     "LLM": {
       "ProviderType": "FoundryLocal"
     }
   }
   ```

3. **Run with Aspire**:
   ```bash
   aspire run
   ```
   
   Aspire will automatically download, configure, and start FoundryLocal with the specified models.

### Option 2: Manual Installation

If you prefer to run FoundryLocal separately:

1. Install FoundryLocal following the official Microsoft documentation:
   - [What is Foundry Local?](https://learn.microsoft.com/en-us/azure/ai-foundry/foundry-local/what-is-foundry-local?view=foundry-classic)

2. Pull the required models:
   ```bash
   foundry local pull qwen2.5:14b-instruct
   foundry local pull nomic-embed-text
   ```

3. Start FoundryLocal service:
   ```bash
   foundry local start --port 5272
   ```

4. Update `appsettings.json` to use FoundryLocal:
   ```json
   {
     "LLM": {
       "ProviderType": "FoundryLocal"
     }
   }
   ```

5. Run the application:
   ```bash
   aspire run
   ```

## Features

### LLM Provider Abstraction

The `LLMProvider.fs` module provides a unified interface (`ILLMProvider`) for both Ollama and FoundryLocal:

```fsharp
type ILLMProvider =
    abstract member GenerateAsync: prompt:string -> Task<string>
    abstract member GenerateWithToolsAsync: request:GenerateRequest -> Task<GenerateResponse>
    abstract member GenerateEmbeddingAsync: text:string -> Task<float32 array>
```

### Tool Calling Support

FoundryLocal provider supports OpenAI-style tool/function calling:

```fsharp
type ToolDefinition = {
    Name: string
    Description: string
    Parameters: string  // JSON schema as string
}

type GenerateRequest = {
    Messages: ChatMessage list
    Tools: ToolDefinition list option
}
```

### Agentic Workflow

When `UseAgenticMode` is enabled, the deck builder uses the agentic workflow with tool calling for:
- Searching cards with specific criteria
- Validating deck legality
- Building synergies with iterative refinement

## Architecture

### Provider Selection

The LLM provider is selected at startup based on configuration:

1. **Program.fs** reads configuration and creates the appropriate provider
2. Provider is registered as a singleton in DI container
3. Endpoints use the `ILLMProvider` interface for LLM operations

### Ollama Provider

- Uses existing `OllamaSharp` client
- Simple prompt-based generation
- No native tool calling (uses prompt-based approach)

### FoundryLocal Provider

- Uses `Azure.AI.OpenAI` SDK
- OpenAI API-compatible endpoints
- Native tool calling support
- Embeddings via OpenAI embedding client

## Benefits of FoundryLocal

1. **Tool Calling**: Native support for function/tool calling enables more sophisticated agentic workflows
2. **OpenAI Compatibility**: Drop-in replacement for OpenAI API
3. **Local Inference**: All data stays on your machine
4. **GPU Acceleration**: Auto-detects and uses available GPUs
5. **Multiple Models**: Support for various model sizes and types

## Comparison

| Feature | Ollama | FoundryLocal |
|---------|--------|--------------|
| Local Inference | ✅ | ✅ |
| GPU Acceleration | ✅ | ✅ |
| OpenAI API Compatible | ❌ | ✅ |
| Native Tool Calling | ❌ | ✅ |
| Embedding Generation | ✅ | ✅ |
| Ease of Setup | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |

## Troubleshooting

### FoundryLocal Connection Issues

If the API can't connect to FoundryLocal:

1. Verify FoundryLocal is running:
   ```bash
   foundry local status
   ```

2. Check the port matches your configuration (default: 5272)

3. Test the endpoint manually:
   ```bash
   curl http://localhost:5272/v1/models
   ```

### Model Not Found

If you get a "model not found" error:

1. List available models:
   ```bash
   foundry local list
   ```

2. Pull the required model:
   ```bash
   foundry local pull qwen2.5:14b-instruct
   ```

### Switching Back to Ollama

To switch back to Ollama, simply change the configuration:

```json
{
  "LLM": {
    "ProviderType": "Ollama"
  }
}
```

No code changes required!

## Future Enhancements

- [ ] Tool calling examples for card search
- [ ] Agentic workflow improvements
- [ ] Support for custom tool definitions
- [ ] Performance benchmarking
- [ ] Multi-model support (different models for different tasks)
