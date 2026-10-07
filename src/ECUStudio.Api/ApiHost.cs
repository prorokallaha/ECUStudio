using System.Text.Json.Serialization;
using ECUStudio.Core;
using ECUStudio.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

namespace ECUStudio.Api;

public sealed record ApiHostOptions
{
    /// <summary>Overrides configuration-derived options (desktop host passes SQLite path, tests pass in-memory).</summary>
    public EcuStudioOptions? Studio { get; init; }
    /// <summary>Directory with the static frontend export (index.html). Defaults to ./wwwroot next to the binary.</summary>
    public string? WebRoot { get; init; }
    /// <summary>e.g. "http://127.0.0.1:0" for the desktop host (random loopback port).</summary>
    public string? Urls { get; init; }
}

/// <summary>Builds the HTTP host. Used by the standalone server, the desktop shell and integration tests.</summary>
public static class ApiHost
{
    public const long MaxUploadBytes = 16 * 1024 * 1024;

    public static WebApplication Build(string[] args, ApiHostOptions? hostOptions = null)
    {
        hostOptions ??= new ApiHostOptions();
        var webRoot = hostOptions.WebRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Directory.Exists(webRoot) ? webRoot : null,
        });
        if (hostOptions.Urls is not null) builder.WebHost.UseUrls(hostOptions.Urls);

        var studio = hostOptions.Studio ?? ReadOptions(builder.Configuration);
        builder.Services.AddEcuStudio(studio);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes + 64 * 1024);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUploadBytes + 64 * 1024);
        builder.Services.AddOpenApi(o => o.AddSchemaTransformer((schema, ctx, _) =>
        {
            // The API never omits non-null properties (only nulls are skipped), so mark non-nullable ones required:
            // generated TypeScript types then match the wire format exactly. Nullability comes from the CLR type
            // (Nullable<T> or a nullable reference annotation), not from the JSON schema shape.
            if (schema.Properties is { Count: > 0 } && ctx.JsonTypeInfo.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
            {
                schema.Required ??= new HashSet<string>();
                foreach (var p in ctx.JsonTypeInfo.Properties)
                {
                    var nullable = p.IsGetNullable || Nullable.GetUnderlyingType(p.PropertyType) is not null;
                    if (!nullable && schema.Properties.ContainsKey(p.Name)) schema.Required.Add(p.Name);
                }
            }
            return Task.CompletedTask;
        }));
        builder.Services.AddProblemDetails();
        builder.Services.AddCors(o => o.AddPolicy("dev", p => p
            .WithOrigins("http://localhost:3000", "http://127.0.0.1:3000").AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = WriteError,
            // Domain errors (4xx, AI unavailable) are expected outcomes, not server faults.
            SuppressDiagnosticsCallback = c => c.Exception is EcuStudioException,
        });
        if (app.Environment.IsDevelopment()) app.UseCors("dev");
        app.MapOpenApi("/api/openapi/{documentName}.json");

        app.MapGroup("/api/v1").MapStudioEndpoints();
        app.MapGet("/api/health", (ECUStudio.Application.Analysis.StudioService s) => Results.Ok(new { status = "ok", ai = s.AIConfigured, version = ECUStudio.Application.Analysis.AnalysisPipeline.AnalysisVersion }));
        app.MapFallback("/api/{**rest}", () => Results.Json(new { error = new { code = "NOT_FOUND", message = "Unknown API route" } }, statusCode: 404));

        if (app.Environment.WebRootPath is { } root)
        {
            var files = new PhysicalFileProvider(root);
            app.UseDefaultFiles();
            app.UseStaticFiles();
            // Static Next.js export: /project/maps → /project/maps/index.html, otherwise the app shell.
            app.MapFallback(async ctx =>
            {
                var path = ctx.Request.Path.Value?.Trim('/') ?? "";
                var candidate = string.IsNullOrEmpty(path) ? "index.html" : $"{path}/index.html";
                var file = files.GetFileInfo(candidate);
                if (!file.Exists) file = files.GetFileInfo($"{path}.html");
                if (!file.Exists) file = files.GetFileInfo("index.html");
                if (!file.Exists) { ctx.Response.StatusCode = 404; return; }
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(file);
            });
        }
        return app;
    }

    public static EcuStudioOptions ReadOptions(IConfiguration c)
    {
        var storage = c["ECUSTUDIO_STORAGE"] ?? c["EcuStudio:Storage"] ?? "sqlite";
        var conn = c["ECUSTUDIO_CONNECTION"]
            ?? (storage.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) ? c.GetConnectionString("Postgres") : c.GetConnectionString("Sqlite"));
        return new EcuStudioOptions
        {
            Storage = storage,
            ConnectionString = string.IsNullOrWhiteSpace(conn) ? null : conn,
            DefinitionsPath = c["ECUSTUDIO_DEFINITIONS"] ?? c["EcuStudio:DefinitionsPath"],
            IncludeDemoDefinitions = !string.Equals(c["EcuStudio:IncludeDemoDefinitions"], "false", StringComparison.OrdinalIgnoreCase),
            AIModel = c["ECUSTUDIO_CLAUDE_MODEL"] ?? c["EcuStudio:AIModel"] ?? "claude-opus-5-5",
        };
    }

    private static async Task WriteError(HttpContext ctx)
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, code, message, details) = ex switch
        {
            EcuStudioException e => (e.HttpStatus, e.Code, e.Message, (object?)e.Details),
            BadHttpRequestException b => (b.StatusCode, "BAD_REQUEST", b.Message, null),
            System.Text.Json.JsonException j => (400, "INVALID_JSON", j.Message, null),
            FormatException f => (400, "INVALID_FORMAT", f.Message, null),
            OperationCanceledException => (499, "CANCELLED", "Request cancelled", null),
            _ => (500, "INTERNAL", "Internal error. See server log.", null),
        };
        if (status >= 500 && ex is not EcuStudioException) ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ECUStudio.Api").LogError(ex, "Unhandled error");
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = new { code, message, details } });
    }
}
