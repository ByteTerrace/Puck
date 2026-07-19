using Puck.Authoring;
using Puck.Forge.Tune;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.World.Audio;

/// <summary>
/// The headless tune host: one <c>puck.audio.v1</c> document compiled to a cart
/// (<see cref="TuneRom.Build(AudioDocument, string)"/>) and played by a synchronous Humble core stepped cycle-exactly
/// inside <see cref="Pull"/> — never a queued worker (its worker thread is the one nondeterministic scheduling
/// element). The exact-rational cycle accumulator (subtract-not-reset) keeps machine time locked to the
/// mixer rate with zero drift. Acquired by the audio director while any speaker references the tune, released when
/// orphaned — hosting is a runtime derivation, never a data concept.
/// </summary>
public sealed class TuneMachineSource : IAudioBlockSource, IDisposable {
    private const long CyclesPerSecond = 4_194_304L;
    // Boot pre-roll: the jukebox reaches its play state within 8 frames; the buffered boot
    // audio simply becomes the stream's deterministic head.
    private const ulong BootPrerollFrames = 8UL;
    private const ulong CyclesPerVideoFrame = 70_224UL;

    private readonly MachineInstance m_machine;
    private readonly IAudioSink m_sink;
    private long m_cycleAccumulator;
    private bool m_disposed;

    /// <summary>Initializes the host: compiles the document to a cart, boots the core through the pre-roll, and
    /// configures its resampler to the mixer rate.</summary>
    /// <param name="document">The canonical (validated + normalized) <c>puck.audio.v1</c> document.</param>
    public TuneMachineSource(AudioDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);
        m_machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: TuneRom.Build(document: document)),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );
        m_sink = m_machine.GetRequiredService<IAudioSink>();
        m_sink.Configure(sampleRate: WorldAudioMixer.SampleRate);
        m_machine.Machine.Run(tCycles: (BootPrerollFrames * CyclesPerVideoFrame));
    }

    /// <inheritdoc/>
    public int Pull(Span<short> interleavedStereo, int frames) {
        if (m_disposed) {
            return 0;
        }

        m_cycleAccumulator += (frames * CyclesPerSecond);

        var run = (m_cycleAccumulator / WorldAudioMixer.SampleRate);

        m_machine.Machine.Run(tCycles: ((ulong)run));
        m_cycleAccumulator -= (run * WorldAudioMixer.SampleRate);

        return (m_sink.ReadSamples(destination: interleavedStereo[..(frames * 2)]) / 2);
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (!m_disposed) {
            m_disposed = true;
            m_machine.Dispose();
        }
    }
}
