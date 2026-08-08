using Puck.Demo.Audio;
using Puck.Demo.Forge;
using Puck.Demo.Forge.Tune;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Tracker;

/// <summary>
/// The tracker's "hear it" preview: compiles the working document through <see cref="TuneRom.Build"/> exactly like
/// <c>--forge-tune</c> does, boots it on a HEADLESS scratch Humble machine (<see cref="MachineFactory.Create"/> —
/// machines are pure CPU, no GPU/render dependency), and drains its <see cref="IAudioSink"/> into a
/// <see cref="CabinetAudioOutput"/> — mirroring <see cref="GamingBrickChildNode"/>'s own open/configure/pump/dispose
/// shape in a small self-contained class that owns nothing else. Output-only: nothing here ever writes back into the
/// machine (the jukebox's play state already starts its loop on boot — see <see cref="Forge.Tune.TuneGame"/> — so no
/// input is needed to hear it), and nothing here touches the demo's deterministic simulation or shared timeline —
/// this is host-side presentation exactly like a booted cabinet's speaker path. Stepping is driven by the caller once
/// per rendered frame (<see cref="StepOneFrame"/>), so playback pace follows the engine's own frame cadence rather
/// than a second clock.
/// </summary>
internal sealed class TrackerPreviewPlayer : IDisposable {
    // One machine frame's fixed T-cycle budget — the same constant TuneVerify/VerifyGameAudio drive a Humble machine
    // with; simpler than GamingBrickChildNode's tick-accumulator (this player has no shared timeline to stay
    // rational against, so a fixed per-frame budget is exact and sufficient).
    private const ulong TCyclesPerFrame = 70_224UL;
    // A couple of frames to let the cart's boot state land on its one play state (mirrors TuneVerify's own boot
    // settle) before the caller starts stepping frame-by-frame.
    private const int BootSettleFrames = 8;
    // A generous per-pump staging span: one produced frame emits far fewer samples than a whole second's ring
    // capacity, so this never truncates a drain.
    private const int StagingSampleCapacity = (CabinetAudioOutput.SampleRate * 2);

    private readonly IAudioSink m_audioSink;
    private readonly HumbleAudioMachine m_audioMachine;
    private readonly CabinetAudioOutput? m_audioOutput;
    private readonly MachineInstance m_machine;
    // Manual drain staging: used ONLY when no CabinetAudioOutput opened (headless/non-Windows), so the sink's ring
    // still empties every pump and SamplesPumped counts real drained samples rather than the ring's saturating
    // "currently buffered" watermark (which stops growing once the ring fills and nothing reads it).
    private readonly short[] m_staging = new short[StagingSampleCapacity];
    private bool m_disposed;

    private TrackerPreviewPlayer(MachineInstance machine) {
        m_machine = machine;
        m_audioSink = machine.GetRequiredService<IAudioSink>();
        m_audioMachine = new HumbleAudioMachine(sink: m_audioSink);
        m_audioOutput = CabinetAudioOutput.TryOpen();
        m_audioSink.Configure(sampleRate: CabinetAudioOutput.SampleRate);
    }

    /// <summary>Gets the total sample count actually drained from the audio sink so far (stereo shorts; the headless
    /// proof counts this to show the preview path actually produced audio without a listener) — a real drain count,
    /// never the ring's saturating "currently buffered" watermark.</summary>
    public long SamplesPumped { get; private set; }

    /// <summary>Compiles <paramref name="document"/> into a jukebox cart and boots it on a fresh headless machine,
    /// already past its boot settle and with the loop started (the jukebox's play state starts playing on boot —
    /// see <see cref="Forge.Tune.TuneGame"/> — so no START press is needed here).</summary>
    /// <param name="document">The normalized document to preview (see <see cref="AudioDocumentStore"/>).</param>
    /// <returns>The ready-to-step player.</returns>
    public static TrackerPreviewPlayer Start(AudioDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var rom = TuneRom.Build(document: document, title: "PREVIEW");
        var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );
        var player = new TrackerPreviewPlayer(machine: machine);

        for (var frame = 0; (frame < BootSettleFrames); frame++) {
            player.m_machine.Machine.Run(tCycles: TCyclesPerFrame);
        }

        player.PumpAudio();

        return player;
    }

    /// <summary>Steps the machine by exactly one frame's T-cycle budget and drains any newly produced samples to the
    /// host output — call once per rendered frame while the preview is playing.</summary>
    public void StepOneFrame() {
        if (m_disposed) {
            return;
        }

        m_machine.Machine.Run(tCycles: TCyclesPerFrame);
        PumpAudio();
    }

    // Drains the sink for real every pump, one way or the other, so SamplesPumped is always a true count of samples
    // that left the ring (never the saturating "currently buffered" watermark a headless/undrained ring would stall
    // at after one second): with a host device, CabinetAudioOutput's own ReadSamples call does the draining and this
    // just totals what was available beforehand; without one (no device/non-Windows), this drains manually into a
    // staging span that is otherwise discarded.
    private void PumpAudio() {
        if (m_audioOutput is { } output) {
            SamplesPumped += m_audioSink.AvailableSampleCount;
            output.Pump(machine: m_audioMachine);

            return;
        }

        while (m_audioSink.AvailableSampleCount > 0) {
            var read = m_audioSink.ReadSamples(destination: m_staging);

            if (read == 0) {
                break;
            }

            SamplesPumped += read;
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_audioOutput?.Dispose();
        m_machine.Dispose();
    }
}
