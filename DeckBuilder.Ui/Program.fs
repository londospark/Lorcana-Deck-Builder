
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

type CardVM = { count:int; fullName:string; inkable:bool; cardMarketUrl:string }

type Model = {
    Request: string
    DeckSize: int
    SelectedColor1: string option
    SelectedColor2: string option
    SelectedFormat: DeckBuilder.Shared.DeckFormat
    IsBuilding: bool
    Result: string
    Cards: CardVM array
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
}

type Message =
    | SetRequest of string
    | SetDeckSize of int
    | SetColor1 of string option
    | SetColor2 of string option
    | SetFormat of DeckBuilder.Shared.DeckFormat
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
                format = Some model.SelectedFormat
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
                Console.WriteLine("About to call tryPost...")
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
    | SetColor1 oc -> { model with SelectedColor1 = oc }, Cmd.none
    | SetColor2 oc -> { model with SelectedColor2 = oc }, Cmd.none
    | SetFormat f -> { model with SelectedFormat = f }, Cmd.none
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
                    Built (text, cards |> Array.map (fun c -> { count = c.count; fullName = c.fullName; inkable = c.inkable; cardMarketUrl = c.cardMarketUrl })))
                (fun ex -> 
                    Console.WriteLine("Build failed: " + ex.Message)
                    BuildError ex.Message)
        Console.WriteLine("Command created, updating model...")
        { model with IsBuilding = true; Result = ""; Cards = [||] }, cmd
    | Built (text, cards) ->
        Console.WriteLine($"Built message received with {cards.Length} cards")
        let vms = cards |> Array.map (fun c -> { count = c.count; fullName = c.fullName; inkable = c.inkable; cardMarketUrl = c.cardMarketUrl })
        { model with IsBuilding = false; Result = text; Cards = vms }, Cmd.none
    | BuildError msg ->
        Console.WriteLine("BuildError message received: " + msg)
        { model with IsBuilding = false; Result = $"[ERROR] {msg}"; Cards = [||] }, Cmd.none

let view model dispatch =
    div {
        h3 { text "Lorcana Deck Builder (Bolero)" }
        p { text "Fill in the fields below to describe your request for the deck. Each field includes guidance and an example." }
        
        // Request
        div {
            label {
                attr.``for`` "request"
                b { text "Request" }
                text " — What kind of deck do you want? Include goals and themes."
            }
            br {}
            small { text "Example: Build an aggressive Amber/Amethyst list focused on quick lore gain with low-curve characters and minimal songs." }
            br {}
            textarea {
                attr.id "request"
                on.input (fun e -> dispatch (SetRequest (string e.Value)))
                attr.rows 6
                attr.cols 90
                attr.placeholder "e.g., I want a tempo deck that curves out on turns 1–3, prioritizes cheap characters and evasion. Avoid heavy control."
                text model.Request
            }
        }
        hr {}
        
        // Deck size
        div {
            label {
                attr.``for`` "decksize"
                b { text "Deck size" }
                text " — How many cards should the final deck contain?"
            }
            br {}
            input {
                attr.id "decksize"
                attr.``type`` "number"
                attr.value (string model.DeckSize)
                attr.placeholder "60"
                on.input (fun e ->
                    let s = string e.Value
                    let mutable v = 0
                    if Int32.TryParse(s, &v) then dispatch (SetDeckSize v))
            }
            small { text " Common sizes: 60 (default)" }
        }
        
        // Ink colors (choose exactly two)
        div {
            label {
                b { text "Ink colors" }
                text " — Choose exactly two inks to constrain search (optional)."
            }
            br {}
            div {
                span { text "Color 1: " }
                select {
                    attr.id "color1"
                    attr.value (defaultArg model.SelectedColor1 "")
                    on.change (fun e -> let v = string e.Value in dispatch (SetColor1 (if String.IsNullOrWhiteSpace v then None else Some v)))
                    option { attr.value ""; text "-- choose color --" }
                    option { attr.value "Amber"; text "Amber" }
                    option { attr.value "Amethyst"; text "Amethyst" }
                    option { attr.value "Emerald"; text "Emerald" }
                    option { attr.value "Ruby"; text "Ruby" }
                    option { attr.value "Sapphire"; text "Sapphire" }
                    option { attr.value "Steel"; text "Steel" }
                }
                text "\u00A0\u00A0"
                span { text "Color 2: " }
                select {
                    attr.id "color2"
                    attr.value (defaultArg model.SelectedColor2 "")
                    on.change (fun e -> let v = string e.Value in dispatch (SetColor2 (if String.IsNullOrWhiteSpace v then None else Some v)))
                    option { attr.value ""; text "-- choose color --" }
                    option { attr.value "Amber"; text "Amber" }
                    option { attr.value "Amethyst"; text "Amethyst" }
                    option { attr.value "Emerald"; text "Emerald" }
                    option { attr.value "Ruby"; text "Ruby" }
                    option { attr.value "Sapphire"; text "Sapphire" }
                    option { attr.value "Steel"; text "Steel" }
                }
                br {}
                small { text "If left blank, the server will attempt to infer colors from the rules text." }
            }
        }
        
        // Format selection
        div {
            label {
                b { text "Format" }
                text " — Choose the deck format (Core has rotation, Infinite includes all cards)."
            }
            br {}
            select {
                attr.id "format"
                attr.value (match model.SelectedFormat with | DeckBuilder.Shared.DeckFormat.Core -> "Core" | DeckBuilder.Shared.DeckFormat.Infinite -> "Infinite")
                on.change (fun e -> 
                    let v = string e.Value
                    let format = if v = "Infinite" then DeckBuilder.Shared.DeckFormat.Infinite else DeckBuilder.Shared.DeckFormat.Core
                    dispatch (SetFormat format))
                option { attr.value "Core"; text "Core (Standard rotation)" }
                option { attr.value "Infinite"; text "Infinite (No rotation, all cards)" }
            }
            br {}
            small { 
                text (match model.SelectedFormat with
                      | DeckBuilder.Shared.DeckFormat.Core -> "Core format uses currently legal cards with rotation."
                      | DeckBuilder.Shared.DeckFormat.Infinite -> "Infinite format includes all cards, no rotation restrictions.")
            }
        }
        br {}
        button {
            attr.disabled model.IsBuilding
            on.click (fun _ -> dispatch Build)
            text (if model.IsBuilding then "Building deck..." else "Build Deck")
        }
        
        if model.IsBuilding then
            div {
                attr.style "margin-top: 20px; color: #0066cc;"
                p { 
                    b { text "⏳ Building your deck..." }
                }
                p { 
                    small { text "This may take 30-60 seconds while the AI generates card suggestions." }
                }
            }
        
        if not (String.IsNullOrWhiteSpace model.Result) && not model.IsBuilding then
            div {
                (*
                h4 { text "Result" }
                pre { text model.Result }
                *)
                if model.Cards.Length > 0 then
                    div {
                        h5 { text "Cards" }
                        ul {
                            forEach (model.Cards |> Array.sortBy _.fullName |> Array.sortByDescending _.count)(fun c ->
                                li {
                                    let nameNode = if String.IsNullOrWhiteSpace c.cardMarketUrl then text c.fullName else a { attr.href c.cardMarketUrl; attr.target "_blank"; text c.fullName }
                                    concat {
                                            text $"%d{c.count} x "
                                            nameNode
                                        }
                                })
                        }
                    }
            }
        else
            empty()
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
        new HttpClient(BaseAddress = Uri(apiBaseUrl))) |> ignore

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
