namespace Puck.Abstractions.Machines;

/// <summary>
/// Optional live-reconfiguration capability for an <see cref="IScreenMachine"/> — retargeting a RUNNING machine across
/// the engine's own options vocabulary with no reboot and no lost progress, mirroring <see cref="IAudioMachine"/>'s and
/// <see cref="IQueuedScreenMachine"/>'s optional-capability precedent. The world never names a concrete hardware model:
/// it forwards the same opaque options string the engine parsed at <see cref="IScreenMachineEngine.Create"/>, so the
/// dmg↔cgb↔agb device swap is nothing but passing <c>cgb</c> to a machine whose engine's vocabulary contains it, and a
/// future engine gets region/BIOS/overclock retargeting for free with zero world changes.
/// <para>
/// An engine advertises which reconfigurations are honest for a given content by the reason text it
/// echoes — a swap with no live-detection recipe reports that fact rather than faking a hardware retarget. Rejecting an
/// unknown options string returns <see langword="false"/> with a reason; it never throws. A machine that runs an
/// internal worker marshals the reconfigure onto it, so the swap lands between steps and never races the running core.
/// </para>
/// </summary>
public interface IReconfigurableMachine {
    /// <summary>Gets the machine's CURRENT options string — the engine-vocabulary spelling of what it is running now
    /// (e.g. <c>dmg</c> after a live swap from <c>cgb</c>), the value <c>world.save</c> folds back into the screen row's
    /// options and the <c>screen.options</c> query echoes.</summary>
    string Options { get; }

    /// <summary>Retargets the running machine across the engine's options vocabulary, live. A <see langword="null"/> or
    /// empty <paramref name="options"/> selects the engine's defaults. Returns <see langword="false"/> with a reason for
    /// an options string the engine rejects; on success <paramref name="reason"/> carries any advisory the host echoes
    /// (an honest note such as "no live-detection recipe — the running game keeps its boot code path").</summary>
    /// <param name="options">The engine-specific options string to retarget to, or <see langword="null"/> for defaults.</param>
    /// <param name="reason">The rejection reason on failure, or an advisory/empty string on success.</param>
    /// <returns>Whether the reconfiguration was accepted.</returns>
    bool TryReconfigure(string? options, out string reason);
}
