using System.Windows;
using SqlAgent.Client.Wpf.Services;
using SqlAgent.Client.Wpf.ViewModels;

namespace SqlAgent.Client.Wpf;

/// <summary>
/// Composition root. Wires the named-pipe client, the (replaceable) voice service, and the shell view model,
/// then shows the window. There is no DI container — the object graph is small and built once here.
/// To enable real voice input later, swap <see cref="NullVoiceInputService"/> for a concrete engine.
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Same env var the MCP server reads (CD-51 Story 1.7). Null when unset, which is exactly what a host
        // with no configured token expects — but without this the client is locked out of a host that has one.
        var api = new LocalApiClient(authToken: Environment.GetEnvironmentVariable("SQLAGENT_AUTH_TOKEN"));
        IVoiceInputService voice = new NullVoiceInputService();
        var main = new MainViewModel(api, voice);

        var window = new MainWindow { DataContext = main };
        window.Show();

        // Load the connection list once the window is up; a missing host surfaces as a status line, not a crash.
        await main.InitializeAsync();
    }
}
