# Deck Size Validation & Rules Injection Fix ✅

## Problem Solved

### Issue 1: Under-sized Decks
- **Problem:** Agent was returning 52-card decks when 60 were required
- **Root Cause:** Premature finalization without proper validation
- **Solution:** Strict validation at multiple layers

### Issue 2: Hardcoded Deck Sizes
- **Problem:** Prompt contained hardcoded "60 cards" 
- **Root Cause:** Copy-paste from examples
- **Solution:** All references now use dynamic `TargetSize`

### Issue 3: Missing Rules Context
- **Problem:** LLM had no understanding of Lorcana rules
- **Root Cause:** Rules not being injected into prompts
- **Solution:** RAG-based rules fetching (same as old service)

## What Was Fixed

### 1. Strict Deck Size Validation (3 Layers)

#### Layer 1: Finalize Action Validation
```fsharp
| "finalize" ->
    let totalCards = newState.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0
    if totalCards >= state.TargetSize then
        return Ok { newState with Complete = true }
    else
        let shortage = state.TargetSize - totalCards
        return Error (sprintf "Cannot finalize: deck has only %d cards, need at least %d (short by %d cards)." 
                              totalCards state.TargetSize shortage)
```

**Result:** LLM cannot finalize until target size is met

#### Layer 2: Final Validation Before Return
```fsharp
let totalCards = finalState.CurrentDeck |> Map.fold (fun acc _ count -> acc + count) 0

if totalCards < query.deckSize then
    return Error (sprintf "Deck building failed: only %d cards built, required at least %d." 
                          totalCards query.deckSize)
```

**Result:** Safety net - never returns under-sized deck

#### Layer 3: Clear Prompt Instructions
```fsharp
sb.AppendLine("CRITICAL RULE: The deck MUST contain AT LEAST the target size.") |> ignore
sb.AppendLine(sprintf "- You CANNOT finalize until you have AT LEAST %d cards total" state.TargetSize) |> ignore
sb.AppendLine(sprintf "- Target: %d cards minimum (a few extra %d-%d is acceptable)" 
                      state.TargetSize state.TargetSize (state.TargetSize + 2)) |> ignore
```

**Result:** LLM clearly understands the requirement

### 2. Dynamic Deck Size (No Hardcoded Values)

#### Before (Bad):
```fsharp
sb.AppendLine("- Standard deck size: 60 cards") |> ignore
sb.AppendLine("- 60-62 cards is acceptable, but 59 or fewer is NOT ALLOWED") |> ignore
```

#### After (Good):
```fsharp
sb.AppendLine(sprintf "GOAL: Build a complete %d-card deck" state.TargetSize) |> ignore
sb.AppendLine(sprintf "- Target: %d cards minimum (a few extra %d-%d is acceptable)" 
                      state.TargetSize state.TargetSize (state.TargetSize + 2)) |> ignore
```

**All deck size references now use the target parameter!**

### 3. RAG-Based Rules Injection

#### How It Works:
```fsharp
let getRulesForPrompt (qdrant: QdrantClient) (embeddingGen) (userRequest: string) = task {
    // 1. Generate embedding for user request
    let! vector = embeddingGen.Invoke(userRequest)
    
    // 2. Search rules collection for relevant excerpts
    let! hits = qdrant.SearchAsync("lorcana_rules", vector, limit = 6uL)
    
    // 3. Extract text from top results
    let texts = hits |> Seq.choose extractText |> Seq.truncate 6 |> Seq.toArray
    
    // 4. Join and cap to 1500 chars for prompt efficiency
    let joined = String.Join("\n\n---\n\n", texts)
    return Some (if joined.Length > 1500 then joined.Substring(0, 1500) else joined)
}
```

#### In Prompt:
```
LORCANA RULES (relevant excerpts from official comprehensive rules):
[RAG-retrieved text about deck building, ink colors, lore, etc.]

Key deck building rules:
- Maximum 4 copies of any card (except cards with specific limits)
- 1-2 ink colors per deck
- Game objective: Reach 20 lore before your opponent
```

#### Why This Approach?
✅ **Proven:** Same pattern as existing DeckService  
✅ **Efficient:** Only ~1500 chars of relevant rules (not entire rulebook)  
✅ **Dynamic:** Different rules for different requests  
✅ **Smart:** Vector search finds what matters  
✅ **Safe:** Falls back to core rules if RAG fails  

## Validation Flow

```
User: "Build me a 40-card deck"
    ↓
Agent Loop Iteration 1-N:
  - LLM searches and adds cards
  - Prompt shows: "current: 32, target: 40" 
  - Prompt warns: "DO NOT finalize yet"
    ↓
Agent tries to finalize at 38 cards:
  ❌ "Cannot finalize: deck has 38 cards, need at least 40 (short by 2 cards)"
    ↓
Agent continues, reaches 41 cards, tries finalize:
  ✅ Allowed! Returns deck
    ↓
Final safety check:
  ✅ 41 >= 40, proceed
    ↓
Return deck with explanation
```

## Testing Instructions

### Test Case 1: Standard 60-Card Deck
```bash
curl -X POST http://localhost:5245/api/deck/agentic \
  -H "Content-Type: application/json" \
  -d '{"request":"aggressive deck","deckSize":60}'
```
**Expected:** 60-62 cards returned

### Test Case 2: Non-Standard Size
```bash
curl -X POST http://localhost:5245/api/deck/agentic \
  -H "Content-Type: application/json" \
  -d '{"request":"control deck","deckSize":50}'
```
**Expected:** 50-52 cards returned (never 49 or less!)

### Test Case 3: Large Deck
```bash
curl -X POST http://localhost:5245/api/deck/agentic \
  -H "Content-Type: application/json" \
  -d '{"request":"fun casual deck","deckSize":80}'
```
**Expected:** 80-82 cards returned

## Benefits

### For Users
- ✅ Always get at least the requested deck size
- ✅ Works with any deck size (40, 50, 60, 80, etc.)
- ✅ Clear error messages if something goes wrong
- ✅ LLM understands Lorcana rules properly

### For Developers
- ✅ No magic numbers in code
- ✅ Multiple validation layers (defense in depth)
- ✅ Reusable rules RAG pattern
- ✅ Easy to extend with more rules

### For the LLM
- ✅ Gets relevant rules context
- ✅ Clear deck size expectations
- ✅ Explicit finalization criteria
- ✅ Real-time progress feedback

## Rules Injection - Architecture Decision

### Question: Should we inject rules?
**Answer: YES** ✅

**Why:**
1. LLM needs game context (lore, ink costs, win conditions)
2. Ensures legal deck building (colors, copy limits)
3. Improves card selection quality
4. Matches proven approach from existing service

### Question: Are we doing it right?
**Answer: YES** ✅

**Best Practices Followed:**
1. ✅ **RAG over Full Rules:** Only relevant excerpts (~1500 chars)
2. ✅ **Semantic Search:** Vector similarity finds what matters
3. ✅ **Graceful Fallback:** Core rules if RAG fails
4. ✅ **Prompt Efficiency:** Doesn't bloat context window
5. ✅ **Dynamic:** Different rules for different requests

**Example:**
- Request: "aggressive low-cost deck"
- RAG finds: rules about lore, questing, early game strategy
- Request: "control deck with removal"
- RAG finds: rules about challenges, banishment, effects

## Summary

**Fixed:**
- ❌ 52-card decks when 60 required → ✅ Always meets minimum
- ❌ Hardcoded "60 cards" → ✅ Dynamic target size
- ❌ No rules context → ✅ RAG-based rules injection

**Validation Layers:**
1. Prompt instructions (prevention)
2. Finalize rejection (enforcement)  
3. Final safety check (guarantee)

**Rules Injection:**
- Smart RAG search for relevant rules
- ~1500 chars max (efficient)
- Falls back gracefully
- Proven pattern from existing service

**Ready to test!** 🚀
