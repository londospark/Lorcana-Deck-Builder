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

/// Build Qdrant filter condition for format legality
/// Note: This only filters by allowed=true. Date-based rotation filtering
/// would require converting dates to timestamps or using post-filtering.
/// For now, we filter by the boolean flag which indicates current legality.
let buildFormatFilter (format: DeckBuilder.Shared.DeckFormat) : Qdrant.Client.Grpc.Filter option =
    match format with
    | DeckBuilder.Shared.DeckFormat.Core ->
        // Core: (allowed is missing OR allowed=true) AND (no allowedUntilTs OR allowedUntilTs >= nowUnix)
        //             AND (no allowedFromTs OR allowedFromTs <= nowUnix)
        let nowUnix = float (DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        let filter = Qdrant.Client.Grpc.Filter()
        
        // allowed: missing OR true (to handle cards without the field)
        let allowedFilter = Qdrant.Client.Grpc.Filter()
        let isNullAllowed = Qdrant.Client.Grpc.IsNullCondition(Key = "allowedInFormats.Core.allowed")
        allowedFilter.Should.Add(Qdrant.Client.Grpc.Condition(IsNull = isNullAllowed))
        let allowedCondition = Qdrant.Client.Grpc.FieldCondition()
        allowedCondition.Key <- "allowedInFormats.Core.allowed"
        let matchValue = Qdrant.Client.Grpc.Match()
        matchValue.Boolean <- true
        allowedCondition.Match <- matchValue
        allowedFilter.Should.Add(Qdrant.Client.Grpc.Condition(Field = allowedCondition))
        filter.Must.Add(Qdrant.Client.Grpc.Condition(Filter = allowedFilter))

        // until: missing OR >= nowUnix (card hasn't rotated out yet)
        let untilFilter = Qdrant.Client.Grpc.Filter()
        let isNullUntil = Qdrant.Client.Grpc.IsNullCondition(Key = "allowedInFormats.Core.allowedUntilTs")
        untilFilter.Should.Add(Qdrant.Client.Grpc.Condition(IsNull = isNullUntil))
        let untilField = Qdrant.Client.Grpc.FieldCondition(Key = "allowedInFormats.Core.allowedUntilTs")
        let untilRange = Qdrant.Client.Grpc.Range()
        untilRange.Gte <- nowUnix
        untilField.Range <- untilRange
        untilFilter.Should.Add(Qdrant.Client.Grpc.Condition(Field = untilField))
        filter.Must.Add(Qdrant.Client.Grpc.Condition(Filter = untilFilter))

        // from: missing OR <= nowUnix (card has been released)
        let fromFilter = Qdrant.Client.Grpc.Filter()
        let isNullFrom = Qdrant.Client.Grpc.IsNullCondition(Key = "allowedInFormats.Core.allowedFromTs")
        fromFilter.Should.Add(Qdrant.Client.Grpc.Condition(IsNull = isNullFrom))
        let fromField = Qdrant.Client.Grpc.FieldCondition(Key = "allowedInFormats.Core.allowedFromTs")
        let fromRange = Qdrant.Client.Grpc.Range()
        fromRange.Lte <- nowUnix
        fromField.Range <- fromRange
        fromFilter.Should.Add(Qdrant.Client.Grpc.Condition(Field = fromField))
        filter.Must.Add(Qdrant.Client.Grpc.Condition(Filter = fromFilter))

        Some filter
        
    | DeckBuilder.Shared.DeckFormat.Infinity ->
        // Infinity format: allowedInFormats.Infinity.allowed = true
        let filter = Qdrant.Client.Grpc.Filter()
        let allowedCondition = Qdrant.Client.Grpc.FieldCondition()
        allowedCondition.Key <- "allowedInFormats.Infinity.allowed"
        let matchValue = Qdrant.Client.Grpc.Match()
        matchValue.Boolean <- true
        allowedCondition.Match <- matchValue
        filter.Must.Add(Qdrant.Client.Grpc.Condition(Field = allowedCondition))
        Some filter

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
