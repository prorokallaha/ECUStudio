using ECUStudio.Api;
using ECUStudio.Infrastructure;

var app = ApiHost.Build(args);
await app.Services.InitializeEcuStudioAsync();
await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
