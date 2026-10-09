using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The per-view transfer pass clearing its deferred receiver diagnostic before the receiver executes.</summary>
    public const string IndirectReceiverReset = "indirectReceiverReset";
    /// <summary>The per-pixel receiver answers views reads (<c>indirect/sdf-indirect-answer.hlsli</c>): status,
    /// replacement and field evaluations, then the replacing answer's source-masked total.</summary>
    public const string IndirectAnswer = "indirectAnswer";
    /// <summary>The receiver answers as the receiver kernel writes them.</summary>
    public const string IndirectAnswerWritten = "indirectAnswerRW";
    /// <summary>The bytes of one pixel's receiver answer: four words.</summary>
    public const uint IndirectAnswerByteLength = (4 * sizeof(uint));
    /// <summary>The selected receiver record after views writes its part beside the receiver's near ray and
    /// replacing sources.</summary>
    public const string IndirectPickShaded = "indirectPickShaded";
    /// <summary>The cleared per-view receiver diagnostic.</summary>
    public const string IndirectDeferredClear = "indirectDeferredClear";
    /// <summary>The per-view deferred and reader counts, copied only after their Views submission.</summary>
    public const string IndirectDeferred = "indirectDeferred";
    /// <summary>The per-view receiver diagnostic's writable shader member.</summary>
    public const string IndirectDeferredWritten = "indirectDeferredRW";
    /// <summary>The selected receiver's header, eight corners, five RGB sources and captured Near direction/predecessor.</summary>
    public const int IndirectPickWords = 72;
    /// <summary>The selected indirect cache tier, zero disabling every cache read.</summary>
    public const string IndirectTier = "indirectTier";
    /// <summary>The enabled source-category bits, shared by the solve and every receiver algorithm.</summary>
    public const string IndirectSources = "indirectSources";
    /// <summary>Origin gains for direct lights, material emission, screen emission and sky exits.</summary>
    public const string IndirectSourceGains = "indirectSourceGains";
    /// <summary>The gain applied once per reflected previous-bank hop.</summary>
    public const string IndirectFeedbackGain = "indirectFeedbackGain";
    /// <summary>Receiver-only tint RGB and intensity.</summary>
    public const string IndirectApply = "indirectApply";
    /// <summary>Receiver-only strength of existing ambient-occlusion attenuation.</summary>
    public const string IndirectContact = "indirectContact";
    /// <summary>The dynamic-body policy; Default resolves against the actual bound cache tier.</summary>
    public const string IndirectBodies = "indirectBodies";
    /// <summary>The per-view counted comparison method: cache, screen-space samples or one-bounce field cones.</summary>
    public const string IndirectMethod = "indirectMethod";
    /// <summary>The geometry epoch carried by valid probe states.</summary>
    public const string IndirectEpoch = "indirectEpoch";
    /// <summary>The complete 64-bit identity of the cache allocation qualifying a receiver certificate.</summary>
    public const string IndirectAllocation = "indirectAllocation";
    /// <summary>The exact transport and slot revision qualifying a receiver certificate.</summary>
    public const string IndirectCertificateRevision = "indirectCertificateRevision";
    /// <summary>Whether Primary rewrites the same submitted surface inputs in the same visibility allocation.</summary>
    public const string PreserveIndirectReceivers = "preserveIndirectReceivers";
    /// <summary>The visibility version after the receiver publishes only its receiver-certificate fields.</summary>
    public const string IndirectVisibility = "indirectVisibility";
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
    /// <summary>The first item of the pass's admitted chunk, counted from its kind's first scheduled entry. Each
    /// workgroup runs the item this value plus its group index names.</summary>
    public const string IndirectItemFirst = "indirectItemFirst";
    /// <summary>The chunk's first admitted unit within its first item: a probe, cell or ray of a transport item, or a
    /// ray of a shaded probe.</summary>
    public const string IndirectUnitFirst = "indirectUnitFirst";
    /// <summary>The chunk's admitted units, counted across item boundaries; units outside the range do not run.</summary>
    public const string IndirectUnitCount = "indirectUnitCount";
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
    /// <summary>Whether this view has fenced the exact current source and may replace a High cache sample.</summary>
    public const string IndirectNearEnabled = "indirectNearEnabled";
    /// <summary>The preceding whole lighting bank's exact stamp for the same source; zero disables Near feedback.</summary>
    public const string IndirectPreviousPublication = "indirectPreviousPublication";
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
    public static IReadOnlyList<ShaderInterfaceMember> WorldIndirectValues => WorldIndirectInterface.Values;

    private static class WorldIndirectInterface {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Values = [
            Value(name: IndirectEpoch, type: ShaderValueType.Uint),
            Value(name: IndirectAllocation, type: ShaderValueType.Uint2),
            Value(name: IndirectCertificateRevision, type: ShaderValueType.Uint),
            Value(name: PreserveIndirectReceivers, type: ShaderValueType.Uint),
            Value(name: IndirectFrame, type: ShaderValueType.Uint),
            Value(name: IndirectReadGeneration, type: ShaderValueType.Uint),
            Value(name: IndirectReadPublication, type: ShaderValueType.Uint),
            Value(name: IndirectReceiverProofs, type: ShaderValueType.Uint),
            Value(name: IndirectNearEnabled, type: ShaderValueType.Uint),
            Value(name: IndirectPreviousPublication, type: ShaderValueType.Uint),
            Value(name: IndirectPickPixel, type: ShaderValueType.Uint4),
        ];
    }

    /// <summary>Gets the indirect kernels' interface members. Every host table is a region.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> IndirectMembers { get; } = [
        .. Values,
        Value(name: IndirectEpoch, type: ShaderValueType.Uint),
        Value(name: IndirectFrame, type: ShaderValueType.Uint),
        Value(name: IndirectPlaceCount, type: ShaderValueType.Uint),
        Value(name: IndirectClassifyCount, type: ShaderValueType.Uint),
        Value(name: IndirectTraceCount, type: ShaderValueType.Uint),
        Value(name: IndirectPhase, type: ShaderValueType.Uint),
        Value(name: IndirectItemFirst, type: ShaderValueType.Uint),
        Value(name: IndirectUnitFirst, type: ShaderValueType.Uint),
        Value(name: IndirectUnitCount, type: ShaderValueType.Uint),
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
    /// <summary>Adds the cache's buffer edge, the receiver pass, the selected-receiver diagnostic and the per-view
    /// deferred completion counter. One transfer reset precedes each receiver execution; the receiver proves every
    /// shaded pixel against the cache, publishes its certificate in the visibility record and its answer, and views reads
    /// both and the cache. Primary has no dependency on mutable cache contents.</summary>
    /// <param name="fragment">The view's selected quality fragment.</param>
    /// <param name="bytes">The residency's cache size.</param>
    /// <returns>The view fragment with its external cache dependency.</returns>
    public static RenderGraphPackageFragment WithIndirect(RenderGraphPackageFragment fragment, ulong bytes) {
        // The certificate version and the answers follow the visibility records' own extent, the render grid in a reduced view.
        var shadow = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.ShadowVisibility));

        return fragment with {
            InputVersions = [.. fragment.InputVersions, IndirectCache],
            Resources = [.. fragment.Resources, new ShaderPipelineResource(Name: IndirectCache,
                Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes, StrideBytes: sizeof(uint), Initialization: ShaderPipelineInitialization.External),
                shadow with { Name = IndirectVisibility, From = Parts.ShadowVisibility },
                shadow with { Name = IndirectAnswer, From = null, StrideBytes = IndirectAnswerByteLength, Retained = true, PreservesPredecessor = false },
                new ShaderPipelineResource(Name: IndirectPick, Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: (IndirectPickWords * sizeof(uint)), StrideBytes: sizeof(uint), Retained: true),
                new ShaderPipelineResource(Name: IndirectPickShaded, Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: (IndirectPickWords * sizeof(uint)), StrideBytes: sizeof(uint), From: IndirectPick, PreservesPredecessor: true),
                new ShaderPipelineResource(Name: IndirectDeferredClear, Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: (2 * sizeof(uint)), StrideBytes: sizeof(uint), Retained: true),
                new ShaderPipelineResource(Name: IndirectDeferred, Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: (2 * sizeof(uint)), StrideBytes: sizeof(uint), From: IndirectDeferredClear, PreservesPredecessor: true)],
            Passes = [.. fragment.Passes.SelectMany(selector: WithReceiver)],
        };
    }

    private static RenderGraphFragmentPass[] WithReceiver(RenderGraphFragmentPass pass) {
        if (pass.Name != Parts.Views) { return [pass]; }
        // The reset precedes the same shading inputs. Its recorder executes whenever this node renders, as the receiver
        // and views must while their mutable residency input is bound; the whole view can still stand when complete.
        // The receiver reads what views reads and writes the certificate, the answers, the near ray and replacing
        // sources of the selected pixel, and the deferred counts; views reads the certified records, the answers, the
        // cache and the counts, which its readback copies, and finishes the selected pixel's record.
        var shaded = pass.Inputs.Select(selector: static input => ((input.Name == Parts.ShadowVisibility) ? new ResourceReference(Name: IndirectVisibility) : input));

        return [
            new RenderGraphFragmentPass(Name: IndirectReceiverReset,
                Inputs: pass.Inputs, InputAccesses: pass.InputAccesses,
                Outputs: [IndirectDeferredClear], OutputAccesses: [RenderGraphPortAccess.TransferWrite], Members: []),
            pass with {
                Name = Parts.Receiver,
                Inputs = [.. pass.Inputs, new ResourceReference(Name: IndirectCache), new ResourceReference(Name: IndirectDeferredClear)],
                InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeReadWrite, RenderGraphPortAccess.ComputeRead],
                Outputs = [IndirectVisibility, IndirectAnswer, IndirectPick, IndirectDeferred],
                OutputAccesses = [RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite],
            },
            pass with {
                Inputs = [.. shaded, new ResourceReference(Name: IndirectAnswer), new ResourceReference(Name: IndirectCache), new ResourceReference(Name: IndirectDeferred)],
                InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead],
                Outputs = [.. pass.Outputs, IndirectPickShaded],
                OutputAccesses = [.. pass.OutputAccesses, RenderGraphPortAccess.ComputeWrite],
            },
        ];
    }
}
