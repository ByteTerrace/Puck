namespace Puck.Abstractions.Machines;

/// <summary>
/// A cable-linked group of <see cref="IScreenMachine"/>s advanced as ONE deterministically interleaved unit — a LINK is
/// an object, not a relationship between two peers. The engine that owns the machines creates it (see
/// <see cref="IMachineLinkingEngine.TryLink"/>); a host steps the link once per frame INSTEAD of stepping its members
/// individually, so the engine's own deterministic interleave — not the host's frame order — decides who runs when.
/// Disposing the link severs the cable; its members then step independently again (an unfinished external-clock transfer
/// stays pending, as on unplugged hardware).
/// </summary>
public interface IMachineLink : IDisposable {
    /// <summary>Gets the linked machines, in cable order — the same instances handed to
    /// <see cref="IMachineLinkingEngine.TryLink"/>. A host that steps a link skips these from its per-machine step loop.</summary>
    IReadOnlyList<IScreenMachine> Machines { get; }

    /// <summary>Gets the number of serial bytes exchanged across the cable since the link formed — the pipe-assertable
    /// liveness signal a host echoes (a live link's count climbs; a dormant one never advances). Nothing depends on the
    /// bytes themselves.</summary>
    long CompletedTransfers { get; }

    /// <summary>Advances every linked machine forward by one shared step budget, interleaved deterministically by the
    /// engine's own pair-stepper. Each member is driven with its own controller image (by cable order), so a
    /// multiplayer link still routes per-seat input.</summary>
    /// <param name="deltaTicks">The shared fixed-step tick budget for this frame.</param>
    /// <param name="inputs">Each member's controller image, in cable order (length equals <see cref="Machines"/>).</param>
    void Step(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs);
}
