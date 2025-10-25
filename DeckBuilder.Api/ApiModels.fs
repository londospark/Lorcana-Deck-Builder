module ApiModels

open DeckBuilder.Shared

// Re-export shared models to keep existing `open ApiModels` usages working

type DeckQuery = DeckBuilder.Shared.DeckQuery

type CardEntry = DeckBuilder.Shared.CardEntry

type DeckResponse = DeckBuilder.Shared.DeckResponse
