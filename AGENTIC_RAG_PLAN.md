# Agentic RAG Architecture Plan

## Current Architecture (Stuffing RAG)
```
User Request → Embed → Search Qdrant (120 cards) 
→ Dump ALL cards into prompt → LLM generates deck → Done
```

**Problems:**
- Huge prompts (thousands of tokens)
- Context window limits card selection
- No iterative refinement
- Slow generation

## New Architecture (Agentic RAG)
```
User Request → Initial Strategy
↓
┌─────────────────────────────────────┐
│  AGENTIC LOOP (3-5 iterations)      │
│                                      │
│  LLM: Decides what to search for    │
│    ↓                                 │
│  System: Queries Qdrant              │
│    ↓                                 │
│  LLM: Reviews results, picks cards   │
│    ↓                                 │
│  System: Validates choices           │
│    ↓                                 │
│  LLM: Refines or declares done       │
└─────────────────────────────────────┘
↓
Final Deck Built
```

## Implementation Steps

### Phase 1: Tool/Function Definitions
Define functions LLM can call:
1. `search_cards(query, color_filter, cost_range, limit)` - Search for specific cards
2. `get_synergies(card_name)` - Find cards that work well with a card
3. `validate_deck(current_cards)` - Check legality/balance
4. `finalize_deck(cards)` - Complete the deck

### Phase 2: Conversation Loop
```fsharp
type AgentState = {
    CurrentDeck: (string * int) list  // (cardName, count)
    SearchHistory: string list
    Iteration: int
    Complete: bool
}

let rec agentLoop state maxIterations = task {
    if state.Complete || state.Iteration >= maxIterations then
        return state.CurrentDeck
    else
        // LLM decides next action
        let! action = getLLMDecision state
        // Execute action (search, add cards, etc)
        let! newState = executeAction action state
        // Continue loop
        return! agentLoop newState maxIterations
}
```

### Phase 3: Smart Prompting
Instead of dumping all cards:
```
"You're building an Amber/Amethyst aggro deck.
Current deck: 12 cards (28 more needed)
What type of cards should we search for next?
Options: 
- Low cost creatures (1-3)
- Removal/interaction
- Card draw
- Finishers
- Support cards"
```

### Phase 4: Structured Output
Use Qwen 2.5's structured output:
```json
{
  "action": "search_cards",
  "query": "low cost amber creatures with lore",
  "filters": { "cost_max": 3, "colors": ["Amber"] },
  "reasoning": "Need early game threats"
}
```

## Benefits

### Speed
- **Before:** 1 huge prompt, 100+ seconds
- **After:** 3-5 small prompts, 20-30 seconds total

### Quality  
- Targeted searches for each role
- Iterative refinement
- Can explore entire collection
- Better synergies

### Scalability
- Works with any collection size
- No context window limits
- Can add more card sets easily

## Migration Strategy

1. **Keep old endpoint** - `/api/deck` (fallback)
2. **New endpoint** - `/api/deck/agentic`
3. **Test new approach** with same queries
4. **Switch default** when proven better
5. **Remove old** after validation

## Timeline

- **Step 1:** Tool definitions (30 min)
- **Step 2:** Agent loop logic (1 hour)
- **Step 3:** Integration with Qdrant (30 min)
- **Step 4:** Testing & refinement (1 hour)

**Total: ~3 hours of focused work**

## Next Actions

1. Create new `AgenticDeckService.fs` 
2. Define tool/function schemas
3. Implement conversation loop
4. Add new endpoint in `Endpoints.fs`
5. Test with real queries

Ready to start?
