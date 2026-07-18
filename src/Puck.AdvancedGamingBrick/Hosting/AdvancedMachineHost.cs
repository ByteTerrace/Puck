using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// Hosts one native ARM7TDMI AdvancedGamingBrick behind <see cref="IScreenMachine"/> and
/// <see cref="IQueuedScreenMachine"/> — a thin adapter that builds an <see cref="AdvancedGamingBrickCore"/> and forwards
/// the neutral surface to the shared <see cref="QueuedMachineWorker"/> substrate. The substrate owns the machine-owning
/// worker thread, the bounded eight-segment FIFO with producer backpressure, the triple-buffer publication rotation, and
/// the native-frame-keyed save-flush debounce; this class only wires the core's BIOS and cartridge.
/// </summary>
public sealed class AdvancedMachineHost : IScreenMachine, IQueuedScreenMachine, IAudioMachine, IFeedbackMachine, ITimeTravelMachine {
    /// <summary>The native framebuffer width.</summary>
    public const int ScreenWidth = 240;
    /// <summary>The native framebuffer height.</summary>
    public const int ScreenHeight = 160;
    /// <summary>The finite number of exact tick/input segments that may be accepted but incomplete. This is a segment
    /// bound, not a wall-clock duration.</summary>
    public const int DefaultMaximumPendingSteps = 8;

    private readonly byte[] m_bios;
    private readonly QueuedMachineWorker m_worker;
    private string? m_savePath;

    /// <summary>Creates an empty host or direct-boots <paramref name="cartridgeRom"/> when supplied.</summary>
    /// <param name="cartridgeRom">The native AGB cartridge image, or <see langword="null"/> for an empty host.</param>
    /// <param name="savePath">The optional battery-save path.</param>
    /// <param name="biosImage">A 16 KiB BIOS image; <see langword="null"/> selects the zeroed replacement image.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second the neutral
    /// <see cref="IAudioMachine"/> surface reports, or 0 (the default) when no consumer wants audio from this host —
    /// a silent host performs zero presentation-side audio synthesis.</param>
    public AdvancedMachineHost(byte[]? cartridgeRom = null, string? savePath = null, byte[]? biosImage = null, int audioSampleRate = 0) {
        if ((biosImage is not null) && (biosImage.Length != ReplacementBios.ImageSize)) {
            throw new ArgumentException(message: $"The BIOS image must be {ReplacementBios.ImageSize} bytes; got {biosImage.Length}.", paramName: nameof(biosImage));
        }

        m_bios = (biosImage?.ToArray() ?? new byte[ReplacementBios.ImageSize]);
        m_savePath = savePath;
        m_worker = new QueuedMachineWorker(width: ScreenWidth, height: ScreenHeight, maximumPendingSteps: DefaultMaximumPendingSteps, workerName: "Puck AdvancedGamingBrick", audioSampleRate: audioSampleRate);

        if (cartridgeRom is not null) {
            m_worker.Load(core: new AdvancedGamingBrickCore(bios: m_bios, cartridgeRom: cartridgeRom, savePath: m_savePath));
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
        m_worker.Load(core: new AdvancedGamingBrickCore(bios: m_bios, cartridgeRom: data, savePath: m_savePath));
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
}
