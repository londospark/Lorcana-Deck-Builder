module Card

open System
open System.Text.Json
// A useful representation of a Lorcana card parsed from the JSON dataset
// Captures commonly used fields, plus retains the raw JsonElement for full-fidelity payload copy.

type Card = {
    // Identifiers as they appear in the dataset
    UuidRaw: string option
    IdRaw: string option
    // Display/name and set info
    Name: string option
    FullName: string option
    Set: string option
    SetNumber: string option
    // Rules/text for embeddings
    Rules: string option
    Text: string option
    FlavorText: string option
    // Numbers we frequently query on
    Cost: float option
    InkCost: float option
    // Full original JSON object for payload copy
    Raw: JsonElement
}

module private Json =
    let tryGetProperty (name: string) (el: JsonElement) =
        let mutable p = Unchecked.defaultof<JsonElement>
        if el.ValueKind = JsonValueKind.Object && el.TryGetProperty(name, &p) then Some p else None

    let tryGetString (name: string) (el: JsonElement) =
        match tryGetProperty name el with
        | Some p when p.ValueKind = JsonValueKind.String -> Some (p.GetString())
        | _ -> None

    let tryGetNumber (name: string) (el: JsonElement) =
        match tryGetProperty name el with
        | Some p when p.ValueKind = JsonValueKind.Number ->
            let mutable i64 = 0L
            if p.TryGetInt64(&i64) then Some (float i64) else Some (p.GetDouble())
        | _ -> None

    // Convert JsonElement to Qdrant.Value recursively so we can copy every property into the payload
    let rec toQdrantValue (el: JsonElement) : Qdrant.Client.Grpc.Value =
        let v = Qdrant.Client.Grpc.Value()
        match el.ValueKind with
        | JsonValueKind.String -> v.StringValue <- el.GetString(); v
        | JsonValueKind.Number ->
            let mutable i64 = 0L
            if el.TryGetInt64(&i64) then v.DoubleValue <- float i64; v
            else v.DoubleValue <- el.GetDouble(); v
        | JsonValueKind.True -> v.BoolValue <- true; v
        | JsonValueKind.False -> v.BoolValue <- false; v
        | JsonValueKind.Null -> v.NullValue <- Qdrant.Client.Grpc.NullValue.NullValue; v
        | JsonValueKind.Array ->
            let lv = Qdrant.Client.Grpc.ListValue()
            for item in el.EnumerateArray() do
                lv.Values.Add(toQdrantValue item)
            v.ListValue <- lv; v
        | JsonValueKind.Object ->
            let s = Qdrant.Client.Grpc.Struct()
            for p in el.EnumerateObject() do
                s.Fields[p.Name] <- toQdrantValue p.Value
            v.StructValue <- s; v
        | _ -> v.NullValue <- Qdrant.Client.Grpc.NullValue.NullValue; v

let ofJson (el: JsonElement) : Card =
    {
        UuidRaw = Json.tryGetString "uuid" el
        IdRaw = Json.tryGetString "id" el
        Name = Json.tryGetString "name" el
        FullName = Json.tryGetString "fullName" el
        Set = Json.tryGetString "set" el
        SetNumber = Json.tryGetString "setNumber" el
        Rules = Json.tryGetString "rules" el
        Text = Json.tryGetString "text" el
        FlavorText = Json.tryGetString "flavorText" el
        Cost = Json.tryGetNumber "cost" el
        InkCost = Json.tryGetNumber "inkCost" el
        Raw = el
    }

let private safe (s: string option) =
    match s with
    | Some s when not (String.IsNullOrWhiteSpace s) -> s.Trim()
    | _ -> ""

/// Construct the text sent to the embedding model
let embeddingText (c: Card) =
    // Include effects text (array of strings) and character types (subtypes) to enrich semantic search
    // IMPORTANT: Exclude flavorText from embeddings so it does not influence candidate search.
    let effects =
        match Json.tryGetProperty "effects" c.Raw with
        | Some el when el.ValueKind = JsonValueKind.Array ->
            el.EnumerateArray()
            |> Seq.choose (fun x -> if x.ValueKind = JsonValueKind.String then Some (x.GetString()) else None)
            |> Seq.choose (fun s -> if isNull s || String.IsNullOrWhiteSpace s then None else Some (s.Trim()))
            |> String.concat ". "
        | _ -> ""
    let subtypesText =
        match Json.tryGetProperty "subtypes" c.Raw with
        | Some el when el.ValueKind = JsonValueKind.Array ->
            el.EnumerateArray()
            |> Seq.choose (fun x -> if x.ValueKind = JsonValueKind.String then Some (x.GetString()) else None)
            |> Seq.choose (fun s -> if isNull s || String.IsNullOrWhiteSpace s then None else Some (s.Trim()))
            |> String.concat ". "
        | _ -> ""
    [ safe c.Name; safe c.Rules; safe c.Text; subtypesText; effects ]
    |> List.filter (fun s -> s <> "")
    |> String.concat ". "

/// Determine the Qdrant PointId from uuid/id or fallback to the running index
let pointId (c: Card) (fallbackIndex: int) : Qdrant.Client.Grpc.PointId =
    let idStrOpt =
        match c.UuidRaw, c.IdRaw with
        | Some s, _ when not (String.IsNullOrWhiteSpace s) -> Some s
        | _, Some s when not (String.IsNullOrWhiteSpace s) -> Some s
        | _ -> None
    match idStrOpt with
    | Some idStr ->
        match Guid.TryParse idStr with
        | true, g -> Qdrant.Client.Grpc.PointId(Uuid = g.ToString())
        | _ ->
            let mutable n = 0UL
            if UInt64.TryParse(idStr, &n) then Qdrant.Client.Grpc.PointId(Num = n)
            else Qdrant.Client.Grpc.PointId(Num = uint64 fallbackIndex)
    | None -> Qdrant.Client.Grpc.PointId(Num = uint64 fallbackIndex)

/// Populate the Qdrant payload with all original properties and normalized fields (name, set, cost)
let fillPayload (c: Card) (setNameByNumber: System.Collections.Generic.IDictionary<string,string>) (payload: Google.Protobuf.Collections.MapField<string,Qdrant.Client.Grpc.Value>) =
    // copy all original props
    for prop in c.Raw.EnumerateObject() do
        payload[prop.Name] <- Json.toQdrantValue prop.Value
    // normalized name and fullName
    let nameSafe = safe c.Name
    if nameSafe <> "" then
        payload["name"] <- Qdrant.Client.Grpc.Value(StringValue = nameSafe)
    let fullNameSafe =
        match c.FullName with
        | Some s when not (String.IsNullOrWhiteSpace s) -> s.Trim()
        | _ -> nameSafe
    if fullNameSafe <> "" then
        payload["fullName"] <- Qdrant.Client.Grpc.Value(StringValue = fullNameSafe)
    // normalized set (prefer c.Set, else map setNumber)
    let setStr =
        match c.Set, c.SetNumber with
        | Some s, _ -> s
        | None, Some s2 -> s2
        | _ -> null
    if not (isNull setStr) then
        let setName = if setNameByNumber.ContainsKey(setStr) then setNameByNumber[setStr] else setStr
        payload["set"] <- Qdrant.Client.Grpc.Value(StringValue = setName)
    // normalized cost (prefer Cost, else InkCost)
    let costVal = match c.Cost with | Some v -> Some v | None -> c.InkCost
    match costVal with
    | Some v -> payload["cost"] <- Qdrant.Client.Grpc.Value(DoubleValue = v)
    | None -> ()
    // normalized inkable flag under canonical key 'inkwell'
    let parseBoolLikeJson (el: JsonElement) : bool option =
        match el.ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | JsonValueKind.String ->
            let s = el.GetString()
            if isNull s then None else
            let t = s.Trim().ToLowerInvariant()
            if t = "true" || t = "yes" || t = "1" then Some true
            elif t = "false" || t = "no" || t = "0" then Some false
            else None
        | JsonValueKind.Number ->
            let mutable n = 0
            if el.TryGetInt32(&n) then Some (n <> 0)
            else
                try Some (el.GetDouble() <> 0.0) with _ -> None
        | _ -> None
    let inkPropOpt =
        match Json.tryGetProperty "inkwell" c.Raw with
        | Some p -> Some p
        | None ->
            match Json.tryGetProperty "inkWell" c.Raw with
            | Some p -> Some p
            | None ->
                match Json.tryGetProperty "inkable" c.Raw with
                | Some p -> Some p
                | None -> Json.tryGetProperty "playableAsInk" c.Raw
    match inkPropOpt |> Option.bind parseBoolLikeJson with
    | Some b -> payload["inkwell"] <- Qdrant.Client.Grpc.Value(BoolValue = b)
    | None -> ()

/// Build a PointStruct for upsert given the vector
let toPoint (c: Card) (setNameByNumber: System.Collections.Generic.IDictionary<string,string>) (vector: float32 array) (fallbackIndex: int) =
    let p = Qdrant.Client.Grpc.PointStruct()
    p.Id <- pointId c fallbackIndex
    let vectors = Qdrant.Client.Grpc.Vectors()
    vectors.Vector <- Qdrant.Client.Grpc.Vector()
    vectors.Vector.Data.AddRange(vector)
    p.Vectors <- vectors
    // payload - add into map field directly
    fillPayload c setNameByNumber p.Payload
    p

/// Helpers to read card-like data back from Qdrant payloads
module Payload =
    open Qdrant.Client.Grpc
    open Google.Protobuf.Collections

    type CardInfo = {
        FullName: string
        Name: string
        Set: string
        Cost: float option
        Inkable: bool option
        DisplayText: string
    }

    let private normalizeColorName (s:string) =
        if String.IsNullOrWhiteSpace s then "" else s.Trim()

    // Get colors as a normalized list from payload: prefer 'colors' array; else split 'color' string by common separators
    let colors (payload: MapField<string, Value>) : string list =
        let tryGet name = if payload.ContainsKey(name) then Some payload[name] else None
        match tryGet "colors" with
        | Some v when not (isNull v.ListValue) && v.ListValue.Values.Count > 0 ->
            v.ListValue.Values
            |> Seq.choose (fun x -> if not (isNull x.StringValue) && x.StringValue <> "" then Some (normalizeColorName x.StringValue) else None)
            |> Seq.toList
        | _ ->
            match tryGet "color" with
            | Some v when not (isNull v.StringValue) && v.StringValue <> "" ->
                v.StringValue.Split([|','; '/'; '-'; '|'; '+'|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> normalizeColorName s)
                |> Array.toList
            | _ -> []


    let private tryGet (payload: MapField<string, Value>) (name: string) : Value option =
        if isNull (box payload) then None
        elif payload.ContainsKey(name) then Some payload[name]
        else None

    // Try get nested externalLinks.cardmarketUrl
    let cardMarketUrl (payload: MapField<string, Value>) : string option =
        match tryGet payload "externalLinks" with
        | Some v when not (isNull v.StructValue) && not (isNull (box v.StructValue.Fields)) ->
            let fields = v.StructValue.Fields
            if fields.ContainsKey("cardmarketUrl") then
                let inner = fields.["cardmarketUrl"]
                if not (isNull inner.StringValue) && inner.StringValue <> "" then Some inner.StringValue else None
            else None
        | _ -> None

    let tryGetString (payload: MapField<string, Value>) (name: string) : string option =
        match tryGet payload name with
        | Some v when not (isNull v.StringValue) && v.StringValue <> "" -> Some v.StringValue
        | _ -> None

    let name payload = tryGetString payload "name" |> Option.defaultValue ""
    let fullName payload =
        match tryGetString payload "fullName" with
        | Some s -> s
        | None -> name payload
    let setName payload = tryGetString payload "set" |> Option.defaultValue ""
    let rules payload = tryGetString payload "rules" |> Option.defaultValue ""
    let text payload = tryGetString payload "text" |> Option.defaultValue ""
    let flavorText payload = tryGetString payload "flavorText" |> Option.defaultValue ""

    // Read effects as a list of strings from the payload
    let effects (payload: MapField<string, Value>) : string list =
        match tryGet payload "effects" with
        | Some v when not (isNull v.ListValue) && v.ListValue.Values.Count > 0 ->
            v.ListValue.Values
            |> Seq.choose (fun x -> if not (isNull x.StringValue) && x.StringValue <> "" then Some (x.StringValue.Trim()) else None)
            |> Seq.toList
        | _ -> []

    // Read maxCopiesInDeck if present
    let maxCopiesInDeck (payload: MapField<string, Value>) : int option =
        match tryGet payload "maxCopiesInDeck" with
        | Some v when not (isNull v.StringValue) && v.StringValue <> "" ->
            let mutable n = 0
            if System.Int32.TryParse(v.StringValue.Trim(), &n) then Some n else None
        | Some v when v.DoubleValue <> 0.0 ->
            // Value numbers come via DoubleValue in Qdrant payload
            Some (int v.DoubleValue)
        | Some v when v.BoolValue -> Some 1
        | _ -> None

    let cost (payload: MapField<string, Value>) : float option =
        match tryGet payload "cost" with
        | Some v when not (isNull (box v)) && v.DoubleValue <> 0.0 -> Some v.DoubleValue
        | _ -> None

    let private parseBoolLike (v: Value) : bool option =
        if v.BoolValue then Some true
        elif not (isNull v.StringValue) && v.StringValue <> "" then
            let t = v.StringValue.Trim().ToLowerInvariant()
            Some (t = "true" || t = "yes" || t = "1")
        elif v.DoubleValue <> 0.0 then Some true
        else None

    let inkable (payload: MapField<string, Value>) : bool option =
        // Prefer Qdrant payload key 'inkwell' (lowercase) as the source of truth. Fallbacks kept for resilience.
        match tryGet payload "inkwell" with
        | Some v -> parseBoolLike v
        | None ->
            match tryGet payload "inkWell" with
            | Some v -> parseBoolLike v
            | None ->
                match tryGet payload "inkable" with
                | Some v -> parseBoolLike v
                | None ->
                    match tryGet payload "playableAsInk" with
                    | Some v -> parseBoolLike v
                    | None -> None

    let cardType (payload: MapField<string, Value>) : string =
        tryGetString payload "type" |> Option.defaultValue ""
    
    let subtypes (payload: MapField<string, Value>) : string list =
        match tryGet payload "subtypes" with
        | Some v when not (isNull v.ListValue) && v.ListValue.Values.Count > 0 ->
            v.ListValue.Values
            |> Seq.choose (fun x -> if not (isNull x.StringValue) && x.StringValue <> "" then Some (x.StringValue.Trim()) else None)
            |> Seq.toList
        | _ -> []
    
    let strength (payload: MapField<string, Value>) : int option =
        match tryGet payload "strength" with
        | Some v when v.DoubleValue <> 0.0 -> Some (int v.DoubleValue)
        | _ -> None
    
    let willpower (payload: MapField<string, Value>) : int option =
        match tryGet payload "willpower" with
        | Some v when v.DoubleValue <> 0.0 -> Some (int v.DoubleValue)
        | _ -> None
    
    let lore (payload: MapField<string, Value>) : int option =
        match tryGet payload "lore" with
        | Some v when v.DoubleValue <> 0.0 -> Some (int v.DoubleValue)
        | _ -> None
    
    let rarity (payload: MapField<string, Value>) : string =
        tryGetString payload "rarity" |> Option.defaultValue ""
    
    let story (payload: MapField<string, Value>) : string =
        tryGetString payload "story" |> Option.defaultValue ""
    
    let fullText (payload: MapField<string, Value>) : string =
        tryGetString payload "fullText" |> Option.defaultValue ""
    
    let isAllowedInFormat (payload: MapField<string, Value>) (format: DeckBuilder.Shared.DeckFormat) : bool =
        match tryGet payload "allowedInFormats" with
        | Some v when not (isNull v.StructValue) && not (isNull (box v.StructValue.Fields)) ->
            let fields = v.StructValue.Fields
            let formatKey = 
                match format with
                | DeckBuilder.Shared.DeckFormat.Core -> "Core"
                | DeckBuilder.Shared.DeckFormat.Infinity -> "Infinity"
            
            if fields.ContainsKey(formatKey) then
                let formatValue = fields.[formatKey]
                if not (isNull formatValue.StructValue) && not (isNull (box formatValue.StructValue.Fields)) then
                    let innerFields = formatValue.StructValue.Fields
                    if innerFields.ContainsKey("allowed") then
                        let allowedValue = innerFields.["allowed"]
                        // Strict: must be explicitly true
                        allowedValue.BoolValue
                    else
                        false // No "allowed" field, not legal
                else
                    false
            else
                false // Format not found, not legal
        | _ -> 
            false // No allowedInFormats field, not legal

    let displayText payload =
        let parts = [ rules payload; text payload; flavorText payload ] |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        match parts with
        | [] -> ""
        | xs -> String.Join(". ", xs)

    let toInfo (payload: MapField<string, Value>) : CardInfo =
        {
            FullName = fullName payload
            Name = name payload
            Set = setName payload
            Cost = cost payload
            Inkable = inkable payload
            DisplayText = displayText payload
        }
