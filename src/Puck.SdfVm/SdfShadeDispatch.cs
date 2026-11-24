using System.Buffers.Binary;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Maths;
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

    // The key that decides whether an existing plan stands for the next call to For/ForEngine without replanning. A plan
    // prices the scene's shading programs alone, so it keys on the scene's appearance, which holds them, never on the
    // program: a plan a view keeps while it stops rendering must not keep a replaced program's words alive.
    private readonly struct PlanKey(SdfAppearance? scene, SdfShadeProgram? fallback, bool enabled, uint width, uint height, uint lights, bool glass, bool binned, SdfShadeTier tier) : IEquatable<PlanKey> {
        public readonly SdfAppearance? Scene = scene;
        public readonly SdfShadeProgram? Fallback = fallback;
        public readonly uint Width = width;
        public readonly uint Height = height;
        public readonly uint Lights = lights;
        public readonly SdfShadeTier Tier = tier;
        public readonly bool Enabled = enabled;
        public readonly bool Glass = glass;
        public readonly bool Binned = binned;

        public bool Equals(PlanKey other) =>
            ReferenceEquals(objA: Scene, objB: other.Scene) &&
            ReferenceEquals(objA: Fallback, objB: other.Fallback) &&
            (Width == other.Width) &&
            (Height == other.Height) &&
            (Lights == other.Lights) &&
            (Tier == other.Tier) &&
            (Enabled == other.Enabled) &&
            (Glass == other.Glass) &&
            (Binned == other.Binned);
    }

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
    private PlanKey m_key;
    private bool m_hasKey;

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

    /// <summary>The kernels whose work the plan bounds: <see cref="SdfShadeTier.Neutral"/> for a plan that prices no program,
    /// <see cref="SdfShadeTier.Straight"/> for one priced at the specialized kernels' straight-line arms
    /// (<see cref="SdfShadeStraight.Costs"/>), <see cref="SdfShadeTier.Interpreted"/> at the interpreter. A plan records only
    /// with a tier no later than its own (<c>SdfShadeSlots.RecordSelection</c>).</summary>
    public SdfShadeTier Tier { get; private set; }

    /// <summary>Returns a frame's plan for a pass of the given extent, reusing <paramref name="previous"/> without
    /// allocating whenever every input in the key still holds. Prices each pixel's walk at the frame's light-walk
    /// maximum (<see cref="SdfLights.Count"/> plus active screens), which every view's light mask
    /// (<see cref="SdfLightSelection.Select"/>) and every tile's walk narrows and never widens, so camera motion across a
    /// light's range never replans.</summary>
    /// <param name="previous">The prior plan to reuse on a key match, or <see langword="null"/>.</param>
    /// <param name="frame">The frame being shaded.</param>
    /// <param name="width">The extent's width, in pixels.</param>
    /// <param name="height">The extent's height, in pixels.</param>
    /// <param name="screens">The bound screen count.</param>
    /// <param name="glass">Whether the pass shades glass, pricing both interfaces.</param>
    /// <param name="tier">The kernel tier the residency records the frame with.</param>
    /// <param name="binned">Whether to produce a binned plan when the tier runs programs.</param>
    /// <returns>The standing or newly built plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static SdfShadeDispatch For(SdfShadeDispatch? previous, SdfFrame frame, uint width, uint height, uint screens, bool glass, SdfShadeTier tier, bool binned = false) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var lights = checked(((uint)frame.Lights.Count) + (frame.DisableScreenLights ? 0u : screens));
        var key = new PlanKey(
            binned: binned,
            enabled: frame.ShadePrograms,
            fallback: frame.ShadeDefault,
            glass: glass,
            height: height,
            lights: lights,
            scene: frame.Program.Appearance,
            tier: tier,
            width: width
        );

        if ((previous is not null) && previous.m_hasKey && previous.m_key.Equals(other: key)) {
            return previous;
        }

        var usePrograms = (frame.ShadePrograms && (tier != SdfShadeTier.Neutral));
        var appearancePrograms = (usePrograms ? frame.Program.Appearance.ShadingPrograms : []);
        var fallback = (usePrograms ? frame.ShadeDefault : null);
        var hasPrograms = ((appearancePrograms.Count != 0) || (fallback is not null));
        var straight = (tier == SdfShadeTier.Straight);
        var (pixelWork, identities) = PriceList(appearancePrograms: appearancePrograms, fallback: fallback, lights: lights, straight: straight);
        var plan = ((binned && hasPrograms)
            ? BuildBins(height: height, identities: identities, lights: lights, shadedWork: pixelWork, straight: straight, width: width)
            : BuildUnsplit(glass: glass, height: height, identities: identities, pixelWork: pixelWork, straight: straight, width: width));

        plan.m_key = key;
        plan.m_hasKey = true;

        return plan;
    }
    // The plan of an extent's pass that runs no program (a binned view's filter): one dispatch of its groups.
    internal static SdfShadeDispatch ForEngine(SdfShadeDispatch? previous, uint width, uint height) {
        var key = new PlanKey(
            binned: false,
            enabled: false,
            fallback: null,
            glass: false,
            height: height,
            lights: 0u,
            scene: null,
            tier: SdfShadeTier.Neutral,
            width: width
        );

        if ((previous is not null) && previous.m_hasKey && previous.m_key.Equals(other: key)) {
            return previous;
        }

        var plan = BuildUnsplit(glass: false, height: height, identities: [], pixelWork: DefaultWork(lights: 0u), straight: false, width: width);

        plan.m_key = key;
        plan.m_hasKey = true;

        return plan;
    }

    /// <summary>Plans an unsplit extent, conservatively pricing every padded pixel at its largest admitted program.
    /// Glass prices both interfaces. Splitting within a group is unrepresentable. An extent with no program runs only the
    /// engine's fixed stage, which <see cref="SdfShadeProgram.MaxWork"/> does not bound (it bounds what an authored program
    /// adds), so it is one dispatch within the backend's group-count limit, as every engine pass is.</summary>
    /// <param name="width">The extent's width, in pixels.</param>
    /// <param name="height">The extent's height, in pixels.</param>
    /// <param name="programs">The admitted programs the frame runs.</param>
    /// <param name="lights">The lights each pixel's walk may visit, at most <see cref="SdfShadeProgram.MaxLights"/>.</param>
    /// <param name="glass">Whether the pass shades glass, pricing both interfaces.</param>
    /// <param name="straight">Whether a specialized selection records the plan, pricing its straight-line arms.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="programs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lights"/> exceeds
    /// <see cref="SdfShadeProgram.MaxLights"/>.</exception>
    public static SdfShadeDispatch Plan(uint width, uint height, IEnumerable<SdfShadeProgram> programs, uint lights, bool glass, bool straight = false) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, SdfShadeProgram.MaxLights);
        var (pixelWork, identities) = Price(lights: lights, programs: programs, straight: straight);

        return BuildUnsplit(glass: glass, height: height, identities: identities, pixelWork: pixelWork, straight: straight, width: width);
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
    /// <param name="straight">Whether a specialized selection records the plan, pricing its straight-line arms.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="programs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lights"/> exceeds
    /// <see cref="SdfShadeProgram.MaxLights"/>.</exception>
    public static SdfShadeDispatch PlanBins(uint width, uint height, IEnumerable<SdfShadeProgram> programs, uint lights, bool straight = false) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, SdfShadeProgram.MaxLights);
        var (shadedWork, identities) = Price(lights: lights, programs: programs, straight: straight);

        return BuildBins(height: height, identities: identities, lights: lights, shadedWork: shadedWork, straight: straight, width: width);
    }
    private static SdfShadeDispatch BuildUnsplit(uint width, uint height, ulong pixelWork, string[] identities, bool glass, bool straight) {
        var groupWork = checked(pixelWork * TilePixels * (glass ? 2UL : 1UL));
        if (groupWork > SdfShadeProgram.MaxWork) { throw new InvalidOperationException("An admitted shade group exceeds MaxWork."); }
        var total = Tiles(width: width, height: height);
        var capacity = (uint)Math.Min(MaxGroups, ((identities.Length == 0) ? MaxGroups : (SdfShadeProgram.MaxWork / groupWork)));
        var jobCount = ((total == 0u) ? 0u : total.CeilingDivide(divisor: capacity));
        var jobs = ((jobCount == 0u) ? [] : new Job[jobCount]);

        for (uint slot = 0u, first = 0u; (slot < jobCount); slot++, first += capacity) {
            var count = Math.Min(capacity, total - first);
            jobs[slot] = new(first, count, checked(count * groupWork));
        }

        return new(jobs, [], identities, total) { Tier = TierOf(identities: identities, straight: straight) };
    }
    private static SdfShadeDispatch BuildBins(uint width, uint height, ulong shadedWork, string[] identities, uint lights, bool straight) {
        var tiles = Tiles(width: width, height: height);
        Bin[] bins = [BinOf(authored: false, pixelWork: DefaultWork(lights: lights), tiles: tiles), BinOf(authored: true, pixelWork: shadedWork, tiles: tiles)];

        return new([], bins, identities, tiles) { Tier = TierOf(identities: identities, straight: straight) };
    }
    private static SdfShadeTier TierOf(string[] identities, bool straight) =>
        ((identities.Length == 0) ? SdfShadeTier.Neutral : (straight ? SdfShadeTier.Straight : SdfShadeTier.Interpreted));

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
    // A pixel's largest work over the programs, never below the default's, and the sorted distinct identities.
    private static (ulong PixelWork, string[] Identities) Price(uint lights, IEnumerable<SdfShadeProgram> programs, bool straight) {
        if (programs is IReadOnlyList<SdfShadeProgram> list) {
            return PriceList(appearancePrograms: list, fallback: null, lights: lights, straight: straight);
        }

        var pixelWork = DefaultWork(lights: lights);
        var collected = new List<string>();

        foreach (var program in programs) {
            collected.Add(item: program.Identity);
            pixelWork = Math.Max(pixelWork, program.PixelWork(lights: lights, straight: straight));
        }

        return (pixelWork, DistinctSorted(identities: ((collected.Count == 0) ? [] : collected.ToArray())));
    }
    private static (ulong PixelWork, string[] Identities) PriceList(IReadOnlyList<SdfShadeProgram> appearancePrograms, SdfShadeProgram? fallback, uint lights, bool straight) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, SdfShadeProgram.MaxLights);

        var count = (appearancePrograms.Count + ((fallback is not null) ? 1 : 0));
        var pixelWork = DefaultWork(lights: lights);

        if (count == 0) {
            return (pixelWork, []);
        }
        if (count == 1) {
            var single = ((appearancePrograms.Count == 1) ? appearancePrograms[0] : fallback!);

            return (Math.Max(pixelWork, single.PixelWork(lights: lights, straight: straight)), [single.Identity]);
        }

        var identities = new string[count];

        for (var index = 0; (index < appearancePrograms.Count); index++) {
            var program = appearancePrograms[index];

            identities[index] = program.Identity;
            pixelWork = Math.Max(pixelWork, program.PixelWork(lights: lights, straight: straight));
        }
        if (fallback is not null) {
            identities[appearancePrograms.Count] = fallback.Identity;
            pixelWork = Math.Max(pixelWork, fallback.PixelWork(lights: lights, straight: straight));
        }

        return (pixelWork, DistinctSorted(identities: identities));
    }
    private static string[] DistinctSorted(string[] identities) {
        if (identities.Length < 2) {
            return identities;
        }

        Array.Sort(array: identities, comparer: StringComparer.Ordinal);
        var unique = SpanDistinct.Sorted(sorted: identities.AsSpan());

        if (unique != identities.Length) {
            Array.Resize(array: ref identities, newSize: unique);
        }

        return identities;
    }
    // A bin whose tiles run no authored program is bounded by the group-count limit alone, as an unsplit extent with no
    // program is (Plan).
    private static Bin BinOf(bool authored, ulong pixelWork, uint tiles) {
        var tileWork = checked(pixelWork * TilePixels);
        if (tileWork > SdfShadeProgram.MaxWork) { throw new InvalidOperationException("An admitted shade tile exceeds MaxWork."); }
        var width = (uint)Math.Min(MaxGroups, (authored ? (SdfShadeProgram.MaxWork / tileWork) : MaxGroups));
        return new(Jobs: tiles.CeilingDivide(divisor: width), TileWork: tileWork, Width: width);
    }
    private static uint Tiles(uint width, uint height) =>
        checked(width.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels) * height.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels));
}
