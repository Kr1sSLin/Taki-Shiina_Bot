using System.Windows;
using TKSDesktop.App;
using TKSDesktop.Views;
using Xunit;

namespace TKSDesktop.Tests;

[CollectionDefinition("WpfConstruction", DisableParallelization = true)]
public sealed class WpfConstructionCollection;

[Collection("WpfConstruction")]
public sealed class WpfConstructionTests
{
    [Fact]
    public async Task ApplicationResources_WindowsAndPanels_CompleteLayout()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "tks-wpf-" + Guid.NewGuid().ToString("N"));
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var dictionary in new[] { "Controls", "DoodleBackground" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/TKSDesktop;component/Resources/Themes/{dictionary}.xaml"),
                    });
                }

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
                    var login = new LoginWindow(provider) { ShowActivated = false, ShowInTaskbar = false };
                    login.Show();
                    login.UpdateLayout();
                    Assert.True(login.IsVisible);
                    login.Hide();
                    var main = new MainWindow(provider) { ShowActivated = false, ShowInTaskbar = false };
                    main.Show();
                    main.UpdateLayout();
                    Assert.True(main.IsVisible);
                    main.Hide();
                    foreach (var panel in new FrameworkElement[]
                    {
                        new SettingsView(), new HistoryView(), new ProfileView(),
                        new RemindersView(), new InteractionMenu(), new ChatView(),
                    })
                    {
                        panel.Measure(new Size(800, 800));
                        panel.Arrange(new Rect(0, 0, 800, 800));
                        panel.UpdateLayout();
                    }
                    var chatView = new ChatView();
                    var messageTemplate = (DataTemplate)chatView.Resources["Tks.MessageTemplate"];
                    foreach (var role in new[] { "user", "assistant" })
                    {
                        var bubble = (FrameworkElement)messageTemplate.LoadContent();
                        bubble.DataContext = new TKSDesktop.ViewModels.MessageItemViewModel(
                            new TKSDesktop.Core.Services.ChatMessageView(
                                "layout-" + role, role, "text", "Layout probe", "sent", 0,
                                null, null, null, false, false, []));
                        bubble.Measure(new Size(600, 800));
                        bubble.Arrange(new Rect(0, 0, 600, 800));
                        bubble.UpdateLayout();
                    }
                    app.Shutdown();
                }
                finally
                {
                    provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                app.Shutdown();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
