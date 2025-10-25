module DeckHelpers

open System
open System.Text
open OllamaSharp
open Inkable

let splitLines (s:string) =
    s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun l -> l.Trim())
    |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))

/// Build counted deck lines like "N x FullName" and a flat cards array respecting maxCopies and deckSize
let buildCountedDeck (items:string array) (deckSize:int) (maxCopies:int) =
    let counts = System.Collections.Generic.Dictionary<string,int>(StringComparer.Ordinal)
    let order = System.Collections.Generic.List<string>()
    let mutable total = 0
    for it in items do
        if total < deckSize then
            let name = it.Trim()
            if name <> "" then
                let current = if counts.ContainsKey(name) then counts[name] else 0
                if current < maxCopies then
                    if current = 0 then order.Add(name)
                    counts[name] <- current + 1
                    total <- total + 1
    // Build lines preserving first-seen order
    let lines =
        order
        |> Seq.map (fun n -> let c = counts[n] in if c > 1 then $"%d{c} x %s{n}" else n)
        |> Seq.toArray
    // Build flat cards array of exact total copies added
    let flat =
        order
        |> Seq.collect (fun n -> Seq.init counts[n] (fun _ -> n))
        |> Seq.toArray
    lines, flat

let validateDeckSize (deckText:string) (deckSize:int) =
    let lines = deckText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    // If lines are in "N x Name" format, sum counts; otherwise use line count.
    let total =
        lines
        |> Array.sumBy (fun l ->
            let t = l.Trim()
            let marker = " x "
            let idx = t.IndexOf(marker, StringComparison.Ordinal)
            if idx > 0 then
                let left = t.Substring(0, idx).Trim()
                let mutable n = 0
                if Int32.TryParse(left, &n) && n > 0 then n else 1
            else 1)
    if total <> deckSize then
        $"%s{deckText}\n\n[WARNING: Deck size mismatch. Expected %d{deckSize} cards, got %d{total}]"
    else deckText

// Append the warning (if any) from validateDeckSize computed on the base (unformatted) text
let appendWarningIfAny (baseDeckText:string) (deckSize:int) (formatted:string) =
    let validated = validateDeckSize baseDeckText deckSize
    if validated.Length > baseDeckText.Length then
        formatted + validated.Substring(baseDeckText.Length)
    else formatted

// Ensure the generated deck reaches the target size by asking the model to add missing cards only (text mode)
let ensureDeckSize (genModel:string) (ollamaApiClient: IOllamaApiClient) (rules:string) (_:string) (candidates:string) (deckSize:int) (initialText:string) = task {
    // legacy text-based top up retained as fallback
    let splitLocal (s:string) =
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun l -> l.TrimEnd())
        |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
    let mutable currentText = initialText.TrimEnd()
    let mutable lines = splitLocal currentText
    let maxIterations = 3
    let mutable iter = 0
    while lines.Length < deckSize && iter < maxIterations do
        let missing = deckSize - lines.Length
        let prompt = StringBuilder()
        prompt.AppendLine("You are an expert Lorcana deck builder.") |> ignore
        prompt.AppendLine("RULES:") |> ignore
        prompt.AppendLine(rules) |> ignore
        prompt.AppendLine() |> ignore
        prompt.AppendLine("CANDIDATE CARDS (CSV columns: fullName, cost, inkable, effects, maxCopies) from Qdrant collection:") |> ignore
        prompt.AppendLine(candidates) |> ignore
        prompt.AppendLine() |> ignore
        prompt.AppendLine("CURRENT DECK (one line per card):") |> ignore
        prompt.AppendLine(String.Join('\n', lines)) |> ignore
        prompt.AppendLine() |> ignore
        prompt.AppendLine($"TASK: Output exactly {missing} additional, legal cards to add so the deck reaches {deckSize} cards total.") |> ignore
        prompt.AppendLine("- Use ONLY cards from the CANDIDATE CARDS list above (Qdrant). Do NOT invent names.") |> ignore
        prompt.AppendLine("- If distinct names are insufficient, REPEAT cards (up to each card's legal limit) to reach the exact target.") |> ignore
        prompt.AppendLine("IMPORTANT:") |> ignore
        prompt.AppendLine("- Use only cards from CANDIDATE CARDS.") |> ignore
        prompt.AppendLine("- Duplicates are allowed where legal (typically up to 4 copies per card). Output ONLY the additional cards, one per line.") |> ignore
        prompt.AppendLine("- STRICT: ONLY return valid Disney Lorcana card fullNames. No bullets, numbering, quotes, JSON, code fences, headers, or explanations. No extra text.") |> ignore
        prompt.AppendLine("- Do not add counts in the string; one name per line equals one copy. No trailing punctuation.") |> ignore
        prompt.AppendLine("- Playset strategy: Minimize singletons; fill out 3–4 copies of existing key cards before adding new names when appropriate.") |> ignore
        prompt.AppendLine("- Inkable balance: Prefer to maintain ~70–85% inkable cards overall unless constrained by the plan; use inkable options when possible.") |> ignore
        prompt.AppendLine("- Cost curve: Prefer low-to-mid curve additions if the curve is light there; avoid oversaturating 6+ unless required.") |> ignore
        prompt.AppendLine("- Do NOT force every card to 4-of. 2–3 copies can be optimal for legend rule, curve smoothing, redundancy, or role-specific tech. Use judgment and balance.") |> ignore
        let genReq = OllamaSharp.Models.GenerateRequest()
        genReq.Model <- genModel
        genReq.Prompt <- prompt.ToString()
        let stream = ollamaApiClient.GenerateAsync(genReq)
        let sb = StringBuilder()
        let e = stream.GetAsyncEnumerator()
        let mutable more = true
        while more do
            let! moved = e.MoveNextAsync().AsTask()
            if moved then
                let chunk = e.Current
                if not (isNull chunk) && not (isNull chunk.Response) then
                    sb.Append(chunk.Response) |> ignore
            else
                more <- false
        do! e.DisposeAsync().AsTask()
        let additionsRaw = sb.ToString()
        let additions =
            additionsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun l -> l.Trim())
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Array.truncate missing
        if additions.Length > 0 then
            currentText <-
                if currentText.EndsWith("\n") then currentText + String.Join('\n', additions)
                else currentText + "\n" + String.Join('\n', additions)
            lines <- splitLocal currentText
        iter <- iter + 1
    return currentText
}

// Resolver-aware helpers so we can prefer inkable flag from current Qdrant payloads
let annotateInkableWith (resolver: string -> bool) (line:string) =
    let marker = " x "
    let idx = line.IndexOf(marker, StringComparison.Ordinal)
    if idx > 0 then
        let countPart = line.Substring(0, idx)
        let name = line.Substring(idx + marker.Length).Trim()
        if resolver name then $"%s{countPart.Trim()} x %s{name} (Inkable)" else line
    else
        let name = line.Trim()
        if resolver name then $"%s{name} (Inkable)" else line

let countInkableCopiesWith (resolver: string -> bool) (flat:string array) =
    flat |> Array.sumBy (fun n -> if resolver (n.Trim()) then 1 else 0)

let formatDeckTextWith (resolver: string -> bool) (lines:string array) (deckSize:int) (inkableCount:int) =
    let annotated = lines |> Array.map (annotateInkableWith resolver)
    let header = $"Deck (%d{deckSize} cards, %d{inkableCount} inkable):"
    String.Join('\n', Array.append [| header |] annotated)

// Backward-compatible wrappers using static JSON index
let annotateInkable (line:string) = annotateInkableWith isInkable line
let countInkableCopies (flat:string array) = countInkableCopiesWith isInkable flat
let formatDeckText (lines:string array) (deckSize:int) (inkableCount:int) = formatDeckTextWith isInkable lines deckSize inkableCount
