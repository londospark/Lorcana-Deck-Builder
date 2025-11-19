# Enhanced RAG Filtering & Color Selection

## Changes Made

### 1. Comprehensive Qdrant Filtering (AgenticDeckService.fs)

Previously, the search only filtered by format legality. Now it filters by **ALL** criteria:

- ✅ **Format Legality** - Infinity vs Core format restrictions
- ✅ **Colors** - Card must have at least one of the specified colors
- ✅ **Cost Range** - Filter by min/max ink cost
- ✅ **Inkable Status** - Filter cards that can/cannot be played as ink

**Before:**
```fsharp
// Only checked format
match filters with
| Some f when f.Format.IsSome ->
    results |> Seq.filter (fun point -> Payload.isAllowedInFormat point.Payload f.Format.Value)
```

**After:**
```fsharp
// Check ALL filter criteria
results
|> Seq.filter (fun point ->
    let formatOk = ... // Format legality check
    let colorsOk = ... // Color matching check  
    let costOk = ...   // Cost range check
    let inkableOk = ... // Inkable status check
    formatOk && colorsOk && costOk && inkableOk
)
```

### 2. Improved Color Selection Prompt

The LLM was defaulting to Amber+Emerald for everything. New prompt explicitly:

- ⚠️ Instructs to do **first search with NO color filters**
- 📊 Tells LLM to **count which colors appear most** in search results
- 🎯 Chooses colors **based on data**, not arbitrary defaults
- 🚫 Explicitly warns **NOT to default to Amber+Emerald**

**Key additions:**
```fsharp
sb.AppendLine("- ⚠️ CRITICAL: DO NOT apply color filters in your first search!") 
sb.AppendLine("- First search should have NO filters to discover which colors support this theme")
sb.AppendLine("- WORKFLOW:")
sb.AppendLine("  1. search_cards with NO color filters → see all matching cards")
sb.AppendLine("  2. Count which colors appear most in the COLORS column")
sb.AppendLine("  3. add_cards from the 1-2 colors with most/best support")
sb.AppendLine("- Do NOT default to Amber+Emerald - let the search results guide color choice!")
```

### 3. Format Legality Now Works

Previously Infinity format was showing all cards. Now:
- Infinity format: Only cards with `allowedInFormats.Infinity = true`
- Core format: Only cards with `allowedInFormats.Core = true`
- No format specified: All cards available

## Testing

The changes will take effect when Aspire reloads the API. Test with:

1. **Format filtering**: Build an Infinity deck - should exclude banned cards
2. **Color selection**: Request "magic brooms" without specifying colors - should pick Amethyst (not Amber+Emerald)
3. **Search filtering**: Request low-cost inkable cards - should respect cost/inkable filters

## Next Steps (Optional Improvements)

1. **Native Qdrant filtering** - Move color/cost/inkable filtering into Qdrant query (faster, more efficient)
2. **Payload optimization** - Add indexed fields for better search performance
3. **Hybrid search** - Combine semantic search with keyword matching for specific card names

## Date-Based Legality Filtering (Rotation)

To ensure legality stays correct even when source `allowed` flags lag behind set rotations, we enforce date windows directly in Qdrant using explicit nulls and IsNull checks.

- Ingestion writes nested timestamp fields under the card payload:
    - `allowedInFormats.Core.allowedFromTs` / `allowedInFormats.Core.allowedUntilTs`
    - `allowedInFormats.Infinity.allowedFromTs` / `allowedInFormats.Infinity.allowedUntilTs`
- For missing or empty dates, ingestion sets these fields to explicit `NullValue` (not omitted).
- API filter uses native Qdrant conditions:
    - `allowed = true`
    - `(allowedFromTs IsNull OR allowedFromTs <= now)`
    - `(allowedUntilTs IsNull OR allowedUntilTs >= now)`

This combination guarantees:
- Cards without rotation dates remain legal by default (nulls pass).
- Cards with future start dates are excluded until they’re active.
- Cards with past end dates are excluded after rotation.

Implementation touchpoints:
- Ingestion: `DeckBuilder.Worker/Worker.fs` ensures nested structs exist and sets `NullValue` when dates are missing.
- Filter: `DeckBuilder.Api/QdrantHelpers.fs` builds IsNull-or-range Must clauses for Core/Infinity.

Operational note: Re-run ingestion when date data changes (create `.force_reimport` in the worker runtime `bin/Debug/net10.0/Data/` and start the `data-worker`).
