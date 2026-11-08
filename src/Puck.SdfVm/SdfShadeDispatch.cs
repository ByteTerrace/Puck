using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Sizes and records every views and glass shading dispatch: unsplit jobs of whole groups including padding, or a
/// binned view's jobs of whole tiles of one bin, read indirectly. A job's predicted work never exceeds the provisional
/// <see cref="SdfShadeProgram.MaxWork"/>.</summary>
public sealed class SdfShadeDispatch {
    /// <summary>One admitted rectangle of complete 8 by 8 groups.</summary>
    public readonly record struct Job(uint FirstGroup, uint Groups, ulong Work);
    /// <summary>One bin of a binned plan: the tiles one of its jobs shades at most, the jobs it records (enough for the
    /// bin to hold every tile of the extent) and the work one of its tiles is priced at.</summary>
    public readonly record struct Bin(uint Width, uint Jobs, ulong TileWork);

    // The pixels of one tile, which a group shades.
    private const ulong TilePixels = (SdfVisibility.GroupEdgePixels * SdfVisibility.GroupEdgePixels);
    // The bits of a binned job's pushed index that name its bin; its first entry in the bin's tile list fills the rest
    // (sdf-pixel-entry.hlsli).
    private const int BinBits = 4;
    // The most groups one dispatch names on an axis, on both backends.
    private const ulong MaxGroups = 65535UL;

    // The backing arrays, which the recording doors iterate without an enumerator; callers read the read-only views.
    private readonly Job[] m_jobs;
    private readonly Bin[] m_bins;
    private readonly IReadOnlyList<Job> m_jobsView;
    private readonly IReadOnlyList<Bin> m_binsView;
    private readonly IReadOnlyList<string> m_identities;
    // The extent's tiles, a binned job's argument record's stride between bins.
    private readonly uint m_tiles;
    private (SdfProgram? Scene, SdfShadeProgram? Default, bool Enabled, uint Width, uint Height, uint Lights, bool Glass, bool Binned)? m_key;

    private SdfShadeDispatch(Job[] jobs, Bin[] bins, string[] identities, uint tiles) {
        m_jobs = jobs;
        m_bins = bins;
        m_jobsView = Array.AsReadOnly(jobs);
        m_binsView = Array.AsReadOnly(bins);
        m_identities = Array.AsReadOnly(identities);
        m_tiles = tiles;
    }

    /// <summary>The unsplit plan's bounded jobs, in raster order; empty for a binned plan.</summary>
    public IReadOnlyList<Job> Jobs => m_jobsView;
    /// <summary>The binned plan's bins in bin order (<c>SdfWorldPackage.ShadeBins</c> at most); empty for an unsplit
    /// plan.</summary>
    public IReadOnlyList<Bin> Bins => m_binsView;
    /// <summary>Whether the plan shades in bins.</summary>
    public bool Binned => (m_bins.Length != 0);
    /// <summary>The canonical identities any job may execute, retained through submission retirement.</summary>
    public IReadOnlyList<string> Identities => m_identities;

    // A frame's plan for a pass of the given extent: binned when asked for and the frame runs programs, unsplit otherwise.
    // The same inputs give the same plan, so every pass of one frame agrees.
    internal static SdfShadeDispatch For(SdfShadeDispatch? previous, SdfFrame frame, uint width, uint height, uint screens, bool glass, bool binned = false) {
        // The walk visits at most the frame's lights (a view's mask holds only some of them) and the bound screens.
        var lights = checked((uint)frame.Lights.Count + (frame.DisableScreenLights ? 0u : screens));
        var key = ((SdfProgram?)frame.Program, frame.ShadeDefault, frame.ShadePrograms, width, height, lights, glass, binned);
        if (previous is not null && previous.m_key == key) { return previous; }
        var programs = (frame.ShadePrograms ? frame.ShadingPrograms.ToArray() : []);
        var plan = ((binned && (programs.Length != 0)) ? PlanBins(height: height, lights: lights, programs: programs, width: width) : Plan(width, height, programs, lights, glass));
        plan.m_key = key;
        return plan;
    }
    // The plan of an extent's pass that runs no program (a binned view's filter): one dispatch of its groups.
    internal static SdfShadeDispatch ForEngine(SdfShadeDispatch? previous, uint width, uint height) {
        var key = ((SdfProgram?)null, (SdfShadeProgram?)null, false, width, height, 0u, false, false);
        if (previous is not null && previous.m_key == key) { return previous; }
        var plan = Plan(width, height, [], 0u, glass: false);
        plan.m_key = key;
        return plan;
    }

    /// <summary>Plans an unsplit extent, conservatively pricing every padded pixel at its largest admitted program.
    /// Glass prices both interfaces. Splitting within a group is unrepresentable. An extent with no program runs only the
    /// engine's fixed stage, which <see cref="SdfShadeProgram.MaxWork"/> does not bound (it bounds what an authored program
    /// adds), so it is one dispatch within the backend's group-count limit, as every engine pass is.</summary>
    public static SdfShadeDispatch Plan(uint width, uint height, IEnumerable<SdfShadeProgram> programs, uint lights, bool glass) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, SdfShadeProgram.MaxLights);
        var (pixelWork, identities) = Price(lights: lights, programs: programs);
        var groupWork = checked(pixelWork * TilePixels * (glass ? 2UL : 1UL));
        if (groupWork > SdfShadeProgram.MaxWork) { throw new InvalidOperationException("An admitted shade group exceeds MaxWork."); }
        var total = Tiles(width: width, height: height);
        var capacity = (uint)Math.Min(MaxGroups, ((identities.Length == 0) ? MaxGroups : (SdfShadeProgram.MaxWork / groupWork)));
        var jobs = new List<Job>();
        for (uint first = 0; first < total;) {
            var count = Math.Min(capacity, total - first);
            jobs.Add(new(first, count, checked(count * groupWork)));
            first += count;
        }
        return new(jobs.ToArray(), [], identities, total);
    }
    /// <summary>Plans a binned views extent: bin zero, the tiles no pixel of which has a program, which run only the
    /// engine's fixed stage and are bounded by the group-count limit alone; bin one, every other tile, priced at the
    /// largest admitted program, its jobs at most as many tiles as keep a job's work within
    /// <see cref="SdfShadeProgram.MaxWork"/>. Each bin records enough jobs to shade every tile of the extent, since a
    /// bin's count exists only on the GPU when the frame records; a job past its bin's count dispatches no group.</summary>
    /// <param name="width">The extent's width, in pixels.</param>
    /// <param name="height">The extent's height, in pixels.</param>
    /// <param name="programs">The admitted programs the frame runs.</param>
    /// <param name="lights">The lights each pixel's walk may visit, at most <see cref="SdfShadeProgram.MaxLights"/>.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="programs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lights"/> exceeds
    /// <see cref="SdfShadeProgram.MaxLights"/>.</exception>
    public static SdfShadeDispatch PlanBins(uint width, uint height, IEnumerable<SdfShadeProgram> programs, uint lights) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, SdfShadeProgram.MaxLights);
        var (shadedWork, identities) = Price(lights: lights, programs: programs);
        var tiles = Tiles(width: width, height: height);
        Bin[] bins = [BinOf(authored: false, pixelWork: DefaultWork(lights: lights), tiles: tiles), BinOf(authored: true, pixelWork: shadedWork, tiles: tiles)];

        return new([], bins, identities, tiles);
    }

    // Only these doors record a shade dispatch. The pushed ordinal remaps whole groups into the original 2D grid,
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
    // A bin's jobs, each reading its group count from the arguments the bins pass wrote from the bin's count
    // (sdf-world-views-bins.comp.hlsl), a record a tile a bin, its pushed index naming the bin and the job's first entry
    // in the bin's tile list.
    internal void RecordBin(in RenderGraphPackageRecording recording, int bin, IGpuComputePipeline pipeline) {
        var arguments = (recording.Arguments ?? throw new InvalidOperationException(message: "A binned views pass records with no indirect arguments."));
        var plan = m_bins[bin];
        Span<byte> pushed = stackalloc byte[sizeof(uint)];

        for (var job = 0U; (job < plan.Jobs); job++) {
            BinaryPrimitives.WriteUInt32LittleEndian(pushed, (((uint)bin) | ((job * plan.Width) << BinBits)));
            recording.Recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer,
                data: pushed, offset: 0, pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recording.Recorder.DispatchIndirect(commandBufferHandle: recording.CommandBuffer, argumentBufferHandle: arguments.BufferHandle,
                argumentBufferOffset: (ShaderPipelineDispatch.ArgumentBytes * (((ulong)(uint)bin * m_tiles) + job)));
        }
    }

    // The light walk's and composition's default work beside the stage's fixed work: a pixel with no program.
    private static ulong DefaultWork(uint lights) => checked(SdfShadeProgram.EngineWork + (ulong)lights * SdfShadeProgram.DefaultLightWork + SdfShadeProgram.DefaultComposeWork);
    // A pixel's largest work over the programs, never below the default's, and the programs' identities.
    private static (ulong PixelWork, string[] Identities) Price(uint lights, IEnumerable<SdfShadeProgram> programs) {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var pixelWork = DefaultWork(lights: lights);
        foreach (var program in programs) { identities.Add(program.Identity); pixelWork = Math.Max(pixelWork, program.PixelWork(lights)); }
        return (pixelWork, identities.Order(StringComparer.Ordinal).ToArray());
    }
    // A bin whose tiles run no authored program is bounded by the group-count limit alone, as an unsplit extent with no
    // program is (Plan).
    private static Bin BinOf(bool authored, ulong pixelWork, uint tiles) {
        var tileWork = checked(pixelWork * TilePixels);
        if (tileWork > SdfShadeProgram.MaxWork) { throw new InvalidOperationException("An admitted shade tile exceeds MaxWork."); }
        var width = (uint)Math.Min(MaxGroups, (authored ? (SdfShadeProgram.MaxWork / tileWork) : MaxGroups));
        return new(Jobs: ((tiles + width) - 1U) / width, TileWork: tileWork, Width: width);
    }
    private static uint Tiles(uint width, uint height) =>
        checked(((width + (SdfVisibility.GroupEdgePixels - 1U)) / SdfVisibility.GroupEdgePixels) * ((height + (SdfVisibility.GroupEdgePixels - 1U)) / SdfVisibility.GroupEdgePixels));
}
