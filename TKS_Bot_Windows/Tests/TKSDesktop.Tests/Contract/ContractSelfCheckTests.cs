using Xunit;

namespace TKSDesktop.Tests.Contract;

/// <summary>
/// V-W-C2 -- contract self-check against the mock backend contract surface, at least 66 assertions.
///
/// Offline by design: the mock backend is not started. The assertions pin the exact wire surface
/// (DTO field names, protocol constants, error catalogue, level visuals and frame shapes) that
/// mock-server/server.mjs and the real backend both implement, so any drift breaks a named check.
///
/// Each group report lists every failing check with its own id, so one renamed field yields an
/// actionable failure rather than a single opaque assertion.
/// </summary>
public sealed class ContractSelfCheckTests
{
    /// <summary>V-W-C2: the assertion count must be >= 66 (PRD section 16.3).</summary>
    [Fact]
    public void Gate_VWC2_ContractSelfCheck_HasAtLeast66Assertions()
    {
        var total = ContractChecks.Total;
        var skipped = ContractChecks.Skipped;
        var executed = total - skipped;

        Assert.True(total >= 66,
            $"V-W-C2: the contract self-check must contain at least 66 assertions, found {total}.");

        Assert.True(executed >= 66,
            $"V-W-C2: at least 66 assertions must actually execute, but only {executed} run " +
            $"({skipped} skipped pending integration).");

        // Every check must be uniquely identified, otherwise failures cannot be attributed.
        var duplicates = ContractChecks.All
            .GroupBy(c => c.Group + "/" + c.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(duplicates.Length == 0,
            "V-W-C2: duplicate contract check id(s): " + string.Join(", ", duplicates));
    }

    /// <summary>Section 5.2 / 5.3 DTOs: exact [JsonPropertyName] sets, field by field.</summary>
    [Fact]
    public void Gate_VWC2_DtoFieldMapsMatchFrozenContract() => ContractEngine.AssertGroup("dto");

    /// <summary>Section 7.0 gamification DTOs (interaction / points / level / makeup card).</summary>
    [Fact]
    public void Gate_VWC2_GamificationDtoFieldMapsMatchFrozenContract() => ContractEngine.AssertGroup("dto7");

    /// <summary>Protocol constants, level visuals (C-1), error catalogue (C-6) and i18n coverage.</summary>
    [Fact]
    public void Gate_VWC2_ProtocolConstantsLevelVisualsAndErrorCatalog() => ContractEngine.AssertGroup("proto");

    /// <summary>PRD section 5.6 -- the 12 inherited backend contract traps.</summary>
    [Fact]
    public void Gate_VWC2_TwelveBackendContractTraps() => ContractEngine.AssertGroup("trap");

    /// <summary>Frame parsing plus NFR-W-12 forward compatibility (unknown type / malformed JSON).</summary>
    [Fact]
    public void Gate_VWC2_WsFrameParsingAndForwardCompatibility() => ContractEngine.AssertGroup("ws");

    /// <summary>Reports the executed assertion count so the gate result is auditable in CI logs.</summary>
    [Fact]
    public void Gate_VWC2_AssertionCountIsReportedPerGroup()
    {
        var groups = ContractChecks.All
            .GroupBy(c => c.Group, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(groups.Length >= 5,
            $"V-W-C2: expected at least 5 check groups, found {groups.Length}.");

        foreach (var group in groups)
        {
            Assert.True(group.Count() > 0, $"group '{group.Key}' is empty.");
        }

        Assert.Equal(ContractChecks.Total, groups.Sum(g => g.Count()));
    }
}
