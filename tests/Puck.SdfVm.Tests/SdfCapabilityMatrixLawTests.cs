using System.Reflection;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The capability matrix of the SDF engine: every feature the engine provides, the frame-graph equivalent that replaces
/// it, and the check that proves the equivalent. The inventory is read from the engine's public surface: every public
/// member of <see cref="SdfWorldEngine"/>, <see cref="SdfEngineNode"/>, <see cref="SdfFrame"/>,
/// <see cref="SdfViewSnapshot"/>, <see cref="SdfWorldEngineOptions"/> and <see cref="SdfWorldRenderSpec"/> belongs to
/// exactly one row, and every pass in <see cref="SdfWorldEngine.PassLabels"/> names the graph pass that replaces it. A
/// console verb reaches the engine only through these members, so a verb is covered by the row of the member it drives.
/// A row turns green only when its equivalent passes its check, and nothing a row claims is deleted before then. A row
/// with no check is a named gap: the list of gaps changes only in the change that adds a check.
/// </summary>
public sealed class SdfCapabilityMatrixLawTests {
    private sealed record Row(string Capability, string Equivalent, string? Check, bool Green, params string[] Members);

    private static readonly Type[] Surface = [
        typeof(SdfWorldEngine),
        typeof(SdfEngineNode),
        typeof(SdfFrame),
        typeof(SdfViewSnapshot),
        typeof(SdfWorldEngineOptions),
        typeof(SdfWorldRenderSpec),
    ];
    // Members a record or a type carries for its own identity rather than as a feature.
    private static readonly string[] IdentityMembers = [".ctor", "<Clone>$", "Deconstruct", "Equals", "GetHashCode", "ToString"];
    // The capabilities the plan names; each is the whole or part of a row's capability.
    private static readonly string[] NamedCapabilities = [
        "screen slots", "decals", "viewports", "render scale", "tonemap", "captures", "pass labels",
        "kernel variants", "brick baking", "glyph atlas", "volumes", "lights", "far field", "debug views",
    ];
    // The graph pass that replaces each engine pass.
    private static readonly Dictionary<string, string> PassEquivalents = new(comparer: StringComparer.Ordinal) {
        ["upload"] = "no pass: the frame tables upload through GpuRegion",
        ["sky"] = "sdf.world sky",
        ["mask"] = "sdf.world mask",
        ["beam"] = "sdf.world beam",
        ["cull-args"] = "sdf.world cull arguments",
        ["primary"] = "sdf.world primary, dispatched indirectly, writing visibility version 0",
        ["surface"] = "sdf.world surface, visibility version 1",
        ["ambient"] = "sdf.world ambient, visibility version 2",
        ["views"] = "sdf.world shadow, light and volume shading, color versions 0 to 2",
    };
    // The rows no check proves yet.
    private static readonly string[] Gaps = [
        "live program report",
        "render scale",
        "screen slots",
        "decals",
        "glyph atlas",
        "volumes",
        "shading levers",
        "debug views",
        "grid overlay",
        "brick baking",
        "output image and export",
        "mesh draws",
        "assembly and lifetime",
    ];
    private static readonly Row[] Matrix = [
        new(
            Capability: "program upload and capacity",
            Equivalent: "the world group's program words and instance tables, uploaded through GpuRegion and sized by the ProgramWords and Instances counts",
            Check: "SdfWorldEngineUploadLawTests; puck parity (materials, noise, vocabulary)",
            Green: false,
            Members: [
                "SdfWorldEngine.UploadProgram", "SdfWorldEngine.ProgramWordCapacity", "SdfEngineNode.ProgramWordCapacity",
                "SdfEngineNode.CopyLiveProgramWords", "SdfFrame.Program", "SdfFrame.ProgramChanged",
                "SdfWorldEngineOptions.Program", "SdfWorldEngineOptions.ProgramWordCapacity", "SdfWorldEngineOptions.InstanceCapacity",
                "SdfWorldRenderSpec.ProgramWordCapacity", "SdfWorldRenderSpec.InstanceCapacity",
            ]
        ),
        new(
            Capability: "live program report",
            Equivalent: "SdfWorldResidency's report of the resident program, read by world.budget",
            Check: null,
            Green: false,
            Members: [
                "SdfEngineNode.LiveProgramWords", "SdfEngineNode.LiveProgramInstances", "SdfEngineNode.LiveProgramStepScale",
                "SdfEngineNode.LiveProgramStepScaleBinder", "SdfEngineNode.LiveProgramFieldScopeClamps", "SdfEngineNode.LiveVolumes",
            ]
        ),
        new(
            Capability: "frame submission",
            Equivalent: "the graph runtime records the sdf.world passes into the graph's command list",
            Check: "SdfWorldEngineWorkLawTests; SdfEngineNodeWorkLawTests; puck parity",
            Green: false,
            Members: [
                "SdfWorldEngine.SubmitFrame", "SdfWorldEngine.RenderFrame", "SdfWorldEngine.ReadPixels", "SdfWorldEngine.FrameRingSize",
                "SdfEngineNode.ProduceFrame", "SdfEngineNode.Descriptor", "SdfFrame.Time", "SdfWorldEngine.FrameValues",
            ]
        ),
        new(
            Capability: "viewports",
            Equivalent: "one sdf.world instance per view, scheduled by RenderGraphScheduler, each view's output placed by the root's place pass",
            Check: "puck parity (one view); the split-seats canary (two views)",
            Green: false,
            Members: [
                "SdfFrame.Views", "SdfViewSnapshot.Camera", "SdfViewSnapshot.Region", "SdfViewSnapshot.AsymmetricFrustumOffset",
                "SdfWorldEngine.RequestViewExtent", "SdfWorldEngine.HasViewOutput", "SdfWorldEngine.TryAcquireViewOutput",
                "SdfWorldEngine.ReleaseViewOutput", "SdfWorldEngine.ViewOutputHolds", "SdfEngineNode.ViewProducer",
                "SdfEngineNode.HasViewOutput",
                "SdfWorldEngine.MaxViewports", "SdfWorldEngine.ConeNear", "SdfWorldEngine.PrimaryMarchSteps",
                "SdfWorldEngineOptions.ViewportCapacity", "SdfWorldRenderSpec.ViewportCapacity", "SdfWorldRenderSpec.Width",
                "SdfWorldRenderSpec.Height",
            ]
        ),
        new(
            Capability: "render scale",
            Equivalent: "a reduced instance extent, reconstructed by the root's place pass",
            Check: null,
            Green: false,
            Members: ["SdfViewSnapshot.RenderScale", "SdfWorldEngine.DefaultViewExtent"]
        ),
        new(
            Capability: "screen slots",
            Equivalent: "P12 image sources bound as the pass group's screen-source array with a sampler table",
            Check: null,
            Green: false,
            Members: [
                "SdfWorldEngine.SetScreenSource", "SdfWorldEngine.SetScreenSurface", "SdfWorldEngine.SetScreenLight",
                "SdfWorldEngine.MaxScreenSurfaces", "SdfWorldRenderSpec.ScreenSources", "SdfEngineNode.BoundScreenSource",
            ]
        ),
        new(
            Capability: "decals",
            Equivalent: "the world group's decal table",
            Check: null,
            Green: false,
            Members: ["SdfWorldEngine.SetScreenDecal", "SdfWorldEngine.ClearScreenDecal", "SdfWorldEngine.MaxScreenDecalCells"]
        ),
        new(
            Capability: "glyph atlas",
            Equivalent: "the world group's glyph atlas",
            Check: null,
            Green: false,
            Members: ["SdfWorldEngine.SetGlyphAtlas"]
        ),
        new(
            Capability: "volumes",
            Equivalent: "the frame group's volume table, read by the volume shading stage",
            Check: null,
            Green: false,
            Members: ["SdfFrame.Volumes", "SdfWorldEngine.MaxVolumes"]
        ),
        new(
            Capability: "lights, sky and tonemap",
            Equivalent: "the generated frame block's environment, the light stage, and P16's display transform for the tonemap",
            Check: "PackEnvironmentLawTests; WorldRenderLightingSkyLawTests; puck parity (sky, materials)",
            Green: false,
            Members: ["SdfFrame.Environment", "SdfFrame.AmbientScale", "SdfFrame.SunScale", "SdfFrame.SampleIndex"]
        ),
        new(
            Capability: "far field",
            Equivalent: "the generated frame block's far distance and the beam's far bound",
            Check: "WorldRenderFarDistanceLawTests",
            Green: false,
            Members: ["SdfFrame.FarDistance", "SdfFrame.DefaultFarDistance", "SdfFrame.DisableFarBound"]
        ),
        new(
            Capability: "shading levers",
            Equivalent: "staged shading's stage options in the generated frame block",
            Check: null,
            Green: false,
            Members: [
                "SdfFrame.DisableAmbientOcclusion", "SdfFrame.DisableSoftShadows", "SdfFrame.DisableShadowCull",
                "SdfFrame.DisableScreenLights", "SdfFrame.EnableShadowProxy", "SdfFrame.ShadowDistanceScale",
                "SdfFrame.UseCameraTileShadowMask", "SdfFrame.UseFastAmbientOcclusion", "SdfFrame.UseFastSoftShadowMarch",
                "SdfFrame.UseFiniteDifferenceNormals",
            ]
        ),
        new(
            Capability: "debug views",
            Equivalent: "the debug module's passes selected per instance",
            Check: null,
            Green: false,
            Members: ["SdfWorldEngine.DebugMode", "SdfEngineNode.DebugMode", "SdfFrame.DebugSliceAxis", "SdfFrame.DebugSliceOffset"]
        ),
        new(
            Capability: "grid overlay",
            Equivalent: "the debug module's grid stage",
            Check: null,
            Green: false,
            Members: [
                "SdfFrame.GridFlags", "SdfFrame.GridFloorY", "SdfFrame.GridObjectFrame", "SdfFrame.GridObjectOrigin",
                "SdfFrame.GridObjectPatchRadius", "SdfFrame.GridObjectPitch", "SdfFrame.GridWorldPitch",
            ]
        ),
        new(
            Capability: "dynamic transforms",
            Equivalent: "the frame group's dynamic-transform table, uploaded through GpuRegion by the moved set",
            Check: "WorldSceneMovedTransformsLawTests; SdfMovedTransformsWorkLawTests; puck parity (vocabulary)",
            Green: false,
            Members: [
                "SdfFrame.DynamicTransforms", "SdfFrame.MovedTransforms", "SdfWorldEngineOptions.DynamicTransformCapacity",
                "SdfWorldRenderSpec.DynamicTransformCapacity",
            ]
        ),
        new(
            Capability: "brick baking",
            Equivalent: "sdf.bricks, a world-scoped instance joined to the views by buffer edges",
            Check: null,
            Green: false,
            Members: [
                "SdfWorldEngine.BrickBakeAvailable", "SdfWorldEngine.GetBrickState", "SdfWorldEngine.RequestBrickBake",
                "SdfWorldEngine.UploadBrick", "SdfWorldEngine.DefaultBrickPoolVoxelCapacity",
                "SdfWorldEngineOptions.BrickPoolVoxelCapacity", "SdfWorldRenderSpec.BrickPoolVoxelCapacity",
            ]
        ),
        new(
            Capability: "cadence",
            Equivalent: "the scheduler's refresh of an unchanged instance",
            Check: "SdfWorldEngineWorkLawTests (cadence-skipped passes)",
            Green: false,
            Members: ["SdfFrame.EnableCadenceGate", "SdfWorldEngine.CadenceSkippedPassLabels"]
        ),
        new(
            Capability: "pass labels and counted work",
            Equivalent: "the planner's pass order and per-instance pass counts",
            Check: "SdfPassPlanLawTests; SdfWorldEngineWorkLawTests; world-counters canary",
            Green: false,
            Members: [
                "SdfWorldEngine.PassLabels", "SdfWorldEngine.PassClasses", "SdfEngineNode.PassLabels", "SdfWorldEngine.Work", "SdfWorldEngine.WorkLifetime",
                "SdfEngineNode.Work", "SdfEngineNode.WorkLifetime", "SdfWorldEngineOptions.WorkLedger", "SdfWorldEngine.DebugLabel",
            ]
        ),
        new(
            Capability: "buffer sizing and uploads",
            Equivalent: "the planner's counted buffers and GpuRegion uploads",
            Check: "SdfPassPlanLawTests; SdfWorldEngineUploadLawTests",
            Green: false,
            Members: [
                "SdfWorldEngine.FrameBufferBytes", "SdfWorldEngine.TileSize",
                "SdfWorldEngine.VisibilityRecordBytes",
                "SdfWorldEngine.VisibilityRecordByteLength", "SdfEngineNode.VisibilityRecordBytes",
                "SdfWorldEngine.DescriptorPoolSizes", "SdfWorldEngine.DescriptorPools", "SdfWorldEngine.CheckAdmission",
            ]
        ),
        new(
            Capability: "captures",
            Equivalent: "captures from the graph's root output",
            Check: "RenderGraphRuntimeLawTests; WorldCaptureHoldLawTests; WorldCaptureSchedulerLawTests; puck parity",
            Green: true,
            Members: ["SdfEngineNode.RequestCapture", "SdfEngineNode.PendingCapturePath"]
        ),
        new(
            Capability: "kernel variants and reload",
            Equivalent: "the graph's pass-pipeline cache, built off the frame thread",
            Check: "SdfViewsKernelVariantLawTests; SdfWorldPipelineCacheLawTests; SdfPipelineBuildLivenessLawTests",
            Green: false,
            Members: [
                "SdfWorldEngine.InstallReload", "SdfEngineNode.RequestShaderReload", "SdfEngineNode.ShaderReloadStatus",
                "SdfEngineNode.IsReady", "SdfEngineNode.NotReadyReason",
            ]
        ),
        new(
            Capability: "output image and export",
            Equivalent: "the graph's root output, imported or exported by the graph runtime",
            Check: null,
            Green: false,
            Members: [
                "SdfWorldEngine.OutputImageHandle", "SdfWorldEngine.OutputImageViewHandle", "SdfWorldEngine.OutputLayout", "SdfWorldEngine.ExportSharedHandle",
                "SdfWorldEngine.OutputWidth", "SdfWorldEngine.OutputHeight",
                "SdfWorldEngineOptions.CreateOutputImage",
            ]
        ),
        new(
            Capability: "mesh draws",
            Equivalent: "a P3 geometry pass over the shared visibility record",
            Check: null,
            Green: false,
            Members: [
                "SdfFrame.MeshDraws", "SdfEngineNode.MeshRegionBytes", "SdfEngineNode.MeshDrawCount", "SdfWorldEngine.MeshRegionBytes",
                "SdfWorldEngine.MeshRegionLayout",
            ]
        ),
        new(
            Capability: "assembly and lifetime",
            Equivalent: "SdfWorldResidency for the world's half and the graph runtime for the views",
            Check: null,
            Green: false,
            Members: [
                "SdfWorldEngine.Dispose", "SdfEngineNode.Dispose", "SdfEngineNode.OnDeviceLost", "SdfWorldRenderSpec.FrameSource",
                "SdfEngineNode.Produce", "SdfEngineNode.TryAcquireOutput", "SdfEngineNode.OutputLeases", "SdfEngineNode.RetiringEngines",
                "SdfWorldRenderSpec.DecorateFrameSource", "SdfWorldRenderSpec.HostsOnDirectX",
            ]
        ),
    ];

    private static IEnumerable<string> SurfaceMembers() {
        foreach (var type in Surface) {
            foreach (var member in type.GetMembers(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (
                    (member is MethodInfo { IsSpecialName: true }) ||
                    IdentityMembers.Contains(value: member.Name)
                ) {
                    continue;
                }

                yield return $"{type.Name}.{member.Name}";
            }
        }
    }

    [Fact]
    public void EveryPublicMemberOfTheEngineBelongsToExactlyOneRow() {
        var claimed = Matrix.SelectMany(selector: static row => row.Members).ToArray();

        Assert.Empty(collection: claimed.GroupBy(keySelector: static member => member).Where(predicate: static group => (group.Count() > 1)).Select(selector: static group => group.Key));
        Assert.Equal(
            actual: claimed.Order(comparer: StringComparer.Ordinal),
            expected: SurfaceMembers().Distinct().Order(comparer: StringComparer.Ordinal)
        );
    }
    [Fact]
    public void EveryEnginePassNamesTheGraphPassThatReplacesIt() {
        Assert.Equal(
            actual: PassEquivalents.Keys,
            expected: SdfWorldEngine.PassLabels.ToArray()
        );
    }
    [Fact]
    public void EveryCapabilityThePlanNamesIsARow() {
        Assert.All(
            action: static named => Assert.Contains(
                collection: Matrix,
                filter: row => row.Capability.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: named
                )
            ),
            collection: NamedCapabilities
        );
    }
    [Fact]
    public void EveryRowNamesItsEquivalentAndOnlyTheNamedGapsLackACheck() {
        Assert.Equal(
            actual: Matrix.Select(selector: static row => row.Capability).Distinct().Count(),
            expected: Matrix.Length
        );
        Assert.All(
            action: static row => Assert.False(condition: string.IsNullOrWhiteSpace(value: row.Equivalent)),
            collection: Matrix
        );
        Assert.Equal(
            actual: Matrix.Where(predicate: static row => (row.Check is null)).Select(selector: static row => row.Capability),
            expected: Gaps
        );
        Assert.All(
            action: static row => Assert.False(condition: (row.Green && (row.Check is null))),
            collection: Matrix
        );
    }
}
