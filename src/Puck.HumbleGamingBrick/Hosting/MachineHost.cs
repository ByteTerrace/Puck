using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83-family GamingBrick as an <see cref="IScreenMachine"/> — the first implementation of the neutral
/// screen-machine contract. A thin adapter that builds a <see cref="HumbleGamingBrickCore"/> and forwards the neutral
/// surface to the shared <see cref="QueuedMachineWorker"/> substrate: the machine is advanced by an exact integer tick
/// budget (converted to CPU T-cycles through a remainder-carrying accumulator, so it stays a pure function of the engine's
/// deterministic clock and its sampled input), and its unresampled 160x144 framebuffer is uploaded to a shader-readable
/// GPU image whose stable view handle a screen source samples directly. It carries the queued/backpressure behavior of the
/// substrate — a host that recognizes <see cref="IQueuedScreenMachine"/> keeps commercial-ROM CPU work off its
/// simulation/render pump — and answers a work-RAM peek.
/// <para>
/// This is the generic core, without the overworld's presentation costume, viewport resample, fleet-choir mirroring,
/// serial link, peripherals, or audio output — a machine that steps, shows a frame, and answers a work-RAM peek.
/// </para>
/// </summary>
public sealed class MachineHost : IScreenMachine, IMachineMemoryPeek, IQueuedScreenMachine, IAudioMachine, IFeedbackMachine, ITimeTravelMachine {
    /// <summary>The machine's native framebuffer width (160).</summary>
    public const int ScreenWidth = Framebuffer.ScreenWidth;
    /// <summary>The machine's native framebuffer height (144).</summary>
    public const int ScreenHeight = Framebuffer.ScreenHeight;
    /// <summary>The finite number of exact tick/input segments that may be accepted but incomplete.</summary>
    public const int DefaultMaximumPendingSteps = 8;

    private readonly ConsoleModel m_model;
    private readonly bool m_dmgSpeed;
    private readonly QueuedMachineWorker m_worker;
    private string? m_savePath;

    /// <summary>Initializes a new machine host. When <paramref name="cartridgeRom"/> is non-null the machine assembles
    /// at once; a null ROM leaves the host UNASSIGNED (a dark framebuffer) until <see cref="LoadContent"/> runs.</summary>
    /// <param name="model">The hardware model to emulate (<see cref="ConsoleModel.Dmg"/>/<see cref="ConsoleModel.Cgb"/>/
    /// <see cref="ConsoleModel.Agb"/>).</param>
    /// <param name="cartridgeRom">The cartridge ROM image, or <see langword="null"/> to start empty.</param>
    /// <param name="savePath">The cartridge's battery-save path (conventionally <c>&lt;romPath&gt;.sav</c>), or
    /// <see langword="null"/> for an in-memory-only save.</param>
    /// <param name="dmgSpeed">When <see langword="true"/>, the FAIRNESS pin: the tick-to-cycle budget stays at the DMG
    /// rate regardless of the KEY1 double-speed latch, so the budget is a function of configuration alone and every
    /// machine consumes identical cycle counts per engine tick.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second the neutral
    /// <see cref="IAudioMachine"/> surface reports, or 0 (the default) when no consumer wants audio from this host —
    /// a silent host performs zero presentation-side audio synthesis.</param>
    public MachineHost(ConsoleModel model, byte[]? cartridgeRom = null, string? savePath = null, bool dmgSpeed = false, int audioSampleRate = 0) {
        m_model = model;
        m_dmgSpeed = dmgSpeed;
        m_savePath = savePath;
        m_worker = new QueuedMachineWorker(width: ScreenWidth, height: ScreenHeight, maximumPendingSteps: DefaultMaximumPendingSteps, workerName: "Puck GamingBrick", audioSampleRate: audioSampleRate);

        if (cartridgeRom is not null) {
            Assemble(cartridgeRom: cartridgeRom);
        }
    }

    /// <inheritdoc/>
    public bool IsAssigned => m_worker.IsAssigned;

    /// <inheritdoc/>
    public nint NativeImageViewHandle => m_worker.NativeImageViewHandle;

    /// <inheritdoc/>
    public Vector3 EmittedLight => m_worker.EmittedLight;

    /// <inheritdoc/>
    public long CompletedSteps => m_worker.CompletedSteps;

    /// <inheritdoc/>
    public long PendingSteps => m_worker.PendingSteps;

    /// <inheritdoc/>
    public int MaximumPendingSteps => m_worker.MaximumPendingSteps;

    /// <inheritdoc/>
    public long BackpressureEvents => m_worker.BackpressureEvents;

    /// <inheritdoc/>
    public string? QueueFault => m_worker.QueueFault;

    /// <inheritdoc/>
    public void LoadContent(byte[] data, string? savePath = null) {
        ArgumentNullException.ThrowIfNull(argument: data);

        m_savePath = savePath;
        Assemble(cartridgeRom: data);
    }

    /// <inheritdoc/>
    public void Eject() =>
        m_worker.Eject();

    /// <inheritdoc/>
    public bool Step(ulong deltaTicks, in MachinePadState input) =>
        m_worker.Step(deltaTicks: deltaTicks, input: in input);

    /// <inheritdoc/>
    public QueuedMachineSubmission Submit(ulong deltaTicks, in MachinePadState input) =>
        m_worker.Submit(deltaTicks: deltaTicks, input: in input);

    /// <inheritdoc/>
    public void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) =>
        m_worker.PublishFrame(deviceContext: deviceContext, gpu: gpu);

    /// <inheritdoc/>
    public void NotifyDeviceLost() =>
        m_worker.NotifyDeviceLost();

    /// <inheritdoc/>
    public byte PeekByte(int address) =>
        m_worker.PeekByte(address: address);

    /// <inheritdoc/>
    public void PokeByte(int address, byte value) =>
        m_worker.PokeByte(address: address, value: value);

    /// <inheritdoc/>
    public int SampleRate =>
        m_worker.AudioSampleRate;

    /// <inheritdoc/>
    public int ReadSamples(Span<short> destination) =>
        m_worker.ReadAudioSamples(destination: destination);

    /// <inheritdoc/>
    public float MotorLevel =>
        m_worker.MotorLevel;

    /// <inheritdoc/>
    public void SetRewindEnabled(bool enabled) =>
        m_worker.SetRewindEnabled(enabled: enabled);

    /// <inheritdoc/>
    public int RewindBy(int frames) =>
        m_worker.RewindBy(frames: frames);

    /// <inheritdoc/>
    public void SetRunahead(int frames) =>
        m_worker.SetRunahead(frames: frames);

    /// <inheritdoc/>
    public void SetFastForward(int factor) =>
        m_worker.SetFastForward(factor: factor);

    /// <inheritdoc/>
    public TimeTravelStatus TimeTravelStatus =>
        m_worker.TimeTravelStatus;

    /// <inheritdoc/>
    public void FlushSave(bool force = false) =>
        m_worker.FlushSave(force: force);

    /// <inheritdoc/>
    public void Dispose() =>
        m_worker.Dispose();

    // Build the core (which loads any battery save) and attach it to the worker; the worker stops and disposes any prior
    // core first. Every access — stepping AND the memory peek/poke — goes through the worker, which owns the core on its
    // single execution thread; the host retains no cross-thread reference into it.
    private void Assemble(byte[] cartridgeRom) =>
        m_worker.Load(core: new HumbleGamingBrickCore(model: m_model, cartridgeRom: cartridgeRom, savePath: m_savePath, dmgSpeed: m_dmgSpeed));
}
