using System.Numerics;
using System.Text;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// The demo's BUILT-IN run documents, plus the synthesizer that turns the legacy CLI flags into the SAME
/// <see cref="PuckRunDocument"/> the <c>--run</c> path consumes. This is what collapses the old imperative flag path
/// into the single data-driven one: every <c>--world</c>/<c>--validate*</c> flag is now a thin alias that builds a
/// document. The canonical world scene + camera layouts are authored as DATA (the two embedded documents below, verified
/// bit-identical to <see cref="BuildReferenceScene"/> and the historic inline cameras) rather than as builder calls.
/// </summary>
internal static class DemoRunDocuments {
    // The canonical world: a ground plane with three primitives smooth-melded above it, viewed by one hero orbit
    // (the 1-up layout) or four independent orbits (the 2x2 split). These two literals ARE the lifted
    // WorldSdfFrameSource.BuildScene + camera layouts; docs/examples/world-single.json and world-split.json are the
    // same documents (kept in sync). A trailing 'world' graph is present only so each parses as a complete run; the
    // synthesizer reuses just the scene + viewports.
    private const string SingleDocumentJson = """
    {
        "version": "puck.run.v1",
        "scene": {
            "materials": [
                { "albedo": [0.46, 0.48, 0.54] },
                { "albedo": [0.90, 0.27, 0.21] },
                { "albedo": [0.30, 0.80, 0.40] },
                { "albedo": [0.27, 0.45, 0.92] }
            ],
            "objects": [
                { "shape": "plane", "normal": [0.0, 1.0, 0.0], "offset": 1.0, "material": 0 },
                { "shape": "sphere", "ops": [{ "op": "translate", "offset": [-1.3, 0.0, 0.0] }], "radius": 0.85, "material": 1, "blend": "SmoothUnion", "smooth": 0.35 },
                { "shape": "box", "ops": [{ "op": "translate", "offset": [1.3, 0.0, 0.2] }], "halfExtents": [0.65, 0.65, 0.65], "round": 0.12, "material": 3, "blend": "SmoothUnion", "smooth": 0.35 },
                { "shape": "torus", "ops": [{ "op": "translate", "offset": [0.0, 0.1, -1.6] }], "majorRadius": 0.9, "minorRadius": 0.3, "material": 2, "blend": "SmoothUnion", "smooth": 0.35 }
            ]
        },
        "viewports": [
            { "source": { "$type": "orbit", "angularSpeed": 0.4, "azimuth": 0.0, "fieldOfView": 60.0, "height": 1.6, "radius": 5.2, "target": [0.0, 0.1, 0.0] }, "region": [0.0, 0.0, 1.0, 1.0] }
        ],
        "graph": { "$type": "world" }
    }
    """;
    private const string SplitDocumentJson = """
    {
        "version": "puck.run.v1",
        "scene": {
            "materials": [
                { "albedo": [0.46, 0.48, 0.54] },
                { "albedo": [0.90, 0.27, 0.21] },
                { "albedo": [0.30, 0.80, 0.40] },
                { "albedo": [0.27, 0.45, 0.92] }
            ],
            "objects": [
                { "shape": "plane", "normal": [0.0, 1.0, 0.0], "offset": 1.0, "material": 0 },
                { "shape": "sphere", "ops": [{ "op": "translate", "offset": [-1.3, 0.0, 0.0] }], "radius": 0.85, "material": 1, "blend": "SmoothUnion", "smooth": 0.35 },
                { "shape": "box", "ops": [{ "op": "translate", "offset": [1.3, 0.0, 0.2] }], "halfExtents": [0.65, 0.65, 0.65], "round": 0.12, "material": 3, "blend": "SmoothUnion", "smooth": 0.35 },
                { "shape": "torus", "ops": [{ "op": "translate", "offset": [0.0, 0.1, -1.6] }], "majorRadius": 0.9, "minorRadius": 0.3, "material": 2, "blend": "SmoothUnion", "smooth": 0.35 }
            ]
        },
        "viewports": [
            { "source": { "$type": "orbit", "angularSpeed": 0.4, "azimuth": 0.0, "fieldOfView": 55.0, "height": 1.7, "radius": 5.0, "target": [0.0, 0.0, -0.3] }, "region": [0.0, 0.0, 0.5, 0.5] },
            { "source": { "$type": "orbit", "angularSpeed": -0.55, "azimuth": 3.141592653589793, "fieldOfView": 52.0, "height": 0.5, "radius": 3.6, "target": [0.0, 0.15, -0.3] }, "region": [0.5, 0.0, 0.5, 0.5] },
            { "source": { "$type": "orbit", "angularSpeed": 0.25, "azimuth": 0.0, "fieldOfView": 55.0, "height": 5.0, "radius": 0.9, "target": [0.0, -0.5, -0.3] }, "region": [0.0, 0.5, 0.5, 0.5] },
            { "source": { "$type": "orbit", "angularSpeed": 0.6, "azimuth": 0.0, "fieldOfView": 50.0, "height": 0.3, "radius": 1.8, "target": [-1.3, 0.0, 0.0] }, "region": [0.5, 0.5, 0.5, 0.5] }
        ],
        "graph": { "$type": "world" }
    }
    """;

    private static readonly PuckRunDocument s_single = RunDocument.Parse(utf8Json: Encoding.UTF8.GetBytes(SingleDocumentJson));
    private static readonly PuckRunDocument s_split = RunDocument.Parse(utf8Json: Encoding.UTF8.GetBytes(SplitDocumentJson));

    /// <summary>The canonical world scene (four materials, a ground plane + three smooth-melded primitives).</summary>
    internal static SceneDocument WorldScene => s_single.Scene;
    /// <summary>The 1-up hero-orbit viewport filling the frame.</summary>
    internal static IReadOnlyList<Viewport> SingleViewports => s_single.Viewports;
    /// <summary>The 2x2 split-screen of four independent orbits over the same scene.</summary>
    internal static IReadOnlyList<Viewport> SplitViewports => s_split.Viewports;

    /// <summary>Turns the resolved CLI flags into the run document the single data-driven path consumes. Mirrors the
    /// historic flag precedence exactly: the self-contained gates first, then the <c>world</c> gates, then the live
    /// world/showcase producers.</summary>
    /// <param name="flags">The parsed launch flags.</param>
    /// <returns>The synthesized run document (always valid).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="flags"/> is <see langword="null"/>.</exception>
    public static PuckRunDocument Synthesize(DemoFlags flags) {
        ArgumentNullException.ThrowIfNull(argument: flags);

        // The validation/fuzzing gates render OFFSCREEN on a forced Vulkan host (host.backend:"directx" is rejected for
        // them); a live render hosts on the requested backend. produce selects the world's render backend: on a Vulkan
        // host it picks the producer (Direct3D 12 zero-copy import, or same-device Vulkan); on a Direct3D 12 host
        // produce:"vulkan" is the REVERSE cross-backend live path (a bespoke Vulkan producer the host imports). It is
        // dropped only for a Direct3D 12 SHOWCASE host, which has no reverse cross-backend path (it would yield a blank
        // window — the validator rejects that combination).
        var offscreen = (flags.Validate || flags.ValidateExport || flags.ValidateCompute || flags.ValidateMiniAction || flags.ValidateDeterminism || flags.ValidateCliDeterminism || flags.ValidateReverseShare || flags.ValidateIndirect || flags.ValidateResample || flags.ValidateViewports || flags.ValidatePixelate || flags.ValidateCapture || flags.ValidateCamera || flags.ValidateCameraLive || flags.ValidateCameraGpu || flags.ValidateWorld || flags.ValidateWorldChild);
        var directXHost = (!offscreen && string.Equals(flags.Backend, "directx", StringComparison.OrdinalIgnoreCase));
        var directXShowcaseHost = (directXHost && !flags.World && !flags.WorldSplit && !flags.WorldChild && !flags.WorldRt);
        var host = new HostDocument {
            Backend = (offscreen ? null : flags.Backend),
            ExitAfterSeconds = flags.ExitAfterSeconds,
            PresentMode = flags.PresentMode,
            SurfaceFormat = flags.SurfaceFormat,
        };
        var produce = (directXShowcaseHost ? null : flags.Produce);

        // The self-contained gates (each a full-frame offscreen smoke test) map one flag to one gate name; collapsing
        // them into a single lookup keeps this synthesizer's branching in check as gates are added.
        if (SelfContainedGateName(flags: flags) is string selfContainedGate) {
            return Gate(host: host, gate: selfContainedGate);
        }

        if (flags.ValidateWorldChild) {
            return WorldGate(child: true, host: host, split: flags.WorldSplit);
        }

        if (flags.ValidateWorld) {
            if (flags.FuzzSeed >= 0) {
                // --validate-world --fuzz-seed N: one in-process differential-fuzzing iteration on a GENERATED scene over
                // the canonical single hero view (the same view `tools fuzz` uses); --world-split has no effect here.
                if (flags.WorldSplit) {
                    Console.Error.WriteLine(value: "[run] --world-split is ignored with --fuzz-seed; a fuzz run diffs the single canonical view.");
                }

                return new PuckRunDocument { Fuzzing = new FuzzingDocument { Seed = flags.FuzzSeed }, Host = host, Version = PuckRunDocument.CurrentVersion };
            }

            return WorldGate(child: false, host: host, split: flags.WorldSplit);
        }

        if (flags.MiniAction) {
            // The live action demo builds its own dynamic scene + chase camera (no document scene/viewports), like the
            // showcase. Vulkan host for this milestone (a Direct3D 12 host is a later phase).
            return new PuckRunDocument {
                Graph = new MiniActionNode(),
                Host = (host with { Backend = "vulkan" }),
                Version = PuckRunDocument.CurrentVersion,
            };
        }

        if (flags.Camera) {
            // The live camera owns its own Direct3D 12 producer device and hands the host a shared handle; the host must
            // be Vulkan to import it. It renders its own content (no document scene/viewports), like the showcase.
            return new PuckRunDocument {
                Graph = new CameraNode(),
                Host = (host with { Backend = "vulkan" }),
                Version = PuckRunDocument.CurrentVersion,
            };
        }

        if (flags.WorldRt) {
            // The ray-query world: the built-in scene + one full-frame viewport, host device chosen by host.backend.
            return new PuckRunDocument {
                Graph = new RtNode(),
                Host = host,
                Scene = WorldScene,
                Version = PuckRunDocument.CurrentVersion,
                Viewports = SingleViewports,
            };
        }

        if (flags.World || flags.WorldSplit || flags.WorldChild) {
            var split = (flags.WorldSplit || flags.WorldChild);

            return new PuckRunDocument {
                Graph = new WorldNode { Child = flags.WorldChild, Produce = produce },
                Host = host,
                Scene = WorldScene,
                Version = PuckRunDocument.CurrentVersion,
                Viewports = (split ? SplitViewports : SingleViewports),
            };
        }

        // Default: the cross-backend SDF showcase (renders its own built-in scene; consumes no document scene/viewports).
        return new PuckRunDocument {
            Graph = new ShowcaseNode { Produce = produce },
            Host = host,
            Version = PuckRunDocument.CurrentVersion,
        };
    }

    /// <summary>The hand-authored reference scene the data-driven <c>--check-run</c> gate asserts a JSON scene
    /// reproduces word-for-word. It is the independent oracle the canonical <see cref="WorldScene"/> is checked against,
    /// so the data form can never silently drift from the intended geometry.</summary>
    /// <returns>The reference scene program.</returns>
    public static SdfProgram BuildReferenceScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.46f, 0.48f, 0.54f)));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.90f, 0.27f, 0.21f)));
        var green = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.30f, 0.80f, 0.40f)));
        var blue = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.27f, 0.45f, 0.92f)));

        _ = builder.ResetPoint().Plane(normal: new Vector3(0f, 1f, 0f), offset: 1f, material: ground);
        _ = builder.ResetPoint().Translate(offset: new Vector3(-1.3f, 0f, 0f)).Sphere(radius: 0.85f, material: red, blend: SdfBlendOp.SmoothUnion, smooth: 0.35f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(1.3f, 0f, 0.2f)).Box(halfExtents: new Vector3(0.65f, 0.65f, 0.65f), round: 0.12f, material: blue, blend: SdfBlendOp.SmoothUnion, smooth: 0.35f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0.1f, -1.6f)).Torus(majorRadius: 0.9f, minorRadius: 0.3f, material: green, blend: SdfBlendOp.SmoothUnion, smooth: 0.35f);

        return builder.Build();
    }

    // Maps the self-contained validation flags (each a full-frame offscreen smoke test) to their gate name, in the
    // historic precedence order; null when none is set (the caller falls through to the world gates / live producers).
    private static string? SelfContainedGateName(DemoFlags flags) {
        if (flags.Validate) { return "parity"; }
        if (flags.ValidateExport) { return "export"; }
        if (flags.ValidateCompute) { return "compute"; }
        if (flags.ValidateMiniAction) { return "mini-action"; }
        if (flags.ValidateDeterminism) { return "determinism"; }
        if (flags.ValidateCliDeterminism) { return "cli-determinism"; }
        if (flags.ValidateReverseShare) { return "reverse"; }
        if (flags.ValidateIndirect) { return "indirect"; }
        if (flags.ValidateResample) { return "resample"; }
        if (flags.ValidateViewports) { return "viewports"; }
        if (flags.ValidatePixelate) { return "pixelate"; }
        if (flags.ValidateCapture) { return "capture"; }
        if (flags.ValidateCamera) { return "camera"; }
        if (flags.ValidateCameraLive) { return "camera-live"; }
        if (flags.ValidateCameraGpu) { return "camera-gpu"; }

        return null;
    }
    private static PuckRunDocument Gate(HostDocument host, string gate) {
        return new PuckRunDocument {
            Host = host,
            Validation = new ValidationDocument { Gate = gate },
            Version = PuckRunDocument.CurrentVersion,
        };
    }
    // The 'world' validation gate over the canonical scene. A child viewport OR an explicit --world-split uses the 2x2
    // split layout (four-up); plain --validate-world uses the single hero view.
    private static PuckRunDocument WorldGate(HostDocument host, bool child, bool split) {
        var fourUp = (child || split);

        return new PuckRunDocument {
            Host = host,
            Scene = WorldScene,
            Validation = new ValidationDocument { Child = child, Gate = "world" },
            Version = PuckRunDocument.CurrentVersion,
            Viewports = (fourUp ? SplitViewports : SingleViewports),
        };
    }
}
