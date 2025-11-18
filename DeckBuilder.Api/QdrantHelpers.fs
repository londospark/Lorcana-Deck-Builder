module QdrantHelpers

open System
open System.Globalization
open ApiModels
open Card

// Helpers for gRPC ScoredPoint results from Qdrant.Client.Grpc

// Read nested payload values safely
module private PayloadRead =
    open Qdrant.Client.Grpc
    open Google.Protobuf.Collections

    let tryGet (payload: MapField<string, Value>) (name: string) : Value option =
        if isNull (box payload) then None
        elif payload.ContainsKey(name) then Some payload[name]
        else None

    let tryGetStruct (v: Qdrant.Client.Grpc.Value) : Qdrant.Client.Grpc.Struct option =
        if isNull (box v) then None
        elif isNull v.StructValue then None
        else Some v.StructValue

    let tryGetField (s: Qdrant.Client.Grpc.Struct) (name:string) : Qdrant.Client.Grpc.Value option =
        if isNull (box s) then None
        elif isNull (box s.Fields) then None
        elif s.Fields.ContainsKey(name) then Some s.Fields[name]
        else None

    let tryGetBool (v: Qdrant.Client.Grpc.Value) : bool option =
        if isNull (box v) then None
        elif v.BoolValue then Some true
        elif not (isNull v.StringValue) && v.StringValue <> "" then
            let t = v.StringValue.Trim().ToLowerInvariant()
            if t = "true" || t = "yes" || t = "1" then Some true
            elif t = "false" || t = "no" || t = "0" then Some false
            else None
        elif v.DoubleValue <> 0.0 then Some true
        else None

    let tryGetString (v: Qdrant.Client.Grpc.Value) : string option =
        if isNull (box v) then None else
        if not (isNull v.StringValue) && v.StringValue <> "" then Some v.StringValue else None

    let tryGetNested (payload: MapField<string, Value>) (path: string list) : Qdrant.Client.Grpc.Value option =
        let rec go (curOpt: Qdrant.Client.Grpc.Value option) parts =
            match parts, curOpt with
            | [], _ -> curOpt
            | name::rest, None ->
                match tryGet payload name with
                | Some v when rest.IsEmpty -> Some v
                | Some v -> go (Some v) rest
                | None -> None
            | name::rest, Some v ->
                match tryGetStruct v with
                | Some s ->
                    match tryGetField s name with
                    | Some v2 when rest.IsEmpty -> Some v2
                    | Some v2 -> go (Some v2) rest
                    | None -> None
                | None -> None
        match path with
        | [] -> None
        | _ -> go None path

/// Determine if a card is currently legal in Core format based on Qdrant payload fields
let private isCoreLegalNow (nowUtc: DateTime) (payload: Google.Protobuf.Collections.MapField<string, Qdrant.Client.Grpc.Value>) : bool =
    // Expect nested structure: allowedInFormats -> Core -> allowed (bool), optional allowedFromDate, allowedUntilDate (yyyy-MM-dd)
    let pathCore = [ "allowedInFormats"; "Core" ]
    let vCoreOpt = PayloadRead.tryGetNested payload pathCore
    match vCoreOpt with
    | None -> false // be conservative: if unknown, treat as not legal
    | Some vCore ->
        match (PayloadRead.tryGetField vCore.StructValue "allowed") |> Option.bind PayloadRead.tryGetBool with
        | Some true ->
            // Check optional date bounds on the Core object
            let parseDate (s:string) =
                let mutable dt = DateTime.MinValue
                if DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal, &dt) then Some dt
                else None
            let fromOk =
                match PayloadRead.tryGetField vCore.StructValue "allowedFromDate" |> Option.bind PayloadRead.tryGetString |> Option.bind parseDate with
                | Some fromDate -> nowUtc >= fromDate
                | None -> true
            let untilOk =
                match PayloadRead.tryGetField vCore.StructValue "allowedUntilDate" |> Option.bind PayloadRead.tryGetString |> Option.bind parseDate with
                | Some untilDate -> nowUtc <= untilDate
                | None -> true
            fromOk && untilOk
        | _ -> false

/// Determine if a card is legal in Infinity format
let private isInfiniteLegal (payload: Google.Protobuf.Collections.MapField<string, Qdrant.Client.Grpc.Value>) : bool =
    let pathInfinite = [ "allowedInFormats"; "Infinity" ]
    let vInfiniteOpt = PayloadRead.tryGetNested payload pathInfinite
    match vInfiniteOpt with
    | None -> false // If not specified, not legal in Infinity
    | Some vInfinite ->
        match (PayloadRead.tryGetField vInfinite.StructValue "allowed") |> Option.bind PayloadRead.tryGetBool with
        | Some allowed -> allowed
        | None -> false

let filterLegalCardsPoints (query: DeckQuery) (candidates: seq<Qdrant.Client.Grpc.ScoredPoint>) =
    let format = query.format |> Option.defaultValue DeckBuilder.Shared.DeckFormat.Core
    match format with
    | DeckBuilder.Shared.DeckFormat.Core ->
        let nowUtc = DateTime.UtcNow
        candidates |> Seq.filter (fun p -> isCoreLegalNow nowUtc p.Payload)
    | DeckBuilder.Shared.DeckFormat.Infinity ->
        candidates |> Seq.filter (fun p -> isInfiniteLegal p.Payload)

let applyMaxCopiesPoints (maxCopies:int) (cards: seq<Qdrant.Client.Grpc.ScoredPoint>) =
    let counts = System.Collections.Generic.Dictionary<string,int>()
    seq {
        for c in cards do
            let fn = Payload.fullName c.Payload
            let current = if counts.ContainsKey(fn) then counts[fn] else 0
            if current < maxCopies then
                counts[fn] <- current + 1
                yield c
    }

/// Apply per-card copy caps based on payload field `maxCopiesInDeck` (default 4)
let applyMaxCopiesPointsPerCard (cards: seq<Qdrant.Client.Grpc.ScoredPoint>) =
    let counts = System.Collections.Generic.Dictionary<string,int>()
    seq {
        for c in cards do
            let fn = Payload.fullName c.Payload
            let current = if counts.ContainsKey(fn) then counts[fn] else 0
            let maxFor =
                match Card.Payload.maxCopiesInDeck c.Payload with
                | Some n when n > 0 -> n
                | _ -> 4
            if current < maxFor then
                counts[fn] <- current + 1
                yield c
    }
