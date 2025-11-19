namespace DeckBuilder.Shared

type DeckFormat =
    | Core
    | Infinity

[<CLIMutable>]
type DeckQuery = {
    request: string
    deckSize: int
    selectedColors: string[] option
    format: DeckFormat
}

[<CLIMutable>]
type CardEntry = {
    count: int
    fullName: string
    inkable: bool
    cardMarketUrl: string
    inkColor: string
    cost: int option
    subtypes: string array
}

[<CLIMutable>]
type DeckResponse = {
    cards: CardEntry array
    explanation: string
}
