using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform.Probes;

/// <summary>One socket a kernel reads, occupying one or two consecutive <c>t</c> registers in declaration order (see
/// <see cref="RegisterCount"/>).</summary>
public abstract record ProbeKernelInput {
    private ProbeKernelInput() {
    }

    /// <summary>Sums the t-register span of a kernel's declared inputs — a <see cref="StrobePair"/> socket spans two
    /// consecutive registers; every other arm spans one.</summary>
    /// <param name="inputs">The kernel's declared inputs, in socket order.</param>
    public static int RegisterCount(IReadOnlyList<ProbeKernelInput> inputs) {
        var count = 0;

        for (var index = 0; (index < inputs.Count); index++) {
            count += ((inputs[index] is StrobePair)
                ? 2
                : 1
            );
        }

        return count;
    }

    /// <summary>The graph's converted current frame for one sensor — one register.</summary>
    /// <param name="Kind">The sensor whose converted frame is bound.</param>
    public sealed record Sensor(CameraSensor Kind) : ProbeKernelInput;
    /// <summary>A strobing sensor's lit frame and the unlit frame kept before it — two consecutive registers, the
    /// lit frame then the unlit frame.</summary>
    /// <param name="Kind">The strobing sensor.</param>
    public sealed record StrobePair(CameraSensor Kind) : ProbeKernelInput;
    /// <summary>An external shared ring the kernel's host opens read-only on its own device — one register, bound to the
    /// ring's latest published slot each cycle, or a null SRV on a cycle with no published slot yet. A slot published
    /// with a nonzero fence value is read only once the ring's shared fence reaches it: the host queues that wait on its
    /// own device ahead of the cycle's dispatch.</summary>
    /// <param name="Width">The ring's width in pixels.</param>
    /// <param name="Height">The ring's height in pixels.</param>
    /// <param name="Format">The ring's pixel format.</param>
    /// <param name="SharedTargetHandles">The ring's shared textures (opaque NT handles on Windows): one for a view
    /// export, two or more for a probe output.</param>
    /// <param name="Slots">The publication the host acquires the latest completed slot from.</param>
    /// <param name="SharedFenceHandle">The shared fence the ring's producer signals after each write, whose value each
    /// publication names, or zero when every write has finished before it is published.</param>
    public sealed record Ring(int Width, int Height, GpuPixelFormat Format, IReadOnlyList<nint> SharedTargetHandles, ISharedSlotRing Slots, nint SharedFenceHandle) : ProbeKernelInput;
    /// <summary>An optional socket left unbound — one register, always a null SRV.</summary>
    public sealed record Unbound() : ProbeKernelInput;
}
/// <summary>The declared socket ceiling for one kernel request (<c>puck.probe.manifest.v1</c>: 1..8 sockets) and the derived
/// t-register ceiling once a <see cref="ProbeKernelInput.StrobePair"/> socket's two registers are counted.</summary>
public static class ProbeKernelInputLimits {
    /// <summary>The maximum number of declared sockets.</summary>
    public const int MaxInputs = 8;
    /// <summary>The maximum number of flattened t registers (every socket a <see cref="ProbeKernelInput.StrobePair"/>).</summary>
    public const int MaxRegisters = (MaxInputs * 2);
}
/// <summary>A kernel's texture output: a consumer-provisioned shared ring the kernel writes one slot of per cycle
/// and publishes through <paramref name="Slots"/>, exactly as a camera stream publishes its frames: with the value its
/// write signals on the consumer's shared fence, or zero when the kernel keeps the CPU wait.</summary>
/// <param name="Width">The ring's width in pixels.</param>
/// <param name="Height">The ring's height in pixels.</param>
/// <param name="TargetFormat">The ring's pixel format.</param>
/// <param name="SharedTargetHandles">The ring's shared textures (opaque NT handles on Windows), two or more.</param>
/// <param name="Slots">The publication the consumer acquires completed slots from; configured for the ring's size.</param>
/// <param name="SharedFenceHandle">The consumer's shared fence the kernel signals after each write, or zero to keep the
/// CPU wait.</param>
public readonly record struct ProbeKernelOutput(int Width, int Height, GpuPixelFormat TargetFormat, IReadOnlyList<nint> SharedTargetHandles, LatestSlotPublication Slots, nint SharedFenceHandle);
/// <summary>One kernel-class probe's request: the kernel's precompiled entry points, the packed constant-buffer bytes
/// bound from the kind's config, its declared sockets, which socket's new frame triggers a cycle, and its optional
/// texture output. The kernel runs on its host's own device and worker thread, which creates it from the bytecode and
/// compiles nothing.</summary>
/// <param name="AccumulateEntry">The name of the per-pixel entry point, dispatched over the output extent when an
/// output is declared, else over the trigger input's extent.</param>
/// <param name="AccumulateBytecode">The per-pixel entry point's Direct3D 11 compute bytecode (<c>cs_5_0</c>), as the
/// build compiles it.</param>
/// <param name="FinalizeEntry">The name of the single-dispatch entry point that writes the reading's channels.</param>
/// <param name="FinalizeBytecode">The single-dispatch entry point's Direct3D 11 compute bytecode.</param>
/// <param name="Constants">The packed constant-buffer bytes, in the kind manifest's declared field order.</param>
/// <param name="ChannelCount">The number of channels the kind declares.</param>
/// <param name="RateHz">The cycle ceiling; trigger frames arriving faster are skipped.</param>
/// <param name="Inputs">The declared sockets, flattened to <c>t0, t1, …</c> in order (see
/// <see cref="ProbeKernelInput.RegisterCount"/>).</param>
/// <param name="Trigger">The index into <paramref name="Inputs"/> of the socket whose new frame starts a cycle: a camera
/// sensor's frame, which a camera graph hosts, or a ring's publication, which the render adapter's own host
/// (<see cref="IRenderedProbeKernelHost"/>) hosts.</param>
/// <param name="Output">The texture output, or <see langword="null"/> for a readings-only kernel.</param>
public readonly record struct ProbeKernelRequest(
    string AccumulateEntry,
    ReadOnlyMemory<byte> AccumulateBytecode,
    string FinalizeEntry,
    ReadOnlyMemory<byte> FinalizeBytecode,
    ReadOnlyMemory<byte> Constants,
    int ChannelCount,
    uint RateHz,
    IReadOnlyList<ProbeKernelInput> Inputs,
    int Trigger,
    ProbeKernelOutput? Output = null
) {
    /// <summary>Gets the camera sensor whose frame the trigger socket reads, or <see langword="null"/> when the trigger
    /// socket reads a ring, is unbound, or names no socket.</summary>
    public CameraSensor? TriggerSensor => (((Trigger >= 0) && (Trigger < Inputs.Count))
        ? Inputs[Trigger] switch {
            ProbeKernelInput.Sensor sensor => sensor.Kind,
            ProbeKernelInput.StrobePair strobe => strobe.Kind,
            _ => null,
        }
        : null);
    /// <summary>Gets the ring the trigger socket reads, or <see langword="null"/> when it reads a camera sensor, is
    /// unbound, or names no socket.</summary>
    public ProbeKernelInput.Ring? TriggerRing => (((Trigger >= 0) && (Trigger < Inputs.Count))
        ? (Inputs[Trigger] as ProbeKernelInput.Ring)
        : null);
}
/// <summary>A device that hosts kernel-class probes on its own worker: a kernel is attached once, runs after each of
/// its trigger socket's new frames with every declared input bound, and publishes readings (and its output slot) before
/// the worker moves on. A camera graph hosts the kernels its sensors trigger; the render adapter's own host
/// (<see cref="IRenderedProbeKernelHost"/>) hosts the kernels a rendered source triggers.</summary>
public interface IProbeKernelHost {
    /// <summary>Tries to attach a kernel. Its shaders are created on the worker; a refusal after attachment surfaces as
    /// the run's <see cref="IProbeKernelRun.Fault"/> with <see cref="IProbeKernelRun.IsEnded"/> set.</summary>
    /// <param name="request">The kernel request.</param>
    /// <param name="ring">The ring the kernel publishes readings into.</param>
    /// <param name="run">When this returns <see langword="true"/>, the attached run.</param>
    /// <param name="fault">A human-readable refusal reason when this returns <see langword="false"/>; otherwise empty.</param>
    /// <returns><see langword="true"/> if the request was accepted by this host.</returns>
    bool TryAttachKernel(in ProbeKernelRequest request, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault);
}
/// <summary>The kernel host on the render adapter's own device, for kernels whose trigger socket reads a rendered source
/// (a view export or another probe's output) and whose sockets bind no camera. Its worker wakes on
/// <see cref="Signal"/> and runs every attached kernel whose trigger ring published since that kernel last ran.
/// Disposing it ends every run and releases the device on the worker.</summary>
public interface IRenderedProbeKernelHost : IProbeKernelHost, IDisposable {
    /// <summary>Wakes the worker to run every kernel whose trigger ring has published since it last ran. The render thread
    /// calls it once a frame; it never blocks.</summary>
    void Signal();
}
/// <summary>Opens the render adapter's kernel host (<see cref="IRenderedProbeKernelHost"/>) on a platform whose kernels
/// run on a device of their own.</summary>
public interface IProbeKernelHostService {
    /// <summary>Tries to open a kernel host on the adapter the render device runs on.</summary>
    /// <param name="adapterLuid">The render adapter's LUID.</param>
    /// <param name="host">When this returns <see langword="true"/>, the host, owned by the caller.</param>
    /// <param name="fault">Why no host opened, when this returns <see langword="false"/>; otherwise empty.</param>
    /// <returns>Whether the host opened.</returns>
    bool TryOpen(long adapterLuid, [NotNullWhen(true)] out IRenderedProbeKernelHost? host, out string fault);
}
/// <summary>The kernel host service of a platform with no kernel device: every open is refused by name.</summary>
public sealed class NullProbeKernelHostService : IProbeKernelHostService {
    /// <inheritdoc/>
    public bool TryOpen(long adapterLuid, [NotNullWhen(true)] out IRenderedProbeKernelHost? host, out string fault) {
        host = null;
        fault = "this platform hosts no probe kernels";

        return false;
    }
}
/// <summary>One attached kernel. Disposing detaches it; the worker releases its native objects.</summary>
public interface IProbeKernelRun : IDisposable {
    /// <summary>Gets a value indicating whether the run has permanently stopped — a compile or dispatch fault, or
    /// the graph ending.</summary>
    bool IsEnded { get; }
    /// <summary>Gets the fault that ended the run, or <see langword="null"/>.</summary>
    string? Fault { get; }
    /// <summary>Gets the number of completed cycles.</summary>
    long Cycles { get; }
    /// <summary>Gets the number of trigger frames skipped because no output slot was writable.</summary>
    long Drops { get; }
    /// <summary>Gets how the kernel's output writes are ordered before the consumer reads them: through the consumer's
    /// shared fence, or by the kernel's CPU wait and why; <see cref="SharedFenceOrder.Pending"/> until the kernel is
    /// created.</summary>
    SharedFenceOrder Order { get; }

    /// <summary>Replaces the kernel's constant-buffer bytes from the next cycle on (latest wins).</summary>
    /// <param name="constants">The packed bytes, the same length as the request's.</param>
    void SetConstants(ReadOnlyMemory<byte> constants);
}
