using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the parity world's shipped bakes reach its captures as meshes. The only station that shows a baked
/// placement is <c>vocabulary</c>, whose camera stands thirty units from the six static creations. The bounding sphere of
/// each placement that draws its bake projects there to at least twice the impostor's sixteen-pixel switch, so the view
/// records the mesh of every pair and not its card, and the station's pixels, which parity compares between backends,
/// never depend on an impostor. The other stations film from elsewhere: their cameras look away from the placements, which
/// stand five hundred and more units from the origin along +X. A bake's impostor therefore changes no parity capture, and
/// nothing in the parity world's contract is re-recorded for it.
/// </summary>
public sealed class ParityBakeSelectionLawTests {
    // The vocabulary station's pose, as parity.puck authors it: anchor, look-at point and field of view, filmed at
    // 640x480.
    private static readonly Vector3 Eye = new(x: 604f, y: 9f, z: -26f);
    private static readonly Vector3 Target = new(x: 604f, y: 2f, z: 0f);

    private const float FieldOfView = 1.4f;
    private const float RenderHeight = 480f;

    [Fact]
    public void EveryBakedPlacementOfTheVocabularyStationDrawsItsMesh() {
        var definition = AuthoredGameFixtures.Load(relativePath: "tests/Puck.Parity/parity.puck");
        using var schedule = new WorldBakeSchedule(store: new WorldBakeStore());
        var deadline = (DateTime.UtcNow + TimeSpan.FromMinutes(value: 5));

        schedule.Pump(definition: definition);

        while (schedule.IsBusy) {
            Assert.True(condition: (DateTime.UtcNow < deadline), userMessage: "the parity world's bakes did not finish");
            Thread.Sleep(millisecondsTimeout: 1);
            schedule.Pump(definition: definition);
        }

        var draws = new List<SdfMeshDraw>();

        WorldPlacementStamper.EmitStatic(
            bakedFor: prototypeId => (schedule.TryGetDraw(draw: out var baked, prototypeId: prototypeId) ? baked : null),
            builder: new SdfProgramBuilder(),
            creations: definition.Creations,
            definition: definition,
            meshDraws: draws,
            placements: definition.Placements
        );

        // Each placement that draws its bake emits a pair; one whose creation carries noise relief, which a bake does not hold,
        // draws its field.
        Assert.True(condition: ((draws.Count > 0) && ((draws.Count % 2) == 0)), userMessage: $"{draws.Count} draws");

        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: FieldOfView, position: Eye, target: Target, viewportHeight: 480u, viewportWidth: 640u);
        var perUnit = SdfMeshLod.PixelsPerUnitDepth(renderHeight: RenderHeight, tanHalfFieldOfView: camera.TanHalfFieldOfView);

        foreach (var draw in draws.Where(predicate: static draw => (draw.Lod is { Far: false }))) {
            var pixels = draw.Lod!.Value.ProjectedPixels(cameraForward: camera.Forward, cameraPosition: camera.Position, objectToWorld: draw.ObjectToWorld, pixelsPerUnitDepth: perUnit);

            Assert.True(condition: (pixels >= (2f * draw.Lod!.Value.SwitchPixels)), userMessage: $"{draw.Identity}: {pixels} pixels across");
        }

        var recorded = new bool[draws.Count];

        new SdfMeshLodSelector(work: new Puck.Abstractions.Counting.WorkCounterSet(kinds: SdfMeshLodSelector.ProcessWork.WorkKinds, name: SdfMeshLodSelector.SourceName)).Select(
            cameraForward: camera.Forward,
            cameraPosition: camera.Position,
            draws: draws,
            impostorsAvailable: true,
            pixelsPerUnitDepth: perUnit,
            recorded: recorded
        );

        for (var index = 0; (index < draws.Count); index++) {
            Assert.Equal(expected: (draws[index].Lod is { Far: false }), actual: recorded[index]);
        }

        // The other stations' placements are out of view: every placement stands at least 588 units along +X, and every
        // other station's camera looks along the Z axis or down it from the origin's side, within a field of view well under
        // ninety degrees of it.
        Assert.All(collection: definition.Placements, action: static placement => Assert.True(condition: (placement.Position.Value.X >= 588f)));
    }
}
