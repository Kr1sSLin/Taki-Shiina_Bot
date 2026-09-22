using System.Runtime.InteropServices;
using System.Windows;

namespace TKSDesktop;

/// <summary>
/// 进程入口（PRD NFR-W-15 / FR-W-TEST-1 / FR-W-DSK-9 / §14.3 AUMID）。
///
/// ⚠️ 刻意**不**使用 WPF 的自动生成 `Main`（`EnableDefaultApplicationDefinition=false`）：
///    自检模式必须在**无图形界面**环境下运行（CI / 无人值守），因此由本类自行决定
///    是否创建 <see cref="Application"/> 与窗口。
/// </summary>
public static class Program
{
    /// <summary>AUMID 声明（§14.3 第 1 步）。Toast 能弹出的前提之一。</summary>
    private const string Aumid = Contracts.ProtocolConstants.Aumid;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            try
            {
                var paths = new App.AppPaths();
                using var log = new App.JsonLineFileLoggerProvider(paths.LogsDir);
                Microsoft.Extensions.Logging.LoggerExtensions.LogCritical(
                    log.CreateLogger("App.Startup"), ex, "Application startup or UI failed");
            }
            catch (Exception)
            {
                // Reporting must remain available even when the log directory is unwritable.
            }

            MessageBox.Show(App.I18n.T("startup.failed"), App.AppVersion.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = App.CliOptions.Parse(args);

        if (options.ShowVersion)
        {
            Console.Out.WriteLine(App.AppVersion.Informational);
            return 0;
        }

        // --selftest：无图形界面路径（不创建 Application / Window）。
        if (options.SelfTest)
        {
            return Diagnostics.SelfTestHost.RunAsync(options).GetAwaiter().GetResult();
        }

        // ⚠️ 必须在创建任何窗口之前声明 AUMID，否则 Toast 无法与快捷方式匹配（§14.3）。
        SetAumid();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/TKSDesktop;component/Resources/Themes/Controls.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/TKSDesktop;component/Resources/Themes/DoodleBackground.xaml"),
        });
        return App.AppBootstrap.Run(app, options);
    }

    private static void SetAumid()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(Aumid);
        }
        catch (Exception)
        {
            // AUMID 声明失败不阻断启动：通知层会按 EDGE-W-10 降级为托盘气泡。
        }
    }
}
