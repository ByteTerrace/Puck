namespace Puck.Abstractions.Machines;

/// <summary>
/// A factory for one kind of <see cref="IScreenMachine"/> — the registered engine a host resolves a declared or
/// inserted machine against by its <see cref="Id"/>. Each engine owns parsing its own options vocabulary (an SM83 brick
/// reads a model + speed pin; another engine reads a region or a BIOS variant), so the host stays engine-agnostic: it
/// forwards the raw options string and the content bytes and takes back a machine. Engines are collected from the
/// composition root; the host keeps the registry, never the concrete engine type.
/// </summary>
public interface IScreenMachineEngine {
    /// <summary>Gets the engine's stable identifier — a short kebab-case token (e.g. <c>gaming-brick</c>) a run document
    /// or an insert verb names, and the key the host registry looks the engine up by.</summary>
    string Id { get; }

    /// <summary>Creates a machine from content and an options string this engine owns the vocabulary of. A null
    /// <paramref name="contentBytes"/> leaves the machine empty (awaiting <see cref="IScreenMachine.LoadContent"/>); a
    /// null or empty <paramref name="options"/> selects the engine's defaults.</summary>
    /// <param name="options">The engine-specific options string (e.g. <c>agb dmgspeed</c>), or <see langword="null"/>.</param>
    /// <param name="contentBytes">The content image, or <see langword="null"/> to create an empty machine.</param>
    /// <param name="savePath">The content's persistent-save path, or <see langword="null"/> for an in-memory-only save.</param>
    /// <param name="audioSampleRate">The <see cref="IAudioMachine"/> output rate in frames per emulated second, or 0
    /// (the default) when no consumer will drain audio. Construction-fixed by design: the rate sizes the host's audio
    /// ring and configures the core's resampler exactly once, so a machine created at 0 answers
    /// <see cref="IAudioMachine.SampleRate"/> 0 forever and performs zero presentation-side audio work.</param>
    /// <returns>The created machine.</returns>
    IScreenMachine Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0);
}
