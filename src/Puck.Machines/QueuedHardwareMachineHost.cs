using Puck.Abstractions.Machines;

namespace Puck.Machines;

/// <summary>
/// A <see cref="QueuedMachineHost"/> whose core exposes scalar hardware access (<see cref="IMachineHardwareAccess"/>),
/// marshaled onto the worker's execution thread between steps. A host that derives from it names its address spaces; the
/// core behind it implements the same interface and performs the access.
/// </summary>
public abstract class QueuedHardwareMachineHost : QueuedMachineHost, IMachineHardwareAccess {
    /// <summary>Initializes a queued screen-machine host with hardware access.</summary>
    /// <param name="width">The native framebuffer width.</param>
    /// <param name="height">The native framebuffer height.</param>
    /// <param name="workerName">The worker thread's diagnostic name.</param>
    /// <param name="audioSampleRate">The requested audio sample rate, or zero to disable audio synthesis.</param>
    /// <param name="savePath">The initial battery-save path.</param>
    protected QueuedHardwareMachineHost(int width, int height, string workerName, int audioSampleRate, string? savePath)
        : base(
        audioSampleRate: audioSampleRate,
        height: height,
        savePath: savePath,
        width: width,
        workerName: workerName
    ) { }

    /// <inheritdoc/>
    public abstract IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces { get; }

    /// <inheritdoc/>
    public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) =>
        Worker.AccessHardware(
            address: address,
            mode: mode
        );
    /// <inheritdoc/>
    public MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode) =>
        Worker.AccessHardware(
            address: address,
            mode: mode,
            value: value
        );
}
