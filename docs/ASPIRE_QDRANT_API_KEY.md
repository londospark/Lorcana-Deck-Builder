# Qdrant API Key via Aspire

This guide shows how to fetch the Qdrant API key from Aspire and use it for direct HTTP calls (e.g., listing collections, counting points). The Deck API and Worker are already wired to Qdrant via Aspire; you only need the key for ad‑hoc HTTP requests from tools like PowerShell.

## Get the API Key (Aspire)

1. Start the app if it isn’t running:
   ```powershell
   aspire run
   ```
2. Open the Aspire Dashboard (auto-opens on run or visit the URL printed in the console).
3. Select the resource named `qdrant`.
4. Open the Environment tab.
5. Copy the value of `QDRANT__SERVICE__API_KEY`.

Alternatives:
- The `deck-api` and `data-worker` resources also expose an `QDRANT_APIKEY` environment variable you can use for the same purpose.

Security:
- Treat the API key as a secret. Do not commit it to source control.

## Use the Key in PowerShell (HTTP)

Set the header (use either an env var or a literal string):
```powershell
# Recommended: store once in your session
$env:QDRANT_APIKEY = "<PASTE_YOUR_KEY_HERE>"
$headers = @{ "api-key" = $env:QDRANT_APIKEY }

# Or: one-off literal (avoid in shared terminals)
# $headers = @{ "api-key" = "<PASTE_YOUR_KEY_HERE>" }
```

Qdrant HTTP base URL comes from Aspire: `http://localhost:64802` (Dashboard: `http://localhost:64802/dashboard`).

Common checks:
```powershell
# 1) List collections
Invoke-RestMethod -Uri 'http://localhost:64802/collections' -Headers $headers -Method GET | ConvertTo-Json -Depth 5

# 2) Inspect a collection (e.g., lorcana_cards)
Invoke-RestMethod -Uri 'http://localhost:64802/collections/lorcana_cards' -Headers $headers -Method GET | ConvertTo-Json -Depth 6

# 3) Count points in a collection
$body = '{"count": {"exact": true}}'
Invoke-RestMethod -Uri 'http://localhost:64802/collections/lorcana_cards/points/count' -Headers $headers -Method POST -Body $body -ContentType 'application/json'

# 4) Scroll a few points to verify payloads
$scroll = '{"limit": 3, "with_payload": true}'
Invoke-RestMethod -Uri 'http://localhost:64802/collections/lorcana_cards/points/scroll' -Headers $headers -Method POST -Body $scroll -ContentType 'application/json' | ConvertTo-Json -Depth 6
```

## Expected Collections & Counts

- `lorcana_cards`: ~2455 points (full card set with metadata + embeddings)
- `lorcana_rules`: present if rules have been ingested (chunked rules PDF)

If a collection is missing or counts are zero, see Troubleshooting.

## Troubleshooting

- Error: `Must provide an API key or an Authorization bearer token`
  - Cause: Missing `api-key` header. Set `$headers = @{ "api-key" = $env:QDRANT_APIKEY }` and retry.

- `lorcana_cards` missing or very small count
  - The Worker may not have ingested yet or needs a reimport.
  - Trigger a force reimport and start the Worker (don’t restart Aspire itself):
    ```powershell
    # Create trigger file in the Worker runtime directory
    New-Item -ItemType File -Path "DeckBuilder.Worker\bin\Debug\net10.0\Data\.force_reimport" -Force
    ```
    Then in Aspire (via the Dashboard):
    - Start the `data-worker` resource (or use the MCP command for `resource-start`).
    - Watch the `data-worker` console logs for: "Data ingestion completed successfully!"

- Deck API calls failing or returning zero results after ingestion
  - Restart only the affected resource (Deck API) via Aspire: `resource-restart` on `deck-api`.
  - Re-test the API (see below) once the API is listening again.

## Optional: Quick Deck API Test

Format is required and must be sent as a DU case object: `{"Case":"Core"}` or `{"Case":"Infinity"}`.
```powershell
$body = '{"request":"Lion King deck","deckSize":60,"selectedColors":null,"format":{"Case":"Core"}}'
Invoke-RestMethod -Uri 'http://localhost:5001/api/deck' -Method POST -Body $body -ContentType 'application/json' | ConvertTo-Json -Depth 10
```

Notes:
- The API enforces 1–2 colors per deck. When colors aren’t specified, the model will select the best 2 colors based on the request and search results.
- Qdrant format legality is applied via native filters; ensure collections are populated before testing.
