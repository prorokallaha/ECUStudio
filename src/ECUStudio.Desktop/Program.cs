using ECUStudio.Api;
using ECUStudio.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ECUStudio.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Embedded database in %LOCALAPPDATA%\ECUStudio — no server, no Python, no Node required.
        var options = new ApiHostOptions
        {
            Urls = "http://127.0.0.1:0",
            WebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Studio = new EcuStudioOptions
            {
                Storage = "sqlite",
                ConnectionString = $"Data Source={EcuStudioOptions.DefaultSqlitePath()}",
                DefinitionsPath = Path.Combine(AppContext.BaseDirectory, "definitions"),
            },
        };

        WebApplication app;
        try
        {
            app = ApiHost.Build(args, options);
            app.Services.InitializeEcuStudioAsync().GetAwaiter().GetResult();
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ECUStudio could not start its local engine:\n\n{ex.Message}", "ECUStudio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        try
        {
            System.Windows.Forms.Application.Run(new MainForm(new Uri(address)));
        }
        finally
        {
            app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
