namespace DeckBuilder.Shared

type DeckFormat =
    | Core
    | Infinite

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
}

[<CLIMutable>]
type DeckResponse = {
    cards: CardEntry array
    explanation: string
}
