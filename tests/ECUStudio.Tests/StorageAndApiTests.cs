using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECUStudio.Api;
using ECUStudio.Application.Projects;
using ECUStudio.Infrastructure;
using ECUStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ECUStudio.Tests;

public class SchemaMigratorTests
{
    [Theory]
    [InlineData(DatabaseProvider.Sqlite)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void Embedded_migrations_load_in_order(DatabaseProvider provider)
    {
        var m = SchemaMigrator.Load(provider);
        Assert.NotEmpty(m);
        Assert.Equal(1, m[0].Version);
        Assert.Contains("projects", m[0].Sql);
        Assert.Equal(m.OrderBy(x => x.Version).Select(x => x.Version), m.Select(x => x.Version));
    }
}

public sealed class SqliteStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ecustudio-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        _sp = new ServiceCollection().AddEcuStudio(new EcuStudioOptions { Storage = "sqlite", ConnectionString = $"Data Source={_path}", AnthropicApiKey = null }).BuildServiceProvider();
        await _sp.InitializeEcuStudioAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        await using var db = await _sp.GetRequiredService<IDbContextFactory<StudioDbContext>>().CreateDbContextAsync();
        Assert.Empty(await SchemaMigrator.MigrateAsync(db));
    }

    [Fact]
    public async Task Project_document_files_and_decisions_round_trip()
    {
        var store = _sp.GetRequiredService<IProjectStore>();
        var fileId = Guid.NewGuid();
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "Golf", Vin = Fixtures.GolfVin,
            HardwareOverrides = [new() { Kind = ECUStudio.Components.ComponentKind.Transmission, CatalogId = "trans_dsg_dq250" }],
            Files = [new ProjectFile { Id = fileId, Name = "stock.bin", Label = "Stock", Role = FileRole.Stock, Sha256 = "ab", Size = 3 }],
        };
        await store.SaveAsync(project);
        await store.SaveFileContentAsync(fileId, [1, 2, 3]);
        await store.SaveAnalysisAsync(Guid.NewGuid(), project.Id, "{\"ok\":true}");

        var loaded = await store.GetAsync(project.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Golf", loaded.Name);
        Assert.Equal("trans_dsg_dq250", Assert.Single(loaded.HardwareOverrides).CatalogId);
        Assert.Equal(FileRole.Stock, Assert.Single(loaded.Files).Role);
        Assert.Equal([1, 2, 3], await store.GetFileContentAsync(fileId));
        Assert.Single(await store.ListAsync());

        await store.SaveAsync(loaded with { Name = "Golf V" });
        Assert.Equal("Golf V", (await store.GetAsync(project.Id))!.Name);

        await store.DeleteAsync(project.Id);
        Assert.Null(await store.GetAsync(project.Id));
        Assert.Null(await store.GetFileContentAsync(fileId));
    }

    [Fact]
    public async Task AI_cache_persists_entries()
    {
        var cache = _sp.GetRequiredService<ECUStudio.AI.IAICacheStore>();
        await cache.PutAsync(new ECUStudio.AI.AICacheEntry("k1", "Engine Analyst", "m", "{\"a\":1}", new(1, 2, 3, 4), DateTimeOffset.UtcNow));
        var e = await cache.GetAsync("k1");
        Assert.NotNull(e);
        Assert.Equal("{\"a\":1}", e.Json);
        Assert.Equal(2, e.Usage.OutputTokens);
        Assert.Null(await cache.GetAsync("missing"));
    }
}

/// <summary>End-to-end over real HTTP (Kestrel on a random loopback port) with in-memory storage.</summary>
public sealed class ApiIntegrationTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _app = ApiHost.Build([], new ApiHostOptions
        {
            Studio = new EcuStudioOptions { Storage = "memory", AnthropicApiKey = null },
            Urls = "http://127.0.0.1:0",
            WebRoot = Path.Combine(Path.GetTempPath(), "ecustudio-no-webroot"),
        });
        await _app.Services.InitializeEcuStudioAsync();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Health_and_info()
    {
        var health = await _http.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.False(health.GetProperty("ai").GetBoolean());
        var info = await _http.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.Contains(info.GetProperty("plugins").EnumerateArray(), p => p.GetProperty("id").GetString() == "edc16u34");
    }

    [Fact]
    public async Task Errors_use_uniform_payload()
    {
        var bad = await _http.GetAsync("/api/v1/vin/NOTAVIN");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_VIN", body.GetProperty("error").GetProperty("code").GetString());

        var missing = await _http.GetAsync($"/api/v1/projects/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("NOT_FOUND", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/nope")).StatusCode);
    }

    [Fact]
    public async Task Demo_project_analysis_flow()
    {
        var project = await (await _http.PostAsync("/api/v1/projects/demo", null)).Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var projectId = project.GetProperty("id").GetGuid();

        var started = await _http.PostAsJsonAsync($"/api/v1/projects/{projectId}/analyses", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var jobId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

        JsonElement job = default;
        for (var i = 0; i < 600; i++)
        {
            job = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}");
            var status = job.GetProperty("status").GetString();
            if (status is "Completed" or "Failed") break;
            await Task.Delay(250);
        }
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        var analysisId = job.GetProperty("events").EnumerateArray().Select(e => e.TryGetProperty("analysisId", out var r) ? r.GetString() : null).Last(x => x is not null);

        var report = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/analyses/{analysisId}");
        Assert.Equal("edc16u34", report.GetProperty("ecu").GetProperty("pluginId").GetString());
        Assert.Equal("Agrees", report.GetProperty("logs")[0].GetProperty("status").GetString());
        var mapId = report.GetProperty("maps")[0].GetProperty("id").GetString();
        var map = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/analyses/{analysisId}/maps/{mapId}");
        Assert.True(map.GetProperty("values").GetArrayLength() > 0);

        var hex = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/analyses/{analysisId}/hex?offset=0&length=256");
        Assert.Equal(256, Convert.FromBase64String(hex.GetProperty("modified").GetString()!).Length);

        var md = await _http.GetStringAsync($"/api/v1/analyses/{analysisId}/report.md");
        Assert.Contains("estimate", md, StringComparison.OrdinalIgnoreCase);

        var ai = await _http.PostAsync($"/api/v1/analyses/{analysisId}/ai/ask", JsonContent.Create(new { question = "safe?" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ai.StatusCode);
        Assert.Equal("AI_UNAVAILABLE", (await ai.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Log_upload_validates_content()
    {
        var project = await (await _http.PostAsJsonAsync("/api/v1/projects", new { name = "logs" })).Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var id = project.GetProperty("id").GetGuid();

        using var bad = new MultipartFormDataContent { { new ByteArrayContent("a,b\n1,2\n"u8.ToArray()), "file", "bad.csv" } };
        var badResp = await _http.PostAsync($"/api/v1/projects/{id}/logs", bad);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badResp.StatusCode);
        Assert.Equal("LOG_FORMAT", (await badResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        using var ok = new MultipartFormDataContent { { new ByteArrayContent("rpm,boost (mbar)\n2000,2100\n2500,2200\n"u8.ToArray()), "file", "pull.csv" } };
        var okResp = await _http.PostAsync($"/api/v1/projects/{id}/logs", ok);
        Assert.Equal(HttpStatusCode.Created, okResp.StatusCode);
        var log = await okResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(2, log.GetProperty("samples").GetInt32());

        var after = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{id}", JsonOpts);
        Assert.Equal(1, after.GetProperty("logCount").GetInt32());
        var del = await _http.DeleteAsync($"/api/v1/projects/{id}/logs/{log.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    [Fact]
    public async Task Openapi_document_is_served()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/api/openapi/v1.json");
        Assert.True(doc.GetProperty("paths").TryGetProperty("/api/v1/projects/{id}/analyses", out _));
    }
}
