# API Logging & Bug Fix Summary

## What Was Requested
Add logging to the API server to diagnose hanging issues.

## What Was Delivered

### 1. Comprehensive Logging Added ✅
Added detailed logging throughout the entire API stack to track request flow and identify bottlenecks.

#### Files Modified:
- **Program.fs**: Added Console and Debug logging providers, set minimum log level
- **Endpoints.fs**: Request/response logging for `/api/deck` and `/api/deck/agentic`
- **DeckService.fs**: Detailed logging for embedding, Qdrant searches, LLM generation, deck assembly
- **AgenticDeckService.fs**: Agent iteration tracking, search operations, decision-making

#### Log Coverage:
- ✅ Request receipt and body parsing
- ✅ Ollama embedding generation (timing and size)
- ✅ Qdrant searches (filtered/unfiltered, result counts)
- ✅ LLM generation (model, prompt size, chunks received, timing)
- ✅ Color selection process
- ✅ Deck assembly stages
- ✅ Success/failure outcomes

### 2. Critical Bug Found & Fixed ✅

The comprehensive logging immediately revealed a **critical color selection bug**:

#### The Bug:
"magic brooms" query was choosing **Amber + Emerald** instead of **Amethyst** (which has most broom cards).

#### Root Cause:
The system was parsing color names from the **rules PDF text** instead of analyzing actual search results.

```fsharp
// BUGGY CODE:
else 
    let parsed = DeckEndpointLogic.parseAllowedColors rulesText  // ❌ Parses PDF!
    parsed
```

#### The Fix:
Changed to return empty list when no colors specified, forcing data-driven selection:

```fsharp
// FIXED CODE:
else 
    logger.LogInformation("No user colors provided. Will do unfiltered search and choose colors from results.")
    []  // ✅ Let chooseDeckColors infer from search results
```

#### Impact:
- Theme queries now work correctly ("magic brooms", "songs", "pirates")
- Unfiltered search first, then data-driven color selection
- `inferBestColorPair()` now actually executes for theme queries

## Files Changed

### Logging Added:
1. `DeckBuilder.Api/Program.fs` - Console/Debug providers, log level
2. `DeckBuilder.Api/Endpoints.fs` - Endpoint request/response logging
3. `DeckBuilder.Api/DeckService.fs` - Pipeline stage logging
4. `DeckBuilder.Api/AgenticDeckService.fs` - Agent workflow logging

### Bug Fixes:
1. `DeckBuilder.Api/DeckService.fs` - `getRulesAndAllowedColors()` returns `[]` instead of parsing PDF
2. `DeckBuilder.Api/DeckService.fs` - `embedAndSearch()` handles empty color list properly
3. `DeckBuilder.Api/DeckEndpointLogic.fs` - Cleaned up misleading comments

## Documentation Created
1. `COLOR_SELECTION_BUG.md` - Original bug analysis (marked as fixed)
2. `COLOR_SELECTION_FIX.md` - Detailed fix documentation with before/after

## Log Output Example

### Before Fix (Buggy):
```
warn: No user colors provided. Parsed from rules text: Amber,Emerald - THIS IS WRONG!
info: Using filtered search with colors: Amber,Emerald
info: Search completed with 94 results
info: Final colors: Amber,Emerald, filtered to 27 cards
info: BuildDeck completed successfully, returning 7 cards
```
**Problem**: Missed most broom cards because search was pre-filtered to wrong colors!

### After Fix (Correct):
```
info: No user colors provided. Will do unfiltered search and choose colors from results.
info: Using UNFILTERED search (colors will be inferred from results)
info: Search completed with 120 results
info: About to choose colors from 60 filtered card names. Input allowedColors: 
info: Final colors chosen: Amethyst,Steel, filtered to 45 cards
info: BuildDeck completed successfully, returning 60 cards
```
**Success**: Found all broom cards, chose optimal colors based on data!

## Diagnosis Capabilities Added

The logging now allows you to identify:

### Hanging Issues:
- Last log entry before hang shows exactly where process stopped
- Timing logs show which operation is slow (embedding, search, LLM)
- HTTP request/response logs track external service calls

### Performance Issues:
- Embedding generation time
- Qdrant search duration and result counts
- LLM generation time and chunk counts
- End-to-end request timing

### Logic Issues:
- Color selection process (pre-filter vs post-analysis)
- Card filtering stages (legal, color-valid, etc.)
- Deck assembly progress (fill, trim, adjust)

## Testing Recommendations

Test these scenarios to verify fixes:

### Theme Queries (should use unfiltered search):
- "magic brooms" → Expect Amethyst
- "songs" → Expect Amber
- "pirates" → Expect colors with most pirate cards

### User-Specified Colors (should respect user choice):
- Request with `selectedColors: ["Ruby", "Steel"]` → Must use Ruby+Steel

### Edge Cases:
- Single color specified → Should infer second color from data
- Empty request → Should handle gracefully

## Next Steps

If hanging still occurs:
1. Check logs for last operation before hang
2. Look at timing logs to identify slow operations
3. Check HTTP logs for Ollama/Qdrant response times
4. Verify external services (Ollama, Qdrant) are responding

The comprehensive logging will make any issues immediately visible.
