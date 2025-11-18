# Agentic Deck Builder - Architecture Redesign

## Current Problems

### What We Learned

1. **LLMs struggle with open-ended agent loops**
   - Models (qwen2.5:7b, llama3.1:8b, gemma3:12b) all get stuck in endless search loops
   - They don't naturally transition from "search" to "add_cards" actions
   - Even with explicit instructions, they repeat the same action indefinitely

2. **Context overload kills decision-making**
   - Returning 400+ cards (50KB+ of data) overwhelms the model
   - Even 20 cards with full details is too much information
   - Models lose track of what they're supposed to do

3. **JSON tool calling is unreliable**
   - Models add explanatory text before/after JSON
   - They hallucinate actions that don't exist
   - Deserialization frequently fails

4. **The agent never learns from previous iterations**
   - Search results don't influence next actions
   - No memory of what was already tried
   - No progress toward goal

---

## New Architecture: **Staged Pipeline with Focused LLM Calls**

Instead of one general-purpose agent loop, use a **deterministic pipeline** where each stage has a specific, narrow purpose.

### Core Principle
**"LLMs are good at single-purpose reasoning, not iterative workflows"**

---

## Pipeline Stages

### Stage 1: Intent Analysis (LLM)
**Input:** User request
**LLM Task:** Extract deck theme keywords only
**Output:** List of 2-5 keywords

```
Prompt: "Extract 2-5 keywords that describe this deck request: 'Build me a banish deck'"
Response: ["banish", "removal", "control"]
```

**Why it works:** Single, focused task. No iteration. Clear success criteria.

---

### Stage 2: Card Discovery (Deterministic + Vector Search)
**Input:** Keywords from Stage 1
**Task:** 
1. Search Qdrant with each keyword (parallel)
2. Merge results, deduplicate
3. Return top 50 cards by relevance score

**Output:** 50 relevant cards

**Why it works:** No LLM needed. Pure vector search and ranking.

---

### Stage 3: Color Selection (LLM)
**Input:** 
- User request
- Top 50 cards (minimal format: name, color, cost only)

**LLM Task:** Choose 1-2 colors that best support the theme

```json
Prompt: "Given these cards, which 1-2 colors best support a 'banish' theme?"
Response: {"colors": ["Amber", "Steel"], "reasoning": "Most banish effects"}
```

**Why it works:** Single decision. Small dataset (50 cards x 3 fields = ~5KB).

---

### Stage 4: Card Filtering (Deterministic)
**Input:** 
- 50 cards from Stage 2
- 1-2 colors from Stage 3

**Task:** Filter cards to only chosen colors

**Output:** ~20-30 cards in chosen colors

**Why it works:** Simple filter. No LLM needed.

---

### Stage 5: Deck Core Selection (LLM)
**Input:**
- User request
- 20-30 filtered cards (compact format)

**LLM Task:** Select 8-12 "core" cards (4x each = 32-48 cards total)

```json
Prompt: "Select 8-12 core cards for this deck. Return card names only."
Response: {
  "coreCards": [
    "Hades - Lord of the Underworld",
    "Maleficent - Monstrous Dragon",
    ...
  ]
}
```

**Why it works:** Single focused selection. Clear count. No iteration.

---

### Stage 6: Synergy Search (Deterministic + Vector Search)
**Input:** 8-12 core cards

**Task:**
1. For each core card, search Qdrant for cards with similar text/effects
2. Merge results, deduplicate, exclude already-selected cards
3. Return top 20 synergy cards

**Output:** 20 synergy candidates

**Why it works:** No LLM. Pure semantic similarity.

---

### Stage 7: Synergy Selection (LLM)
**Input:**
- Core cards (already selected)
- 20 synergy candidates

**LLM Task:** Select 3-6 synergy cards (4x each = 12-24 cards total)

**Output:** 3-6 synergy card names

**Why it works:** Single focused selection. Small candidate pool.

---

### Stage 8: Deck Completion (Deterministic)
**Input:** 
- Core cards (32-48 cards)
- Synergy cards (12-24 cards)
- Target size (60 cards)

**Task:**
1. Calculate remaining slots
2. Fill with most-played cards from chosen colors (from card database stats)
3. Ensure 60 cards total

**Output:** Complete 60-card deck

**Why it works:** Deterministic fill. No decisions needed.

---

## Implementation Plan

### Phase 1: MVP Pipeline (No LLM)
Build deterministic version to prove pipeline works:
1. Keyword extraction (hardcoded: split user request)
2. Vector search (existing)
3. Color selection (heuristic: count colors in top 50)
4. Filter by color
5. Select top 15 cards by score (4x each = 60 cards)

**Goal:** Working end-to-end pipeline without LLM complexity.

### Phase 2: Add LLM for Color Selection
Replace color heuristic with LLM call (Stage 3).

### Phase 3: Add LLM for Core Selection
Replace top-15 heuristic with LLM selection (Stage 5).

### Phase 4: Add Synergy Stage
Implement Stages 6-7 (synergy search + selection).

### Phase 5: Add Intent Analysis
Improve keyword extraction with LLM (Stage 1).

---

## Key Advantages

### 1. **No Agent Loop**
- Each stage runs once
- Linear pipeline, predictable flow
- No risk of infinite loops

### 2. **Focused LLM Tasks**
- Each LLM call has ONE job
- Clear input/output contracts
- Success/failure is obvious

### 3. **Small Context Windows**
- Never more than 50 cards at once
- Compact card representation (name, color, cost)
- LLM doesn't need full card text

### 4. **Deterministic Fallbacks**
- If LLM fails, use heuristics
- Pipeline always completes
- Graceful degradation

### 5. **Testable & Debuggable**
- Each stage can be tested independently
- Easy to identify which stage fails
- Can run stages manually for debugging

### 6. **Fast**
- Only 3-4 LLM calls total
- Rest is fast vector search and filtering
- Parallel searches where possible

---

## Error Handling

### LLM Failures
- **Stage 1 (Keywords):** Fallback to simple word extraction
- **Stage 3 (Colors):** Fallback to color frequency heuristic
- **Stage 5 (Core):** Fallback to top-N by score
- **Stage 7 (Synergy):** Fallback to top-N by similarity score

### Insufficient Cards
- If filtered cards < 15: relax color constraint (add 3rd color)
- If still insufficient: use all colors, sort by relevance

---

## Data Formats

### Compact Card Format (for LLM)
```json
{
  "name": "Hades - Lord of the Underworld",
  "color": "Amethyst",
  "cost": 7,
  "type": "Character",
  "inkable": true
}
```

**Exclude:**
- Full text (too verbose)
- Set/collector info (not relevant for gameplay)
- URLs (not needed)

Only include what LLM needs to make decisions.

---

## Metrics & Observability

### Track Per-Stage:
- Duration
- Success/failure
- Fallback usage
- Card counts

### Track Overall:
- Total pipeline time
- Deck quality (via user feedback)
- LLM cost (token usage)

---

## Migration Strategy

### Week 1: Build MVP (Phase 1)
- Implement deterministic pipeline
- Prove 60-card decks can be built reliably
- Establish baseline performance

### Week 2: Add Color LLM (Phase 2)
- Single focused LLM call
- A/B test vs heuristic
- Measure improvement

### Week 3: Add Core Selection LLM (Phase 3)
- More sophisticated card selection
- Compare deck quality

### Week 4: Synergy System (Phase 4)
- Multi-stage card selection
- Improved deck coherence

### Week 5: Polish & Optimization
- Intent analysis
- Performance tuning
- Error handling improvements

---

## Success Criteria

### Must Have:
- ✅ Always produces 60-card decks
- ✅ Respects color constraints
- ✅ Completes in <10 seconds
- ✅ Works with all LLM models

### Nice to Have:
- Competitive deck quality
- Synergy between cards
- Respects format legality

---

## Lessons Applied

1. **"Don't ask LLMs to iterate"** → Pipeline is linear, each stage runs once
2. **"Small context windows"** → Never show >50 cards, minimal fields
3. **"Single-purpose calls"** → Each LLM call does ONE thing
4. **"Deterministic fallbacks"** → Every LLM stage has a non-LLM backup
5. **"Test without LLM first"** → MVP uses zero LLM calls

---

## Next Steps

1. Document current agent code as "deprecated"
2. Create new `DeckBuilder.Pipeline` project
3. Implement Phase 1 (deterministic MVP)
4. Run comparison tests: old agent vs new pipeline
5. Gradually add LLM stages (Phase 2-5)

---

**Status:** Design Complete - Ready for Implementation
**Author:** AI Assistant
**Date:** 2025-01-18