using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform.Probes;

/// <summary>One texture a kernel reads, by the camera sensor it comes from.</summary>
/// <param name="Sensor">The sensor whose converted frame is bound.</param>
/// <param name="Previous">Whether to bind the sensor's previous frame instead of its current one — on a strobing
/// infrared stream, the unlit frame beside the lit one.</param>
public readonly record struct ProbeKernelInput(CameraSensor Sensor, bool Previous = false);

/// <summary>A kernel's texture output: a consumer-provisioned shared ring the kernel writes one slot of per cycle
/// and publishes through <paramref name="Slots"/>, exactly as a camera stream publishes its frames.</summary>
/// <param name="Width">The ring's width in pixels.</param>
/// <param name="Height">The ring's height in pixels.</param>
/// <param name="TargetFormat">The ring's pixel format.</param>
/// <param name="SharedTargetHandles">The ring's shared textures (opaque NT handles on Windows), two or more.</param>
/// <param name="Slots">The publication the consumer acquires completed slots from; configured for the ring's size.</param>
public readonly record struct ProbeKernelOutput(int Width, int Height, SurfaceFormat TargetFormat, IReadOnlyList<nint> SharedTargetHandles, LatestSlotPublication Slots);

/// <summary>One kernel-class probe's request: what to compile, the packed constant-buffer bytes bound from the
/// kind's config, which camera frames it reads, which sensor's frame triggers a cycle, and its optional texture
/// output. The kernel runs on the camera graph's own device and worker thread.</summary>
/// <param name="KernelSource">The kernel's HLSL source text.</param>
/// <param name="AccumulateEntry">The per-pixel entry point, dispatched over the output extent when an output is
/// declared, else over the trigger input's extent.</param>
/// <param name="FinalizeEntry">The single-dispatch entry point that writes the reading's channels.</param>
/// <param name="Constants">The packed constant-buffer bytes, in the kind manifest's declared field order.</param>
/// <param name="ChannelCount">The number of channels the kind declares.</param>
/// <param name="RateHz">The cycle ceiling; trigger frames arriving faster are skipped.</param>
/// <param name="Inputs">The textures bound at <c>t0, t1, …</c> in order.</param>
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
