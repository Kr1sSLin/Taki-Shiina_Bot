namespace TKSDesktop.Tests.Gate;

/// <summary>
/// Records gate assertions that cannot be enforced yet because the owning artifact has not landed
/// (the data layer, chat core, platform layer and Views are implemented in parallel).
///
/// A pending entry is NOT a pass: it is a recorded, printed obligation. When the artifact appears,
/// the corresponding gate becomes strict automatically -- the precondition test flips from
/// "artifact absent -> record pending" to "artifact present -> assert hard", so a gate can never
/// stay soft once its subject exists.
///
/// The list is printed by <see cref="GatePendingReportTests"/> so the hand-off report can state
/// exactly which assertions are still unenforced.
/// </summary>
internal static class GatePending
{
    private static readonly List<string> Recorded = [];
    private static readonly object Gate = new();

    public static IReadOnlyList<string> Entries
    {
        get
        {
            lock (Gate)
            {
                return Recorded.ToArray();
            }
        }
    }

    /// <summary>
    /// Returns true when the gate must be enforced (artifact present). When the artifact is absent
    /// the obligation is recorded and false is returned, so the caller can return early.
    /// </summary>
    public static bool Enforce(bool artifactPresent, string gateId, string obligation)
    {
        if (artifactPresent)
        {
            return true;
        }

        lock (Gate)
        {
            var entry = $"{gateId}: {obligation}";
            if (!Recorded.Contains(entry, StringComparer.Ordinal))
            {
                Recorded.Add(entry);
            }
        }

        return false;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Recorded.Clear();
        }
    }
}
