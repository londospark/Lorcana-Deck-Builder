namespace DeckBuilder.Shared

[<CLIMutable>]
type DeckQuery = {
    request: string
    deckSize: int
    selectedColors: string[] option
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
