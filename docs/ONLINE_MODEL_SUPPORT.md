# Online Model Support for Lorcana Deck Builder

## Overview

The Lorcana Deck Builder now supports using online AI models (OpenAI, Anthropic) in addition to local Ollama models. This enables more powerful deck building with enhanced reasoning capabilities.

## Architecture

### Abstraction Layer

The implementation uses an abstraction layer with two core interfaces:

- **ILlmService**: Large Language Model generation
- **IEmbeddingService**: Vector embedding generation

### Supported Providers

1. **Local (Ollama)** - Default, runs locally
   - Generation: `qwen2.5:14b-instruct` (or custom)
   - Embeddings: `nomic-embed-text` (or custom)

2. **OpenAI** - Cloud-based
   - Generation: `gpt-4o` (or `gpt-4`, `gpt-3.5-turbo`, etc.)
   - Embeddings: `text-embedding-3-small` (or custom)

3. **Anthropic** - Cloud-based (stub implementation)
   - Generation: `claude-3-5-sonnet-20241022` (planned)
   - Embeddings: Uses OpenAI (Anthropic doesn't provide embeddings)

## API Endpoints

### New Endpoint: `/api/deck/online`

Accepts a `DeckQuery` with optional `modelConfig`:

```json
{
  "request": "aggressive red/steel deck with lots of rush characters",
  "deckSize": 60,
  "selectedColors": null,
  "format": {"Case": "Core"},
  "modelConfig": {
    "provider": "OpenAI",
    "apiKey": "sk-...",
    "generationModel": "gpt-4o",
    "embeddingModel": "text-embedding-3-small"
  }
}
```

### Existing Endpoint: `/api/deck`

Uses the local Ollama model (deterministic pipeline). No breaking changes.

## Configuration

### ModelConfiguration Fields

```fsharp
type ModelProvider =
    | Local      // Ollama
    | OpenAI
    | Anthropic

type ModelConfig = {
    provider: ModelProvider
    apiKey: string option           // Required for OpenAI/Anthropic
    generationModel: string option  // Model name (uses defaults if omitted)
    embeddingModel: string option   // Model name (uses defaults if omitted)
}
```

### Defaults

```fsharp
// Local (Ollama)
{
    provider = Local
    apiKey = None
    generationModel = Some "qwen2.5:14b-instruct"
    embeddingModel = Some "nomic-embed-text"
}

// OpenAI
{
    provider = OpenAI
    apiKey = Some "sk-..."
    generationModel = Some "gpt-4o"
    embeddingModel = Some "text-embedding-3-small"
}
```

## Usage Examples

### Using Local Ollama Model

```bash
curl -X POST http://localhost:5001/api/deck/online \
  -H "Content-Type: application/json" \
  -d '{
    "request": "Magic Broom tribal deck",
    "deckSize": 60,
    "format": {"Case": "Core"}
  }'
```

(If `modelConfig` is omitted, it defaults to local Ollama)

### Using OpenAI

```bash
curl -X POST http://localhost:5001/api/deck/online \
  -H "Content-Type: application/json" \
  -d '{
    "request": "Control deck with lots of removal",
    "deckSize": 60,
    "format": {"Case": "Infinity"},
    "modelConfig": {
      "provider": "OpenAI",
      "apiKey": "sk-YOUR_OPENAI_API_KEY",
      "generationModel": "gpt-4o",
      "embeddingModel": "text-embedding-3-small"
    }
  }'
```

### Using OpenAI with Custom Models

```bash
curl -X POST http://localhost:5001/api/deck/online \
  -H "Content-Type: application/json" \
  -d '{
    "request": "Fast lore-generating deck",
    "deckSize": 60,
    "format": {"Case": "Core"},
    "modelConfig": {
      "provider": "OpenAI",
      "apiKey": "sk-YOUR_OPENAI_API_KEY",
      "generationModel": "gpt-4",
      "embeddingModel": "text-embedding-ada-002"
    }
  }'
```

## Security Considerations

**⚠️ Important**: API keys are sent in the request body. In production:

1. **Never commit API keys** to source control
2. **Use environment variables** for API keys on the server side
3. **Implement API key storage** in a secure backend service
4. **Add authentication** to the endpoints
5. **Use HTTPS** in production

## Future Enhancements

- [ ] UI support for provider selection
- [ ] Server-side API key management (avoid sending keys from client)
- [ ] Anthropic provider full implementation
- [ ] Support for additional providers (Azure OpenAI, Google Gemini, etc.)
- [ ] Caching of embeddings to reduce API costs
- [ ] Rate limiting and cost tracking

## Files Changed

- `DeckBuilder.Shared/ModelConfiguration.fs` - Configuration models
- `DeckBuilder.Api/ILlmService.fs` - Abstraction interfaces
- `DeckBuilder.Api/OllamaProvider.fs` - Local Ollama implementation
- `DeckBuilder.Api/OpenAIProvider.fs` - OpenAI implementation
- `DeckBuilder.Api/AnthropicProvider.fs` - Anthropic stub
- `DeckBuilder.Api/ModelFactory.fs` - Provider factory
- `DeckBuilder.Api/OnlineModelDeckService.fs` - Deck building service
- `DeckBuilder.Api/Endpoints.fs` - New `/api/deck/online` endpoint
- `DeckBuilder.Api/Program.fs` - Endpoint registration

## Testing

To test locally:

1. Start the application: `aspire run`
2. Wait for services to start (Ollama, Qdrant, API)
3. Use curl or Postman to POST to `/api/deck/online`
4. Check logs for any errors

For OpenAI testing, you'll need a valid API key from https://platform.openai.com/api-keys
