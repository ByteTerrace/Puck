namespace Puck.Abstractions.Machines;

/// <summary>
/// Optional capability on an <see cref="IScreenMachine"/> whose loaded content carries its own deterministic tempo —
/// the diegetic-instrument counterpart to <see cref="IAudioMachine"/>'s audio-output capability, following the same
/// optional-capability precedent <see cref="IFeedbackMachine"/>/<see cref="IReconfigurableMachine"/>/
/// <see cref="ITimeTravelMachine"/> already set. A host that recognizes this on an engaged machine may fold
/// <see cref="TicksPerBeat"/> into an ADDITIONAL beat-boundary signal alongside a world's own compiled music clock —
/// never a replacement for it.
/// </summary>
public interface IInstrumentClockSource {
    /// <summary>Gets the engine-tick length of one authored beat, in the SAME fixed-tick domain
    /// <c>Puck.Audio.Simulation.MusicClock.TicksPerBeat</c> uses (ticks at the engine's canonical fixed-tick rate —
    /// see <c>Puck.World.FixedTickConversion.TicksPerSecond</c> — never <c>RateHz</c>-relative), derived once from
    /// the loaded content's own authored tempo. Zero when no content is loaded — an unassigned machine has no tempo
    /// to report.</summary>
    long TicksPerBeat { get; }
}
