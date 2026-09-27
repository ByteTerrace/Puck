using System.Reflection;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The capability matrix of the SDF engine: every feature the engine provides, the frame-graph equivalent that replaces
/// it, and the check that proves the equivalent. The inventory is read from the engine's public surface: every public
/// member of <see cref="SdfWorldTables"/>, <see cref="SdfWorldResidency"/>, <see cref="SdfWorldPasses"/>,
/// <see cref="SdfWorldView"/>, <see cref="SdfFrame"/>, <see cref="SdfViewSnapshot"/>, <see cref="SdfWorldTablesOptions"/>
/// and <see cref="SdfWorldRenderSpec"/> belongs to
/// exactly one row, and every pass in <see cref="SdfWorldTables.PassLabels"/> names the graph pass that replaces it. A
/// console verb reaches the engine only through these members, so a verb is covered by the row of the member it drives.
/// A row turns green only when its equivalent passes its check, and nothing a row claims is deleted before then. A row
/// with no check is a named gap: the list of gaps changes only in the change that adds a check.
/// </summary>
public sealed class SdfCapabilityMatrixLawTests {
    private sealed record Row(string Capability, string Equivalent, string? Check, bool Green, params string[] Members);

    private static readonly Type[] Surface = [
        typeof(SdfWorldTables),
        typeof(SdfWorldResidency),
        typeof(SdfWorldPasses),
        typeof(SdfWorldView),
        typeof(SdfFrame),
        typeof(SdfViewSnapshot),
        typeof(SdfWorldTablesOptions),
        typeof(SdfWorldRenderSpec),
    ];
    // Members a record or a type carries for its own identity rather than as a feature.
    private static readonly string[] IdentityMembers = [".ctor", "<Clone>$", "Deconstruct", "Equals", "GetHashCode", "ToString"];
    // The capabilities the plan names; each is the whole or part of a row's capability.
    private static readonly string[] NamedCapabilities = [
        "screen slots", "decals", "viewports", "render scale", "tonemap", "captures", "pass labels",
        "kernel variants", "brick baking", "glyph atlas", "volumes", "lights", "far field", "debug views",
    ];
    // The graph pass that replaces each engine pass: the upload is the residency's own; every other pass is a part of the
    // sdf.world fragment (SdfPassPlanLawTests).
    private static readonly Dictionary<string, string> PassEquivalents = new(comparer: StringComparer.Ordinal) {
        ["upload"] = "no graph pass: the residency's one upload a frame, through GpuRegion, ahead of every view's submission",
    };
    // The rows no check proves yet.
    private static readonly string[] Gaps = [
        "live program report",
        "render scale",
        "decals",
        "glyph atlas",
        "volumes",
        "shading levers",
        "debug views",
        "grid overlay",
        "brick baking",
    ];
    private static readonly Row[] Matrix = [
        new(
            Capability: "program upload and capacity",
            Equivalent: "the residency's program words and instance grid, uploaded through GpuRegion, and each view's scratch counted by the Instances and InstanceMaskWords the residency states",
            Check: "SdfWorldTablesUploadLawTests; RenderGraphFragmentLawTests; puck parity (materials, noise, vocabulary)",
            Green: true,
            Members: [
                "SdfWorldTables.UploadProgram", "SdfWorldTables.ProgramWordCapacity", "SdfWorldTables.InstanceCapacity",
                "SdfWorldTables.InstanceMaskWordCount", "SdfWorldResidency.ProgramWordCapacity",
                "SdfWorldResidency.CopyLiveProgramWords", "SdfWorldResidency.CapacityRevision", "SdfWorldResidency.CountsAt",
                "SdfWorldPasses.CounterOf", "SdfFrame.Program", "SdfFrame.ProgramChanged",
                "SdfWorldTablesOptions.Program", "SdfWorldTablesOptions.ProgramWordCapacity", "SdfWorldTablesOptions.InstanceCapacity",
                "SdfWorldRenderSpec.ProgramWordCapacity", "SdfWorldRenderSpec.InstanceCapacity",
            ]
        ),
        new(
            Capability: "live program report",
            Equivalent: "SdfWorldResidency's report of the resident program, read by world.budget",
            Check: null,
            Green: false,
            Members: [
                "SdfWorldResidency.LiveProgramWords", "SdfWorldResidency.LiveProgramInstances", "SdfWorldResidency.LiveProgramStepScale",
                "SdfWorldResidency.LiveProgramStepScaleBinder", "SdfWorldResidency.LiveProgramFieldScopeClamps", "SdfWorldResidency.LiveVolumes",
            ]
        ),
        new(
            Capability: "frame submission",
            Equivalent: "the residency's one upload a frame, then the sdf.world passes recorded into each view instance's submission by the graph runtime",
            Check: "SdfWorldTablesWorkLawTests; SdfWorldResidencyWorkLawTests; RenderGraphRuntimeLawTests (package instances); puck parity",
            Green: true,
            Members: [
                "SdfWorldTables.Pack", "SdfWorldTables.SubmitUpload", "SdfWorldTables.CurrentSlot", "SdfWorldTables.FrameRingSize",
                "SdfFrame.Time", "SdfWorldResidency.HostFrame", "SdfWorldResidency.BeginFrame", "SdfWorldResidency.Prepare",
                "SdfWorldResidency.Submit", "SdfWorldResidency.Frame", "SdfWorldResidency.Tables", "SdfWorldResidency.RequestExtent",
                "SdfWorldPasses.Build", "SdfWorldPasses.Create", "SdfWorldPasses.Regions", "SdfWorldPasses.BeginFrame",
            ]
        ),
        new(
            Capability: "viewports",
            Equivalent: "one sdf.world instance per view, scheduled by RenderGraphScheduler, each with its own viewport row and scratch, its output placed by the root's place pass",
            Check: "puck parity (one view); the split-seats canary (two views)",
            Green: true,
            Members: [
                "SdfFrame.Views", "SdfViewSnapshot.Camera", "SdfViewSnapshot.Region", "SdfViewSnapshot.AsymmetricFrustumOffset",
                "SdfWorldTables.WriteViewportRow", "SdfWorldTables.ViewportByteLength", "SdfWorldTables.ConeNear",
                "SdfWorldTables.PrimaryMarchSteps", "SdfWorldView.Residency", "SdfWorldView.View", "SdfWorldRenderSpec.Width",
                "SdfWorldRenderSpec.Height",
            ]
        ),
        new(
            Capability: "render scale",
            Equivalent: "a reduced instance extent, reconstructed by the root's place pass",
            Check: null,
            Green: false,
            Members: ["SdfViewSnapshot.RenderScale"]
        ),
        new(
            Capability: "screen slots",
            Equivalent: "the view instance's reads its graph binds to no version, bound as the pass group's screen-source array with a sampler table",
            Check: "RenderGraphRuntimeLawTests (unbound reads); the view-screens canary",
            Green: true,
            Members: [
                "SdfWorldTables.SetScreenBound", "SdfWorldTables.SetScreenSurface", "SdfWorldTables.SetScreenMapping", "SdfWorldTables.SetScreenLight",
                "SdfWorldTables.MaxScreenSurfaces", "SdfWorldRenderSpec.ScreenSources", "SdfWorldResidency.BoundScreenSource",
                "SdfWorldResidency.ScreenImage", "SdfWorldResidency.ScreenSources", "SdfWorldPasses.SamplesReads",
            ]
        ),
        new(
            Capability: "decals",
            Equivalent: "the residency's decal table",
            Check: null,
            Green: false,
            Members: ["SdfWorldTables.SetScreenDecal", "SdfWorldTables.ClearScreenDecal", "SdfWorldTables.MaxScreenDecalCells"]
        ),
        new(
            Capability: "glyph atlas",
            Equivalent: "the residency's glyph atlas",
            Check: null,
            Green: false,
            Members: ["SdfWorldTables.SetGlyphAtlas"]
        ),
        new(
            Capability: "volumes",
            Equivalent: "the residency's volume table, read by the volume shading stage",
            Check: null,
            Green: false,
            Members: ["SdfFrame.Volumes", "SdfWorldTables.MaxVolumes"]
        ),
        new(
            Capability: "lights, sky and tonemap",
            Equivalent: "the residency's screen-light table's environment, the light stage, and P16's display transform for the tonemap",
            Check: "PackEnvironmentLawTests; WorldRenderLightingSkyLawTests; puck parity (sky, materials)",
            Green: false,
            Members: ["SdfFrame.Environment", "SdfFrame.AmbientScale", "SdfFrame.SunScale", "SdfFrame.SampleIndex"]
        ),
        new(
            Capability: "far field",
            Equivalent: "each view's viewport row's far distance and the beam's far bound",
            Check: "WorldRenderFarDistanceLawTests",
            Green: false,
            Members: ["SdfFrame.FarDistance", "SdfFrame.DefaultFarDistance", "SdfFrame.DisableFarBound"]
        ),
        new(
            Capability: "shading levers",
            Equivalent: "staged shading's stage options in the residency's screen-light table",
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
            Members: ["SdfWorldTables.DebugMode", "SdfWorldResidency.DebugMode", "SdfFrame.DebugSliceAxis", "SdfFrame.DebugSliceOffset"]
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
            Equivalent: "the residency's dynamic-transform table, uploaded through GpuRegion by the moved set",
            Check: "WorldSceneMovedTransformsLawTests; SdfMovedTransformsWorkLawTests; SdfWorldTablesUploadLawTests; puck parity (vocabulary)",
            Green: false,
            Members: [
                "SdfFrame.DynamicTransforms", "SdfFrame.MovedTransforms", "SdfWorldTablesOptions.DynamicTransformCapacity",
                "SdfWorldRenderSpec.DynamicTransformCapacity",
            ]
        ),
        new(
            Capability: "brick baking",
            Equivalent: "sdf.bricks, a world-scoped instance joined to the views by buffer edges; the residency's upload bakes today",
            Check: null,
            Green: false,
            Members: [
                "SdfWorldTables.BrickBakeAvailable", "SdfWorldTables.GetBrickState", "SdfWorldTables.RequestBrickBake",
                "SdfWorldTables.UploadBrick", "SdfWorldTables.DefaultBrickPoolVoxelCapacity",
                "SdfWorldTablesOptions.BrickPoolVoxelCapacity", "SdfWorldRenderSpec.BrickPoolVoxelCapacity",
            ]
        ),
        new(
            Capability: "cadence",
            Equivalent: "the scheduler's unchanged instance (RenderGraphFrame.Unchanged): a view whose residency saw nothing it renders from change is not due",
            Check: "RenderGraphSchedulerLawTests (unchanged); RenderGraphRuntimeLawTests (package instances)",
            Green: true,
            Members: [
                "SdfFrame.EnableCadenceGate", "SdfWorldTables.ForcesRender", "SdfWorldTables.ViewSignature",
                "SdfWorldTables.UpdateTablesSignature", "SdfWorldTables.SampleIndex", "SdfWorldResidency.IsUnchanged",
                "SdfWorldResidency.MarkRendered", "SdfWorldPasses.IsUnchanged",
            ]
        ),
        new(
            Capability: "pass labels and counted work",
            Equivalent: "the planner's pass order and per-instance pass counts, and the residency's upload ledger",
            Check: "SdfPassPlanLawTests; SdfWorldTablesWorkLawTests; world-counters canary",
            Green: true,
            Members: [
                "SdfWorldTables.PassLabels", "SdfWorldTables.PassClasses", "SdfWorldTables.Work", "SdfWorldTables.WorkLifetime",
                "SdfWorldTables.DebugLabel", "SdfWorldResidency.Work", "SdfWorldResidency.WorkLifetime", "SdfWorldResidency.Name",
                "SdfWorldTablesOptions.WorkLedger", "SdfWorldRenderSpec.Name",
            ]
        ),
        new(
            Capability: "buffer sizing and uploads",
            Equivalent: "the planner's transient counted buffers and GpuRegion uploads",
            Check: "SdfPassPlanLawTests; RenderGraphFragmentLawTests; SdfWorldTablesUploadLawTests",
            Green: true,
            Members: ["SdfWorldTables.DescriptorPoolSizes", "SdfWorldTables.DescriptorPools", "SdfWorldTables.CheckAdmission"]
        ),
        new(
            Capability: "captures",
            Equivalent: "captures of a view instance's or the root's output, served by the instance's node",
            Check: "RenderGraphRuntimeLawTests; WorldCaptureHoldLawTests; WorldCaptureSchedulerLawTests; puck parity",
            Green: true,
            Members: []
        ),
        new(
            Capability: "kernel variants and reload",
            Equivalent: "the graph's pass-pipeline cache, built off the frame thread",
            Check: "SdfViewsKernelVariantLawTests; SdfWorldPipelineCacheLawTests; SdfPipelineBuildLivenessLawTests",
            Green: false,
            Members: [
                "SdfWorldTables.InstallReload", "SdfWorldResidency.RequestShaderReload", "SdfWorldResidency.ShaderReloadStatus",
                "SdfWorldResidency.IsReady", "SdfWorldResidency.NotReadyReason", "SdfWorldResidency.WaitReady",
            ]
        ),
        new(
            Capability: "output image and export",
            Equivalent: "each view instance's output, and a node's exported output (ShaderPipelineRenderNode.Export) for a probe's view",
            Check: "ShaderPipelineOutputExportLawTests; the probe-sources canary",
            Green: true,
            Members: []
        ),
        new(
            Capability: "mesh draws",
            Equivalent: "the sdf.world mesh pass, rasterizing the frame's draws into the mesh visibility target that bounds primary over the shared visibility record",
            Check: "SdfWorldTablesUploadLawTests; SdfPassPlanLawTests; puck canary sdf-mesh-visibility sdf-mesh-motion",
            Green: true,
            Members: [
                "SdfFrame.MeshDraws", "SdfFrame.MeshDrawsRevision", "SdfWorldResidency.MeshRegionBytes", "SdfWorldResidency.MeshDrawCount",
                "SdfWorldTables.MeshRegionBytes", "SdfWorldTables.MeshRegionLayout", "SdfWorldTables.MeshDrawCount", "SdfWorldTables.MeshAtlas",
            ]
        ),
        new(
            Capability: "assembly and lifetime",
            Equivalent: "SdfWorldResidency, held by its host and every view's passes, and the graph runtime for the views",
            Check: "RenderGraphRuntimeLawTests (package instances); the device-loss-windowed canary",
            Green: true,
            Members: [
                "SdfWorldTables.Dispose", "SdfWorldResidency.Dispose", "SdfWorldResidency.Retain", "SdfWorldResidency.Release", "SdfWorldResidency.IsReleased",
                "SdfWorldResidency.OnDeviceLost", "SdfWorldPasses.OnDeviceLost", "SdfWorldRenderSpec.FrameSource",
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
            expected: SdfWorldTables.PassLabels.ToArray()
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
