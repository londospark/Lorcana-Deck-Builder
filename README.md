Lorcana Aspire - Filled Template (F# + FsBolero)
===================================================

Note: Documentation has moved to the `docs/` folder. See `docs/README.md` for the full index.

Projects in this solution:
- DeckBuilder.AppHost: Aspire AppHost orchestrating Ollama and Qdrant containers; wires `DeckBuilder.Api`, `DeckBuilder.Ui`, `DeckBuilder.Server`, and `DeckBuilder.Worker`.
- DeckBuilder.Api: F# minimal API for deck building and ingestion; uses embeddings via Ollama and stores vectors in Qdrant.
- DeckBuilder.Server: C# hosting/proxy for the UI/API (reverse proxy/front door).
- DeckBuilder.Ui: FsBolero frontend (F#) that calls `/api/deck` and renders results.
- DeckBuilder.Shared: F# shared models and DTOs used by API and UI.
- DeckBuilder.Worker: Background ingestion worker for initial card/rules population into Qdrant.

## Documentation

- Full docs index: `docs/README.md`
- Key topics: Agentic + RAG, UI/Styling, Operations, Analysis, and Session Summaries.

## Running the Application

```bash
aspire run
```

**That's it!** `aspire run` automatically builds all projects before starting.

## Important Notes

- **Bolero Compatibility**: The UI project targets .NET 8 (Bolero doesn't support .NET 10 yet)
- **Automatic Building**: `aspire run` detects changes and rebuilds automatically
- Replace Data/allCards.json with the full LorcanaJSON dump
- Pull Ollama models locally (GPU-capable) using Ollama CLI:
  - Embedding model: `nomic-embed-text` (768 dims)
  - Generation model: `qwen2.5:14b-instruct`
- The AppHost will automatically start Ollama and Qdrant containers
- Adjust model names in code if you use different model tags

## Troubleshooting

If you encounter console errors, see [docs/BOLERO_FIX.md](docs/BOLERO_FIX.md) for detailed troubleshooting.

### Manual Build (if needed)
```bash
dotnet build
aspire run
```
