using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TKSDesktop.Tests.Gate;

/// <summary>
/// Shared plumbing for the mechanical acceptance gates (PRD section 16.1 / 16.3).
///
/// All gate tests are pure source/reflection scans and never mutate the product tree.
/// The product tree is located by walking up from the test output directory until a
/// directory containing both "TKSDesktop/TKSDesktop.csproj" and "Directory.Build.props"
/// is found. An explicit override is honoured via the environment variable TKS_GATE_ROOT
/// (used when the suite is executed from an isolated build harness).
/// </summary>
internal static class GateRepo
{
    public const string GateRootEnvVar = "TKS_GATE_ROOT";

    public static string Root { get; } = ResolveRoot();

    public static string ProductDir => Path.Combine(Root, "TKSDesktop");

    public static string TestsDir => Path.Combine(Root, "Tests", "TKSDesktop.Tests");

    public static int HanLiteralCacheCount;

    private static string ResolveRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable(GateRootEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && IsRoot(fromEnv))
        {
            return Path.GetFullPath(fromEnv.Trim());
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (IsRoot(dir.FullName))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "GateRepo: could not locate TKS_Bot_Windows (looked for TKSDesktop/TKSDesktop.csproj + Directory.Build.props). " +
            "Set the environment variable " + GateRootEnvVar + " to the TKS_Bot_Windows directory.");
    }

    private static bool IsRoot(string candidate)
        => File.Exists(Path.Combine(candidate, "TKSDesktop", "TKSDesktop.csproj"))
           && File.Exists(Path.Combine(candidate, "Directory.Build.props"));

    /// <summary>Absolute path built from a TKS_Bot_Windows relative path.</summary>
    public static string Path_(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>Product .cs files (bin/obj excluded), optional sub directory filter.</summary>
    public static IReadOnlyList<string> ProductCsFiles(params string[] subDirs)
    {
        var roots = subDirs.Length == 0
            ? new[] { ProductDir }
            : subDirs.Select(s => Path.Combine(ProductDir, s)).ToArray();

        var result = new List<string>();
        foreach (var r in roots)
        {
            if (!Directory.Exists(r))
            {
                continue;
            }

            result.AddRange(Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildArtifact(f))
                .OrderBy(f => f, StringComparer.Ordinal));
        }

        return result;
    }

    /// <summary>Product .xaml files (bin/obj excluded), optional sub directory filter.</summary>
    public static IReadOnlyList<string> ProductXamlFiles(params string[] subDirs)
    {
        var roots = subDirs.Length == 0
            ? new[] { ProductDir }
            : subDirs.Select(s => Path.Combine(ProductDir, s)).ToArray();

        var result = new List<string>();
        foreach (var r in roots)
        {
            if (!Directory.Exists(r))
            {
                continue;
            }

            result.AddRange(Directory.EnumerateFiles(r, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !IsBuildArtifact(f))
                .OrderBy(f => f, StringComparer.Ordinal));
        }

        return result;
    }

    /// <summary>All .csproj / .props files below TKS_Bot_Windows (bin/obj excluded).</summary>
    public static IReadOnlyList<string> BuildFiles()
        => Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f)
                        && (f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    public static bool IsBuildArtifact(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.Ordinal)
            || p.Contains("/obj/", StringComparison.Ordinal);
    }

    /// <summary>Display path relative to the repository root (stable in failure messages).</summary>
    public static string Rel(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(Root.Length).TrimStart('\\', '/').Replace('\\', '/')
            : full;
    }

    public static string Read(string absolutePath) => File.ReadAllText(absolutePath, Encoding.UTF8);

    public static IReadOnlyList<string> ReadLines(string absolutePath)
        => File.ReadAllLines(absolutePath, Encoding.UTF8);

    /// <summary>
    /// Removes comments AND the *contents* of string/char literals, so that identifier lookups
    /// ("does the code contain an identifier named X") cannot be satisfied by documentation,
    /// log text or other literals. Line numbering is preserved (every removed run leaves a space).
    /// </summary>
    public static string CodeOnly(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var n = text.Length;

        while (i < n)
        {
            var c = text[i];

            // Line comment
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            // Block comment
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < n)
                {
                    sb.Append("  ");
                    i += 2;
                }

                continue;
            }

            // Verbatim string @"..." (doubled quotes are escapes)
            if (c == '@' && i + 1 < n && text[i + 1] == '"')
            {
                sb.Append("  ");
                i += 2;
                while (i < n)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"')
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }

                        sb.Append(' ');
                        i++;
                        break;
                    }

                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            // Raw string (""" ... """) - treated as opaque literal.
            if (c == '"' && i + 2 < n && text[i + 1] == '"' && text[i + 2] == '"')
            {
                sb.Append("   ");
                i += 3;
                while (i < n && !(i + 2 < n && text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < n)
                {
                    sb.Append("   ");
                    i += 3;
                }

                continue;
            }

            // Regular string / char literal
            if (c == '"' || c == '\'')
            {
                var quote = c;
                sb.Append(' ');
                i++;
                while (i < n)
                {
                    if (text[i] == '\\')
                    {
                        sb.Append("  ");
                        i += 2;
                        continue;
                    }

                    if (text[i] == quote)
                    {
                        sb.Append(' ');
                        i++;
                        break;
                    }

                    if (text[i] == '\n')
                    {
                        // Unterminated literal: keep the newline and bail out of the literal.
                        break;
                    }

                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Removes comment lines/regions only; string literal contents are preserved.
    /// Used by the "hardcoded Chinese UI copy" scans.
    /// </summary>
    public static IReadOnlyList<(int Line, string Text)> LinesWithoutComments(string absolutePath)
    {
        var raw = ReadLines(absolutePath);
        var result = new List<(int, string)>();
        var inBlock = false;

        for (var idx = 0; idx < raw.Count; idx++)
        {
            var line = raw[idx];
            var sb = new StringBuilder(line.Length);
            var i = 0;
            var keepNewlineOnly = false;

            while (i < line.Length)
            {
                if (inBlock)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        i = line.Length;
                        break;
                    }

                    inBlock = false;
                    i = end + 2;
                    continue;
                }

                var start = line.IndexOf("/*", i, StringComparison.Ordinal);
                var lineComment = line.IndexOf("//", i, StringComparison.Ordinal);

                if (lineComment >= 0 && (start < 0 || lineComment < start))
                {
                    sb.Append(line, i, lineComment - i);
                    i = line.Length;
                    keepNewlineOnly = true;
                    break;
                }

                if (start >= 0)
                {
                    sb.Append(line, i, start - i);
                    inBlock = true;
                    i = start + 2;
                    continue;
                }

                sb.Append(line, i, line.Length - i);
                i = line.Length;
            }

            _ = keepNewlineOnly;
            result.Add((idx + 1, sb.ToString()));
        }

        return result;
    }

    public static readonly Regex Han = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    /// <summary>
    /// Heuristic: a source line whose Chinese literal is logger/diagnostic text rather than UI copy.
    /// Log text is intentionally not part of NFR-W-10 (which governs UI copy routed through I18n).
    /// Kept for single-line call sites; multi-line calls are handled by
    /// <see cref="IsNonUiDiagnosticStatement"/>.
    /// </summary>
    public static bool IsLogOrDiagnosticLine(string line)
    {
        ReadOnlySpan<string> markers =
        [
            "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical",
            "logger.", "_logger.", "ILogger", "Console.", "Debug.Write", "Trace.Write",
        ];

        foreach (var m in markers)
        {
            if (line.Contains(m, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits source into logical statements (joined across line breaks until a ';' or a brace),
    /// with comment text removed but **string literal contents preserved**.
    ///
    /// Needed because a Chinese format string usually sits on the *continuation* line of a
    /// wrapped <c>_logger.LogWarning(...)</c> call: a line-local scan cannot tell that such a
    /// literal is log text rather than UI copy. The reported line number is the statement start.
    /// </summary>
    public static IReadOnlyList<(int Line, string Text)> StatementsWithoutComments(string absolutePath)
    {
        const int MaxStatementLength = 8000;

        var result = new List<(int, string)>();
        var buffer = new StringBuilder();
        var startLine = 0;

        foreach (var (lineNumber, line) in LinesWithoutComments(absolutePath))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (buffer.Length == 0)
            {
                startLine = lineNumber;
            }

            buffer.Append(' ').Append(text);

            var endsStatement = text.Contains(';', StringComparison.Ordinal)
                                || text.EndsWith('{')
                                || text.EndsWith('}');

            if (endsStatement || buffer.Length >= MaxStatementLength)
            {
                result.Add((startLine, buffer.ToString()));
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            result.Add((startLine, buffer.ToString()));
        }

        return result;
    }

    /// <summary>
    /// True when a whole statement is log / exception / diagnostic text -- i.e. not UI copy.
    /// NFR-W-10 governs what the user reads in the interface; logger messages, thrown exception
    /// messages and self-test report text are developer/diagnostic surfaces (see also
    /// <see cref="IsChineseLiteralAllowListed"/>).
    /// </summary>
    public static bool IsNonUiDiagnosticStatement(string statement)
    {
        ReadOnlySpan<string> markers =
        [
            "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical",
            "logger.", "_logger.", "ILogger", "Console.", "Debug.Write", "Trace.Write",
            "throw new",
        ];

        foreach (var m in markers)
        {
            if (statement.Contains(m, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Product files that are allowed to carry non-UI Chinese text (contract data / diagnostics),
    /// each justified by an explicit PRD anchor. UI copy must never land here.
    /// </summary>
    public static bool IsChineseLiteralAllowListed(string absolutePath)
    {
        var rel = Rel(absolutePath);

        return rel.Equals("TKSDesktop/App/I18n.cs", StringComparison.Ordinal)
               || rel.Equals("TKSDesktop/Contracts/ProtocolConstants.cs", StringComparison.Ordinal)   // C-5 history separator (server constant)
               || rel.Equals("TKSDesktop/Contracts/LevelVisuals.cs", StringComparison.Ordinal)       // C-1 level display names (cross-end contract values)
               || rel.StartsWith("TKSDesktop/Diagnostics/", StringComparison.Ordinal);               // NFR-W-15 self-test report text
    }

    /// <summary>Extracts every double-quoted literal of a single source line (naive but adequate for scans).</summary>
    public static IReadOnlyList<string> DoubleQuotedLiterals(string line)
    {
        var result = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            var start = line.IndexOf('"', i);
            if (start < 0)
            {
                break;
            }

            var sb = new StringBuilder();
            i = start + 1;
            var closed = false;

            while (i < line.Length)
            {
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    sb.Append(line[i]).Append(line[i + 1]);
                    i += 2;
                    continue;
                }

                if (line[i] == '"')
                {
                    closed = true;
                    i++;
                    break;
                }

                sb.Append(line[i]);
                i++;
            }

            if (closed)
            {
                result.Add(sb.ToString());
            }
        }

        return result;
    }
}
