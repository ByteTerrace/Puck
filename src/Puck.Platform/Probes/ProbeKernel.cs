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
            count += ((inputs[index] is StrobePair) ? 2 : 1);
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
    /// <summary>An external shared ring the graph opens read-only on its own device — one register, bound to the
    /// ring's latest published slot each cycle, or a null SRV on a cycle with no published slot yet.</summary>
    /// <param name="Width">The ring's width in pixels.</param>
    /// <param name="Height">The ring's height in pixels.</param>
    /// <param name="Format">The ring's pixel format.</param>
    /// <param name="SharedTargetHandles">The ring's shared textures (opaque NT handles on Windows), two or more.</param>
    /// <param name="Slots">The publication the graph acquires the latest completed slot from.</param>
    public sealed record Ring(int Width, int Height, SurfaceFormat Format, IReadOnlyList<nint> SharedTargetHandles, ISharedSlotRing Slots) : ProbeKernelInput;
    /// <summary>An optional socket left unbound — one register, always a null SRV.</summary>
    public sealed record Unbound() : ProbeKernelInput;
}
/// <summary>The declared socket ceiling for one kernel request (<c>puck.probe.v1</c>: 1..8 sockets) and the derived
/// t-register ceiling once a <see cref="ProbeKernelInput.StrobePair"/> socket's two registers are counted.</summary>
public static class ProbeKernelInputLimits {
    /// <summary>The maximum number of declared sockets.</summary>
    public const int MaxInputs = 8;
    /// <summary>The maximum number of flattened t registers (every socket a <see cref="ProbeKernelInput.StrobePair"/>).</summary>
    public const int MaxRegisters = (MaxInputs * 2);
}
/// <summary>A kernel's texture output: a consumer-provisioned shared ring the kernel writes one slot of per cycle
/// and publishes through <paramref name="Slots"/>, exactly as a camera stream publishes its frames.</summary>
/// <param name="Width">The ring's width in pixels.</param>
/// <param name="Height">The ring's height in pixels.</param>
/// <param name="TargetFormat">The ring's pixel format.</param>
/// <param name="SharedTargetHandles">The ring's shared textures (opaque NT handles on Windows), two or more.</param>
/// <param name="Slots">The publication the consumer acquires completed slots from; configured for the ring's size.</param>
public readonly record struct ProbeKernelOutput(int Width, int Height, SurfaceFormat TargetFormat, IReadOnlyList<nint> SharedTargetHandles, LatestSlotPublication Slots);
/// <summary>One kernel-class probe's request: what to compile, the packed constant-buffer bytes bound from the
/// kind's config, its declared sockets, which camera sensor's frame triggers a cycle, and its optional texture
/// output. The kernel runs on the camera graph's own device and worker thread.</summary>
/// <param name="KernelSource">The kernel's HLSL source text.</param>
/// <param name="AccumulateEntry">The per-pixel entry point, dispatched over the output extent when an output is
/// declared, else over the trigger input's extent.</param>
/// <param name="FinalizeEntry">The single-dispatch entry point that writes the reading's channels.</param>
/// <param name="Constants">The packed constant-buffer bytes, in the kind manifest's declared field order.</param>
/// <param name="ChannelCount">The number of channels the kind declares.</param>
/// <param name="RateHz">The cycle ceiling; trigger frames arriving faster are skipped.</param>
/// <param name="Inputs">The declared sockets, flattened to <c>t0, t1, …</c> in order (see
/// <see cref="ProbeKernelInput.RegisterCount"/>).</param>
/// <param name="Trigger">The sensor whose new frame starts a cycle; must be one of <paramref name="Inputs"/>' sensors.</param>
/// <param name="Output">The texture output, or <see langword="null"/> for a readings-only kernel.</param>
public readonly record struct ProbeKernelRequest(
    string KernelSource,
    string AccumulateEntry,
    string FinalizeEntry,
    ReadOnlyMemory<byte> Constants,
    int ChannelCount,
    uint RateHz,
    IReadOnlyList<ProbeKernelInput> Inputs,
    CameraSensor Trigger,
    ProbeKernelOutput? Output = null
);
/// <summary>A camera graph that hosts kernel-class probes on its own device and worker: a kernel is attached once,
/// runs after each trigger frame's conversion with that frame and the other declared inputs bound, and publishes
/// readings (and its output slot) before the worker moves on.</summary>
public interface ICameraKernelHost {
    /// <summary>Tries to attach a kernel. Compilation happens on the worker; a refusal after attachment surfaces as
    /// the run's <see cref="IProbeKernelRun.Fault"/> with <see cref="IProbeKernelRun.IsEnded"/> set.</summary>
    /// <param name="request">The kernel request.</param>
    /// <param name="ring">The ring the kernel publishes readings into.</param>
    /// <param name="run">When this returns <see langword="true"/>, the attached run.</param>
    /// <param name="fault">A human-readable refusal reason when this returns <see langword="false"/>; otherwise empty.</param>
    /// <returns><see langword="true"/> if the request was accepted for this graph's streams.</returns>
    bool TryAttachKernel(in ProbeKernelRequest request, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault);
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

    /// <summary>Replaces the kernel's constant-buffer bytes from the next cycle on (latest wins).</summary>
    /// <param name="constants">The packed bytes, the same length as the request's.</param>
    void SetConstants(ReadOnlyMemory<byte> constants);
}
