using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Sizes and records every views and glass shading dispatch. Jobs contain whole groups, including padding;
/// their predicted work never exceeds the provisional <see cref="SdfShadeProgram.MaxWork"/>.</summary>
public sealed class SdfShadeDispatch {
    /// <summary>One admitted rectangle of complete 8 by 8 groups.</summary>
    public readonly record struct Job(uint FirstGroup, uint Groups, ulong Work);

    private readonly IReadOnlyList<Job> m_jobs;
    private readonly IReadOnlyList<string> m_identities;
    private (SdfProgram Scene, SdfShadeProgram? Default, bool Enabled, uint Width, uint Height, uint Lights, bool Glass)? m_key;

    private SdfShadeDispatch(Job[] jobs, string[] identities) { m_jobs = Array.AsReadOnly(jobs); m_identities = Array.AsReadOnly(identities); }

    /// <summary>The bounded jobs, in raster order.</summary>
    public IReadOnlyList<Job> Jobs => m_jobs;
    /// <summary>The canonical identities any job may execute, retained through submission retirement.</summary>
    public IReadOnlyList<string> Identities => m_identities;

    internal static SdfShadeDispatch For(SdfShadeDispatch? previous, SdfFrame frame, uint width, uint height, uint screens, bool glass) {
        var lights = checked((uint)frame.Lights.Records.Length + (frame.DisableScreenLights ? 0u : screens));
        var key = (frame.Program, frame.ShadeDefault, frame.ShadePrograms, width, height, lights, glass);
        if (previous is not null && previous.m_key == key) { return previous; }
        var plan = Plan(width, height, frame.ShadePrograms ? frame.ShadingPrograms : [], lights, glass);
        plan.m_key = key;
        return plan;
    }

    /// <summary>Plans an unsplit extent, conservatively pricing every padded pixel at its largest admitted program.
    /// Glass prices both interfaces. Splitting within a group is unrepresentable.</summary>
    public static SdfShadeDispatch Plan(uint width, uint height, IEnumerable<SdfShadeProgram> programs, uint lights, bool glass) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, SdfShadeProgram.MaxLights);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var pixelWork = checked(SdfShadeProgram.EngineWork + (ulong)lights * SdfShadeProgram.DefaultLightWork + SdfShadeProgram.DefaultComposeWork);
        foreach (var program in programs) { identities.Add(program.Identity); pixelWork = Math.Max(pixelWork, program.PixelWork(lights)); }
        var groupWork = checked(pixelWork * 64UL * (glass ? 2UL : 1UL));
        if (groupWork > SdfShadeProgram.MaxWork) { throw new InvalidOperationException("An admitted shade group exceeds MaxWork."); }
        var across = checked((width + 7u) / 8u);
        var down = checked((height + 7u) / 8u);
        var total = checked(across * down);
        var capacity = (uint)Math.Min(65535UL, SdfShadeProgram.MaxWork / groupWork);
        var jobs = new List<Job>();
        for (uint first = 0; first < total;) {
            var count = Math.Min(capacity, total - first);
            jobs.Add(new(first, count, checked(count * groupWork)));
            first += count;
        }
        return new(jobs.ToArray(), identities.Order(StringComparer.Ordinal).ToArray());
    }

    // Only this door records a shade dispatch. The pushed ordinal remaps whole groups into the original 2D grid,
    // preserving the views filter's local lanes, apron and barrier even across a partial group row.
    internal void Record(in RenderGraphPackageRecording recording, IGpuComputePipeline pipeline) {
        Span<byte> pushed = stackalloc byte[sizeof(uint)];
        foreach (var job in m_jobs) {
            BinaryPrimitives.WriteUInt32LittleEndian(pushed, job.FirstGroup);
            recording.Recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer,
                data: pushed, offset: 0, pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: job.Groups, groupCountY: 1, groupCountZ: 1);
        }
    }
}
