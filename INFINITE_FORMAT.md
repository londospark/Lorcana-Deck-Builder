# Infinite Format Support

## Overview

Added support for the **Infinite format** in the Lorcana Deck Builder. This format allows building decks with cards from **all sets**, including those that have rotated out of the Core format.

## Changes Made

### 1. **Shared Models** (`DeckBuilder.Shared/SharedModels.fs`)

Added a discriminated union for format selection:

```fsharp
type DeckFormat =
    | Core
    | Infinite

[<CLIMutable>]
type DeckQuery = {
    request: string
    deckSize: int
    selectedColors: string[] option
    format: DeckFormat option  // New field
}
```

**Benefits of Discriminated Union**:
- ✅ Type-safe at compile time
- ✅ Exhaustive pattern matching enforced
- ✅ JSON serialization supported via `System.Text.Json`
- ✅ No invalid states possible (can't pass "Coree" typo)

### 2. **API Format Filtering** (`DeckBuilder.Api/QdrantHelpers.fs`)

Added format-aware card filtering:

```fsharp
/// Determine if a card is legal in Infinite format (no rotation, all cards legal)
let private isInfiniteLegal (payload: MapField<string, Value>) : bool =
    // In Infinite format, all cards are legal regardless of rotation
    let pathInfinite = [ "allowedInFormats"; "Infinite" ]
    let vInfiniteOpt = PayloadRead.tryGetNested payload pathInfinite
    match vInfiniteOpt with
    | None -> true // If not specified, assume legal in Infinite
    | Some vInfinite ->
        match (PayloadRead.tryGetField vInfinite.StructValue "allowed") 
              |> Option.bind PayloadRead.tryGetBool with
        | Some allowed -> allowed
        | None -> true

let filterLegalCardsPoints (query: DeckQuery) (candidates: seq<ScoredPoint>) =
    let format = query.format |> Option.defaultValue DeckFormat.Core
    match format with
    | DeckFormat.Core ->
        let nowUtc = DateTime.UtcNow
        candidates |> Seq.filter (fun p -> isCoreLegalNow nowUtc p.Payload)
    | DeckFormat.Infinite ->
        candidates |> Seq.filter (fun p -> isInfiniteLegal p.Payload)
```

**Functional Approach**:
- Uses pattern matching on the discriminated union
- Defaults to Core format if not specified
- Pure functions with no side effects
- Composable filtering logic

### 3. **Prompt Enhancement** (`DeckBuilder.Api/DeckService.fs`)

Updated the LLM prompt to include format information:

```fsharp
let buildPrompt (query: DeckQuery) (rulesText:string) (_:string list) (legalCandidates:string) =
    let format = query.format |> Option.defaultValue DeckFormat.Core
    let formatDesc = 
        match format with
        | DeckFormat.Core -> "Core format (standard rotation)"
        | DeckFormat.Infinite -> "Infinite format (no rotation, all cards legal)"
    
    let prompt = StringBuilder()
    prompt.AppendLine("You are an expert Lorcana deck builder.") |> ignore
    prompt.AppendLine($"FORMAT: {formatDesc}") |> ignore
    // ... rest of prompt
    prompt.AppendLine($"TASK: Build a legal {query.deckSize}-card deck for {formatDesc} with this request:") |> ignore
```

This ensures the LLM understands which format it's building for.

### 4. **UI Format Selector** (`DeckBuilder.Ui/Program.fs`)

#### Model Update

```fsharp
type Model = {
    Request: string
    DeckSize: int
    SelectedColor1: string option
    SelectedColor2: string option
    SelectedFormat: DeckFormat  // New field
    Result: string
    Cards: CardVM array
}

let initModel = { 
    // ...
    SelectedFormat = DeckFormat.Core  // Default to Core
    // ...
}
```

#### Message Update

```fsharp
type Message =
    | SetRequest of string
    | SetDeckSize of int
    | SetColor1 of string option
    | SetColor2 of string option
    | SetFormat of DeckFormat  // New message
    | Build
    | Built of string * CardVM array
```

#### UI Element

Added a format selector dropdown in the view:

```fsharp
// Format selection
div {
    label {
        b { text "Format" }
        text " — Choose the deck format (Core has rotation, Infinite includes all cards)."
    }
    br {}
    select {
        attr.id "format"
        attr.value (match model.SelectedFormat with 
                    | DeckFormat.Core -> "Core" 
                    | DeckFormat.Infinite -> "Infinite")
        on.change (fun e -> 
            let v = string e.Value
            let format = if v = "Infinite" then DeckFormat.Infinite else DeckFormat.Core
            dispatch (SetFormat format))
        option { attr.value "Core"; text "Core (Standard rotation)" }
        option { attr.value "Infinite"; text "Infinite (No rotation, all cards)" }
    }
    br {}
    small { 
        text (match model.SelectedFormat with
              | DeckFormat.Core -> "Core format uses currently legal cards with rotation."
              | DeckFormat.Infinite -> "Infinite format includes all cards, no rotation restrictions.")
    }
}
```

## How It Works

### Core Format (Default)

1. User selects "Core (Standard rotation)"
2. Query sent with `format: Some Core`
3. API filters cards using `isCoreLegalNow` function
4. Only cards legal in current Core rotation are candidates
5. LLM builds deck from legal card pool

### Infinite Format

1. User selects "Infinite (No rotation, all cards)"
2. Query sent with `format: Some Infinite`
3. API filters cards using `isInfiniteLegal` function
4. All cards are candidates (unless explicitly banned)
5. LLM builds deck from entire card pool

## Data Requirements

For proper Infinite format support, card data in Qdrant should include:

```json
{
  "allowedInFormats": {
    "Core": {
      "allowed": true,
      "allowedFromDate": "2024-01-15",
      "allowedUntilDate": "2025-12-31"  // Optional rotation date
    },
    "Infinite": {
      "allowed": true  // Usually true for all cards
    }
  }
}
```

**Default Behavior**:
- **Core**: If `allowedInFormats.Core` is missing → card is **NOT legal** (conservative)
- **Infinite**: If `allowedInFormats.Infinite` is missing → card **IS legal** (permissive)

This aligns with the format philosophy: Core is curated, Infinite is open.

## Usage Example

### Building a Core Deck

```
Request: "Build a tempo Amber/Sapphire deck"
Deck Size: 60
Colors: Amber, Sapphire
Format: Core (Standard rotation)
```

Result: Deck with only currently legal cards.

### Building an Infinite Deck

```
Request: "Build a combo deck using early set synergies"
Deck Size: 60
Colors: Ruby, Steel
Format: Infinite (No rotation, all cards)
```

Result: Deck can include cards from Sets 1-2 that have rotated out of Core.

## Functional Programming Highlights

### Pattern Matching

```fsharp
let format = 
    match query.format with
    | Some DeckFormat.Core -> "Core"
    | Some DeckFormat.Infinite -> "Infinite"
    | None -> "Core"  // Default
```

### Option Pipeline

```fsharp
let format = query.format |> Option.defaultValue DeckFormat.Core
```

### Pure Function Composition

```fsharp
let filterByFormat format =
    match format with
    | Core -> Seq.filter isCoreLegalNow
    | Infinite -> Seq.filter isInfiniteLegal

candidates 
|> filterByFormat format
|> applyColorConstraints
|> buildDeck
```

## Testing

### Manual Test Cases

1. **Core Format with rotated card**:
   - Select Core format
   - Request deck with specific strategy
   - Verify no rotated cards appear

2. **Infinite Format with rotated card**:
   - Select Infinite format
   - Request deck mentioning early sets
   - Verify rotated cards can appear

3. **Default behavior**:
   - Don't select format (None)
   - Should default to Core

### Expected Behavior

| Format    | Rotation? | Card Pool          |
|-----------|-----------|-------------------|
| Core      | Yes       | Current legal sets |
| Infinite  | No        | All sets          |

## Future Enhancements

1. **Format-specific rules**:
   - Different ban lists per format
   - Format-specific deck size constraints

2. **Additional formats**:
   ```fsharp
   type DeckFormat =
       | Core
       | Infinite
       | Brawl     // Singleton, different rules
       | Commander // Different deck size
   ```

3. **Format metadata**:
   ```fsharp
   type FormatInfo = {
       name: string
       hasRotation: bool
       deckSize: int
       allowedSets: string list option
   }
   ```

## Summary

✅ **Type-safe format selection** via discriminated union  
✅ **Automatic filtering** based on format choice  
✅ **LLM-aware prompting** with format context  
✅ **Clean UI** with dropdown selector  
✅ **Functional composition** throughout  
✅ **Backwards compatible** (defaults to Core)

The Infinite format now allows players to build decks using the full card pool without rotation restrictions! 🎉
