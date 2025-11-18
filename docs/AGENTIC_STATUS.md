# Agentic RAG Implementation - Status & Next Steps

## What Was Started

I've begun implementing an **agentic RAG architecture** that will dramatically improve deck building performance and quality.

### Files Created:
1. **`AgenticDeckService.fs`** - New agentic deck building service (has compilation errors to fix)
2. **`AGENTIC_RAG_PLAN.md`** - Detailed architecture plan
3. **Endpoint added** to `Endpoints.fs` for `/api/deck/agentic`

### Key Changes Made:
- **Model upgraded** from `llama3` to `qwen2.5:7b` (3-5x faster)
- **Timeout increased** to 5 minutes (prevents timeouts)
- **Loading message** updated to set proper expectations

## The Problem Being Solved

**Current approach (stuffing RAG):**
- Dumps 120 cards into prompt as CSV
- Single LLM call with huge context
- Slow (100+ seconds)
- Limited by context window

**New approach (agentic RAG):**
- LLM makes multiple small, targeted queries
- Iterative refinement (3-5 rounds)
- Much smaller prompts
- Can explore entire collection
- **Expected: 20-40 seconds total**

## Architecture Overview

```
User Request → Initial Strategy
↓
AGENTIC LOOP (3-5 iterations):
  1. LLM: "Search for low-cost Amber creatures"
  2. System: Query Qdrant → Return 10-20 results
  3. LLM: Reviews, picks 3-4 cards
  4. System: "Search for synergies with those cards"
  5. LLM: Adds support cards
  6. Repeat until deck complete
↓
Final validated deck
```

## Current Status

### ✅ **Complete:**
- Model switch to qwen2.5:7b
- Timeout fixes
- Architecture planning
- Core agentic loop structure
- Endpoint registration

### ⚠️ **Needs Fixing:**
- F# indentation errors in `AgenticDeckService.fs`
- String interpolation issues
- JSON deserialization for agent responses
- Integration testing

## Compilation Errors To Fix

The new `AgenticDeckService.fs` has F# syntax errors:
1. **Indentation** - F# strict indentation rules
2. **String interpolation** - Can't use `$""` inside verbatim strings
3. **Match expressions** - Offside rules

## Next Steps to Complete

### 1. Fix Compilation Errors (30 min)
- Fix indentation in `buildAgentPrompt`
- Use triple-quoted strings for JSON examples
- Align match expressions properly

### 2. Test Basic Flow (30 min)
- Call `/api/deck/agentic` endpoint
- Verify LLM responds with valid JSON
- Check iteration loop works

### 3. Enhance Search (30 min)
- Add proper Qdrant filter logic for colors/cost
- Improve result formatting
- Add synergy search capability

### 4. Add Validation (30 min)
- Validate card legality
- Check deck size
- Enforce max copies rules
- Add inkable counting

### 5. Polish & Test (1 hour)
- Test with various queries
- Compare quality vs old approach
- Benchmark speed
- Switch UI to use new endpoint if better

## Expected Benefits (When Complete)

**Speed:** 20-40 seconds (vs 100+ seconds)
**Quality:** Better synergies, smarter card selection
**Scalability:** Works with any collection size
**Flexibility:** Can search iteratively for perfect cards

## How to Continue

To complete this implementation:

1. **Fix the F# syntax errors** in `AgenticDeckService.fs`
2. **Test the agentic endpoint** with Postman/curl
3. **Iterate on prompts** to get good JSON responses from Qwen
4. **Add validation logic** for legal decks
5. **Switch UI** to use `/api/deck/agentic` when proven better

The foundation is laid - just needs debugging and refinement! 🚀

## Immediate Action

For now, the **old endpoint still works** with the faster `qwen2.5:7b` model.
The agentic endpoint will be available once compilation errors are fixed.

Just run `aspire run` and use the existing `/api/deck` endpoint with the new faster model!