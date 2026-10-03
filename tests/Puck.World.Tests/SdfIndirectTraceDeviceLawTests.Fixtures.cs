using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Tests;

public sealed partial class SdfIndirectTraceDeviceLawTests {
    private static ProbeCase[] Cases() => [
        new("an instanced sphere stores its hit and material", Sphere(), ProbeMode.Trace, Vector3.Zero, Vector3.UnitX),
        new("a plane stores its hit", Floor(), ProbeMode.Trace, new Vector3(x: 0, y: 1, z: 0), -Vector3.UnitY),
        new("a surface at the ray endpoint is still hit", Floor(), ProbeMode.Trace, new Vector3(x: 0, y: 1, z: 0), -Vector3.UnitY, Far: 1),
        new("a parallel ray stays unresolved", Floor(), ProbeMode.Trace, new Vector3(x: 0, y: 0.0015f, z: 0), Vector3.UnitX),
        new("a clear ray exits", Sphere(), ProbeMode.Trace, Vector3.Zero, -Vector3.UnitX),
        new("a probe near the floor is active", Floor(), ProbeMode.Place, new Vector3(x: 0, y: 0.8f, z: 0), Vector3.Zero),
        new("a surface cell keeps its distant corner active", Sphere(), ProbeMode.Place, Vector3.Zero, Vector3.Zero),
        new("a probe far from surfaces is dormant", Floor(), ProbeMode.Place, new Vector3(x: 0, y: 4, z: 0), Vector3.Zero),
        new("a probe at the floor relocates", Floor(), ProbeMode.Place, new Vector3(x: 0, y: 0.03f, z: 0), Vector3.Zero),
        new("a probe deep in the floor is inactive", Floor(), ProbeMode.Place, new Vector3(x: 0, y: -0.8f, z: 0), Vector3.Zero),
        new("a thin wall partitions opposite corners", Wall(), ProbeMode.Partition, Vector3.Zero, Vector3.Zero),
        new("a slanted wall fits its actual blocking surface", SlantedWall(), ProbeMode.Partition, Vector3.Zero, Vector3.Zero),
        new("a pocket leaves exterior corners connected", Pocket(), ProbeMode.Partition, Vector3.Zero, Vector3.Zero),
        new("inactive corners never join a partition", Floor(), ProbeMode.Partition, new Vector3(x: 0, y: -1.5f, z: 0), Vector3.Zero),
        new("a floor certifies an outward launch", Floor(), ProbeMode.Launch, Vector3.Zero, Vector3.UnitY),
        new("a slab stops the outward launch", Slab(center: 0.0015f, halfHeight: 0.0003f), ProbeMode.Launch, Vector3.Zero, Vector3.UnitY),
        new("a slab beneath the first sample refuses launch", Slab(center: 0.0001f, halfHeight: 0.00005f), ProbeMode.Launch, Vector3.Zero, Vector3.UnitY),
        new("an exterior proof reaches a free corner", Wall(), ProbeMode.Segment, new Vector3(x: 0, y: 0, z: 0), new Vector3(x: 0, y: 1.5f, z: 1.5f)),
        new("a wall blocks the feedback proof", Wall(), ProbeMode.Segment, new Vector3(x: 0, y: 0, z: 0), new Vector3(x: 1.5f, y: 0, z: 0)),
        new("a sealed pocket blocks its exterior proof", Pocket(), ProbeMode.Segment, new Vector3(value: 0.3f), Vector3.Zero),
        new("a segment ending on a surface is blocked", Floor(), ProbeMode.Segment, Vector3.UnitY, Vector3.Zero),
        new("continuation rejects an endpoint behind the handoff", Floor(), ProbeMode.EndpointSupport, Vector3.Zero, new Vector3(x: -2, y: 0, z: 0)),
        new("continuation rejects a perpendicular endpoint", Floor(), ProbeMode.EndpointSupport, Vector3.Zero, new Vector3(x: 0, y: 2, z: 0)),
        new("continuation rejects an endpoint outside its angle", Floor(), ProbeMode.EndpointSupport, Vector3.Zero, new Vector3(x: 1, y: 1, z: 0)),
        new("continuation accepts a supported endpoint ahead", Floor(), ProbeMode.EndpointSupport, Vector3.Zero, new Vector3(x: 2, y: 0.2f, z: 0)),
        new("an encoded pole remains distinct from no normal", Floor(), ProbeMode.NormalCodec, Vector3.Zero, new Vector3(x: -1.0e-8f, y: -1.0e-8f, z: -1)),
        new("no normal keeps its zero sentinel", Floor(), ProbeMode.NormalCodec, Vector3.Zero, Vector3.Zero),
        new("a partial launch reconstructs its proved point", Slab(center: 0.0015f, halfHeight: 0.0003f), ProbeMode.LaunchRecord, Vector3.Zero, Vector3.UnitY),
        new("an oblique partial launch reconstructs its proved point", ObliqueSlab(), ProbeMode.LaunchRecord, Vector3.Zero, new Vector3(x: 0.6f, y: 0.8f, z: 0)),
        new("anchor bins address every owned proof entry with exact keys", Floor(), ProbeMode.ProofEntries, Vector3.Zero, Vector3.Zero),
        new("two certified balls reuse the anchor proof", Floor(), ProbeMode.ProofReuse, new Vector3(x: 0.06f, y: 0.02f, z: 0.02f), new Vector3(value: 0.02f), Reach: 0.015f, Far: 0.03f, ExpectedReuse: true),
        new("disjoint balls require another proof", Floor(), ProbeMode.ProofReuse, new Vector3(x: 0.1f, y: 0.02f, z: 0.02f), new Vector3(value: 0.02f), Reach: 0.02f, Far: 0.03f, ExpectedReuse: false),
        new("another anchor bin cannot reuse a colliding entry", Floor(), ProbeMode.ProofReuse, new Vector3(x: 0.2f, y: 0.02f, z: 0.02f), new Vector3(value: 0.02f), Reach: 0.2f, Far: 0.2f, ExpectedReuse: false),
        new("another cell cannot reuse an overlapping proof", Floor(), ProbeMode.ProofReuse, new Vector3(x: 1.52f, y: 0.02f, z: 0.02f), new Vector3(value: 0.02f), Reach: 2, Far: 2, ExpectedReuse: false),
        new("a receiver without clearance stays inside the anchor ball", Floor(), ProbeMode.ProofReuse, new Vector3(x: 0.04f, y: 0.02f, z: 0.02f), new Vector3(value: 0.02f), Reach: 0.03f, Far: 0, ExpectedReuse: true),
        new("placement clears every owned proof bucket", Floor(), ProbeMode.StoreCell, Vector3.Zero, Vector3.Zero, Reach: 0),
        new("partition clears every owned proof bucket", Wall(), ProbeMode.StoreCell, Vector3.Zero, Vector3.Zero, Reach: 1),
        new("proof publication is visible only in a later submission", Floor(), ProbeMode.ProofPublication, new Vector3(value: 0.02f), Vector3.Zero),
    ];
    private static SdfProgramBuilder Builder(bool grid = false) {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.4f, z: 0.6f)));
        if (grid) {
            for (var index = 0; (index < 32); index++) {
                var center = new Vector3(x: (-24 + ((index % 8) * 3)), y: (10 + ((index / 8) * 3)), z: 0);

                _ = builder.BeginInstance(boundCenter: center, boundRadius: 0.2f)
                    .ResetPoint().Translate(offset: center).Sphere(material: 0, radius: 0.2f).EndInstance();
            }
        }
        return builder;
    }
    private static SdfProgram Sphere() {
        var builder = Builder(grid: true);
        var center = new Vector3(x: 3, y: 0, z: 0);

        _ = builder.BeginInstance(boundCenter: center, boundRadius: 0.5f)
            .ResetPoint().Translate(offset: center).Sphere(material: 1, radius: 0.5f).EndInstance();
        return builder.Build();
    }
    private static SdfProgram Floor() {
        var builder = Builder();

        _ = builder.Plane(material: 1, normal: Vector3.UnitY, offset: 0);
        return builder.Build();
    }
    private static SdfProgram Wall() {
        var builder = Builder();

        _ = builder.Translate(offset: new Vector3(x: 0.75f, y: 0, z: 0)).Box(halfExtents: new Vector3(x: 0.025f, y: 3, z: 3), material: 1, round: 0);
        return builder.Build();
    }
    private static SdfProgram Pocket() {
        var builder = Builder();

        _ = builder.Translate(offset: new Vector3(value: 0.3f)).Sphere(material: 1, radius: 0.1f)
            .Sphere(blend: SdfBlendOp.Subtraction, material: 1, radius: 0.08f);
        return builder.Build();
    }
    private static SdfProgram SlantedWall() {
        var builder = Builder();

        _ = builder.Translate(offset: new Vector3(value: 0.75f)).Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: 0.3f))
            .Box(halfExtents: new Vector3(x: 0.025f, y: 3, z: 3), material: 1, round: 0);
        return builder.Build();
    }
    private static SdfProgram Slab(float center, float halfHeight) {
        var builder = Builder();

        _ = builder.Plane(material: 1, normal: Vector3.UnitY, offset: 0)
            .ResetPoint().Translate(offset: new Vector3(x: 0, y: center, z: 0))
            .Box(halfExtents: new Vector3(x: 1, y: halfHeight, z: 1), material: 1, round: 0);
        return builder.Build();
    }
    private static SdfProgram ObliqueSlab() {
        var normal = new Vector3(x: 0.6f, y: 0.8f, z: 0);
        var builder = Builder();

        _ = builder.Plane(material: 1, normal: normal, offset: 0)
            .ResetPoint().Translate(offset: (normal * 0.0015f))
            .Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: -MathF.Atan2(x: normal.Y, y: normal.X)))
            .Box(halfExtents: new Vector3(x: 1, y: 0.0003f, z: 1), material: 1, round: 0);
        return builder.Build();
    }
}
