Lorcana Aspire - Filled Template (F# + FsBolero)
===================================================

Note: Documentation has moved to the `docs/` folder. See `docs/README.md` for the full index.

This template includes:
- AppHost: wires Ollama (container), Qdrant (container), DeckBuilder.Api
- DeckBuilder.Api: F# minimal API that ingests LorcanaJSON, generates embeddings via Ollama, stores vectors in Qdrant, and serves /api/deck using RAG
- DeckBuilder.Ui: FsBolero frontend (F#) to call /api/deck and display deck text

## Documentation

- Full docs index: `docs/README.md`
- Key topics: Agentic + RAG, UI/Styling, Operations, Analysis, and Session Summaries.

## Running the Application

```bash
aspire run
```

**That's it!** `aspire run` automatically builds all projects before starting.

### Alternative (PowerShell/Bash scripts)

**Windows**:
```powershell
.\run.ps1
```

**Linux/macOS**:
```bash
chmod +x run.sh
./run.sh
```

## Important Notes

- **Bolero Compatibility**: The UI project targets .NET 8 (Bolero doesn't support .NET 10 yet)
- **Automatic Building**: `aspire run` detects changes and rebuilds automatically
- Replace Data/allCards.json with the full LorcanaJSON dump
- Pull Ollama models locally (GPU-capable) using Ollama CLI:
  - Embedding model: `all-minilm`
  - Generation model for RTX 4080: `llama3`
- The AppHost will automatically start Ollama and Qdrant containers
- Adjust model names in code if you use different model tags

## Troubleshooting

If you encounter console errors, see [docs/BOLERO_FIX.md](docs/BOLERO_FIX.md) for detailed troubleshooting.

### Manual Build (if needed)
```bash
dotnet build
aspire run
```
