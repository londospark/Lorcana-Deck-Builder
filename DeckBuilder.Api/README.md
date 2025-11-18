# DeckBuilder.Api

A clean, modernized ASP.NET Core Web API for building Disney Lorcana decks using AI-powered deck building with RAG (Retrieval-Augmented Generation) and agentic workflows.

## Architecture

This API leverages:
- **Qdrant** for vector similarity search of Lorcana cards
- **Ollama** for LLM-powered deck building decisions and embeddings
- **OpenTelemetry** for observability (traces, metrics, logs)
- **Prometheus** for metrics exposition
- **Aspire** for service orchestration

## Project Structure

### Core Files

| File | Purpose |
|------|---------|
| `Program.fs` | Application entry point, dependency injection, endpoint registration |
| `Endpoints.fs` | HTTP endpoint definitions and handlers |
| `AgenticDeckService.fs` | AI agent-based deck building with iterative card selection |

### Domain Models

| File | Purpose |
|------|---------|
| `ApiModels.fs` | Re-exports shared types from `DeckBuilder.Shared` |
| `Card.fs` | Card data models and Qdrant payload conversion |
| `DeckEndpointLogic.fs` | Business logic for deck constraints and color selection |

### Utilities

| File | Purpose |
|------|---------|
| `QdrantHelpers.fs` | Qdrant-specific helpers for filtering and format legality |
| `DeckHelpers.fs` | Card counting, validation, and formatting utilities |
| `Inkable.fs` | Card data index and normalization from static JSON |
| `RulesProvider.fs` | PDF parsing for Lorcana comprehensive rules |

## API Endpoints

### GET `/rules`
Returns the Lorcana comprehensive rules as plain text (extracted from PDF).

### POST `/ingest-rules`
Manually triggers rules ingestion into Qdrant for RAG. (Auto-run on startup)

### POST `/api/deck`
**Main deck building endpoint**. Uses agentic workflow with iterative search and card selection.

**Request Body:**
```json
{
  "request": "Build an aggressive Ruby/Steel deck",
  "deckSize": 60,
  "selectedColors": ["Ruby", "Steel"],
  "format": "Core"
}
```

**Response:**
```json
{
  "cards": [
    {
      "fullName": "Aladdin - Street Rat",
      "count": 4,
      "inkable": true,
      "cardMarketUrl": "https://...",
      "inkColor": "Ruby"
    }
  ],
  "explanation": "Built 60-card deck in 12 iterations..."
}
```

### POST `/api/admin/force-reimport`
Admin endpoint to trigger card data reimport by the Worker service.

## How Deck Building Works

The agentic deck builder follows this workflow:

1. **Search Phase**: Agent searches Qdrant for cards matching the user's request theme
2. **Color Selection**: Based on search results, picks optimal 1-2 ink colors
3. **Card Selection**: Iteratively adds cards with synergies, respecting:
   - Format legality (Core/Infinity)
   - Color restrictions (1-2 colors)
   - Copy limits (typically 4 per card)
   - Inkable balance (~70-80%)
   - Mana curve (smooth 1-6 cost distribution)
4. **Validation**: Ensures deck meets minimum size before finalizing

## Configuration

Configure via `appsettings.json` or environment variables:
- `ConnectionStrings:qdrant` - Qdrant connection string
- `ConnectionStrings:ollama` - Ollama API endpoint

## Development

### Build
```bash
dotnet build
```

### Run
```bash
dotnet run
```

### Access
- API: http://localhost:5000
- Metrics: http://localhost:5000/metrics
- Health: http://localhost:5000/health (if configured)

## Dependencies

- `.NET 10.0`
- `CommunityToolkit.Aspire.OllamaSharp` - Ollama integration
- `Qdrant.Client` - Vector database client
- `PdfPig` - PDF text extraction
- `FSharp.SystemTextJson` - F# JSON serialization
- OpenTelemetry stack for observability

## Notes

- **Card Ingestion**: Handled by `DeckBuilder.Worker`, not this API
- **Rules PDF**: Place in `Data/` folder (e.g., `*Quick_Start*.pdf`)
- **Format Support**: Core and Infinity formats with date-based rotation
- **Agent Model**: Uses `gemma3:12b` for decision-making
- **Embedding Model**: Uses `nomic-embed-text` (768 dimensions)

## Removed Features

The following have been removed during cleanup:
- ? Old synchronous deck building workflow
- ? `/ingest` endpoint (moved to Worker)
- ? RAG-enhanced deck endpoint (disabled/incomplete)
- ? `DeckService.fs` (replaced by AgenticDeckService)
- ? `FilterBuilders.fs` (unused filter utilities)

## Future Enhancements

- [ ] Re-enable RAG-enhanced workflow with improved design
- [ ] Add health checks and readiness probes
- [ ] Cache frequently used card data in memory
- [ ] Support for more formats/rotation windows
- [ ] Deck validation and recommendations
