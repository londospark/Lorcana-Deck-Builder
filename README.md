Lorcana Aspire - Filled Template (F# + FsBolero)
===================================================

This template includes:
- AppHost: wires Ollama (container), Qdrant (container), DeckBuilder.Api
- DeckBuilder.Api: F# minimal API that ingests LorcanaJSON, generates embeddings via Ollama, stores vectors in Qdrant, and serves /api/deck using RAG
- DeckBuilder.Ui: FsBolero frontend (F#) to call /api/deck and display deck text

Important:
- Replace Data/allCards.json with the full LorcanaJSON dump.
- Pull Ollama models locally (GPU-capable) using Ollama CLI (running in container).
  Suggested: use an embedding model like 'all-minilm' and a generation model that fits RTX 4080 (e.g., 'llama3' variant).
- Start Aspire AppHost. It will attempt to start Ollama and Qdrant containers.
- Adjust model names in appsettings or code if you pulled different model tags.

