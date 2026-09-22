using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Platform.Windows;
using TKSDesktop.ViewModels;
using TKSDesktop.Views;

namespace DesktopAcceptanceProbe;

/// <summary>
/// Opt-in desktop acceptance diagnostics. Does not start the app's login/network
/// lifecycle, open windows, or read the user's credentials. Default execution
/// briefly registers a tray icon and a temporary hotkey, then releases both.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var sendToast = false;
        string? autoStartExecutable = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--toast":
                    sendToast = true;
                    break;
                case "--autostart-roundtrip" when index + 1 < args.Length:
                    autoStartExecutable = Path.GetFullPath(args[++index]);
                    if (!File.Exists(autoStartExecutable)
                        || !string.Equals(Path.GetFileName(autoStartExecutable), "TKSDesktop.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("--autostart-roundtrip requires an existing TKSDesktop.exe path.");
                        return 2;
                    }
                    break;
                default:
                    Console.Error.WriteLine("Usage: DesktopAcceptanceProbe [--toast] [--autostart-roundtrip <installed TKSDesktop.exe>]");
                    return 2;
            }
        }

        var evidence = new Dictionary<string, object?>
        {
            ["observedAtUtc"] = DateTimeOffset.UtcNow,
            ["scope"] = "Adapter and bound-view diagnostics; not visual, login, reboot, resume, or multi-monitor acceptance.",
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["productAssembly"] = typeof(TrayIconManager).Assembly.Location,
        };

        Application.ResourceAssembly = typeof(TrayIconManager).Assembly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            evidence["resources"] = Capture(() =>
            {
                foreach (var dictionary in new[] { "Controls", "DoodleBackground" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/TKSDesktop;component/Resources/Themes/{dictionary}.xaml"),
                    });
                }

                return new { loaded = true };
            });
            evidence["tray"] = Capture(ProbeTray);
            evidence["hotkey"] = Capture(ProbeHotkey);
            evidence["notification"] = Capture(() => ProbeNotification(sendToast));
            evidence["boundSettingsLayout"] = Capture(ProbeBoundSettingsLayout);
            evidence["displayMetrics"] = Capture(() => new
            {
                virtualWidth = SystemParameters.VirtualScreenWidth,
                virtualHeight = SystemParameters.VirtualScreenHeight,
                primaryWidth = SystemParameters.PrimaryScreenWidth,
                primaryHeight = SystemParameters.PrimaryScreenHeight,
                note = "WPF logical display metrics only; no display settings changed or monitor transition tested.",
            });
            evidence["autoStart"] = autoStartExecutable is null
                ? new { skipped = true, reason = "Requires --autostart-roundtrip <installed TKSDesktop.exe>; default execution does not write HKCU Run." }
                : Capture(() => ProbeAutoStart(autoStartExecutable));
        }
        finally
        {
            app.Shutdown();
        }

        Console.WriteLine(JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        // Results are evidence, not an assertion that all manual acceptance has passed.
        return 0;
    }

    private static object Capture(Func<object> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            return new
            {
                completed = false,
                exceptionType = exception.GetType().FullName,
                exception = exception.ToString(),
            };
        }
    }

    private static object ProbeTray()
    {
        using var tray = new TrayIconManager();
        var initialized = tray.Initialize();
        return new
        {
            initialized,
            tray.IsAvailable,
            tray.UnavailableReason,
            note = "Registration only; no manual tray-menu or icon visibility confirmation. Disposed after probe.",
        };
    }

    private static object ProbeHotkey()
    {
        const string combination = "Control+Alt+Shift+F11";
        using var first = new GlobalHotkeyManager();
        using var second = new GlobalHotkeyManager();
        var initial = first.Register(combination);
        var duplicate = second.Register(combination);
        return new
        {
            combination,
            initial = initial.ToString(),
            initialWin32Error = first.LastWin32Error,
            duplicate = duplicate.ToString(),
            duplicateWin32Error = second.LastWin32Error,
            note = "Register/conflict check only; no physical key press dispatched. Both registrations disposed after probe.",
        };
    }

    private static object ProbeNotification(bool sendToast)
    {
        using var presenter = new ToastNotificationPresenter();
        var before = new { presenter.IsToastAvailable, presenter.CapabilityDescription };
        if (sendToast)
        {
            presenter.Present(SemanticNotificationId.Error, "TKS Desktop 验收通知", "本地通知能力验证");
        }

        return new
        {
            presenter.StartMenuShortcutPath,
            before,
            sendRequested = sendToast,
            after = new { presenter.IsToastAvailable, presenter.CapabilityDescription },
            note = "Capability/send-call result only; visual appearance and notification click activation are not verified.",
        };
    }

    private static object ProbeBoundSettingsLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "tks-desktop-acceptance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root, new Dictionary<string, string?>
            {
                [AppPaths.EnvConfigDir] = Path.Combine(root, "config"),
                [AppPaths.EnvDataDir] = Path.Combine(root, "data"),
                [AppPaths.EnvStateDir] = Path.Combine(root, "state"),
            });
            paths.EnsureDirectories();
            var provider = AppBootstrap.BuildServiceProvider(CliOptions.Parse([]), paths);
            try
            {
                var view = new SettingsView
                {
                    DataContext = provider.GetRequiredService<SettingsViewModel>(),
                };
                view.Measure(new Size(800, 1000));
                view.Arrange(new Rect(0, 0, 800, 1000));
                view.UpdateLayout();
                return new
                {
                    completed = true,
                    view.ActualWidth,
                    view.ActualHeight,
                    note = "Production view and view model bound and laid out using isolated temporary paths; no window shown or settings saved.",
                };
            }
            finally
            {
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            // This path is created above solely for this invocation, never supplied by the user.
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static object ProbeAutoStart(string executable)
    {
        // Refuse to create a Run key solely for the probe. Only the specified value
        // may be changed, and its raw data and RegistryValueKind are restored.
        using var run = Registry.CurrentUser.OpenSubKey(AutoStartManager.RunSubKeyPath, writable: true)
            ?? throw new InvalidOperationException("HKCU Run is unavailable; no registry changes made.");
        var existed = run.GetValueNames().Contains(AutoStartManager.ValueName, StringComparer.OrdinalIgnoreCase);
        var original = existed ? run.GetValue(AutoStartManager.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        var originalKind = existed ? run.GetValueKind(AutoStartManager.ValueName) : RegistryValueKind.Unknown;
        if (existed && original is null)
        {
            throw new InvalidOperationException("Could not snapshot the existing Run value; no registry changes made.");
        }

        var result = new Dictionary<string, object?>
        {
            ["executable"] = executable,
            ["originalValueExisted"] = existed,
            ["originalValueKind"] = originalKind.ToString(),
            ["note"] = "Registry roundtrip only; no sign-out, restart, or automatic-launch behavior is tested.",
        };
        try
        {
            var manager = new AutoStartManager(executable);
            result["enableReturned"] = manager.Enable();
            result["enabledReadBack"] = manager.IsEnabled;
            result["commandLineReadBackMatches"] = Equals(
                run.GetValue(AutoStartManager.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                AutoStartManager.BuildCommandLine(manager.ExpectedExecutablePath));
            result["disableReturned"] = manager.Disable();
            result["disabledReadBack"] = !run.GetValueNames().Contains(AutoStartManager.ValueName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            result["exception"] = exception.ToString();
        }
        finally
        {
            if (existed)
            {
                run.SetValue(AutoStartManager.ValueName, original!, originalKind);
            }
            else
            {
                run.DeleteValue(AutoStartManager.ValueName, throwOnMissingValue: false);
            }

            var nowExists = run.GetValueNames().Contains(AutoStartManager.ValueName, StringComparer.OrdinalIgnoreCase);
            result["restored"] = existed == nowExists && (!existed ||
                (run.GetValueKind(AutoStartManager.ValueName) == originalKind
                && JsonSerializer.Serialize(run.GetValue(AutoStartManager.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames))
                    == JsonSerializer.Serialize(original)));
        }

        return result;
    }
}
