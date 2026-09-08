using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick.Forge.Tune;

/// <summary>
/// A player-operated diegetic instrument: a <c>puck.audio.v1</c> document compiled to a jukebox cart
/// (<see cref="TuneRom.Build"/>) and hosted on a real <see cref="MachineHost"/> — every <see cref="IScreenMachine"/>/
/// <see cref="IAudioMachine"/> member composes straight through to it, so this wrapper's only job is owning the
/// content-to-cart compile step and reporting <see cref="TicksPerBeat"/>.
/// </summary>
internal sealed class TuneInstrumentMachine : IScreenMachine, IAudioMachine, IInstrumentClockSource, IDisposable {
    /// <summary>One authored pattern row's engine-tick length, in the SAME fixed-tick domain
    /// <c>Puck.Audio.Simulation.MusicClock</c> uses: <c>Puck.Assets.Documents.AudioDocument.Tempo</c> is frames per
    /// row at the framework's 60 fps sound-driver reference, and <c>Puck.World.FixedTickConversion.TicksPerSecond</c>
    /// (50400) / 60 = 840 exactly.</summary>
    private const long TicksPerRowFrame = 840L;

    private readonly MachineHost m_inner;

    /// <summary>Initializes the instrument, booting <paramref name="content"/> immediately when supplied (a null
    /// content leaves it empty, awaiting <see cref="LoadContent"/> — the same contract every other engine's
    /// <see cref="IScreenMachineEngine.Create"/> follows).</summary>
    public TuneInstrumentMachine(int audioSampleRate, byte[]? content, string? savePath) {
        var document = ((content is null) ? null : TuneInstrumentEngine.ParseContent(content: content));

        m_inner = new MachineHost(
            audioSampleRate: audioSampleRate,
            cartridgeRom: ((document is null) ? null : TuneRom.Build(document: document)),
            model: ConsoleModel.CgbE,
            savePath: savePath
        );

        TicksPerBeat = ((document is null) ? 0L : (document.Tempo!.Value * TicksPerRowFrame));
    }

    /// <inheritdoc/>
    public long TicksPerBeat { get; private set; }
    /// <inheritdoc/>
    public bool IsAssigned => m_inner.IsAssigned;
    /// <inheritdoc/>
    public nint NativeImageViewHandle => m_inner.NativeImageViewHandle;
    /// <inheritdoc/>
    public Vector3 EmittedLight => m_inner.EmittedLight;
    /// <inheritdoc/>
    public int SampleRate => m_inner.SampleRate;

    /// <inheritdoc/>
    public void Dispose() => m_inner.Dispose();
    /// <inheritdoc/>
    public void Eject() {
        m_inner.Eject();
        TicksPerBeat = 0L;
    }
    /// <inheritdoc/>
    public void FlushSave(bool force = false) => m_inner.FlushSave(force: force);
    /// <inheritdoc/>
    public void LoadContent(byte[] data, string? savePath = null) {
        ArgumentNullException.ThrowIfNull(argument: data);

        var document = TuneInstrumentEngine.ParseContent(content: data);

        m_inner.LoadContent(
            data: TuneRom.Build(document: document),
            savePath: savePath
        );
        TicksPerBeat = (document.Tempo!.Value * TicksPerRowFrame);
    }
    /// <inheritdoc/>
    public void NotifyDeviceLost() => m_inner.NotifyDeviceLost();
    /// <inheritdoc/>
    public void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) => m_inner.PublishFrame(
        deviceContext: deviceContext,
        gpu: gpu
    );
    /// <inheritdoc/>
    public int ReadSamples(Span<short> destination) => m_inner.ReadSamples(destination: destination);
    /// <inheritdoc/>
    public bool Step(ulong deltaTicks, in MachinePadState input) => m_inner.Step(
        deltaTicks: deltaTicks,
        input: in input
    );
}
