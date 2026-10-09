using System.Diagnostics.CodeAnalysis;

namespace Puck.World;

/// <summary>Arms a wait for the current shared indirect caches on the render/console owner thread. This is cache
/// convergence, not admission of every view's independent receiver certificate.</summary>
public interface IWorldIndirectReadiness {
    /// <summary>Captures the current produced-frame boundary and starts a wait, or refuses when no active cache exists.</summary>
    /// <param name="wait">The owner-thread wait, or null on refusal.</param>
    /// <param name="reason">The named refusal, or empty.</param>
    /// <returns>Whether a rendered indirect wait was armed.</returns>
    bool TryBegin([NotNullWhen(true)] out IWorldIndirectWait? wait, out string reason);
}
/// <summary>A single armed indirect wait. Polling runs on the render/console owner thread; its completion retains the
/// exact cache identities that satisfied the fence rather than reading later live state.</summary>
public interface IWorldIndirectWait {
    /// <summary>Gets whether a newer frame prepared the desired source and every active shared cache completed its fence.</summary>
    bool IsSettled { get; }
    /// <summary>Gets the immutable completion identities, empty before settlement.</summary>
    IReadOnlyList<WorldIndirectReadyIdentity> Identities { get; }
    /// <summary>Gets why an unsettled wait can never settle: an active cache's solve cannot finish. A wait never holds
    /// for such a solve; it releases at once with this reason.</summary>
    string? Refusal { get; }
}
/// <summary>The actual cache publication that completed a warm-up.</summary>
/// <param name="Residency">The residency's live name.</param>
/// <param name="Allocation">The exact cache allocation.</param>
/// <param name="Epoch">Its geometry epoch.</param>
/// <param name="Generation">Its visible lighting bank.</param>
/// <param name="Stamp">Its complete-sweep publication stamp.</param>
/// <param name="Source">Its published immutable lighting source sequence.</param>
public sealed record WorldIndirectReadyIdentity(string Residency, long Allocation, uint Epoch, int Generation, uint Stamp, ulong Source);
/// <summary>Requires a produced frame after arming before capturing an actual current-source fence. Completion retains
/// a copy of immutable identities once; later source changes cannot relabel it.</summary>
/// <param name="framesProduced">The live render root's produced-frame count, read on its owner thread.</param>
/// <param name="capture">Returns every active cache's actual identity only when all current-source fences hold, or null.</param>
/// <param name="refusal">Names why the current solve cannot finish (a scene that keeps withdrawing its transport, or
/// remaining work beyond the frame bound), or returns null; absent never refuses.</param>
public sealed class WorldIndirectWait(Func<long> framesProduced, Func<IReadOnlyList<WorldIndirectReadyIdentity>?> capture, Func<string?>? refusal = null) : IWorldIndirectWait {
    private readonly long m_armedFrame = framesProduced();

    private IReadOnlyList<WorldIndirectReadyIdentity>? m_identities;

    /// <inheritdoc/>
    public IReadOnlyList<WorldIndirectReadyIdentity> Identities => (m_identities ?? []);
    /// <inheritdoc/>
    public string? Refusal => ((m_identities is null) ? refusal?.Invoke() : null);
    /// <inheritdoc/>
    public bool IsSettled {
        get {
            if (m_identities is not null) { return true; }
            if ((framesProduced() <= m_armedFrame) || (capture() is not { Count: > 0 } identities)) { return false; }
            m_identities = Array.AsReadOnly(array: identities.ToArray());
            return true;
        }
    }
}
