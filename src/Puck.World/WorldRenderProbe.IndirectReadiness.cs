using System.Diagnostics.CodeAnalysis;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World;

public sealed partial class WorldRenderProbe : IWorldIndirectReadiness {
    /// <inheritdoc/>
    public bool TryBegin([NotNullWhen(true)] out IWorldIndirectWait? wait, out string reason) {
        wait = null;
        if ((Root is null) || !m_indirectResidencies.Any(predicate: entry => (entry.Value && (entry.Key.IndirectTier != SdfIndirectTier.Off)))) {
            reason = "no active indirect residency — select medium or high and wait for the renderer";
            return false;
        }
        var armed = m_indirectResidencies.Where(predicate: entry => entry.Value).Select(selector: entry =>
            (Residency: entry.Key, Cache: entry.Key.Tables?.Indirect, Since: (entry.Key.Tables?.Indirect?.Invalidations ?? 0L))).ToArray();

        wait = new WorldIndirectWait(() => (Root?.FramesProduced ?? 0L), CaptureIndirectReady, () => CannotFinish(armed: armed));
        reason = string.Empty;
        return true;
    }

    // The first active cache, armed with the wait, whose solve cannot finish; a cache replaced since arming is judged
    // from its own start.
    private static string? CannotFinish((SdfWorldResidency Residency, SdfIndirectCache? Cache, long Since)[] armed) {
        foreach (var (residency, cache, since) in armed) {
            if ((residency.IndirectTier == SdfIndirectTier.Off) || residency.IsIndirectReady || (residency.Tables?.Indirect is not { } current)) { continue; }
            if (current.CannotFinishReason(since: (ReferenceEquals(objA: current, objB: cache) ? since : 0L)) is { } reason) {
                return $"residency={residency.Name} {reason}";
            }
        }
        return null;
    }

    private IReadOnlyList<WorldIndirectReadyIdentity>? CaptureIndirectReady() {
        // Check without copying mutable cache snapshots or inferring GPU classifications. The residency owns the
        // current-source comparison and existing completed readback fence for this exact shared publication.
        var active = false;

        foreach (var entry in m_indirectResidencies) {
            if (!entry.Value || (entry.Key.IndirectTier == SdfIndirectTier.Off)) { continue; }
            active = true;
            if (!entry.Key.IsIndirectReady) { return null; }
        }
        if (!active) { return null; }
        var identities = new List<WorldIndirectReadyIdentity>();

        foreach (var entry in m_indirectResidencies) {
            if (!entry.Value || (entry.Key.IndirectTier == SdfIndirectTier.Off)) { continue; }
            var cache = entry.Key.Tables!.Indirect!;

            identities.Add(item: new WorldIndirectReadyIdentity(entry.Key.Name, cache.History.Allocation, cache.Epoch,
                cache.PublishedGeneration, cache.PublishedStamp, cache.PublishedLightingSource!.Sequence));
        }
        return identities;
    }
}
