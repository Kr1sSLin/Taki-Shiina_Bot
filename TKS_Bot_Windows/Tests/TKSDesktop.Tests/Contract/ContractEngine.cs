using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using TKSDesktop.Contracts;
using Xunit;

namespace TKSDesktop.Tests.Contract;

/// <summary>
/// One named, independently reported contract assertion.
/// </summary>
internal sealed record ContractCheck(string Group, string Id, Action Body, string? SkipReason = null);

/// <summary>
/// Runs contract checks and reports every failure with its own id, so a single drifted field
/// produces a named, actionable failure instead of one opaque assertion message.
/// </summary>
internal static class ContractEngine
{
    /// <summary>Runs a set of checks; returns how many executed plus the failure details.</summary>
    public static (int Run, int Skipped, IReadOnlyList<string> Failures) Run(IEnumerable<ContractCheck> checks)
    {
        var run = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var check in checks)
        {
            if (check.SkipReason is not null)
            {
                skipped++;
                continue;
            }

            run++;
            try
            {
                check.Body();
            }
            catch (Exception ex)
            {
                failures.Add($"[{check.Group}/{check.Id}] {ex.GetType().Name}: {ex.Message}");
            }
        }

        return (run, skipped, failures);
    }

    /// <summary>Asserts one group of checks and reports the executed count.</summary>
    public static void AssertGroup(string group)
    {
        var checks = ContractChecks.All.Where(c => c.Group == group).ToArray();
        Assert.True(checks.Length > 0, $"no contract checks registered for group '{group}'.");

        var (run, skipped, failures) = Run(checks);
        Assert.True(run > 0, $"contract group '{group}' contains only skipped checks ({skipped} skipped).");
        Assert.True(failures.Count == 0,
            $"{failures.Count} contract assertion(s) failed in group '{group}':" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Asserts the exact [JsonPropertyName] set of one DTO: no missing field, no unexpected field,
    /// no duplicate mapping, and (by default) no public read/write property left unmapped.
    /// </summary>
    public static void AssertJsonMap(Type type, string[] expected, bool requireEveryPublicPropertyMapped = true)
    {
        var props = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        var actual = props
            .Select(p => (Property: p, Attr: p.GetCustomAttribute<JsonPropertyNameAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => x.Attr!.Name)
            .ToArray();

        Assert.True(actual.Length > 0, $"{type.Name}: no [JsonPropertyName] found at all.");

        var duplicates = actual.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToArray();
        Assert.True(duplicates.Length == 0,
            $"{type.Name}: duplicate [JsonPropertyName] value(s): {string.Join(", ", duplicates)}.");

        var missing = expected.Where(e => !actual.Contains(e, StringComparer.Ordinal)).ToArray();
        Assert.True(missing.Length == 0,
            $"{type.Name}: missing mapped field(s): {string.Join(", ", missing)}.");

        var extra = actual.Where(a => !expected.Contains(a, StringComparer.Ordinal)).ToArray();
        Assert.True(extra.Length == 0,
            $"{type.Name}: unexpected mapped field(s): {string.Join(", ", extra)}.");

        Assert.Equal(expected.Length, actual.Length);

        if (!requireEveryPublicPropertyMapped)
        {
            return;
        }

        var unmapped = props
            .Where(p => p.GetMethod is not null && p.GetMethod.IsPublic
                        && p.SetMethod is not null && p.SetMethod.IsPublic
                        && p.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
            .Select(p => p.Name)
            .ToArray();

        Assert.True(unmapped.Length == 0,
            $"{type.Name}: public read/write propert(ies) without [JsonPropertyName]: {string.Join(", ", unmapped)}.");
    }

    /// <summary>Asserts a single property maps to an exact JSON name.</summary>
    public static void AssertMaps(Type type, string propertyName, string jsonName)
    {
        var prop = type.GetProperty(propertyName);
        Assert.True(prop is not null, $"{type.Name}.{propertyName} does not exist.");
        var attr = prop!.GetCustomAttribute<JsonPropertyNameAttribute>();
        Assert.True(attr is not null, $"{type.Name}.{propertyName} has no [JsonPropertyName].");
        Assert.Equal(jsonName, attr!.Name);
    }

    /// <summary>Locates a product type by full name without a compile-time dependency.</summary>
    public static Type? FindType(string fullName)
        => typeof(ProtocolConstants).Assembly.GetType(fullName, throwOnError: false);
}
