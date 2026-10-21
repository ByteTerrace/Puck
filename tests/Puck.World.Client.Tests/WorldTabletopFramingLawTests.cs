using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SdfVm.Views;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: the board view looks down on the whole board from the side the seat stands on. The eye stands back over
/// that side at the authored elevation, the board's footprint stays inside the viewport, the near plane stands short
/// of the board, and the seat's camera eases there and back on the rig's dynamics, or cuts when the rig reports none.
/// </summary>
public sealed class WorldTabletopFramingLawTests {
    private const float FieldOfView = 0.96f;
    private const string TableId = "table";
    private const string TopologyName = "board";

    // A 4 by 4 board of 0.5 m cells whose least corner stands at (-1, 1, -1), so it is centred over the origin, and
    // whose playing surface is 0.1 m above its frame.
    private static readonly WorldTabletopEngage Engage = new(
        Channel: "interact",
        Margin: 0.1d,
        Pitch: 60d,
        Surface: 0.1d
    );

    private static WorldDefinition Document() {
        var creation = CreationFixtures.Sphere(id: TableId, scale: 0.2f);

        return Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementsRaw = new WorldPlacementsSection(
                Policy: Fixtures.StandardAuthoring,
                Rows: [new WorldPlacement(
                    Id: TableId,
                    PrototypeId: creation.Id,
                    Position: Vector3.Zero,
                    YawDegrees: 0f,
                    Scale: 1f,
                    Board: new WorldPlacementBoard(
                        Topology: TopologyName,
                        Occupancy: "cells",
                        Engage: Engage
                    )
                )]
            ),
            StateRaw = new WorldStateSection(
                World: [],
                Lattices: [new LatticeTopology.Grid(
                    Name: TopologyName,
                    Origin: new Vector3(x: -1f, y: 1f, z: -1f),
                    CellSize: 0.5f,
                    Width: 4,
                    Depth: 4
                )]
            ),
        };
    }

    [Fact]
    public void TheEyeLooksDownOnTheWholeBoardFromTheSeatsSide() {
        var topology = WorldTopologyCompilation.Find(
            definition: Document(),
            name: TopologyName
        )!;

        foreach (var (seat, side) in new[] {
            (new Vector3(x: 0f, y: 0f, z: -3f), -Vector3.UnitZ),
            (new Vector3(x: 3f, y: 0f, z: 0.5f), Vector3.UnitX),
            (new Vector3(x: 0.5f, y: 0f, z: 3f), Vector3.UnitZ),
            (new Vector3(x: -3f, y: 0f, z: 0f), -Vector3.UnitX),
        }) {
            Assert.True(condition: WorldTabletopFraming.TryFrame(
                aspectRatio: (16f / 9f),
                engage: Engage,
                eye: out var eye,
                fieldOfViewRadians: FieldOfView,
                near: out var near,
                seatPosition: seat,
                target: out var target,
                topology: topology
            ));
            Assert.Equal(expected: new Vector3(x: 0f, y: 1.1f, z: 0f), actual: target);

            // Back over the seat's side, at the authored elevation.
            var offset = Vector3.Normalize(value: (eye - target));

            Assert.Equal(expected: MathF.Sin(x: (MathF.PI / 3f)), actual: offset.Y, tolerance: 1e-5f);
            Assert.Equal(expected: MathF.Cos(x: (MathF.PI / 3f)), actual: Vector3.Dot(vector1: offset, vector2: side), tolerance: 1e-5f);

            // Every corner of the board draws inside the viewport, beyond the near plane.
            var camera = CameraSnapshot.LookAt(
                fieldOfViewRadians: FieldOfView,
                position: eye,
                target: target,
                viewportHeight: 900f,
                viewportWidth: 1600f
            );

            foreach (var (x, z) in new[] { (-1f, -1f), (-1f, 1f), (1f, -1f), (1f, 1f) }) {
                Assert.True(condition: camera.TryProject(
                    depth: out var depth,
                    ndc: out var ndc,
                    world: new Vector3(x: x, y: 1.1f, z: z)
                ));
                Assert.InRange(actual: MathF.Abs(x: ndc.X), high: 1f, low: 0f);
                Assert.InRange(actual: MathF.Abs(x: ndc.Y), high: 1f, low: 0f);
                Assert.True(condition: (Vector3.Distance(value1: eye, value2: new Vector3(x: x, y: 1.1f, z: z)) > near), userMessage: $"depth {depth}");
            }
        }

        Assert.False(condition: WorldTabletopFraming.TryFrame(
            aspectRatio: 0f,
            engage: Engage,
            eye: out _,
            fieldOfViewRadians: FieldOfView,
            near: out _,
            seatPosition: Vector3.Zero,
            target: out _,
            topology: topology
        ));
    }

    [Fact]
    public void TheCameraEasesToTheBoardViewOnTheRigsDynamicsAndBackAndCutsWithoutThem() {
        var definition = Document();
        var seat = new Vector3(x: 0f, y: 0f, z: -2f);
        var chaseEye = new Vector3(x: 0f, y: 2f, z: -5f);
        var chaseTarget = new Vector3(x: 0f, y: 1f, z: -1f);
        var dynamics = new SdfCameraDynamics(Damping: 1f, Frequency: 4f, Response: 0f);
        var framing = new WorldTabletopFraming();
        var eye = chaseEye;
        var target = chaseTarget;

        // Sitting nowhere, or at a placement that is no tabletop, the seat's camera passes through untouched.
        Assert.Equal(expected: 0f, actual: framing.Apply(aspectRatio: 1.5f, definition: definition, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, placementId: null, seatPosition: seat, target: ref target));
        Assert.Equal(expected: 0f, actual: framing.Apply(aspectRatio: 1.5f, definition: definition, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, placementId: "elsewhere", seatPosition: seat, target: ref target));
        Assert.Equal(expected: chaseEye, actual: eye);

        // Seated, the weight climbs monotonically and settles on the board view.
        var last = 0f;

        for (var frame = 0; (frame < 120); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(aspectRatio: 1.5f, definition: definition, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, placementId: TableId, seatPosition: seat, target: ref target);
            Assert.True(condition: (framing.Weight >= last));
            last = framing.Weight;
        }

        Assert.Equal(expected: 1f, actual: framing.Weight);
        Assert.Equal(expected: new Vector3(x: 0f, y: 1.1f, z: 0f), actual: target);

        // Standing up, it eases back to the seat's camera from the view it last held.
        for (var frame = 0; (frame < 120); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(aspectRatio: 1.5f, definition: definition, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, placementId: null, seatPosition: seat, target: ref target);
        }

        Assert.Equal(expected: 0f, actual: framing.Weight);
        Assert.Equal(expected: chaseEye, actual: eye);

        // A rig reporting no dynamics cuts straight to the board view.
        var near = framing.Apply(aspectRatio: 1.5f, definition: definition, deltaSeconds: (1f / 60f), dynamics: SdfCameraDynamics.None, eye: ref eye, fieldOfViewRadians: FieldOfView, placementId: TableId, seatPosition: seat, target: ref target);

        Assert.Equal(expected: 1f, actual: framing.Weight);
        Assert.True(condition: (near > 0f));
    }
}
