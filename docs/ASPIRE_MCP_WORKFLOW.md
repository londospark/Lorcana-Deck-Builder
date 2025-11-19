# Aspire MCP Integration Workflow

This document describes how the Aspire Model Context Protocol (MCP) integration enables efficient development workflows, particularly for rebuilding services without restarting Aspire and testing APIs.

## Overview

The Aspire MCP server provides tools to manage Aspire resources (services, containers) directly from the development environment. This eliminates the need to manually stop/restart Aspire when rebuilding projects or debugging issues.

**IMPORTANT**: Aspire itself is **never restarted** during development. All resource management happens through MCP commands that control individual services. If Aspire must be restarted for any reason, only the user can perform that action - automated tooling does not restart the Aspire host.

## Available MCP Tools

### Resource Management
- `mcp_aspire-dashbo_list_resources` - List all application resources with their state, endpoints, and health
- `mcp_aspire-dashbo_execute_resource_command` - Execute commands on resources (start, stop, restart)

### Logging & Diagnostics
- `mcp_aspire-dashbo_list_console_logs` - View stdout/stderr from resources and resource commands
- `mcp_aspire-dashbo_list_structured_logs` - View structured logs with filtering by resource
- `mcp_aspire-dashbo_list_traces` - View distributed traces across resources
- `mcp_aspire-dashbo_list_trace_structured_logs` - View logs for a specific trace

## Rebuild Workflow Without Quitting Aspire

### Problem
When rebuilding a .NET project while Aspire is running, the build process often fails with file lock errors:
```
MSB3027: Could not copy "obj\Debug\net10.0\DeckBuilder.Api.dll" to "bin\Debug\net10.0\DeckBuilder.Api.dll". 
Exceeded retry count of 10. Failed. The process cannot access the file because it is being used by another process.
```

### Solution
Use Aspire MCP to restart the resource before building:

1. **Restart the resource** (stops and starts it):
   ```
   mcp_aspire-dashbo_execute_resource_command
   - resourceName: "deck-api" (or "data-worker", etc.)
   - commandName: "resource-restart"
   ```

2. **Build the project**:
   ```powershell
   dotnet build DeckBuilder.Api
   ```

3. **Start the resource** (if needed):
   ```
   mcp_aspire-dashbo_execute_resource_command
   - resourceName: "deck-api"
   - commandName: "resource-start"
   ```

### Example Sequence
```
1. Execute: resource-restart on deck-api
   → Stops running process, releases file locks
2. Run: dotnet build DeckBuilder.Api
   → Build succeeds, outputs to bin/Debug/net10.0/
3. Execute: resource-start on deck-api
   → Aspire starts the rebuilt service automatically
```

## API Testing Workflow

### Test Structure
All API tests use PowerShell with `Invoke-RestMethod` to send HTTP requests to the running API.

### Deck Generation Endpoint Test

**Endpoint**: `POST http://localhost:5001/api/deck`

**Request Format**:
```json
{
  "request": "user deck request string",
  "deckSize": 60,
  "selectedColors": ["Amber", "Steel"] or null,
  "format": {"Case": "Core"} or {"Case": "Infinity"}
}
```

**Test Command**:
```powershell
$body = '{"request":"Magic Brooms","deckSize":60,"selectedColors":null,"format":{"Case":"Core"}}'; 
Invoke-RestMethod -Uri 'http://localhost:5001/api/deck' -Method POST -Body $body -ContentType 'application/json' | ConvertTo-Json -Depth 5
```

**Response Format**:
```json
{
  "cards": [
    {
      "name": "Magic Broom - Bucket Brigade",
      "cost": 2,
      "inkable": true,
      "color": "Sapphire",
      "type": "Character",
      "count": 4
    }
    // ... more cards
  ],
  "totalCards": 60,
  "colors": ["Amethyst", "Sapphire"],
  "explanation": "Built 60-card Amethyst/Sapphire deck in 164ms.\nPhase 1: Found 48 cards...\nPhase 2: Filtered to 32 legal Core cards...\nPhase 3: Assembled deck with 60 inkable cards (100.0%)"
}
```

### Complete Test Workflow

1. **Verify API is running**:
   ```
   mcp_aspire-dashbo_list_resources
   → Check deck-api state is "Running"
   → Note the HTTP endpoint (usually http://localhost:5001)
   ```

2. **Check for errors** (if API not responding):
   ```
   mcp_aspire-dashbo_list_console_logs
   - resourceName: "deck-api"
   → Review startup errors or exceptions
   ```

3. **Restart API if needed**:
   ```
   mcp_aspire-dashbo_execute_resource_command
   - resourceName: "deck-api"
   - commandName: "resource-restart"
   ```

4. **Wait for startup** (optional delay):
   ```powershell
   Start-Sleep -Seconds 3
   ```

5. **Execute test request**:
   ```powershell
   $body = '{"request":"<test case>","deckSize":60,"selectedColors":null,"format":{"Case":"Core"}}'; 
   Invoke-RestMethod -Uri 'http://localhost:5001/api/deck' -Method POST -Body $body -ContentType 'application/json' | ConvertTo-Json -Depth 5
   ```

6. **Analyze results**:
   - Check `totalCards` matches `deckSize`
   - Verify `colors` match request/selected colors
   - Review `explanation` for performance metrics and phase details
   - Validate `cards` array contains expected card types

### Test Cases Used

| Test Case | Purpose | Expected Result |
|-----------|---------|-----------------|
| `"Magic Brooms"` | Test specific card search with format filtering | 60-card deck with Magic Broom variants, Sapphire/Amethyst colors |
| `"banish theme"` | Test thematic deck building | 60-card deck with banish-related cards |
| `"steal effects"` | Test ability-based search | Deck with cards that have steal mechanics |

### Format Testing

**Core Format** (with rotation dates):
```json
{"format": {"Case": "Core"}}
```
- Filters cards by `allowedInFormats.Core.allowed = true`
- Validates rotation dates: `allowedFromTs <= now <= allowedUntilTs`
- Missing fields treated as valid (null-safe OR logic)

**Infinity Format** (all legal cards):
```json
{"format": {"Case": "Infinity"}}
```
- Only checks `allowedInFormats.Infinity.allowed = true`
- No date restrictions

## Debugging with MCP

### View Recent Logs
```
mcp_aspire-dashbo_list_console_logs
- resourceName: "deck-api"
```
Shows stdout/stderr including:
- Startup messages
- Endpoint registration logs
- Request/response logs
- Exception stack traces

### View Structured Logs
```
mcp_aspire-dashbo_list_structured_logs
- resourceName: "deck-api"
```
Returns structured log entries with:
- Timestamp
- Log level
- Category
- Message
- Exception details

### Check Resource State
```
mcp_aspire-dashbo_list_resources
```
Shows:
- Resource name
- Type (.NET Project, Container, etc.)
- State (Running, Stopped, Starting, etc.)
- Health status
- HTTP endpoints
- Environment variables
- Resource relationships

## Worker Testing Workflow

### Force Data Reimport

1. **Create trigger file**:
   ```powershell
   New-Item -ItemType File -Path "DeckBuilder.Worker\bin\Debug\net10.0\Data\.force_reimport" -Force
   ```

2. **Restart worker**:
   ```
   mcp_aspire-dashbo_execute_resource_command
   - resourceName: "data-worker"
   - commandName: "resource-restart"
   ```

3. **Monitor progress**:
   ```
   mcp_aspire-dashbo_list_console_logs
   - resourceName: "data-worker"
   ```
   Expected output:
   ```
   Force reimport triggered. Deleting existing collection...
   Existing collection deleted successfully.
   Loading card data from: X:\Code\Lorcana-Deck-Builder\DeckBuilder.Worker\bin\Debug\net10.0\Data\allCards.json
   Processed 2450/2455 cards (99.80%)...
   Upserting 2455 points in one batch...
   Data ingestion completed successfully! Total cards: 2455
   ```

## Advantages of MCP Integration

1. **No Aspire Restarts**: Stop/start individual services without restarting entire Aspire application host
2. **Fast Iteration**: Rebuild → Restart resource → Test cycle in seconds
3. **Live Debugging**: View logs and traces without switching windows
4. **State Inspection**: Check resource health and endpoints programmatically
5. **Automation**: Integrate testing into automated workflows
6. **Persistent Development**: Aspire runs continuously; only individual resources are managed

## Best Practices

1. **Always restart resources before rebuild** to avoid file lock errors
2. **Check console logs** after resource restart to verify successful startup
3. **Use structured logs** for debugging specific issues (filter by category/level)
4. **Wait 2-3 seconds** after resource restart before sending test requests
5. **Monitor worker logs** during data ingestion to catch payload issues
6. **Never restart Aspire itself** - manage individual resources only; if Aspire must restart, the user performs it manually

## Common Issues

### API Not Responding
- Check: `list_resources` - verify deck-api state is "Running"
- Fix: `resource-restart` on deck-api (restarts the service, not Aspire)
- Verify: `list_console_logs` for startup errors

### Build File Lock
- Check: MSB3027 error mentioning process lock
- Fix: `resource-restart` on the locked service (stops service to release locks)
- Verify: Build succeeds after resource restart

### Worker Not Reimporting
- Check: Trigger file in correct location (bin/Debug/net10.0/Data/)
- Fix: Create trigger in bin directory, not source directory
- Verify: Console logs show "Force reimport triggered..."

### Aspire Host Issues
- **Critical**: If Aspire itself has problems (dashboard not responding, MCP connection lost, etc.)
- **Action Required**: User must manually restart Aspire via terminal (`Ctrl+C` then `aspire run`)
- **Scope**: Automated tooling only manages resources, never the Aspire host

## Resource Names

Current project resource names:
- `deck-api` - Main API service (F#, Kestrel, port 5001)
- `data-worker` - Data ingestion worker (F#, background service)
- Other resources as defined in AppHost

Use these exact names with MCP commands.
