
module DeckBuilder.Ui.Program

open System
open System.Text
open System.Text.Json
open System.Net.Http
open Bolero
open Bolero.Html
open Elmish
open Microsoft.AspNetCore.Components.WebAssembly.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open OpenTelemetry
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open OpenTelemetry.Metrics
open OpenTelemetry.Exporter
open System.Text.Json.Serialization

type CardVM = { count:int; fullName:string; inkable:bool; cardMarketUrl:string; inkColor:string }

type Model = {
    Request: string
    DeckSize: int
    SelectedColor1: string option
    SelectedColor2: string option
    SelectedFormat: DeckBuilder.Shared.DeckFormat
    IsBuilding: bool
    Result: string
    Cards: CardVM array
    Color1DropdownOpen: bool
    Color2DropdownOpen: bool
    FormatDropdownOpen: bool
}

let initModel = { 
    Request = ""
    DeckSize = 60
    SelectedColor1 = None
    SelectedColor2 = None
    SelectedFormat = DeckBuilder.Shared.DeckFormat.Core
    IsBuilding = false
    Result = ""
    Cards = [||]
    Color1DropdownOpen = false
    Color2DropdownOpen = false
    FormatDropdownOpen = false
}

type Message =
    | SetRequest of string
    | SetDeckSize of int
    | SetColor1 of string option
    | SetColor2 of string option
    | SetFormat of DeckBuilder.Shared.DeckFormat
    | ToggleColor1Dropdown
    | ToggleColor2Dropdown
    | ToggleFormatDropdown
    | CloseAllDropdowns
    | Build
    | Built of string * CardVM array
    | BuildError of string

module Api =
    open DeckBuilder.Shared
    // Alias shared models to keep module-local names
    type DeckQuery = DeckBuilder.Shared.DeckQuery
    type CardDto = DeckBuilder.Shared.CardEntry
    type DeckResponse = DeckBuilder.Shared.DeckResponse

    let mutable client : HttpClient option = None
    let setClient (c:HttpClient) = client <- Some c

    let tryPost (client:HttpClient) (url:string) (payload:string) = task {
        use content = new StringContent(payload, Encoding.UTF8, "application/json")
        let! resp = client.PostAsync(url, content)
        let! body = resp.Content.ReadAsStringAsync()
        return resp, body
    }

    let parseDeckResponse (body: string) =
        try
            let options = JsonSerializerOptions()
            options.Converters.Add(JsonFSharpConverter())
            let d = JsonSerializer.Deserialize<DeckResponse>(body, options)
            if isNull (box d) then (body, [||]) else
            let expl = if String.IsNullOrWhiteSpace d.explanation then "" else "\n\nExplanation:\n" + d.explanation
            if not (isNull d.cards) && d.cards.Length > 0 then
                let cards = d.cards |> Array.toList
                let total = cards |> List.sumBy (fun t -> t.count)
                let inkable = cards |> List.sumBy (fun t -> if t.inkable then t.count else 0)
                let sorted =
                    cards
                    |> List.sortWith (fun a b ->
                        if a.count <> b.count then compare b.count a.count
                        else compare (a.fullName.ToLowerInvariant()) (b.fullName.ToLowerInvariant()))
                let lines =
                    sorted
                    |> List.map (fun t ->
                        let name = t.fullName
                        let nameWithLink = if not (String.IsNullOrWhiteSpace t.cardMarketUrl) then sprintf "%s (%s)" name t.cardMarketUrl else name
                        if t.count > 1 then sprintf "%d x %s" t.count nameWithLink else nameWithLink)
                let header = sprintf "Deck (%d cards, %d inkable):" total inkable
                let bodyText = String.Join('\n', lines)
                let finalText = header + "\n" + bodyText
                (finalText + expl, d.cards)
            else
                ("[No cards returned]" + expl, d.cards)
        with _ -> (body, [||])

    let buildDeck (model:Model) = task {
        Console.WriteLine("buildDeck called with request: " + model.Request)
        
        let payload =
            let selColors =
                match model.SelectedColor1, model.SelectedColor2 with
                | Some c1, Some c2 when not (String.IsNullOrWhiteSpace c1) && not (String.IsNullOrWhiteSpace c2) && not (String.Equals(c1, c2, StringComparison.OrdinalIgnoreCase)) -> Some [| c1; c2 |]
                | _ -> None
            let q = { 
                request = model.Request
                deckSize = model.DeckSize
                selectedColors = selColors
                format = model.SelectedFormat
            }
            let options = JsonSerializerOptions()
            options.Converters.Add(JsonFSharpConverter())
            let serialized = JsonSerializer.Serialize(q, options)
            Console.WriteLine("Payload: " + serialized)
            serialized
            
        match client with
        | None -> 
            Console.WriteLine("HttpClient not initialized!")
            return ("[ERROR] HttpClient not initialized", [||])
        | Some c ->
            Console.WriteLine($"Calling API at base address: {c.BaseAddress}")
            Console.WriteLine($"Full URL will be: {c.BaseAddress}api/deck")
            try
                Console.WriteLine("About to call tryPost (deterministic mode)...")
                let! resp, body = tryPost c "/api/deck" payload
                Console.WriteLine($"Response received! Status: {resp.StatusCode}")
                Console.WriteLine($"Response body length: {body.Length}")
                Console.WriteLine($"Response body: {body}")
                if resp.IsSuccessStatusCode then
                    Console.WriteLine("Success! Parsing response...")
                    return parseDeckResponse body
                else
                    Console.WriteLine($"Error response: {int resp.StatusCode}")
                    return ($"[ERROR] API call failed. Status={int resp.StatusCode}, Body={body}", [||])
            with ex ->
                Console.WriteLine($"Exception caught: {ex.GetType().Name}")
                Console.WriteLine($"Exception message: {ex.Message}")
                Console.WriteLine($"Stack trace: {ex.StackTrace}")
                return ($"[ERROR] {ex.Message}", [||])
    }

let update message model =
    match message with
    | SetRequest r -> { model with Request = r }, Cmd.none
    | SetDeckSize n -> { model with DeckSize = n }, Cmd.none
    | SetColor1 oc -> { model with SelectedColor1 = oc; Color1DropdownOpen = false }, Cmd.none
    | SetColor2 oc -> { model with SelectedColor2 = oc; Color2DropdownOpen = false }, Cmd.none
    | SetFormat f -> { model with SelectedFormat = f; FormatDropdownOpen = false }, Cmd.none
    | ToggleColor1Dropdown -> { model with Color1DropdownOpen = not model.Color1DropdownOpen; Color2DropdownOpen = false; FormatDropdownOpen = false }, Cmd.none
    | ToggleColor2Dropdown -> { model with Color2DropdownOpen = not model.Color2DropdownOpen; Color1DropdownOpen = false; FormatDropdownOpen = false }, Cmd.none
    | ToggleFormatDropdown -> { model with FormatDropdownOpen = not model.FormatDropdownOpen; Color1DropdownOpen = false; Color2DropdownOpen = false }, Cmd.none
    | CloseAllDropdowns -> { model with Color1DropdownOpen = false; Color2DropdownOpen = false; FormatDropdownOpen = false }, Cmd.none
    | Build ->
        Console.WriteLine("Build message received!")
        Console.WriteLine("Model request: " + model.Request)
        Console.WriteLine($"Model deckSize: {model.DeckSize}")
        let cmd =
            Cmd.OfTask.either
                (fun m -> 
                    Console.WriteLine("Starting API call task...")
                    Api.buildDeck m)
                model
                (fun (text, cards) -> 
                    Console.WriteLine("Build succeeded: " + text)
                    Built (text, cards |> Array.map (fun c -> { count = c.count; fullName = c.fullName; inkable = c.inkable; cardMarketUrl = c.cardMarketUrl; inkColor = c.inkColor })))
                (fun ex -> 
                    Console.WriteLine("Build failed: " + ex.Message)
                    BuildError ex.Message)
        Console.WriteLine("Command created, updating model...")
        { model with IsBuilding = true; Result = ""; Cards = [||] }, cmd
    | Built (text, cards) ->
        Console.WriteLine($"Built message received with {cards.Length} cards")
        let vms = cards |> Array.map (fun c -> { count = c.count; fullName = c.fullName; inkable = c.inkable; cardMarketUrl = c.cardMarketUrl; inkColor = c.inkColor })
        { model with IsBuilding = false; Result = text; Cards = vms }, Cmd.none
    | BuildError msg ->
        Console.WriteLine("BuildError message received: " + msg)
        { model with IsBuilding = false; Result = $"[ERROR] {msg}"; Cards = [||] }, Cmd.none

let getInkColorClass (color: string) =
    match color.ToLowerInvariant() with
    | "amber" -> "bg-lorcana-amber text-amber-950"
    | "amethyst" -> "bg-lorcana-amethyst text-white"
    | "emerald" -> "bg-lorcana-emerald text-emerald-950"
    | "ruby" -> "bg-lorcana-ruby text-white"
    | "sapphire" -> "bg-lorcana-sapphire text-white"
    | "steel" -> "bg-lorcana-steel text-white"
    | _ -> "bg-gray-400 text-white"

let getInkColorGradient (color: string) =
    match color.ToLowerInvariant() with
    | "amber" -> "from-yellow-400 via-orange-400 to-amber-600"
    | "amethyst" -> "from-purple-400 via-purple-500 to-purple-700"
    | "emerald" -> "from-emerald-400 via-green-500 to-emerald-700"
    | "ruby" -> "from-red-400 via-red-500 to-red-700"
    | "sapphire" -> "from-blue-400 via-blue-500 to-blue-700"
    | "steel" -> "from-gray-400 via-gray-500 to-gray-700"
    | _ -> "from-gray-400 via-gray-500 to-gray-600"

let customDropdown (labelText: string) (selected: string) (options: (string * string) list) (isOpen: bool) (onToggle: unit -> unit) (onSelect: string -> unit) =
    div {
        attr.``class`` "relative z-50"
        
        label { 
            attr.``class`` "block text-xs text-gray-300 mb-2"
            text labelText 
        }
        
        // Dropdown button
        button {
            attr.``type`` "button"
            attr.``class`` "w-full px-4 py-3 bg-white/10 backdrop-blur-xl border border-white/30 rounded-lg text-white text-left focus:outline-none focus:ring-2 focus:ring-blue-400 focus:border-transparent transition-all duration-200 hover:bg-white/15 shadow-lg hover:shadow-xl flex items-center justify-between"
            on.click (fun _ -> onToggle())
            
            span { 
                attr.``class`` "flex items-center gap-2"
                if selected = "" then
                    span { 
                        attr.``class`` "text-gray-400"
                        text "-- Select --" 
                    }
                else
                    span { 
                        attr.``class`` "font-medium"
                        text selected 
                    }
            }
            
            // Arrow icon
            span {
                attr.``class`` (if isOpen then "transform rotate-180 transition-transform duration-200" else "transition-transform duration-200")
                text "▼"
            }
        }
        
        // Dropdown menu
        if isOpen then
            div {
                attr.``class`` "absolute z-[9999] w-full mt-2 bg-slate-900/95 backdrop-blur-2xl border border-white/30 rounded-xl shadow-2xl overflow-hidden"
                
                forEach options (fun (value, displayText) ->
                    button {
                        attr.``type`` "button"
                        attr.``class`` (
                            let baseClasses = "w-full px-4 py-3 text-left transition-all duration-150 font-medium"
                            if value = "" then
                                baseClasses + " text-gray-300 hover:bg-white/10"
                            else
                                baseClasses + " bg-gradient-to-r " + getInkColorGradient value + " text-white hover:scale-[1.02] shadow-md"
                        )
                        on.click (fun _ -> onSelect value)
                        text displayText
                    })
            }
        else
            empty()
    }

let customFormatDropdown (selected: DeckBuilder.Shared.DeckFormat) (isOpen: bool) (onToggle: unit -> unit) (onSelect: DeckBuilder.Shared.DeckFormat -> unit) =
    div {
        attr.``class`` "relative z-50"
        
        label { 
            attr.``class`` "block text-sm font-semibold text-gray-200 mb-2"
            text "Format" 
        }
        
        // Dropdown button
        button {
            attr.``type`` "button"
            attr.``class`` "w-full px-4 py-3 bg-white/10 backdrop-blur-xl border border-white/30 rounded-lg text-white text-left focus:outline-none focus:ring-2 focus:ring-blue-400 focus:border-transparent transition-all duration-200 hover:bg-white/15 shadow-lg hover:shadow-xl flex items-center justify-between"
            on.click (fun _ -> onToggle())
            
            span { 
                text (match selected with
                      | DeckBuilder.Shared.DeckFormat.Core -> "Core (Standard)"
                      | DeckBuilder.Shared.DeckFormat.Infinity -> "Infinity (All Cards)")
            }
            
            // Arrow icon
            span {
                attr.``class`` (if isOpen then "transform rotate-180 transition-transform duration-200" else "transition-transform duration-200")
                text "▼"
            }
        }
        
        // Dropdown menu
        if isOpen then
            div {
                attr.``class`` "absolute z-[9999] w-full mt-2 bg-slate-900/95 backdrop-blur-2xl border border-white/30 rounded-xl shadow-2xl overflow-hidden"
                
                button {
                    attr.``type`` "button"
                    attr.``class`` "w-full px-4 py-3 text-left text-white hover:bg-white/10 transition-all duration-150 font-medium border-b border-white/10"
                    on.click (fun _ -> onSelect DeckBuilder.Shared.DeckFormat.Core)
                    text "Core (Standard)"
                }
                
                button {
                    attr.``type`` "button"
                    attr.``class`` "w-full px-4 py-3 text-left text-white hover:bg-white/10 transition-all duration-150 font-medium"
                    on.click (fun _ -> onSelect DeckBuilder.Shared.DeckFormat.Infinity)
                    text "Infinity (All Cards)"
                }
            }
        else
            empty()
        
        p { 
            attr.``class`` "text-xs text-gray-400 mt-2"
            text (match selected with
                  | DeckBuilder.Shared.DeckFormat.Core -> "Standard rotation with currently legal cards"
                  | DeckBuilder.Shared.DeckFormat.Infinity -> "All cards available, no restrictions")
        }
    }

let view model dispatch =
    // Determine background gradient based on selected colors
    let bgGradient = 
        match model.SelectedColor1, model.SelectedColor2 with
        | Some "Amber", Some "Amethyst" -> "bg-gradient-to-br from-yellow-900 via-purple-900 to-slate-900"
        | Some "Amber", Some "Emerald" -> "bg-gradient-to-br from-yellow-900 via-green-900 to-slate-900"
        | Some "Amber", Some "Ruby" -> "bg-gradient-to-br from-yellow-900 via-red-900 to-slate-900"
        | Some "Amber", Some "Sapphire" -> "bg-gradient-to-br from-yellow-900 via-blue-900 to-slate-900"
        | Some "Amber", Some "Steel" -> "bg-gradient-to-br from-yellow-900 via-gray-800 to-slate-900"
        | Some "Amethyst", Some "Emerald" -> "bg-gradient-to-br from-purple-900 via-green-900 to-slate-900"
        | Some "Amethyst", Some "Ruby" -> "bg-gradient-to-br from-purple-900 via-red-900 to-slate-900"
        | Some "Amethyst", Some "Sapphire" -> "bg-gradient-to-br from-purple-900 via-blue-900 to-slate-900"
        | Some "Amethyst", Some "Steel" -> "bg-gradient-to-br from-purple-900 via-gray-800 to-slate-900"
        | Some "Emerald", Some "Ruby" -> "bg-gradient-to-br from-green-900 via-red-900 to-slate-900"
        | Some "Emerald", Some "Sapphire" -> "bg-gradient-to-br from-green-900 via-blue-900 to-slate-900"
        | Some "Emerald", Some "Steel" -> "bg-gradient-to-br from-green-900 via-gray-800 to-slate-900"
        | Some "Ruby", Some "Sapphire" -> "bg-gradient-to-br from-red-900 via-blue-900 to-slate-900"
        | Some "Ruby", Some "Steel" -> "bg-gradient-to-br from-red-900 via-gray-800 to-slate-900"
        | Some "Sapphire", Some "Steel" -> "bg-gradient-to-br from-blue-900 via-gray-800 to-slate-900"
        | Some "Amber", _ | _, Some "Amber" -> "bg-gradient-to-br from-yellow-900 via-slate-900 to-slate-900"
        | Some "Amethyst", _ | _, Some "Amethyst" -> "bg-gradient-to-br from-purple-900 via-slate-900 to-slate-900"
        | Some "Emerald", _ | _, Some "Emerald" -> "bg-gradient-to-br from-green-900 via-slate-900 to-slate-900"
        | Some "Ruby", _ | _, Some "Ruby" -> "bg-gradient-to-br from-red-900 via-slate-900 to-slate-900"
        | Some "Sapphire", _ | _, Some "Sapphire" -> "bg-gradient-to-br from-blue-900 via-slate-900 to-slate-900"
        | Some "Steel", _ | _, Some "Steel" -> "bg-gradient-to-br from-gray-800 via-slate-900 to-slate-900"
        | _ -> "bg-gradient-to-br from-blue-900 via-slate-900 to-slate-900" // Default to blue
    
    div {
        attr.``class`` (bgGradient + " min-h-screen py-12 px-4 sm:px-6 lg:px-8 transition-colors duration-1000")
        
        div {
            attr.``class`` "max-w-4xl mx-auto"
            
            // Header
            div {
                attr.``class`` "text-center mb-12"
                h1 { 
                    attr.``class`` "text-5xl font-bold text-transparent bg-clip-text bg-gradient-to-r from-cyan-400 via-blue-400 to-indigo-400 mb-4"
                    text "✨ Lorcana Deck Builder"
                }
                p { 
                    attr.``class`` "text-gray-300 text-lg"
                    text "AI-powered deck building for Disney Lorcana"
                }
            }
            
            // Main card (raised z-index so dropdowns overlay results)
            div {
                attr.``class`` "relative z-20 bg-white/10 backdrop-blur-md rounded-2xl shadow-2xl border border-white/20 p-8 space-y-6"
                
                // Request field
                div {
                    label {
                        attr.``class`` "block text-sm font-semibold text-gray-200 mb-2"
                        attr.``for`` "request"
                        text "Deck Request"
                    }
                    p { 
                        attr.``class`` "text-xs text-gray-400 mb-3"
                        text "Describe your ideal deck. Include strategy, themes, and playstyle preferences."
                    }
                    textarea {
                        attr.id "request"
                        attr.``class`` "w-full px-4 py-3 bg-white/5 border border-white/20 rounded-lg text-white placeholder-gray-300 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent transition"
                        on.input (fun e -> dispatch (SetRequest (string e.Value)))
                        attr.rows 5
                        attr.placeholder "e.g., Build an aggressive Amber/Amethyst deck focused on quick lore gain with low-curve characters..."
                        text model.Request
                    }
                }
                
                // Deck size and format row
                div {
                    attr.``class`` "grid grid-cols-1 md:grid-cols-2 gap-6"
                    
                    // Deck size
                    div {
                        label {
                            attr.``class`` "block text-sm font-semibold text-gray-200 mb-2"
                            attr.``for`` "decksize"
                            text "Deck Size"
                        }
                        input {
                            attr.id "decksize"
                            attr.``type`` "number"
                            attr.``class`` "w-full px-4 py-3 bg-white/5 border border-white/20 rounded-lg text-white focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent transition"
                            attr.value (string model.DeckSize)
                            attr.placeholder "60"
                            on.input (fun e ->
                                let s = string e.Value
                                let mutable v = 0
                                if Int32.TryParse(s, &v) then dispatch (SetDeckSize v))
                        }
                    }
                    
                    // Format
                    customFormatDropdown 
                        model.SelectedFormat 
                        model.FormatDropdownOpen 
                        (fun () -> dispatch ToggleFormatDropdown) 
                        (fun f -> dispatch (SetFormat f))
                }
                
                // Ink colors
                div {
                    label {
                        attr.``class`` "block text-sm font-semibold text-gray-200 mb-3"
                        text "Ink Colors (Optional)"
                    }
                    p { 
                        attr.``class`` "text-xs text-gray-400 mb-4"
                        text "Choose up to two ink colors to constrain your deck. Leave blank for AI to decide."
                    }
                    
                    div {
                        attr.``class`` "grid grid-cols-1 md:grid-cols-2 gap-4"
                        
                        
                        // Color 1
                        customDropdown 
                            "Primary Color"
                            (defaultArg model.SelectedColor1 "")
                            [("", "-- Any --"); ("Amber", "⚡ Amber"); ("Amethyst", "💎 Amethyst"); ("Emerald", "🌿 Emerald"); ("Ruby", "🔥 Ruby"); ("Sapphire", "💧 Sapphire"); ("Steel", "⚔️ Steel")]
                            model.Color1DropdownOpen
                            (fun () -> dispatch ToggleColor1Dropdown)
                            (fun v -> dispatch (SetColor1 (if String.IsNullOrWhiteSpace v then None else Some v)))
                        
                        // Color 2
                        customDropdown 
                            "Secondary Color"
                            (defaultArg model.SelectedColor2 "")
                            [("", "-- Any --"); ("Amber", "⚡ Amber"); ("Amethyst", "💎 Amethyst"); ("Emerald", "🌿 Emerald"); ("Ruby", "🔥 Ruby"); ("Sapphire", "💧 Sapphire"); ("Steel", "⚔️ Steel")]
                            model.Color2DropdownOpen
                            (fun () -> dispatch ToggleColor2Dropdown)
                            (fun v -> dispatch (SetColor2 (if String.IsNullOrWhiteSpace v then None else Some v)))
                    }
                    
                    // Display selected colors
                    if model.SelectedColor1.IsSome || model.SelectedColor2.IsSome then
                        div {
                            attr.``class`` "mt-4 flex gap-2 items-center"
                            span { 
                                attr.``class`` "text-sm text-gray-300"
                                text "Selected:" 
                            }
                            if model.SelectedColor1.IsSome then
                                span { 
                                    attr.``class`` ("inline-flex items-center px-3 py-1 rounded-full text-xs font-semibold " + getInkColorClass model.SelectedColor1.Value)
                                    text model.SelectedColor1.Value
                                }
                            if model.SelectedColor2.IsSome then
                                span { 
                                    attr.``class`` ("inline-flex items-center px-3 py-1 rounded-full text-xs font-semibold " + getInkColorClass model.SelectedColor2.Value)
                                    text model.SelectedColor2.Value
                                }
                        }
                }
                
                // Build button
                button {
                    let buttonClasses = 
                        if model.IsBuilding then
                            "w-full py-4 px-6 rounded-lg font-semibold text-white transition-all duration-200 bg-blue-600/50 cursor-not-allowed"
                        else
                            "w-full py-4 px-6 rounded-lg font-semibold text-white transition-all duration-200 bg-gradient-to-r from-blue-600 to-cyan-600 hover:from-blue-700 hover:to-cyan-700 transform hover:scale-[1.02] active:scale-[0.98] cursor-pointer shadow-lg hover:shadow-xl"
                    attr.``class`` buttonClasses
                    attr.disabled model.IsBuilding
                    on.click (fun _ -> dispatch Build)
                    if model.IsBuilding then
                        concat {
                            span { 
                                attr.``class`` "inline-block animate-spin mr-2"
                                text "⚡"
                            }
                            text "Building your deck..."
                        }
                    else
                        text "🎴 Build My Deck"
                }
                
                // Building status
                if model.IsBuilding then
                    div {
                        attr.``class`` "bg-blue-500/20 border border-blue-500/30 rounded-lg p-4 text-center"
                        p { 
                            attr.``class`` "text-blue-200 font-medium mb-1"
                            text "Building your deck..."
                        }
                        p { 
                            attr.``class`` "text-blue-300/70 text-sm"
                            text "Searching cards and assembling your deck"
                        }
                        p { 
                            attr.``class`` "text-blue-400/60 text-xs mt-2"
                            text "This should only take a few seconds ⚡"
                        }
                    }
            }
            
            // Results section
            if not (String.IsNullOrWhiteSpace model.Result) && not model.IsBuilding && model.Cards.Length > 0 then
                div {
                    attr.``class`` "mt-8 bg-white/10 backdrop-blur-md rounded-2xl shadow-2xl border border-white/20 p-8 relative z-0"
                    
                    // Stats header
                    let total = model.Cards |> Array.sumBy _.count
                    let inkable = model.Cards |> Array.sumBy (fun c -> if c.inkable then c.count else 0)
                    
                    div {
                        attr.``class`` "mb-6"
                        h2 { 
                            attr.``class`` "text-2xl font-bold text-white mb-4 flex items-center gap-2"
                            span { text "🎴" }
                            text "Your Deck"
                        }
                        div {
                            attr.``class`` "flex gap-4 text-sm"
                            div {
                                attr.``class`` "bg-blue-500/20 border border-blue-500/30 rounded-lg px-4 py-2"
                                span { 
                                    attr.``class`` "text-blue-300 font-semibold"
                                    text $"{total} cards"
                                }
                            }
                            div {
                                attr.``class`` "bg-purple-500/20 border border-purple-500/30 rounded-lg px-4 py-2"
                                span { 
                                    attr.``class`` "text-purple-300 font-semibold"
                                    text $"{inkable} inkable"
                                }
                            }
                        }
                    }
                    
                    // Card list
                    div {
                        attr.``class`` "space-y-2"
                        forEach (model.Cards |> Array.sortBy _.fullName |> Array.sortByDescending _.count) (fun c ->
                            div {
                                attr.``class`` "bg-white/5 border border-white/10 rounded-lg p-4 hover:bg-white/10 transition-colors flex items-center justify-between"
                                
                                div {
                                    attr.``class`` "flex items-center gap-4 flex-1"
                                    
                                    // Count badge
                                    span { 
                                        attr.``class`` "flex-shrink-0 w-10 h-10 rounded-full bg-gradient-to-br from-blue-500 to-cyan-500 flex items-center justify-center font-bold text-white shadow-lg"
                                        text (string c.count)
                                    }
                                    
                                    // Card name
                                    div {
                                        attr.``class`` "flex-1 flex items-center gap-2 flex-wrap"
                                        
                                        // Ink color badge
                                        if not (String.IsNullOrWhiteSpace c.inkColor) then
                                            span { 
                                                attr.``class`` ("inline-flex items-center px-2 py-1 rounded-lg text-xs font-bold shadow-md " + getInkColorClass c.inkColor)
                                                text c.inkColor
                                            }
                                        
                                        if String.IsNullOrWhiteSpace c.cardMarketUrl then
                                            span { 
                                                attr.``class`` "text-white font-medium"
                                                text c.fullName 
                                            }
                                        else
                                            a { 
                                                attr.href c.cardMarketUrl
                                                attr.target "_blank"
                                                attr.``class`` "text-blue-400 hover:text-blue-300 font-medium hover:underline transition"
                                                text c.fullName
                                            }
                                        
                                        // Inkable badge
                                        if c.inkable then
                                            span { 
                                                attr.``class`` "inline-flex items-center px-2 py-0.5 rounded text-xs bg-cyan-500/20 text-cyan-300 border border-cyan-500/30"
                                                text "💧 Inkable"
                                            }
                                    }
                                }
                            })
                    }
                }
        }
    }

type MyApp() =
    inherit ProgramComponent<Model, Message>()

    override this.Program =
        let init () = initModel, Cmd.none
        Program.mkProgram (fun _ -> init ()) update view

[<EntryPoint>]
let main args =
    let builder = WebAssemblyHostBuilder.CreateDefault(args)
    builder.RootComponents.Add<MyApp>("#app")
    
    // API calls are relative (same origin) - server proxies to backend
    let apiBaseUrl = builder.HostEnvironment.BaseAddress
    
    Console.WriteLine($"API Base URL: {apiBaseUrl}")
    
    builder.Services.AddScoped<HttpClient>(fun _ -> 
        let client = new HttpClient(BaseAddress = Uri(apiBaseUrl))
        client.Timeout <- TimeSpan.FromMinutes(5.0) // 5 minute timeout for deck building
        client) |> ignore

    // OpenTelemetry in Blazor WASM: guard initialization to avoid blocking UI if exporter isn't supported.
    // In browsers, gRPC OTLP isn't supported and HTTP OTLP may be blocked by CORS; proceed best-effort.
    try
        let serviceVersion = "1.0.0"
        let tp =
            Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("DeckBuilder.Ui", serviceVersion = serviceVersion))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(fun o ->
                    // Prefer HTTP/protobuf if available; fall back is no-op if endpoint missing.
                    // Note: In WASM, only http/protobuf is viable; gRPC is not.
                    o.Protocol <- OtlpExportProtocol.HttpProtobuf)
                .Build()
        tp |> ignore
        let mp =
            Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("DeckBuilder.Ui", serviceVersion = serviceVersion))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(fun o -> o.Protocol <- OtlpExportProtocol.HttpProtobuf)
                .Build()
        mp |> ignore
    with ex ->
        // Do not fail the UI if OTel cannot initialize (e.g., protocol unsupported). Log to console and continue.
        Console.Error.WriteLine($"[DEBUG_LOG] OpenTelemetry init skipped: {ex.Message}")

    let host = builder.Build()
    // Provide the DI HttpClient to the API helper for use in commands
    let httpClient = host.Services.GetRequiredService<HttpClient>()
    Api.setClient httpClient
    host.RunAsync().GetAwaiter().GetResult()
    0
