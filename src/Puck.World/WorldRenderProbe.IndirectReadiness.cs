using System.Diagnostics.CodeAnalysis;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World;

public sealed partial class WorldRenderProbe : IWorldIndirectReadiness {
    /// <inheritdoc/>
    public long FrameBound => SdfIndirectCache.FinishFrameBound;

    /// <inheritdoc/>
    public bool TryBegin([NotNullWhen(true)] out IWorldIndirectWait? wait, out string reason) {
        wait = null;
        if (Root is null) {
            reason = "no renderer — indirect lighting needs a rendered host";
            return false;
        }
        // Each cache is judged from the invalidations it had when the wait first saw it: at arming, or when the renderer
        // activates it later (a tier just selected).
        var since = new Dictionary<SdfIndirectCache, long>(comparer: ReferenceEqualityComparer.Instance);

        foreach (var entry in m_indirectResidencies) {
            if (entry.Value && (entry.Key.Tables?.Indirect is { } cache)) { since[cache] = cache.Invalidations; }
        }
        var armed = Root.FramesProduced;

        wait = new WorldIndirectWait(() => (Root?.FramesProduced ?? 0L), CaptureIndirectReady, () => CannotFinish(armed: armed, since: since));
        reason = string.Empty;
        return true;
    }

    // The produced frames a wait gives the renderer to activate a cache (a tier selected just before arming) before it
    // refuses as having none.
    private const long IndirectActivationFrames = 2;

    // Why the wait can never settle: no cache active once the renderer had frames to activate one, or the first active
    // cache whose solve cannot finish since the wait first saw it.
    private string? CannotFinish(Dictionary<SdfIndirectCache, long> since, long armed) {
        if (!m_indirectResidencies.Any(predicate: entry => (entry.Value && (entry.Key.IndirectTier != SdfIndirectTier.Off))) &&
            (((Root?.FramesProduced ?? 0L) - armed) > IndirectActivationFrames)) {
            return "no active indirect residency — select medium or high";
        }
        foreach (var entry in m_indirectResidencies) {
            var residency = entry.Key;

            if (!entry.Value || (residency.IndirectTier == SdfIndirectTier.Off) || residency.IsIndirectReady || (residency.Tables?.Indirect is not { } current)) { continue; }
            if (!since.TryGetValue(key: current, value: out var first)) {
                first = current.Invalidations;
                since[current] = first;
            }
            if (current.CannotFinishReason(since: first) is { } reason) {
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
