using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TKSDesktop.App;
using TKSDesktop.Core.Auth;
using TKSDesktop.Core.Services;
using TKSDesktop.Platform.Windows;
using TKSDesktop.Views;

namespace WindowActivationProbe;

internal static class Program
{
    // Isolated profile, pipe and mutex. No credentials, login or WS lifecycle.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        using var instance = new SingleInstanceGuard(args[1]);
        if (args[0] == "--send")
        {
            if (instance.TryAcquire()) return 3;
            return instance.NotifyExistingInstanceAsync().GetAwaiter().GetResult() ? 0 : 4;
        }
        if (args[0] != "--host" || args.Length != 3 || !instance.TryAcquire()) return 2;
        var root = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var dictionary in new[] { "Controls", "DoodleBackground" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/TKSDesktop;component/Resources/Themes/{dictionary}.xaml")
            });
        var paths = new AppPaths(root, new Dictionary<string, string?>
        {
            [AppPaths.EnvConfigDir] = Path.Combine(root, "config"),
            [AppPaths.EnvDataDir] = Path.Combine(root, "data"),
            [AppPaths.EnvStateDir] = Path.Combine(root, "state")
        });
        paths.EnsureDirectories();
        var provider = AppBootstrap.BuildServiceProvider(CliOptions.Parse([]), paths);
        try
        {
            var window = new MainWindow(new AuthenticatedProvider(provider));
            window.Show();
            window.HideToTray();
            var round = 0;
            Window? modal = null;
            instance.ActivateRequested += (_, _) => app.Dispatcher.Invoke(() =>
            {
                var before = round switch { 0 => "hidden", 1 => "minimized", 2 => "hidden-maximized", _ => "modal-owner" };
                window.ActivateAndFocus();
                var state = window.WindowState;
                var visible = window.IsVisible;
                var active = modal?.IsActive ?? window.IsActive;
                var nativeEnabled = typeof(SingleInstanceGuard).Assembly
                    .GetType("TKSDesktop.Platform.Windows.NativeMethods")!
                    .GetMethod("IsWindowEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                var ownerEnabled = (bool)nativeEnabled.Invoke(null, [new System.Windows.Interop.WindowInteropHelper(window).Handle])!;
                round++;
                if (round == 1) window.WindowState = WindowState.Minimized;
                else if (round == 2) { window.WindowState = WindowState.Maximized; window.HideToTray(); }
                File.WriteAllText(Path.Combine(root, $"round-{round}.json"), JsonSerializer.Serialize(new
                {
                    scenario = before, visible, active, ownerEnabled, state = state.ToString(), pid = Environment.ProcessId
                }));
                if (round == 3)
                {
                    modal = new Window
                    {
                        Owner = window, Title = "Activation probe dialog", Width = 240, Height = 160, ShowInTaskbar = false,
                        Content = new System.Windows.Controls.TextBlock { Text = "Activation probe" }
                    };
                    modal.ContentRendered += (_, _) => File.WriteAllText(Path.Combine(root, "modal-ready"), "ready");
                    _ = app.Dispatcher.BeginInvoke(new Action(() => modal.ShowDialog()));
                }
                if (round == 4)
                {
                    modal!.Close();
                    app.Shutdown(visible && active && state == WindowState.Maximized ? 0 : 1);
                }
            });
            instance.StartListening();
            File.WriteAllText(Path.Combine(root, "ready"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return app.Run();
        }
        finally { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private sealed class AuthenticatedProvider(IServiceProvider provider) : IServiceProvider
    {
        private readonly IAuthService _auth = new ProbeAuth();
        public object? GetService(Type serviceType) => serviceType == typeof(IAuthService) ? _auth : provider.GetService(serviceType);
    }

    private sealed class ProbeAuth : IAuthService
    {
        public AuthState State => AuthState.Authenticated;
        public string UserId => "activation-probe";
        public string DeviceId => "probe";
        public CredentialViewStatus CredentialStatus => new(false, false, "");
        public Task<TKSDesktop.Core.Services.LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default) => throw new NotSupportedException();
        public CredentialRestoreResult TryRestore() => throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
