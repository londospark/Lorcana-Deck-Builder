# Bolero Console Errors - Complete Fix Guide

## Problem Summary

The Bolero Blazor WebAssembly UI was failing with console errors during `aspire run`:

```
Failed to load module script: Expected a JavaScript-or-Wasm module script 
but the server responded with a MIME type of "text/html"

MONO_WASM: Failed to load config file undefined 
TypeError: Failed to fetch dynamically imported module: https://localhost:7173/0
```

## Root Causes

### 1. **Bolero Does NOT Support .NET 10**

**Critical Issue**: Bolero version 0.24.39 (latest as of Nov 2025) only targets:
- ✅ net6.0
- ✅ net8.0
- ❌ **net10.0 NOT SUPPORTED**

The project was targeting `net10.0`, which caused compatibility issues with Bolero's internal dependencies.

### 2. **Missing index.html in Build Output**

F# Blazor WebAssembly projects require explicit file inclusion for wwwroot content. The `index.html` file wasn't being copied to the output directory during `aspire run --no-build`.

### 3. **Fire-and-Forget RunAsync Pattern**

The Blazor host was started with `host.RunAsync() |> ignore`, causing the application to exit immediately before initialization completed.

## Complete Solution

### Fix 1: Downgrade to .NET 8

**File**: `DeckBuilder.Ui/DeckBuilder.Ui.fsproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>  <!-- Changed from net10.0 -->
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>
    <RunAOTCompilation>false</RunAOTCompilation>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Bolero" Version="0.24.39" />
    <!-- Changed from 10.0.0 to 8.0.11 -->
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" Version="8.0.11" PrivateAssets="all" />
    <PackageReference Include="OpenTelemetry" Version="1.14.0-rc.1" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.14.0-rc.1" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.14.0-rc.1" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.13.0" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
    <!-- Explicit index.html inclusion for F# projects -->
    <None Include="wwwroot\index.html">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </None>
    <Content Update="DeckBuilder.Ui.runtimeconfig.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../DeckBuilder.Shared/DeckBuilder.Shared.fsproj" />
  </ItemGroup>
</Project>
```

### Fix 2: Proper Host Lifecycle

**File**: `DeckBuilder.Ui/Program.fs` (line ~312)

```fsharp
[<EntryPoint>]
let main args =
    let builder = WebAssemblyHostBuilder.CreateDefault(args)
    builder.RootComponents.Add<MyApp>("#app")
    builder.Services.AddScoped<HttpClient>(fun _ -> 
        new HttpClient(BaseAddress = Uri(builder.HostEnvironment.BaseAddress))) |> ignore
    
    // OpenTelemetry setup...
    
    let host = builder.Build()
    let httpClient = host.Services.GetRequiredService<HttpClient>()
    Api.setClient httpClient
    
    // FIXED: Block until host completes instead of fire-and-forget
    host.RunAsync().GetAwaiter().GetResult()
    0
```

**Before (BROKEN)**:
```fsharp
host.RunAsync() |> ignore  // ❌ Fire-and-forget exits immediately
0
```

**After (FIXED)**:
```fsharp
host.RunAsync().GetAwaiter().GetResult()  // ✅ Blocks until host completes
0
```

## Build & Run Process

### Clean Build (Required After Changes)

```bash
cd X:\Code\Lorcana-Deck-Builder
dotnet clean
dotnet build
```

### Run with Aspire

```bash
aspire run
```

Aspire will:
1. Start dashboard at `https://localhost:17193`
2. Launch Ollama container (GPU-enabled)
3. Launch Qdrant container (persistent)
4. Start DeckBuilder.Api (.NET 10)
5. Start DeckBuilder.Ui (.NET 8 with Bolero)

## Verification

After `aspire run`, check:

1. **Dashboard**: Navigate to `https://localhost:17193`
2. **UI Service**: Should show "Running" status
3. **Browser Console**: Navigate to UI URL, no errors should appear
4. **index.html**: Should load properly with Bolero app

### Manual Verification

```powershell
# Verify index.html exists in build output
Test-Path "X:\Code\Lorcana-Deck-Builder\DeckBuilder.Ui\bin\Debug\net8.0\wwwroot\index.html"
# Should return: True
```

## Why This Happens

### F# Project Behavior

Unlike C# Blazor projects, F# projects don't automatically include wwwroot files by default. You must explicitly mark them with:

```xml
<None Include="wwwroot\index.html">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

### Aspire's --no-build Flag

`aspire run` executes projects with `--no-build` flag, relying on previously built artifacts. If the initial build didn't properly copy wwwroot files, the runtime will fail.

**Solution**: Always run `dotnet build` before first `aspire run` after clean or project changes.

## Bolero .NET Compatibility Matrix

| Bolero Version | .NET 6 | .NET 8 | .NET 10 |
|---------------|--------|--------|---------|
| 0.24.x        | ✅     | ✅     | ❌      |
| Future?       | ?      | ?      | ?       |

**Recommendation**: Stay on .NET 8 for Bolero projects until official .NET 10 support is released.

## Alternative Solutions (Not Recommended)

### Option A: Switch to Standard Blazor WASM

Remove Bolero and use standard Blazor with manual Elmish implementation:
- ❌ Loses Bolero's F#-friendly HTML DSL
- ❌ Loses type-safe routing
- ❌ More boilerplate code

### Option B: Wait for Bolero .NET 10 Support

Monitor Bolero releases: https://github.com/fsbolero/Bolero
- ⏳ Timeline unknown
- ⏳ May require breaking changes

### Option C: Use Fable.Remoting Instead

Switch to Fable + Elmish + Fable.Remoting:
- ✅ Full .NET 10 support
- ❌ Completely different tech stack
- ❌ Not Blazor-based (uses React/JS runtime)

## Functional Programming Notes

### Why GetAwaiter().GetResult()?

In F#, the main entry point is synchronous but needs to wait for async operations:

```fsharp
// ❌ Wrong: Fire-and-forget
host.RunAsync() |> ignore

// ❌ Wrong: Can't use async in main
let! _ = host.RunAsync()  // Compiler error

// ✅ Correct: Synchronous blocking
host.RunAsync().GetAwaiter().GetResult()

// ✅ Alternative: Async.RunSynchronously
Async.RunSynchronously(host.RunAsync() |> Async.AwaitTask)
```

The functional approach prefers composition, but .NET's entry point requires blocking.

## Troubleshooting

### Issue: "Bolero not found" errors

**Cause**: NuGet restore failed or package cache corrupted

**Solution**:
```bash
dotnet nuget locals all --clear
dotnet restore
dotnet build
```

### Issue: "Module not found" in browser

**Cause**: wwwroot files not copied

**Solution**:
```bash
dotnet clean
dotnet build
# Verify:
Test-Path "DeckBuilder.Ui\bin\Debug\net8.0\wwwroot\index.html"
```

### Issue: Aspire fails to start UI

**Cause**: Build artifacts missing or corrupted

**Solution**:
```bash
# Kill any running processes
Get-Process | Where-Object { $_.ProcessName -like "*DeckBuilder*" } | Stop-Process -Force

# Clean rebuild
dotnet clean
dotnet build
aspire run
```

## Summary

✅ **Bolero requires .NET 8** (not .NET 10)  
✅ **F# projects need explicit wwwroot file inclusion**  
✅ **Host must block until completion** (not fire-and-forget)  
✅ **Always build before first aspire run**

With these fixes, `aspire run` will properly build and serve the Bolero UI without console errors.
