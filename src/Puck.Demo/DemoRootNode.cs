using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Hosting;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// The demo's data-driven root-node registrar + headless utilities. Every run — whether from <c>--run</c> or
/// synthesized from the legacy flags (<see cref="DemoRunDocuments.Synthesize"/>) — arrives here as a
/// <see cref="PuckRunDocument"/>: <see cref="RegisterRunDocument"/> installs its fuzzing/validation gate or its
/// composition-graph producer. Kept out of <c>Program</c> so the entry point stays loosely coupled.
/// </summary>
internal static class DemoRootNode {
    /// <summary>Whether a fuzzing run names a seed RANGE with no single-seed override — which must go through the
    /// process-isolated <c>tools fuzz</c> loop rather than a single in-process <c>--run</c> (a malformed program can TDR
    /// the GPU, so each seed runs in its own process). Logs the redirect when true.</summary>
    /// <param name="document">The validated run document.</param>
    /// <param name="fuzzSeed">The CLI <c>--fuzz-seed</c> (negative when unset).</param>
    /// <returns><see langword="true"/> when the run cannot proceed in-process.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool RequiresExternalFuzzLoop(PuckRunDocument document, int fuzzSeed) {
        ArgumentNullException.ThrowIfNull(document);

        if ((document.Fuzzing is { Seed: null, SeedRange: not null }) && (fuzzSeed < 0)) {
            Console.Error.WriteLine(value: "[run] a fuzzing.seedRange run must go through `tools fuzz -Run`; --run runs ONE seed — pass --fuzz-seed N, or set fuzzing.seed.");

            return true;
        }

        return false;
    }

    /// <summary>Whether a run document's graph CAN host on Direct3D 12 on this OS — gated by the graph's specific
    /// requirement so the window host never diverges from the node's device. The ray-query world hosting on Direct3D
    /// 12 needs DXR 1.1 (Windows 10 1809 / 17763); the compute world and the showcase host need only Direct3D 12
    /// (Windows 10 / 10240). When false, a <c>host.backend:"directx"</c> request degrades to a Vulkan host.</summary>
    /// <param name="document">The validated run document.</param>
    /// <returns><see langword="true"/> when the document's graph can host on Direct3D 12.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool HostDirectXAvailable(PuckRunDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Graph is RtNode) {
            // Without ray query the rt world falls back to the Vulkan BEAM (SPIR-V), which can only host on Vulkan; so
            // a Direct3D 12 host is only viable when ray query is on AND the OS provides DXR 1.1 (Windows 10 1809).
            return !RayQueryDisabled(host: document.Host) && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
        }

        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);
    }

    // Whether the ray-query path is disabled for this run: the document's host.rayQuery field if set, else the ambient
    // PUCK_RAY_QUERY env var (HostSettings.Apply has not yet written the field to the env at host-selection time).
    private static bool RayQueryDisabled(HostDocument? host) {
        if (host?.RayQuery is bool rayQuery) {
            return !rayQuery;
        }

        return string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_RAY_QUERY"), "0", StringComparison.Ordinal);
    }

    /// <summary>Loads and validates a data-driven run document (the <c>--run</c> path) up front, so its host section
    /// can drive the window/launcher/presentation before the host is built. A bad document is reported here as an
    /// actionable, source-attributed error (and <see langword="null"/> returned) rather than a downstream GPU crash.</summary>
    /// <param name="runPath">The path to the run document.</param>
    /// <returns>The validated document, or <see langword="null"/> when it could not be loaded/validated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runPath"/> is <see langword="null"/>.</exception>
    public static PuckRunDocument? LoadRunDocument(string runPath) {
        ArgumentNullException.ThrowIfNull(runPath);

        try {
            var document = RunDocument.Load(path: runPath);
            var summary = ((document.Validation is ValidationDocument validation) ? $"validation gate '{validation.Gate}'" : $"{document.Graph?.GetType().Name} graph");

            Console.Error.WriteLine(value: $"[run] '{runPath}': {summary}, {(document.Scene?.Objects?.Count ?? 0)} object(s), {(document.Viewports?.Count ?? 0)} viewport(s)");

            return document;
        } catch (Exception exception) when ((exception is RunDocumentValidationException) || (exception is JsonException) || (exception is IOException) || (exception is NotSupportedException) || (exception is UnauthorizedAccessException) || (exception is ArgumentException)) {
            Console.Error.WriteLine(value: $"[run] failed to load '{runPath}':{Environment.NewLine}{exception.Message}");

            return null;
        }
    }

    /// <summary>Registers the root render node from an already-loaded run document: its graph selects the producer and
    /// its scene + viewports drive the injected frame source.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="document">The validated run document.</param>
    /// <param name="capturePath">An optional CLI capture path; overrides the graph node's own capture field when set.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (drives per-node host placement).</param>
    /// <param name="width">The resolved render width in pixels (from <c>host.size</c>).</param>
    /// <param name="height">The resolved render height in pixels (from <c>host.size</c>).</param>
    /// <param name="fuzzSeed">The CLI <c>--fuzz-seed</c> (negative when unset); for a fuzzing run it overrides the
    /// document seed — the seed-range <c>tools fuzz</c> loop passes one seed per child process.</param>
    /// <returns>The shared parity result a validation/fuzzing gate fills (its <c>ExitCode</c> is propagated by the entry
    /// point); for a live render run it is unused and left at its default.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="document"/> is <see langword="null"/>.</exception>
    public static ParityResult RegisterRunDocument(IServiceCollection services, PuckRunDocument document, string? capturePath, bool hostsOnDirectX, uint width, uint height, int fuzzSeed) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(document);

        var result = new ParityResult();

        if (document.Fuzzing is FuzzingDocument fuzzing) {
            // One in-process differential-fuzzing iteration: a fuzz-generated scene (within the document's bounds)
            // rendered on both backends and diffed (the WorldFuzz threshold set). The CLI --fuzz-seed overrides the
            // document seed (the seed-range loop's child passes one seed per process); a seedRange with no override is
            // rejected up front by the entry point.
            WarnOffscreenNoOp(capturePath: capturePath, gate: "fuzzing", height: height, width: width);

            var seed = ((fuzzSeed >= 0) ? fuzzSeed : (fuzzing.Seed ?? 0));

            services.AddSingleton<IRenderNode>(implementationFactory: sp => new WorldParityNode(
                bounds: fuzzing.ResolveBounds(),
                fuzzSeed: seed,
                result: result,
                serviceProvider: sp
            ));

            return result;
        }

        // --fuzz-seed pairs with a fuzzing run (a generated scene); it is meaningless once we know this is a validation
        // gate or a render graph. Surface that rather than silently dropping it.
        if (fuzzSeed >= 0) {
            Console.Error.WriteLine(value: "[run] --fuzz-seed applies only to a fuzzing run (a generated scene); it is ignored for a validation gate / render graph — use a 'fuzzing' section or `tools fuzz`.");
        }

        if (document.Validation is ValidationDocument validation) {
            // A validation run installs a cross-backend gate (the composition graph is ignored) and propagates its exit
            // code; the gate renders offscreen on Vulkan at a fixed 960x600, so capture/size do not apply.
            WarnOffscreenNoOp(capturePath: capturePath, gate: "validation", height: height, width: width);

            services.AddSingleton<IRenderNode>(implementationFactory: sp => CreateValidationNode(document: document, result: result, serviceProvider: sp, validation: validation));

            return result;
        }

        services.AddSingleton<IRenderNode>(implementationFactory: sp => GraphBuilder.Build(
            capturePath: capturePath,
            document: document,
            height: height,
            hostsOnDirectX: hostsOnDirectX,
            serviceProvider: sp,
            width: width
        ));

        return result;
    }

    // A validation/fuzzing gate renders OFFSCREEN at a fixed 960x600 and writes its own artifacts, so a CLI --capture
    // path and a host.size are no-ops for it; surface that rather than silently dropping them.
    private static void WarnOffscreenNoOp(string gate, string? capturePath, uint width, uint height) {
        if ((capturePath is not null) || (width != 960) || (height != 600)) {
            Console.Error.WriteLine(value: $"[run] a {gate} gate renders offscreen at a fixed 960x600 and writes its own artifacts; --capture and host.size are ignored.");
        }
    }

    // Maps a validation gate name to its node. The 'world' gate is data-driven: it renders THIS document's scene on
    // both backends via the injected frame-source factory, with optional threshold + artifact-dir overrides. The other
    // four gates are self-contained smoke tests. (Fuzzing a GENERATED scene is the separate `fuzzing` section.)
    private static IRenderNode CreateValidationNode(ValidationDocument validation, PuckRunDocument document, ParityResult result, IServiceProvider serviceProvider) {
        return validation.Gate.ToLowerInvariant() switch {
            "parity" => new ParityValidationNode(result: result, serviceProvider: serviceProvider),
            "export" => new ExportRoundTripNode(result: result, serviceProvider: serviceProvider),
            "compute" => new ComputeValidationNode(result: result, serviceProvider: serviceProvider),
            "reverse" => new CrossShareReverseNode(result: result, serviceProvider: serviceProvider),
            "indirect" => new IndirectDispatchValidationNode(result: result, serviceProvider: serviceProvider),
            "resample" => new ResampleValidationNode(result: result, serviceProvider: serviceProvider),
            "viewports" => new ViewportParityNode(result: result, serviceProvider: serviceProvider),
            "pixelate" => new PixelateParityNode(result: result, serviceProvider: serviceProvider),
            "capture" => new CaptureValidationNode(result: result, serviceProvider: serviceProvider),
            "camera" => new CameraValidationNode(result: result, serviceProvider: serviceProvider),
            "camera-live" => new CameraLiveProbeNode(result: result, serviceProvider: serviceProvider),
            "mini-action" => new MiniAction.MiniActionDeterminismNode(result: result),
            "determinism" => new Replay.DeterminismGateNode(result: result),
            "cli-determinism" => new Replay.CliDeterminismGateNode(result: result),
            "world" => new WorldParityNode(
                artifactDir: validation.ArtifactDir,
                frameSourceFactory: () => RunDocument.CreateFrameSource(document: document),
                result: result,
                serviceProvider: serviceProvider,
                thresholds: BuildThresholds(thresholds: validation.Thresholds),
                withChild: validation.Child
            ),
            _ => throw new NotSupportedException(message: $"Unsupported validation gate '{validation.Gate}'."),
        };
    }

    // Builds a ParityThresholdSet from the world-gate's document overrides, filling any omitted field from the
    // calibrated WorldComposite baseline. Returns null when there is no override (the gate keeps its default set).
    private static ParityThresholdSet? BuildThresholds(ParityThresholdsDocument? thresholds) {
        if (thresholds is null) {
            return null;
        }

        var baseline = ParityThresholds.WorldComposite;

        return new ParityThresholdSet {
            MaxChannelDelta = (thresholds.MaxChannelDelta ?? baseline.MaxChannelDelta),
            MaxMeanAbsError = (thresholds.MaxMeanAbsError ?? baseline.MaxMeanAbsError),
            MaxPercentDiffering = (thresholds.MaxPercentDiffering ?? baseline.MaxPercentDiffering),
            MinIsolatedFraction = (thresholds.MinIsolatedFraction ?? baseline.MinIsolatedFraction),
            MinUnitDeltaFraction = (thresholds.MinUnitDeltaFraction ?? baseline.MinUnitDeltaFraction),
        };
    }

    /// <summary>Writes the run-document JSON Schema to a path (the headless <c>--emit-schema</c> utility): the schema
    /// is exported from the SAME source-gen options that read documents, so it cannot drift from the model.</summary>
    /// <param name="path">The output path; parent directories are created.</param>
    /// <returns>0 on success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public static int EmitSchema(string path) {
        ArgumentNullException.ThrowIfNull(path);

        var schema = RunDocumentSchema.Export();
        var directory = Path.GetDirectoryName(path: path);

        if (!string.IsNullOrEmpty(directory)) {
            _ = Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(contents: schema, path: path);
        Console.WriteLine(value: $"[emit-schema] wrote the run-document schema ({schema.Length} chars) to '{path}'.");

        return 0;
    }

    /// <summary>Asserts a run document's JSON-built scene program reproduces the hand-authored reference scene
    /// (<see cref="DemoRunDocuments.BuildReferenceScene"/>) WORD-FOR-WORD (the headless <c>--check-run</c> determinism
    /// gate): the independent oracle that the canonical data scene can never silently drift from the intended geometry.</summary>
    /// <param name="runPath">The run document to check.</param>
    /// <returns>0 on a bit-exact match, 1 on a word mismatch, 2 when the document could not be loaded/validated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runPath"/> is <see langword="null"/>.</exception>
    public static int CheckRunDocument(string runPath) {
        ArgumentNullException.ThrowIfNull(runPath);

        PuckRunDocument document;

        try {
            document = RunDocument.Load(path: runPath);
        } catch (Exception exception) when ((exception is RunDocumentValidationException) || (exception is JsonException) || (exception is IOException) || (exception is NotSupportedException) || (exception is UnauthorizedAccessException) || (exception is ArgumentException)) {
            Console.Error.WriteLine(value: $"[check-run] failed to load '{runPath}':{Environment.NewLine}{exception.Message}");

            return 2;
        }

        var jsonProgram = SceneBuilder.Build(scene: document.Scene);
        var referenceProgram = DemoRunDocuments.BuildReferenceScene();
        var jsonWords = jsonProgram.Words;
        var referenceWords = referenceProgram.Words;

        if (jsonWords.SequenceEqual(other: referenceWords)) {
            Console.WriteLine(value: $"[check-run] OK: '{runPath}' built {jsonWords.Length} words, bit-identical to the reference scene.");

            return 0;
        }

        Console.Error.WriteLine(value: $"[check-run] MISMATCH: '{runPath}' built {jsonWords.Length} words; the reference scene has {referenceWords.Length}.");

        var limit = Math.Min(val1: jsonWords.Length, val2: referenceWords.Length);

        for (var index = 0; (index < limit); index++) {
            if (jsonWords[index] != referenceWords[index]) {
                Console.Error.WriteLine(value: $"  first divergent word[{index}]: run=0x{jsonWords[index]:x8} reference=0x{referenceWords[index]:x8}");

                break;
            }
        }

        return 1;
    }

    // The ray-query world: a per-frame TLAS over one unit-AABB instance per SDF primitive, ray-traced by an inline
    // RayQuery compute kernel that compiles to BOTH backends (DXIL for Direct3D 12 DXR 1.1, SPIR-V for Vulkan).
    // --backend directx hosts it same-device on the Direct3D 12 window; otherwise it hosts same-device on Vulkan.
    // PUCK_RAY_QUERY=0 forces the beam path (the same-device Vulkan SDF compositor) — the explicit fallback for runs
    // where the ray-query path is unwanted; each node also falls back to a blank surface if its device lacks support.
    // The injected frameSource (the data-driven JsonSdfFrameSource over the document's scene + viewport) is rendered;
    // the OS/feature gates (PUCK_RAY_QUERY, DXR support) stay HERE in the builder, never in the document.
    internal static IRenderNode CreateRtWorldNode(IServiceProvider serviceProvider, bool onDirectX, string? capturePath, ISdfFrameSource frameSource, uint width = 960, uint height = 600) {
        if (string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_RAY_QUERY"), "0", StringComparison.Ordinal)) {
            return CreateWorldNode(
                capturePath: capturePath,
                frameSource: frameSource,
                height: height,
                produceBackend: "vulkan",
                serviceProvider: serviceProvider,
                width: width,
                withChild: false
            );
        }

        if (
            onDirectX &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
        ) {
            return new DirectXRtWorldHostNode(
                bytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-rt-debug.rq.comp.dxil")),
                capturePath: capturePath,
                frameSource: frameSource,
                height: height,
                hostProvider: serviceProvider,
                width: width
            );
        }

        return new RtWorldProducerNode(
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-rt-debug.rq.comp.spv")),
            capturePath: capturePath,
            frameSource: frameSource,
            height: height,
            serviceProvider: serviceProvider,
            width: width
        );
    }

    // Picks the live compute-world root node: hosted on Direct3D 12 (same-device on the D3D12 window, or — with
    // produce:"vulkan" — a bespoke Vulkan producer whose content the D3D12 host imports zero-copy) when requested,
    // otherwise the Vulkan-hosted path (same-device on Vulkan, or D3D12-produced + Vulkan-imported by default).
    internal static IRenderNode CreateWorldRootNode(IServiceProvider serviceProvider, bool withChild, bool onDirectX, string? produceBackend, string? capturePath, ISdfFrameSource frameSource, uint width = 960, uint height = 600) {
        if (
            onDirectX &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
        ) {
            // produce:"vulkan" on a Direct3D 12 host is the REVERSE cross-backend live path (#4): a bespoke Vulkan
            // producer renders into a host-owned shared image the D3D12 window blits. produce omitted/anything else
            // runs the world same-device on Direct3D 12 (#2).
            return string.Equals(produceBackend, "vulkan", StringComparison.OrdinalIgnoreCase)
                ? new VulkanComputeWorldHostNode(capturePath: capturePath, frameSource: frameSource, height: height, serviceProvider: serviceProvider, width: width, withChild: withChild)
                : new DirectXComputeWorldHostNode(capturePath: capturePath, frameSource: frameSource, height: height, hostProvider: serviceProvider, width: width, withChild: withChild);
        }

        return CreateWorldNode(
            capturePath: capturePath,
            frameSource: frameSource,
            height: height,
            produceBackend: produceBackend,
            serviceProvider: serviceProvider,
            width: width,
            withChild: withChild
        );
    }

    // The compute SDF world, produced cross-backend by default (mirroring --produce): the world runs on Direct3D 12
    // and the Vulkan host imports the shared result zero-copy. --produce vulkan (or a non-Windows OS, where there is
    // no Direct3D 12) runs it same-device on the Vulkan host instead. With a hosted child, the bottom-right viewport
    // shows that child node's surface instead of an SDF camera.
    internal static IRenderNode CreateWorldNode(IServiceProvider serviceProvider, string? produceBackend, string? capturePath, bool withChild, ISdfFrameSource frameSource, uint width = 960, uint height = 600) {
        if (
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) &&
            !string.Equals(produceBackend, "vulkan", StringComparison.OrdinalIgnoreCase)
        ) {
            return new CrossBackendComputeWorldNode(capturePath: capturePath, frameSource: frameSource, height: height, serviceProvider: serviceProvider, width: width, withChild: withChild);
        }

        return new WorldProducerNode(
            beamBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-beam.comp.spv")),
            cullArgsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-cull-args.comp.spv")),
            capturePath: capturePath,
            children: (withChild ? ChildSurfaceNode.CreateWorldChildren(serviceProvider: serviceProvider, directX: false) : null),
            compositeBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-composite.comp.spv")),
            frameSource: frameSource,
            height: height,
            serviceProvider: serviceProvider,
            viewsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-views.comp.spv")),
            width: width
        );
    }

    // The live MiniAction action-game prototype: a controller-driven player box, simulated deterministically and
    // rendered through the compute SDF world path (the player rides the per-frame dynamic-transform buffer). Vulkan
    // host for this milestone.
    internal static IRenderNode CreateMiniActionRootNode(IServiceProvider serviceProvider, string? capturePath, uint width = 960, uint height = 600) {
        return new MiniAction.MiniActionRenderNode(capturePath: capturePath, height: height, serviceProvider: serviceProvider, width: width);
    }
}
