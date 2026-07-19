namespace Puck.Abstractions.Machines;

/// <summary>
/// Optional capability on an <see cref="IScreenMachineEngine"/> whose machines can be cable-linked — the factory for an
/// <see cref="IMachineLink"/>. Every member must be a machine THIS engine built (a link crosses no engine boundary); an
/// engine that cannot link the supplied set reports why through a reason string rather than throwing, the same
/// reject-by-return-value convention <see cref="IReconfigurableMachine.TryReconfigure"/> uses. The world layer never
/// names a link protocol: it hands the engine the machines and takes back an object it steps.
/// </summary>
public interface IMachineLinkingEngine {
    /// <summary>Attempts to cable-link two or more machines this engine owns into one deterministically stepped unit.
    /// Returns <see langword="false"/> with a reason when the set cannot be linked (fewer than two machines, a member
    /// this engine did not build, a member with no serial hardware, or a member already linked).</summary>
    /// <param name="machines">The machines to link, in cable order (two or more).</param>
    /// <param name="link">The created link on success, or <see langword="null"/> on failure.</param>
    /// <param name="reason">The failure reason on <see langword="false"/>, or an empty string on success.</param>
    /// <returns>Whether the link was created.</returns>
    bool TryLink(IReadOnlyList<IScreenMachine> machines, out IMachineLink? link, out string reason);
}
