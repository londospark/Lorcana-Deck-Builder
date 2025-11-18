module ApiModels

open DeckBuilder.Shared

// Re-export shared models to keep existing `open ApiModels` usages working

type DeckQuery = DeckBuilder.Shared.DeckQuery

type CardEntry = DeckBuilder.Shared.CardEntry

type DeckResponse = DeckBuilder.Shared.DeckResponse

// Internal card data type used during deck building
type CardData = {
    FullName: string
    Artist: string
    SetName: string
    Classifications: string list
    SetId: int
    CardNum: int
    InkColor: string
    CardMarketUrl: string
    Rarity: string
    InkCost: int
    Inkwell: bool
    Strength: int option
    Willpower: int option
    LoreValue: int option
    FullText: string
    Set_Num: string
}
