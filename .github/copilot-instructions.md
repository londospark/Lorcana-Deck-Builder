# Copilot Instructions - Lorcana Deck Builder

## Project Overview
Disney Lorcana deck-building app using F# functional programming, RAG (vector embeddings), agentic AI, and .NET Aspire orchestration. Builds competitive 60-card decks from user requests via semantic search (Qdrant) and LLM reasoning (Ollama).

## Critical Architecture Rules

### Multi-Target .NET Versions
- **UI Project (DeckBuilder.Ui)**: Must stay on .NET 8 (Bolero 0.24.39 doesn't support .NET 10)
- **All other projects**: .NET 10 (Api, Worker, Shared, AppHost)
- Never upgrade UI to .NET 10 without verifying Bolero compatibility

### Aspire Development Workflow (MCP Integration)
**CRITICAL**: Never restart Aspire itself. Use MCP commands to manage individual resources:

1. **Rebuild without file locks**:
   - `mcp_aspire-dashbo_execute_resource_command` → `resource-restart` on target (e.g., "deck-api")
   - Run `dotnet build <Project>`
   - Resource auto-starts after build

2. **Test API endpoints**:
   ```powershell
   $body = '{"request":"test","deckSize":60,"selectedColors":null,"format":{"Case":"Core"}}';
   Invoke-RestMethod -Uri 'http://localhost:5001/api/deck' -Method POST -Body $body -ContentType 'application/json' | ConvertTo-Json -Depth 5
   ```

3. **Debug issues**: Use `mcp_aspire-dashbo_list_console_logs` with resource name (never inspect Aspire host)

**Resource Names**: `deck-api`, `data-worker`, `ollama`, `qdrant`

### Running the Application
```bash
aspire run  # Single command - auto-builds all projects
```

## Code Patterns

### F# Functional Style
- **Immutability**: All transformations return new values (use `{ model with Field = value }` for records)
- **Railway-oriented programming**: Use `Result<'T, string>` for error handling, chain with `task {}` computation expressions
- **Discriminated Unions**: Model domain concepts (e.g., `DeckFormat = Core | Infinity`)
- **No nulls**: Use `Option<'T>` (`Some`/`None`) instead

**Example** (from AgenticDeckService.fs):
```fsharp
let buildDeck (query: DeckQuery) = task {
    let! searchResults = searchCardsInQdrant query.request query.format
    let! rules = getRulesContext query.request
    return! generateDeck searchResults rules query
}
```

### Qdrant Native Filtering
When filtering by format legality, use nested Must/Should clauses for OR logic with null safety:

```fsharp
// Core format: (allowed missing OR allowed=true) AND (dates valid OR missing)
let allowedFilter = Qdrant.Client.Grpc.Filter()
allowedFilter.Should.Add(Qdrant.Client.Grpc.Condition(IsNull = isNullAllowed))
allowedFilter.Should.Add(Qdrant.Client.Grpc.Condition(Field = matchTrue))
filter.Must.Add(Qdrant.Client.Grpc.Condition(Filter = allowedFilter))
```

**Pattern**: Should = OR logic, Must = AND logic. Always handle missing fields with `IsNull` conditions.

### Worker Data Ingestion
Worker checks force reimport **before** hash comparison:

```fsharp
// CORRECT order:
let! forceReimport = checkForceReimport()
if forceReimport then
    deleteCollection()
    ingestAllCards()
else
    let! storedHash = getStoredHash()
    // ... hash comparison logic
```

Trigger path: `bin/Debug/net10.0/Data/.force_reimport` (runtime directory, not source)

### Shared Models (DeckBuilder.Shared)
- **Required fields**: `format` is NOT optional (discriminated union, no `option`)
- **CLIMutable**: All DTOs need `[<CLIMutable>]` for JSON serialization
- UI sends format as `{"Case":"Core"}` (JsonFSharpConverter handles DU serialization)

### Agentic Deck Building
Three-phase deterministic pipeline (AgenticDeckService.fs):
1. **Search**: Embed user request → Qdrant similarity search (limit 40-120)
   - Searches both `lorcana_cards` for cards AND `lorcana_rules` for rules context
2. **Filter**: Apply format legality (Core/Infinity) + color restrictions
   - Rules excerpts guide legal card selection and synergy identification
3. **Assemble**: Fill to deck size respecting maxCopies (4 per card), inkable balance (70-80%)
   - LLM uses rules context to avoid illegal configurations (wrong colors, banned cards, etc.)

**No iterative loops** - single-pass pipeline for performance. Rules context prevents need for validation retries.

## Project Structure

```
DeckBuilder.Api/          # F# backend (port 5001)
├── AgenticDeckService.fs # Core deck builder (3-phase pipeline)
├── QdrantHelpers.fs      # Vector search filters (format legality)
├── Endpoints.fs          # Minimal API registration
├── Card.fs               # Domain model + Qdrant payload conversion
└── Data/allCards.json    # 2455 Lorcana cards (LorcanaJSON format)

DeckBuilder.Worker/       # Background data ingestion
└── Worker.fs             # SHA256 hash check → embed cards → Qdrant upsert

DeckBuilder.Ui/           # Bolero (F# Blazor WASM) - .NET 8 ONLY
└── Program.fs            # Elmish MVU (Model-View-Update)

DeckBuilder.Shared/       # DTOs shared between API/UI
└── SharedModels.fs       # DeckQuery, DeckResponse, CardEntry

DeckBuilder.AppHost/      # Aspire orchestration
└── Program.cs            # Ollama + Qdrant containers + service refs
```

## Key Dependencies

- **Ollama Models**:
  - `nomic-embed-text`: 768-dim embeddings for semantic search
  - `qwen2.5:14b-instruct`: LLM for deck reasoning (agentic decision-making)
- **Qdrant Collections**:
  - `lorcana_cards`: 2455 points with card metadata + embeddings
  - `lorcana_rules`: Chunked rules PDF for RAG context
- **Qdrant Payload Fields**:
  - `allowedInFormats.Core.allowed` (bool), `allowedFromTs`/`allowedUntilTs` (UNIX timestamps)
  - `inkable`, `color`, `cost`, `fullName`, `rules_text`

## Rules Integration (RAG)

### Rules Ingestion
- Rules PDF stored in `DeckBuilder.Api/Data/` (filename pattern: `*Quick_Start*.pdf` or similar)
- Extracted via `RulesProvider.fs` using UglyToad.PdfPig library
- Auto-ingested on API startup (idempotent - checks if collection exists)
- **Chunking**: 500 chars per chunk, 80 char overlap for context continuity
- Each chunk embedded with `nomic-embed-text` → stored in `lorcana_rules` collection

### RAG Query Flow
When building a deck, the API:
1. Embeds user request with same model (`nomic-embed-text`)
2. Searches `lorcana_rules` collection (limit 6 chunks, cosine similarity)
3. Retrieved chunks provide context about:
   - Deck construction rules (4-copy limit, 1-2 colors)
   - Format-specific restrictions
   - Win condition (20 lore)
   - Card mechanics relevant to user's theme
4. Rules context injected into LLM prompt before card selection phase

**Why RAG for Rules**: Grounds LLM decisions in official comprehensive rules, prevents hallucinating illegal deck configurations.

### Manual Rules Re-ingestion
```fsharp
// POST /ingest-rules endpoint triggers:
// - Delete existing lorcana_rules collection
// - Re-chunk PDF
// - Generate embeddings
// - Recreate collection with fresh data
```

Use when rules PDF updated (new set releases, errata).

## Testing

**API Test Pattern**:
```powershell
# Wait for API startup after rebuild
Start-Sleep -Seconds 3
$body = '{"request":"Magic Brooms","deckSize":60,"selectedColors":null,"format":{"Case":"Core"}}';
Invoke-RestMethod -Uri 'http://localhost:5001/api/deck' -Method POST -Body $body -ContentType 'application/json' | ConvertTo-Json -Depth 5
```

**Validate Response**:
- `totalCards` matches `deckSize`
- `explanation` includes timing (e.g., "164ms")
- `cards` array has 4x copies for optimal playsets

**Worker Force Reimport**:
```powershell
New-Item -ItemType File -Path "DeckBuilder.Worker\bin\Debug\net10.0\Data\.force_reimport" -Force
# Restart worker via MCP, check console logs for "Force reimport triggered..."
```

## Documentation

**All docs in `docs/` folder** (never create `.md` files in project root except `README.md`):
- `docs/ASPIRE_MCP_WORKFLOW.md`: Essential MCP commands and testing workflows
- `docs/AGENTS.md`: Architecture deep-dive
- `docs/README.md`: Documentation index

## Common Pitfalls

1. **Bolero version mismatch**: Don't upgrade UI to .NET 10
2. **Null handling**: Use `Option<'T>`, not null checks (`isNull (box value)` for interop)
3. **Format validation**: API returns 400 if format missing (required field)
4. **Worker trigger path**: Use bin directory (`AppContext.BaseDirectory`), not source Data folder
5. **Build locks**: Always restart resource before `dotnet build` when Aspire running
6. **Qdrant filters**: Use Should clauses for OR logic, handle missing fields with IsNull
7. **Aspire restarts**: User must manually restart Aspire if needed (Ctrl+C → `aspire run`)

## Performance Notes

- **Embedding caching**: Cards embedded once during Worker ingestion (SHA256 hash check)
- **Search limits**: 40-120 results for semantic search (more = slower, less = limited options)
- **GPU acceleration**: Ollama container uses `--gpus=all` (requires NVIDIA Docker runtime)
- **Target latency**: Deck generation <200ms (search + filter + assemble)
