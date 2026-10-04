using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The selected indirect cache tier, zero disabling every cache read.</summary>
    public const string IndirectTier = "indirectTier";
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

    /// <summary>Gets the indirect kernels' interface members. Every host table is a region.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> IndirectMembers { get; } = [
        .. Values,
        Value(name: IndirectEpoch, type: ShaderValueType.Uint),
        Value(name: IndirectFrame, type: ShaderValueType.Uint),
        Value(name: IndirectPlaceCount, type: ShaderValueType.Uint),
        Value(name: IndirectClassifyCount, type: ShaderValueType.Uint),
        Value(name: IndirectTraceCount, type: ShaderValueType.Uint),
        Value(name: IndirectPhase, type: ShaderValueType.Uint),
        Read(element: ShaderValueType.Int4, name: IndirectBricks),
        Read(element: ShaderValueType.Uint4, name: IndirectUpdates),
        Read(element: ShaderValueType.Float4, name: IndirectDirections),
        Read(element: ShaderValueType.Uint, name: IndirectTraceStates),
        Written(element: ShaderValueType.Uint, name: IndirectCacheWritten),
        ShaderWorkCounters.BufferMember,
        .. Tables,
    ];

    /// <summary>Creates the three ordered dispatches over one residency-owned buffer.</summary>
    /// <param name="bytes">The cache layout's exact allocation.</param>
    /// <returns>The package fragment.</returns>
    public static RenderGraphPackageFragment IndirectFragment(ulong bytes) => new(
        InputVersions: [], OutputVersions: [IndirectCache],
        Passes: [
            Pass(name: IndirectPlace, outputs: ["placed"]) with { Members = IndirectMembers },
            Pass(name: IndirectClassify, outputs: ["partitioned"]) with { Members = IndirectMembers },
            Pass(name: IndirectTrace, outputs: [IndirectCache]) with { Members = IndirectMembers },
        ],
        Resources: [
            new(Name: "placed", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: "partitioned", Kind: ShaderPipelineResourceKind.Buffer, From: "placed", SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, From: "partitioned", SizeBytes: bytes, StrideBytes: sizeof(uint)),
        ]);
    /// <summary>Adds the cache's buffer edge to a view. Only the primary and debug shading read it.</summary>
    /// <param name="fragment">The view's selected quality fragment.</param>
    /// <param name="bytes">The residency's cache size.</param>
    /// <returns>The view fragment with its external cache dependency.</returns>
    public static RenderGraphPackageFragment WithIndirect(RenderGraphPackageFragment fragment, ulong bytes) => fragment with {
        InputVersions = [.. fragment.InputVersions, IndirectCache],
        Resources = [.. fragment.Resources, new ShaderPipelineResource(Name: IndirectCache,
            Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes, StrideBytes: sizeof(uint), Initialization: ShaderPipelineInitialization.External)],
        Passes = [.. fragment.Passes.Select(selector: static pass => ((pass.Name is Parts.Primary or Parts.Views) ? pass with {
            Inputs = [.. pass.Inputs, new ResourceReference(Name: IndirectCache)],
            InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
        } : pass))],
    };
}
