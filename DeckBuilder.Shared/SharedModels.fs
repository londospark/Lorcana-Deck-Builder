namespace DeckBuilder.Shared

type DeckFormat =
    | Core
    | Infinity

[<CLIMutable>]
type DeckQuery = {
    request: string
    deckSize: int
    selectedColors: string[] option
    format: DeckFormat option
}

[<CLIMutable>]
type CardEntry = {
    count: int
    fullName: string
    inkable: bool
    cardMarketUrl: string
    inkColor: string
}

[<CLIMutable>]
type DeckResponse = {
    cards: CardEntry array
    explanation: string
}
