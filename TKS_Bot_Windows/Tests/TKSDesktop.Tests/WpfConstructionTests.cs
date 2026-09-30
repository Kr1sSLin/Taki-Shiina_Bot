using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TKSDesktop.ViewModels;
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
                M5VisualTests.CheckResources(app, root);
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
                    var chatPane = (ChatView)main.FindName("ChatPane");
                    var composer = (System.Windows.Controls.Border)chatPane.FindName("ComposerCapsule");
                    Assert.True(composer.MinHeight >= 72);
                    Assert.NotNull(chatPane.FindName("EmojiButton"));
                    Assert.NotNull(chatPane.FindName("AddImageButton"));
                    Assert.NotNull(chatPane.FindName("SendButton"));
                    // Wide windows must keep semicircular ends, not scale into an ellipse.
                    foreach (var width in new[] { 520d, 900d, 1400d })
                    {
                        main.Width = width;
                        main.UpdateLayout();
                        Assert.Equal(composer.ActualHeight / 2, composer.CornerRadius.TopLeft, 1);
                        foreach (var name in new[] { "EmojiButton", "AddImageButton", "SendButton" })
                        {
                            var button = (FrameworkElement)chatPane.FindName(name);
                            var bounds = button.TransformToAncestor(composer).TransformBounds(new Rect(button.RenderSize));
                            Assert.Equal(button.ActualWidth, button.ActualHeight, 1);
                            Assert.InRange(button.ActualHeight / composer.ActualHeight, 0.60, 0.70);
                            Assert.True(bounds.Top >= 10 && bounds.Bottom <= composer.ActualHeight - 10);
                        }
                        if (Environment.GetEnvironmentVariable("TKS_COMPOSER_PREVIEW_DIR") is { Length: > 0 } previewDir)
                        {
                            Directory.CreateDirectory(previewDir);
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                (int)Math.Ceiling(composer.ActualWidth), (int)Math.Ceiling(composer.ActualHeight),
                                96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                            bitmap.Render(composer);
                            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(previewDir, $"composer-{width:0}.png"));
                            png.Save(file);
                        }
                    }
                    var filterFactory = typeof(ChatView).GetMethod(
                        "BuildImageDialogFilter",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    var filter = Assert.IsType<string>(filterFactory!.Invoke(null, null));
                    var filterParts = filter.Split('|');
                    Assert.Equal(2, filterParts.Length);
                    Assert.Contains("*.jpg", filterParts[1], StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("*.jpeg", filterParts[1], StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("*.png", filterParts[1], StringComparison.OrdinalIgnoreCase);
                    chatPane.OpenSearch();
                    main.UpdateLayout();
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)chatPane.FindName("SearchPanel")).Visibility);
                    Assert.True(provider.GetRequiredService<ChatViewModel>().IsSearchOpen);
                    provider.GetRequiredService<ChatViewModel>().CloseSearchCommand.Execute(null);
                    main.Width = 520;
                    main.Height = 640;
                    main.UpdateLayout();
                    Assert.True(((FrameworkElement)main.Content).ActualWidth > 0);
                    var actionStrip = (System.Windows.Controls.WrapPanel)main.FindName("ActionStrip");
                    foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
                    {
                        ((FrameworkElement)main.Content).LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                        main.Width = 520 * scale;
                        main.Height = 640 * scale;
                        main.UpdateLayout();
                        var settings = new SettingsView { DataContext = provider.GetRequiredService<SettingsViewModel>() };
                        settings.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                        settings.Measure(new Size(720 * scale, 620 * scale));
                        settings.Arrange(new Rect(0, 0, 720 * scale, 620 * scale));
                        settings.UpdateLayout();
                        // Exercise the real window factory without authenticating against a server.
                        typeof(MainWindow).GetMethod("ShowSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(main, null);
                        var settingsHost = Assert.Single(main.OwnedWindows.Cast<Window>(), window => window.Content is SettingsView);
                        settingsHost.UpdateLayout();
                        Assert.True(settingsHost.IsVisible);
                        settingsHost.Close();
                        var launcher = (System.Windows.Controls.Button)main.FindName("InteractionLauncher");
                        var point = launcher.TranslatePoint(new Point(launcher.ActualWidth / 2, launcher.ActualHeight / 2), main);
                        var hit = main.InputHitTest(point) as DependencyObject;
                        Assert.NotNull(hit);
                        Assert.True(ReferenceEquals(hit, launcher) || launcher.IsAncestorOf(hit));
                        Assert.NotNull(launcher.Command);
                        Assert.True(launcher.Command.CanExecute(null));
                        launcher.Command.Execute(null);
                        main.UpdateLayout();
                        var overlay = (FrameworkElement)main.FindName("InteractionOverlay");
                        Assert.Equal(Visibility.Visible, overlay.Visibility);
                        provider.GetRequiredService<InteractionMenuViewModel>().CloseCommand.Execute(null);
                        Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                        foreach (FrameworkElement button in actionStrip.Children)
                        {
                            var bounds = button.TransformToAncestor(actionStrip).TransformBounds(new Rect(button.RenderSize));
                            Assert.True(bounds.Right <= actionStrip.ActualWidth + 1);
                            Assert.True(bounds.Bottom <= actionStrip.ActualHeight + 1);
                        }
                    }
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
                    var historyList = (System.Windows.Controls.ItemsControl)chatView.FindName("MessageItems");
                    historyList.ItemsSource = Enumerable.Range(0, 2000).Select(index =>
                        new MessageItemViewModel(new TKSDesktop.Core.Services.ChatMessageView(
                            "virtual-" + index, "assistant", "text", "History row " + index, "sent", index,
                            null, null, null, false, false, []))).ToArray();
                    chatView.Measure(new Size(600, 400));
                    chatView.Arrange(new Rect(0, 0, 600, 400));
                    chatView.UpdateLayout();
                    var realized = Enumerable.Range(0, 2000).Count(index => historyList.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                    Assert.InRange(realized, 1, 100);
                    var pendingVisuals = new Queue<DependencyObject>();
                    pendingVisuals.Enqueue(historyList);
                    System.Windows.Controls.VirtualizingPanel? virtualPanel = null;
                    while (pendingVisuals.TryDequeue(out var visual))
                    {
                        if (visual is System.Windows.Controls.VirtualizingPanel found) { virtualPanel = found; break; }
                        for (var child = 0; child < System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual); child++)
                            pendingVisuals.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(visual, child));
                    }
                    Assert.NotNull(virtualPanel);
                    virtualPanel.BringIndexIntoViewPublic(1999);
                    chatView.UpdateLayout();
                    Assert.NotNull(historyList.ItemContainerGenerator.ContainerFromIndex(1999));
                    // Recycling cleanup is deferred; bounded realization matters, not immediate eviction of row 0.
                    Assert.InRange(Enumerable.Range(0, 2000).Count(index => historyList.ItemContainerGenerator.ContainerFromIndex(index) is not null), 1, 100);
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
