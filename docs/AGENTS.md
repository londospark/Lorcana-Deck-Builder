# Lorcana Deck Builder - Architecture Documentation

## Overview

A Disney Lorcana deck-building application using **RAG (Retrieval-Augmented Generation)** with **vector embeddings** to intelligently suggest competitive decks. Built with F# and functional programming principles, orchestrated via .NET Aspire.

## Technology Stack

- **Language**: F# (functional-first)
- **Orchestration**: .NET Aspire (AppHost)
- **Frontend**: FsBolero (F# Blazor WebAssembly) - **Requires .NET 8**
- **Backend**: F# Minimal API (.NET 10)
- **AI/ML**: Ollama (LLM + embeddings)
- **Vector Store**: Qdrant
- **Observability**: OpenTelemetry (traces, metrics, logs)
- **Metrics**: Prometheus

**Important**: Bolero 0.24.39 only supports .NET 6 and .NET 8. The UI project targets .NET 8 while the API and shared library support .NET 10.

## Running the Application

```bash
aspire run
```

The AppHost orchestrates:
- Ollama container (GPU-enabled with `--gpus=all`)
- Qdrant container (persistent storage)
- DeckBuilder.Api (F# backend)
- DeckBuilder.Ui (Bolero frontend)

## Architecture Components

### 1. AppHost (DeckBuilder.AppHost/Program.cs)

Declarative infrastructure composition using .NET Aspire:

```csharp
var ollama = builder.AddOllama("ollama")
    .WithOpenWebUI()
    .WithDataVolume()
    .WithContainerRuntimeArgs("--gpus=all");

var qwen = ollama.AddModel("qwen2.5:14b-instruct");
var nomic = ollama.AddModel("nomic-embed-text");

var qdrant = builder.AddQdrant("qdrant")
    .WithLifetime(ContainerLifetime.Persistent);

var deckApi = builder.AddProject<Projects.DeckBuilder_Api>("deck-api")
    .WithReference(ollama)
    .WithReference(qwen)
    .WithReference(nomic)
    .WithReference(qdrant);

var deckUi = builder.AddProject<Projects.DeckBuilder_Ui>("deck-ui")
    .WithReference(deckApi);
```

**Models Used**:
- `qwen2.5:14b-instruct`: Text generation for deck building
- `nomic-embed-text`: 768-dim embeddings for semantic search

### 2. DeckBuilder.Api (F# Backend)

#### Program.fs

Bootstraps the API with functional service registration:

```fsharp
// Service registration (pure functional composition)
builder.AddQdrantClient("qdrant")
builder.AddOllamaApiClient("ollama")
    .AddChatClient()
    .AddEmbeddingGenerator()

// Rules provider (lazy-loaded from PDF)
builder.Services.AddSingleton<IRulesProvider>(
    fun sp -> RulesProvider.createWithDir(contentRootPath))

// Deck builder service
builder.Services.AddSingleton<IDeckBuilder, DeckBuilderService>()

// OpenTelemetry instrumentation
builder.Services.AddOpenTelemetry()
    .WithTracing(...)
    .WithMetrics(...)
```

**Startup Pipeline**: Automatically ingests rules into Qdrant RAG collection on boot (idempotent).

#### DeckService.fs

Core deck-building logic following **functional composition** principles:

**Pipeline Structure**:
```fsharp
BuildDeck: DeckQuery -> Task<Result<DeckResponse, string>>
```

Composed of small, pure functions:
1. `validateQuery`: Input validation
2. `embedAndSearch`: Generate embeddings → search Qdrant
3. `getRulesForPrompt`: RAG retrieval from rules collection
4. `prepare`: Build lookup maps (ink, colors, maxCopies)
5. `legalCandidatesAndPrompt`: Filter legal cards → build LLM prompt
6. `generateAndTopUp`: Call Ollama → ensure deck size
7. `cleanAndValidate`: Filter non-card lines
8. `chooseColorsAndFilter`: Color legality enforcement
9. `buildInitialCounts`: Initial card counts
10. `fillToSize`: Respect maxCopies constraints
11. `trimOversize`: Reduce to exact deck size
12. `adjustPlaysets`: Optimize for 5-12 playsets of 4 copies
13. `buildResponse`: Map to response DTO

**Functional Design Patterns**:
- **Railway-oriented programming**: `Result<'T, 'Error>` for error handling
- **Composition over inheritance**: Small functions composed via `task {}` CE
- **Immutable data structures**: All transformations return new values
- **Higher-order functions**: `Array.map`, `Array.filter`, `Seq.choose`
- **Type-safe payloads**: Strong typing via discriminated unions

#### Endpoints.fs

Minimal API endpoint registration:

- `POST /ingest`: Ingest cards from `Data/allCards.json` → generate embeddings → store in Qdrant
- `GET /rules`: Retrieve loaded rules text
- `POST /ingest-rules`: Chunk rules PDF → embed → store in Qdrant
- `POST /api/deck`: Main deck-building endpoint (delegates to `DeckService`)

**Functional Approach**: Handlers are pure functions taking dependencies as parameters.

#### Card.fs

Represents Lorcana cards with:
- Strong typing for common fields (Name, Cost, Rules, etc.)
- Retains raw `JsonElement` for full-fidelity Qdrant payload
- Functional helpers for JSON extraction and Qdrant Value conversion

#### QdrantHelpers.fs

Utilities for:
- Payload field extraction (`fullName`, `inkable`, `colors`, `maxCopiesInDeck`)
- Legality filtering (format checks, set validation)
- Functional wrappers around Qdrant gRPC client

#### RulesProvider.fs

Lazy-loads Disney Lorcana rules PDF:
- Searches `Data/` folder for PDF
- Extracts text using `UglyToad.PdfPig`
- Caches in-memory for RAG queries

### 3. DeckBuilder.Ui (Bolero Frontend)

#### Program.fs

Elmish MVU (Model-View-Update) architecture:

**Model**:
```fsharp
type Model = {
    Request: string
    DeckSize: int
    SelectedColor1: string option
    SelectedColor2: string option
    Result: string
    Cards: CardVM array
}
```

**Messages** (pure data):
```fsharp
type Message =
    | SetRequest of string
    | SetDeckSize of int
    | SetColor1 of string option
    | SetColor2 of string option
    | Build
    | Built of string * CardVM array
```

**Update Function** (pure):
```fsharp
let update message model =
    match message with
    | SetRequest r -> { model with Request = r }, Cmd.none
    | Build -> model, Cmd.OfTask.perform Api.buildDeck model Built
    | Built (text, cards) -> { model with Result = text; Cards = cards }, Cmd.none
    // ...
```

**View Function** (declarative HTML):
```fsharp
let view model dispatch =
    div {
        h3 { text "Lorcana Deck Builder (Bolero)" }
        textarea { on.input (fun e -> dispatch (SetRequest (string e.Value))) }
        button { on.click (fun _ -> dispatch Build); text "Build Deck" }
        // ...
    }
```

**Functional UI Patterns**:
- **Immutable state updates**: Every message produces new model
- **Pure render**: View is pure function of model
- **Type-safe events**: Messages are discriminated unions
- **Command pattern**: Side effects via `Cmd.OfTask`

### 4. DeckBuilder.Shared

Shared DTOs between API and UI:
- `DeckQuery`: Request payload
- `CardEntry`: Individual card in response
- `DeckResponse`: Deck + explanation

**CLIMutable** attribute enables JSON serialization while maintaining immutability.

## Data Flow

### Ingestion Phase (One-time Setup)

```
allCards.json → Parse JSON → For each card:
  ├─ Extract card data
  ├─ Build embedding text (name + rules + flavor)
  ├─ Call Ollama embedding API (nomic-embed-text)
  ├─ Create Qdrant point with 768-dim vector
  └─ Upsert to "lorcana_cards" collection

Rules PDF → Extract text → Chunk (500 chars, 80 overlap):
  ├─ For each chunk:
  ├─ Call Ollama embedding API
  └─ Upsert to "lorcana_rules" collection
```

### Deck Building Request

```
User Input (Request + DeckSize + Colors)
  ↓
Embed user request (nomic-embed-text)
  ↓
Search Qdrant "lorcana_cards" (cosine similarity, limit 40-120)
  ├─ Optional: Filter by color exclusion
  ↓
Search Qdrant "lorcana_rules" (limit 6) → RAG context
  ↓
Build prompt:
  ├─ Rules context from RAG
  ├─ Legal candidates CSV
  ├─ User request
  ├─ Deck construction constraints
  ↓
Call Ollama generate (qwen2.5:14b-instruct)
  ↓
Parse response → Extract card names
  ↓
Apply functional pipeline:
  ├─ Filter non-cards
  ├─ Enforce color legality
  ├─ Respect maxCopies per card
  ├─ Fill to exact deck size
  ├─ Optimize playsets (5-12 full sets)
  ├─ Ensure inkable balance (~70-85%)
  ↓
Build response with explanations
  ↓
Return to UI
```

## Functional Programming Principles

### 1. Immutability
- All data transformations create new values
- No mutation of state (except for perf-critical loops with `mutable`)
- Dictionary lookups wrapped in pure functions

### 2. Composition
Small, single-purpose functions composed into pipelines:
```fsharp
let buildDeck query = task {
    let! result = validateQuery query
    >>= embedAndSearch
    >>= prepare
    >>= generateAndTopUp
    >>= cleanAndValidate
    >>= buildResponse
    return result
}
```

### 3. Type Safety
- Discriminated unions for domain modeling
- Option types instead of null
- Result types for error handling
- Strong typing prevents invalid states

### 4. Pure Functions
- Most functions are pure (no side effects)
- Side effects isolated to:
  - `task {}` computation expressions (I/O)
  - Qdrant/Ollama API calls
  - Bolero commands

### 5. Pattern Matching
Exhaustive matching on discriminated unions:
```fsharp
match result with
| Ok value -> processValue value
| Error msg -> handleError msg
```

### 6. Higher-Order Functions
Extensive use of combinators:
- `Array.map`, `Array.filter`, `Array.choose`
- `Seq.fold`, `Seq.collect`
- `List.sortBy`, `List.groupBy`

## Observability

### OpenTelemetry Integration

**Traces**:
- ASP.NET Core instrumentation
- HttpClient instrumentation
- OTLP exporter

**Metrics**:
- Runtime instrumentation
- Request metrics via Prometheus (`/metrics`)
- OTLP exporter

**Logs**:
- Structured logging via OpenTelemetry
- OTLP exporter

**Frontend Telemetry**:
- Blazor WASM traces/metrics (HTTP/protobuf)
- Graceful degradation if exporter unavailable

## Key Design Decisions

### Why Functional Programming?

1. **Reliability**: Pure functions easier to reason about and test
2. **Composability**: Small functions compose into complex workflows
3. **Concurrency**: Immutability eliminates data races
4. **Maintainability**: Type system prevents entire classes of bugs

### Why RAG (Retrieval-Augmented Generation)?

1. **Grounded responses**: LLM generates decks from actual card pool
2. **Rules adherence**: RAG injects relevant rules context
3. **Semantic search**: Vector embeddings find thematically related cards
4. **Scalability**: Works with any card set size

### Why Aspire?

1. **Declarative orchestration**: Infrastructure as code
2. **Local development**: One command to run entire stack
3. **Service discovery**: Automatic connection strings
4. **Observability**: Built-in telemetry

## Project Structure

```
Lorcana-Deck-Builder/
├── DeckBuilder.AppHost/Program.cs # Aspire orchestration
├── DeckBuilder.Api/              # F# backend
│   ├── Program.fs                # Bootstrap + telemetry
│   ├── DeckService.fs            # Core deck-building logic
│   ├── Endpoints.fs              # API endpoints
│   ├── Card.fs                   # Card domain model
│   ├── QdrantHelpers.fs          # Vector store utilities
│   ├── RulesProvider.fs          # PDF extraction
│   ├── DeckHelpers.fs            # Deck manipulation
│   └── Data/                     # allCards.json + rules PDF
├── DeckBuilder.Ui/               # Bolero frontend
│   ├── Program.fs                # Elmish MVU app
│   └── wwwroot/                  # Static assets
├── DeckBuilder.Shared/           # Shared DTOs
│   └── SharedModels.fs
└── README.md                     # Setup instructions
```

## Configuration

### Ollama Models

Configure in `DeckBuilder.AppHost/Program.cs`:
```csharp
var qwen = ollama.AddModel("qwen2.5:14b-instruct"); // Generation
var nomic = ollama.AddModel("nomic-embed-text");     // Embeddings
```

Pull models locally:
```bash
docker exec -it <ollama-container> ollama pull qwen2.5:14b-instruct
docker exec -it <ollama-container> ollama pull nomic-embed-text
```

### Qdrant Collections

- `lorcana_cards`: 384-dim vectors, cosine similarity
- `lorcana_rules`: 384-dim vectors, cosine similarity

### Data Sources

- `Data/allCards.json`: LorcanaJSON card database
- `Data/*.pdf`: Official Disney Lorcana rules PDF

## Extension Points

### Adding Custom Card Sources
1. Implement parser in `Card.fs`
2. Add endpoint in `Endpoints.fs`
3. Call embedding pipeline from `DeckService.fs`

### Supporting More Formats
1. Extend `DeckQuery` with format field
2. Add format-specific filtering in `QdrantHelpers.fs`
3. Update prompt in `DeckService.buildPrompt`

### Alternative LLMs
1. Update model names in `DeckService.fs` and `Endpoints.fs`
2. Adjust prompt constraints if needed
3. Ensure embedding dimensions match (768 for nomic-embed-text)

## Performance Considerations

- **Embedding caching**: Cards embedded once during ingestion
- **Parallel processing**: F# async/tasks enable concurrent I/O
- **Qdrant indexing**: HNSW index for fast approximate nearest neighbors
- **GPU acceleration**: Ollama container uses `--gpus=all` for inference
- **Lazy loading**: Rules PDF loaded on-demand, cached thereafter

## Testing Strategy

Functional code facilitates testing:

1. **Unit tests**: Pure functions testable without mocks
2. **Property tests**: Use FsCheck for generative testing
3. **Integration tests**: Test full pipeline with test Qdrant instance
4. **Contract tests**: Validate API/UI contract via shared DTOs

## Future Enhancements

- **Mana curve optimization**: Functional scoring of curves
- **Synergy detection**: Graph-based card relationship analysis
- **Meta tracking**: Historical deck performance data
- **Sideboard support**: Extend model for Bo3 formats
- **Export formats**: Text, Arena, MTGO
