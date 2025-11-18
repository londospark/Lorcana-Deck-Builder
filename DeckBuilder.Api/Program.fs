
module DeckBuilder.Api.Program

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open OpenTelemetry.Resources
open OpenTelemetry.Logs
open OpenTelemetry.Trace
open OpenTelemetry.Metrics
open Prometheus
open System.Threading.Tasks
open OllamaSharp
open Qdrant.Client
open System.Text.Json
open System.Text.Json.Serialization

[<EntryPoint>]
let main _ =
    let builder = WebApplication.CreateBuilder()
    
    // Configure JSON options for F# types
    builder.Services.ConfigureHttpJsonOptions(fun options ->
        options.SerializerOptions.Converters.Add(JsonFSharpConverter())
    ) |> ignore
    
    // Register Qdrant client via Aspire integration; uses ConnectionStrings:qdrant provided by AppHost
    builder.AddQdrantClient("qdrant") |> ignore
    let ollama = builder.AddOllamaApiClient("ollama")
    ollama.AddChatClient() |> ignore
    ollama.AddEmbeddingGenerator() |> ignore

    // CORS: allow cross-origin calls during development (e.g., UI dev server at localhost:5173)
    builder.Services.AddCors(fun options ->
        options.AddPolicy("DevCors", fun policy ->
            policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod()
            |> ignore)
    ) |> ignore

    // Load rules provider (searches for PDF under the app content root's Data folder; falls back to AppContext.BaseDirectory via createDefault if needed)
    builder.Services.AddSingleton<RulesProvider.IRulesProvider>(fun sp ->
        let env = sp.GetRequiredService<IHostEnvironment>()
        RulesProvider.createWithDir(env.ContentRootPath)
    ) |> ignore

    // OpenTelemetry logging
    let serviceVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()
    let resource = ResourceBuilder.CreateDefault().AddService(serviceName = "DeckBuilder.Api", serviceVersion = serviceVersion)
    builder.Logging.ClearProviders() |> ignore
    builder.Logging.AddOpenTelemetry(fun o ->
        o.IncludeScopes <- true
        o.ParseStateValues <- true
        o.IncludeFormattedMessage <- true
        o.SetResourceBuilder(resource) |> ignore
        o.AddOtlpExporter() |> ignore
    ) |> ignore

    // OpenTelemetry tracing + metrics
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(fun rb -> rb.AddService(serviceName = "DeckBuilder.Api", serviceVersion = serviceVersion) |> ignore)
        .WithTracing(fun t ->
            t.AddAspNetCoreInstrumentation() |> ignore
            t.AddHttpClientInstrumentation() |> ignore
            t.AddOtlpExporter() |> ignore
        )
        .WithMetrics(fun m ->
            m.AddAspNetCoreInstrumentation() |> ignore
            m.AddHttpClientInstrumentation() |> ignore
            m.AddRuntimeInstrumentation() |> ignore
            m.AddOtlpExporter() |> ignore
        )
        |> ignore

    // Register Deck service (must be before Build to avoid read-only service collection)
    builder.Services.AddSingleton<DeckService.IDeckBuilder, DeckService.DeckBuilderService>() |> ignore

    let app = builder.Build()

    if app.Environment.IsDevelopment() then
        app.UseCors("DevCors") |> ignore

    // Expose Prometheus OpenMetrics endpoint at /metrics and request metrics
    app.UseHttpMetrics() |> ignore
    app.MapMetrics() |> ignore

    // Register endpoints
    Endpoints.registerIngest app
    Endpoints.registerRules app
    Endpoints.registerIngestRules app
    Endpoints.registerDeck app
    Endpoints.registerAgenticDeck app

    // Startup task: ingest rules into Qdrant RAG collection (idempotent)
    do
        let sp = app.Services
        let logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
        try
            let q = sp.GetRequiredService<QdrantClient>()
            let o = sp.GetRequiredService<IOllamaApiClient>()
            let rp = sp.GetRequiredService<RulesProvider.IRulesProvider>()
            let t = Endpoints.ingestRulesAsync q o rp
            t.ContinueWith(fun (t: Task<Result<int,string>>) ->
                match t.Status with
                | TaskStatus.RanToCompletion ->
                    match t.Result with
                    | Ok count -> logger.LogInformation("Rules ingest completed with {Count} chunks", count)
                    | Error msg -> logger.LogWarning("Rules ingest skipped/failed: {Msg}", msg)
                | TaskStatus.Faulted -> logger.LogError(t.Exception, "Rules ingest failed at startup")
                | _ -> ()
            ) |> ignore
        with ex ->
            logger.LogWarning(ex, "Failed to start rules ingest at startup")

    app.Run()
    0
