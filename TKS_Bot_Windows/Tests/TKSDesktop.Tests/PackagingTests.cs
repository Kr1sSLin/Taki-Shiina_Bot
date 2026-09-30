using System.Diagnostics;
using TKSDesktop.App;
using Xunit;

namespace TKSDesktop.Tests;

public sealed class PackagingTests
{
    [Fact]
    public void PortableMarker_ContainsConfigDatabaseCredentialsAndLogsUnderPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "tks-portable-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "portable.flag"), "portable");
            var paths = new AppPaths(root, new Dictionary<string, string?>());
            Assert.True(paths.IsPortable);
            paths.EnsureDirectories();
            foreach (var path in new[] { paths.SettingsFile, paths.CredentialsFile, paths.DatabaseFile, paths.LogsDir, paths.AttachmentsDir })
                Assert.StartsWith(Path.Combine(root, "data") + Path.DirectorySeparatorChar, path);
            Assert.Null(paths.ValidatePortableWritable());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstallerPreflight_RejectsDriveRootPortableAndUnrelatedNonemptyDirectory()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "build.ps1"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var script = Path.Combine(repo.FullName, "installer", "Test-InstallTarget.ps1");
        var root = Path.Combine(Path.GetTempPath(), "tks-installer-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.NotEqual(0, RunPreflight(script, Path.GetPathRoot(root)!));
            File.WriteAllText(Path.Combine(root, "personal.txt"), "must survive");
            Assert.NotEqual(0, RunPreflight(script, root));
            Directory.CreateDirectory(Path.Combine(root, "data"));
            Assert.NotEqual(0, RunPreflight(script, root));
            Assert.Equal("must survive", File.ReadAllText(Path.Combine(root, "personal.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static int RunPreflight(string script, string target)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-InstallDirectory", target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try { Assert.True(process.WaitForExit(15000)); return process.ExitCode; }
        finally { if (!process.HasExited) process.Kill(); }
    }
}
