using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a view records exactly one of a baked placement's two draws, the mesh while the placement's bounding sphere is
/// large on screen and the impostor once it is small. The sphere's projected diameter is twice its world radius over the
/// forward depth of its center, in render pixels; the switch is the impostor's view edge in texels; an impostor hands back
/// to its mesh only past that edge by the stated hysteresis; a camera within a radius of the sphere along its axis is
/// never far. A draw without a level of detail is recorded in every view. The choice is made per view from that view's
/// own camera, so one frame's draws serve two views at two distances, and a changed draw list forgets the last frame.
/// </summary>
public sealed class SdfMeshLodLawTests {
    // A standard-tier impostor's view edge, and so its switch, is sixteen pixels.
    private static readonly SdfMeshImpostor Impostor = new(impostor: SdfBaker.Bake(
        center: Vector3.Zero,
        materials: [new(Albedo: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f))],
        program: Sphere(),
        reach: 1.05f,
        tier: SdfBakeTier.For(quality: SdfBakeQuality.Standard)
    ).Impostor);
    // A 1080-line render at a sixty degree vertical field of view.
    private static readonly float PixelsPerUnit = SdfMeshLod.PixelsPerUnitDepth(renderHeight: 1080f, tanHalfFieldOfView: MathF.Tan(x: (MathF.PI / 6f)));

    private static SdfProgram Sphere() {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f)));
        _ = builder.ResetPoint().Sphere(material: 0, radius: 1f);

        return builder.Build(buildInstanceGrid: false);
    }
    // The pair of draws one baked placement emits, the mesh first, standing at the origin under a scale.
    private static SdfMeshDraw[] Pair(float scale, Vector3 position) {
        var mesh = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        var matrix = (Matrix4x4.CreateScale(scale: scale) * Matrix4x4.CreateTranslation(position: position));

        return [
            new(Identity: "near", Material: 0, Mesh: mesh, ObjectToWorld: matrix) { Lod = SdfMeshLod.ForImpostor(far: false, impostor: Impostor) },
            new(Identity: "far", Material: 0, Mesh: SdfMeshCard.Mesh, ObjectToWorld: matrix) { Impostor = Impostor, Lod = SdfMeshLod.ForImpostor(far: true, impostor: Impostor) },
        ];
    }
    // The depth at which the sphere of a draw projects to a diameter.
    private static float DepthOf(SdfMeshDraw draw, float pixels) =>
        ((((2f * draw.Lod!.Value.Radius) * new Vector3(x: draw.ObjectToWorld.M11, y: draw.ObjectToWorld.M12, z: draw.ObjectToWorld.M13).Length()) * PixelsPerUnit) / pixels);
    // What a view whose camera stands at `depth` before the origin, looking along +Z, records of a draw list.
    private static bool[] Select(SdfMeshLodSelector selector, SdfMeshDraw[] draws, float depth, long revision = 0L) {
        var recorded = new bool[draws.Length];

        selector.Select(
            cameraForward: Vector3.UnitZ,
            cameraPosition: new Vector3(x: 0f, y: 0f, z: -depth),
            draws: draws,
            pixelsPerUnitDepth: PixelsPerUnit,
            recorded: recorded,
            revision: revision
        );

        return recorded;
    }
    private static SdfMeshLodSelector Selector() => new(work: new WorkCounterSet(kinds: SdfMeshLodSelector.ProcessWork.WorkKinds, name: SdfMeshLodSelector.SourceName));

    [Fact]
    public void TheSwitchIsTheImpostorsViewEdgeInPixels() {
        Assert.Equal(expected: 16f, actual: Impostor.ViewTexels);
        Assert.Equal(expected: 16f, actual: SdfMeshLod.ForImpostor(far: true, impostor: Impostor).SwitchPixels);
    }
    [Fact]
    public void TheProjectedDiameterIsTwiceTheWorldRadiusOverTheForwardDepthOfTheCenterInPixels() {
        var lod = SdfMeshLod.ForImpostor(far: true, impostor: Impostor);
        var matrix = (Matrix4x4.CreateScale(scale: 3f) * Matrix4x4.CreateTranslation(position: new Vector3(x: 7f, y: 1f, z: 50f)));
        var pixels = lod.ProjectedPixels(cameraForward: Vector3.UnitZ, cameraPosition: Vector3.Zero, objectToWorld: matrix, pixelsPerUnitDepth: PixelsPerUnit);

        Assert.Equal(expected: ((((2f * Impostor.Radius) * 3f) * PixelsPerUnit) / 50f), actual: pixels, precision: 3);
        Assert.Equal(expected: (1080f / (2f * MathF.Tan(x: (MathF.PI / 6f)))), actual: PixelsPerUnit, precision: 2);

        // Twice as far reads half as large; sliding sideways at one depth reads the same.
        Assert.Equal(expected: (pixels / 2f), actual: lod.ProjectedPixels(cameraForward: Vector3.UnitZ, cameraPosition: new Vector3(x: 0f, y: 0f, z: -50f), objectToWorld: matrix, pixelsPerUnitDepth: PixelsPerUnit), precision: 3);
        Assert.Equal(expected: pixels, actual: lod.ProjectedPixels(cameraForward: Vector3.UnitZ, cameraPosition: new Vector3(x: 20f, y: -4f, z: 0f), objectToWorld: matrix, pixelsPerUnitDepth: PixelsPerUnit), precision: 3);
    }
    [Fact]
    public void ACameraWithinARadiusOfTheSphereOrBeyondItIsNeverFar() {
        var lod = SdfMeshLod.ForImpostor(far: true, impostor: Impostor);
        var radius = Impostor.Radius;

        Assert.True(condition: float.IsPositiveInfinity(f: lod.ProjectedPixels(cameraForward: Vector3.UnitZ, cameraPosition: Vector3.Zero, objectToWorld: Matrix4x4.CreateTranslation(position: new Vector3(x: 0f, y: 0f, z: (radius * 0.5f))), pixelsPerUnitDepth: PixelsPerUnit)));
        Assert.True(condition: float.IsPositiveInfinity(f: lod.ProjectedPixels(cameraForward: Vector3.UnitZ, cameraPosition: Vector3.Zero, objectToWorld: Matrix4x4.CreateTranslation(position: new Vector3(x: 0f, y: 0f, z: -100f)), pixelsPerUnitDepth: PixelsPerUnit)));
        Assert.False(condition: lod.SelectFar(pixels: float.PositiveInfinity, wasFar: true));
        Assert.False(condition: lod.SelectFar(pixels: float.NaN, wasFar: false));
    }
    [Fact]
    public void AnImpostorHandsBackToItsMeshOnlyPastTheSwitchByTheHysteresis() {
        var lod = SdfMeshLod.ForImpostor(far: true, impostor: Impostor);
        var edge = lod.SwitchPixels;

        Assert.True(condition: lod.SelectFar(pixels: (edge - 0.01f), wasFar: false));
        Assert.False(condition: lod.SelectFar(pixels: edge, wasFar: false));
        Assert.False(condition: lod.SelectFar(pixels: (edge + 0.01f), wasFar: false));
        Assert.True(condition: lod.SelectFar(pixels: ((edge * (1f + SdfMeshLod.Hysteresis)) - 0.01f), wasFar: true));
        Assert.False(condition: lod.SelectFar(pixels: (edge * (1f + SdfMeshLod.Hysteresis)), wasFar: true));
    }
    [Fact]
    public void ExactlyOneOfThePairIsRecordedAtEveryDepthAndTheHandoverFollowsTheSwitch() {
        var draws = Pair(scale: 1f, position: Vector3.Zero);
        var selector = Selector();
        var nearDepth = DepthOf(draw: draws[0], pixels: 24f);
        var farDepth = DepthOf(draw: draws[0], pixels: 8f);
        var handoverDepth = DepthOf(draw: draws[0], pixels: 16f);
        var seen = new List<bool>();

        // Walk the camera from near to far: the mesh, until the diameter passes sixteen pixels, then the impostor.
        for (var step = 0; (step <= 40); step++) {
            var depth = (nearDepth + ((farDepth - nearDepth) * (step / 40f)));
            var recorded = Select(depth: depth, draws: draws, selector: selector);

            Assert.True(condition: recorded[0] ^ recorded[1], userMessage: $"depth {depth}: {recorded[0]} {recorded[1]}");
            seen.Add(item: recorded[1]);
            Assert.Equal(expected: (depth > handoverDepth), actual: recorded[1]);
        }

        Assert.False(condition: seen[0]);
        Assert.True(condition: seen[^1]);
        Assert.Equal(expected: 1, actual: seen.Zip(second: seen.Skip(count: 1)).Count(predicate: static pair => (pair.First != pair.Second)));

        // Walking back, the impostor holds through the hysteresis band and hands to the mesh once past it.
        var back = new List<float>();

        for (var step = 40; (step >= 0); step--) {
            var depth = (nearDepth + ((farDepth - nearDepth) * (step / 40f)));
            var recorded = Select(depth: depth, draws: draws, selector: selector);

            Assert.True(condition: recorded[0] ^ recorded[1]);

            if (!recorded[1]) {
                back.Add(item: depth);
            }
        }

        Assert.True(condition: (back[0] <= ((handoverDepth / (1f + SdfMeshLod.Hysteresis)) + 0.5f)), userMessage: $"handed back at {back[0]}, past the band");
        Assert.True(condition: (back[0] < handoverDepth));
    }
    [Fact]
    public void ACameraHoveringAtTheSwitchDoesNotAlternate() {
        var draws = Pair(scale: 1f, position: Vector3.Zero);
        var selector = Selector();
        var edgeDepth = DepthOf(draw: draws[0], pixels: 16f);
        var far = 0;
        var near = 0;

        // Far first, then back and forth across the switch inside the hysteresis band: the impostor holds.
        _ = Select(depth: (edgeDepth * 1.1f), draws: draws, selector: selector);

        for (var frame = 0; (frame < 20); frame++) {
            var recorded = Select(depth: (edgeDepth * (((frame % 2) == 0) ? 0.96f : 1.04f)), draws: draws, selector: selector);

            far += (recorded[1] ? 1 : 0);
            near += (recorded[0] ? 1 : 0);
        }

        Assert.Equal(actual: (far, near), expected: (20, 0));
    }
    [Fact]
    public void ADrawWithoutALodIsRecordedInEveryViewAndTwoViewsChooseApart() {
        var mesh = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        SdfMeshDraw[] draws = [new(Identity: "plain", Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity), .. Pair(scale: 1f, position: Vector3.Zero)];
        var far = Select(depth: 400f, draws: draws, selector: Selector());
        var close = Select(depth: 4f, draws: draws, selector: Selector());

        Assert.Equal(actual: far, expected: [true, false, true]);
        Assert.Equal(actual: close, expected: [true, true, false]);
    }
    [Fact]
    public void ANewRevisionForgetsTheLastChoiceAndTheCountsFollowTheRecordedDraws() {
        var counts = new WorkCounterSet(kinds: SdfMeshLodSelector.ProcessWork.WorkKinds, name: SdfMeshLodSelector.SourceName);
        var selector = new SdfMeshLodSelector(work: counts);
        var draws = Pair(scale: 1f, position: Vector3.Zero);
        var edgeDepth = DepthOf(draw: draws[0], pixels: 16f);

        Assert.Equal(expected: [false, true], actual: Select(depth: (edgeDepth * 1.1f), draws: draws, selector: selector));
        // Inside the band an impostor holds; the same draws at a new revision are chosen afresh, from the near side.
        Assert.Equal(expected: [false, true], actual: Select(depth: (edgeDepth * 0.96f), draws: draws, selector: selector));
        Assert.Equal(expected: [true, false], actual: Select(depth: (edgeDepth * 0.96f), draws: draws, revision: 1L, selector: selector));
        Assert.Equal(expected: (1L, 2L), actual: (counts.Read(kind: SdfMeshLodSelector.Near), counts.Read(kind: SdfMeshLodSelector.Far)));
    }
}
