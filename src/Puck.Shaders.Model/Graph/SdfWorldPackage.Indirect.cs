using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The selected receiver's header, eight corner records and five independent RGB source records.</summary>
    public const int IndirectPickWords = 68;
    /// <summary>The selected indirect cache tier, zero disabling every cache read.</summary>
    public const string IndirectTier = "indirectTier";
    /// <summary>The per-view counted comparison method: cache, screen-space samples or one-bounce field cones.</summary>
    public const string IndirectMethod = "indirectMethod";
    /// <summary>The geometry epoch carried by valid probe states.</summary>
    public const string IndirectEpoch = "indirectEpoch";
    /// <summary>The submitted cache update sequence, for reading only completed proof publications.</summary>
    public const string IndirectFrame = "indirectFrame";
    /// <summary>The scheduled placements at the front of the update region.</summary>
    public const string IndirectPlaceCount = "indirectPlaceCount";
    /// <summary>The number of classification entries at the front of the update region.</summary>
    public const string IndirectClassifyCount = "indirectClassifyCount";
    /// <summary>The scheduled ray strata following the classification entries.</summary>
    public const string IndirectTraceCount = "indirectTraceCount";
    /// <summary>Zero places probes; one partitions their cells.</summary>
    public const string IndirectPhase = "indirectPhase";
    /// <summary>The probes in the current finite shade batch.</summary>
    public const string IndirectShadeCount = "indirectShadeCount";
    /// <summary>The completed generation the feedback sweep reads.</summary>
    public const string IndirectReadGeneration = "indirectReadGeneration";
    /// <summary>The generation this sweep writes, coarser levels first.</summary>
    public const string IndirectWriteGeneration = "indirectWriteGeneration";
    /// <summary>The exact preceding complete sweep's probe stamp; zero disables feedback.</summary>
    public const string IndirectReadPublication = "indirectReadPublication";
    /// <summary>The exact stamp this sweep gives each finished probe.</summary>
    public const string IndirectWritePublication = "indirectWritePublication";
    /// <summary>The finite solve's feedback gain, zero during its direct sweep.</summary>
    public const string IndirectFeedback = "indirectFeedback";
    /// <summary>The maximum new receiver proofs admitted by all views this frame; zero freezes proof writes.</summary>
    public const string IndirectReceiverProofs = "indirectReceiverProofs";
    /// <summary>The selected receiver diagnostic pixel and enabled flag.</summary>
    public const string IndirectPickPixel = "indirectPickPixel";
    /// <summary>The view-owned selected receiver diagnostic record.</summary>
    public const string IndirectPick = "indirectPick";
    /// <summary>The selected receiver record as the views kernel writes it.</summary>
    public const string IndirectPickWritten = "indirectPickRW";
    /// <summary>The residency's published buffer version.</summary>
    public const string IndirectCache = "indirectCache";
    /// <summary>The cache as its kernels write it.</summary>
    public const string IndirectCacheWritten = "indirectCacheRW";
    /// <summary>The slot-indexed brick coordinates and level.</summary>
    public const string IndirectBricks = "indirectBricks";
    /// <summary>The host scheduler's classification and trace entries.</summary>
    public const string IndirectUpdates = "indirectUpdates";
    /// <summary>The host-computed spherical Fibonacci directions.</summary>
    public const string IndirectDirections = "indirectDirections";
    /// <summary>The immutable completed-stratum masks from earlier submissions.</summary>
    public const string IndirectTraceStates = "indirectTraceStates";
    /// <summary>The placement dispatch of classification.</summary>
    public const string IndirectPlace = "place";
    /// <summary>The partition dispatch of classification.</summary>
    public const string IndirectClassify = "classify";
    /// <summary>The transport dispatch.</summary>
    public const string IndirectTrace = "trace";
    /// <summary>The finite lighting solve's dispatch.</summary>
    public const string IndirectShade = "shade";

    /// <summary>Gets the visible cache publication and receiver allowance carried only by world passes.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> WorldIndirectValues { get; } = [
        Value(name: IndirectEpoch, type: ShaderValueType.Uint),
        Value(name: IndirectFrame, type: ShaderValueType.Uint),
        Value(name: IndirectReadGeneration, type: ShaderValueType.Uint),
        Value(name: IndirectReadPublication, type: ShaderValueType.Uint),
        Value(name: IndirectReceiverProofs, type: ShaderValueType.Uint),
        Value(name: IndirectPickPixel, type: ShaderValueType.Uint4),
    ];

    /// <summary>Gets the indirect kernels' interface members. Every host table is a region.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> IndirectMembers { get; } = [
        .. Values,
        Value(name: IndirectEpoch, type: ShaderValueType.Uint),
        Value(name: IndirectFrame, type: ShaderValueType.Uint),
        Value(name: IndirectPlaceCount, type: ShaderValueType.Uint),
        Value(name: IndirectClassifyCount, type: ShaderValueType.Uint),
        Value(name: IndirectTraceCount, type: ShaderValueType.Uint),
        Value(name: IndirectPhase, type: ShaderValueType.Uint),
        Value(name: IndirectShadeCount, type: ShaderValueType.Uint),
        Value(name: IndirectReadGeneration, type: ShaderValueType.Uint),
        Value(name: IndirectWriteGeneration, type: ShaderValueType.Uint),
        Value(name: IndirectReadPublication, type: ShaderValueType.Uint),
        Value(name: IndirectWritePublication, type: ShaderValueType.Uint),
        Value(name: IndirectFeedback, type: ShaderValueType.Float),
        Read(element: ShaderValueType.Int4, name: IndirectBricks),
        Read(element: ShaderValueType.Uint4, name: IndirectUpdates),
        Read(element: ShaderValueType.Float4, name: IndirectDirections),
        Read(element: ShaderValueType.Uint, name: IndirectTraceStates),
        Written(element: ShaderValueType.Uint, name: IndirectCacheWritten),
        ShaderWorkCounters.BufferMember,
        Read(element: ShaderValueType.Float, name: IndirectLightDepth),
        .. Tables,
    ];

    /// <summary>Creates placement, partition, transport and finite lighting dispatches over one residency-owned buffer.</summary>
    /// <param name="bytes">The cache layout's exact allocation.</param>
    /// <returns>The package fragment.</returns>
    public static RenderGraphPackageFragment IndirectFragment(ulong bytes) => new(
        InputVersions: [], OutputVersions: [IndirectCache],
        Passes: [
            Pass(name: IndirectPlace, outputs: ["placed"]) with { Members = IndirectMembers },
            Pass(name: IndirectClassify, outputs: ["partitioned"]) with { Members = IndirectMembers },
            Pass(name: IndirectTrace, outputs: ["traced"]) with { Members = IndirectMembers },
            Pass(name: IndirectShade, outputs: [IndirectCache]) with { Members = IndirectMembers },
        ],
        Resources: [
            new(Name: "placed", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: "partitioned", Kind: ShaderPipelineResourceKind.Buffer, From: "placed", SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: "traced", Kind: ShaderPipelineResourceKind.Buffer, From: "partitioned", SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, From: "traced", SizeBytes: bytes, StrideBytes: sizeof(uint)),
        ]);
    /// <summary>Adds the cache's buffer edge to a view. Primary reads it; views also publish receiver proofs.</summary>
    /// <param name="fragment">The view's selected quality fragment.</param>
    /// <param name="bytes">The residency's cache size.</param>
    /// <returns>The view fragment with its external cache dependency.</returns>
    public static RenderGraphPackageFragment WithIndirect(RenderGraphPackageFragment fragment, ulong bytes) => fragment with {
        InputVersions = [.. fragment.InputVersions, IndirectCache],
        Resources = [.. fragment.Resources, new ShaderPipelineResource(Name: IndirectCache,
            Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes, StrideBytes: sizeof(uint), Initialization: ShaderPipelineInitialization.External),
            new ShaderPipelineResource(Name: IndirectPick, Kind: ShaderPipelineResourceKind.Buffer,
                SizeBytes: IndirectPickWords * sizeof(uint), StrideBytes: sizeof(uint))],
        Passes = [.. fragment.Passes.Select(selector: static pass => ((pass.Name is Parts.Primary or Parts.Views) ? pass with {
            Inputs = [.. pass.Inputs, new ResourceReference(Name: IndirectCache)],
            InputAccesses = [.. pass.InputAccesses, pass.Name == Parts.Views ? RenderGraphPortAccess.ComputeReadWrite : RenderGraphPortAccess.ComputeRead],
            Outputs = pass.Name == Parts.Views ? [.. pass.Outputs, IndirectPick] : pass.Outputs,
            OutputAccesses = pass.Name == Parts.Views ? [.. pass.OutputAccesses, RenderGraphPortAccess.ComputeWrite] : pass.OutputAccesses,
        } : pass))],
    };
}
