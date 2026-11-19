using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshIrradianceLawTests {
    /// <summary>THE LAW: a room's inside reads none of the probes standing outside its walls. Traced over the debug world's
    /// Wonderland room on open ground (walls 0.3 m thick under a roof, the room's floor 5 cm over the ground), every receiver
    /// along the floor at the north wall's foot, low on that wall, and on both walls beside the north-west corner, takes
    /// its whole gather from probes inside the room, at both tiers. An outdoor probe's ground taps in front of the wall's
    /// base, and its taps running clear past the wall's end, carry no evidence of a view through the wall
    /// (<c>SdfMeshProbeDepth.Unresolved</c>); counted as clear, they lit a fifth of the floor's gather along the wall and a
    /// quarter of the west wall's beside the corner with sky at Medium.</summary>
    /// <param name="tier">The indirect tier.</param>
    [Theory]
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    public void ARoomsWallFootAndCornerReadNoProbeOutsideTheRoom(SdfIndirectTier tier) {
        var volume = Volume();

        _ = Trace(frame: Room() with { IndirectTier = tier }, tier: tier, volume: volume);

        var cells = new (int X, int Y, int Z)[SdfMeshIrradiance.GatherSlots];
        var weights = new float[SdfMeshIrradiance.GatherSlots];
        var reports = new SdfMeshProbeReport[SdfMeshIrradiance.GatherSlots];
        var worst = 0f;
        var worstAt = Vector3.Zero;

        void Read(Vector3 point, Vector3 normal) {
            Assert.True(condition: volume.TryGather(cells: cells, normal: normal, point: point, reports: reports, selection: out _, weights: weights));
            var outdoor = 0f;

            for (var slot = 0; (slot < SdfMeshIrradiance.GatherSlots); slot++) {
                var at = reports[slot].Position;

                if ((weights[slot] > 0f) && !((at.X > 1f) && (at.X < 9.4f) && (at.Y > 0.05f) && (at.Y < 4.55f) && (at.Z > 24f) && (at.Z < 34f))) { outdoor += weights[slot]; }
            }
            if (outdoor > worst) { (worst, worstAt) = (outdoor, point); }
        }
        for (var x = 1.5f; (x <= 8.5f); x += 0.5f) {
            foreach (var inset in new[] { 0.01f, 0.05f, 0.1f, 0.2f, 0.4f }) { Read(point: new Vector3(x: x, y: 0.05f, z: (34f - inset)), normal: Vector3.UnitY); }
            foreach (var y in new[] { 0.1f, 0.2f, 0.4f, 0.8f }) { Read(point: new Vector3(x: x, y: y, z: 34f), normal: -Vector3.UnitZ); }
        }
        for (var y = 0.1f; (y <= 4.5f); y += 0.2f) {
            foreach (var inset in new[] { 0.01f, 0.05f, 0.1f, 0.2f }) {
                Read(point: new Vector3(x: 1f, y: y, z: (34f - inset)), normal: Vector3.UnitX);
                Read(point: new Vector3(x: (1f + inset), y: y, z: 34f), normal: -Vector3.UnitZ);
            }
        }
        Assert.True(condition: (worst <= 0.001f), userMessage: $"{tier}: outdoor share {worst} at {worstAt}: {string.Join(separator: " | ", values: volume.DescribeProbes(normal: Vector3.UnitY, point: worstAt))}");
    }

    // The debug world's Wonderland room (worlds/debug/wonderland.puck): floor, four walls 0.3 m thick with a doorway in the
    // south one, and a roof, on open ground 5 cm under the room's floor; no light but the sky. The camera stands where the
    // rendering skill's teal corner check poses it.
    private static SdfFrame Room() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.6f)));

        builder.Sphere(material: material, radius: 0.1f);

        var lights = new SdfLights { Count = 0 };

        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);

        var gains = new SdfIndirectGains(Emission: 0f, Feedback: 1f, Lights: 1f, Screens: 0f, Sky: 1f);
        (Vector3 Low, Vector3 High)[] boxes = [
            (new Vector3(x: -40f, y: -0.5f, z: -20f), new Vector3(x: 50f, y: 0f, z: 80f)),
            (new Vector3(x: 0.7f, y: -0.2f, z: 23.7f), new Vector3(x: 9.7f, y: 0.05f, z: 34.3f)),
            (new Vector3(x: 9.4f, y: 0.05f, z: 23.7f), new Vector3(x: 9.7f, y: 4.55f, z: 34.3f)),
            (new Vector3(x: 0.7f, y: 0.05f, z: 23.7f), new Vector3(x: 1f, y: 4.55f, z: 34.3f)),
            (new Vector3(x: 1f, y: 0.05f, z: 34f), new Vector3(x: 9.4f, y: 4.55f, z: 34.3f)),
            (new Vector3(x: 1f, y: 0.05f, z: 23.7f), new Vector3(x: 3.5f, y: 4.55f, z: 24f)),
            (new Vector3(x: 5.5f, y: 0.05f, z: 23.7f), new Vector3(x: 9.4f, y: 4.55f, z: 24f)),
            (new Vector3(x: 3.5f, y: 3.05f, z: 23.7f), new Vector3(x: 5.5f, y: 4.55f, z: 24f)),
            (new Vector3(x: 0.7f, y: 4.55f, z: 23.7f), new Vector3(x: 9.7f, y: 4.85f, z: 34.3f)),
        ];

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: true,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 5.929f, y: 2.188f, z: 27.222f), target: new Vector3(x: 4.6f, y: 1.3f, z: 29.76f), viewportHeight: 4u, viewportWidth: 4u), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))],
            Time: 0f
        ) {
            ConvergesLighting = true,
            FarDistance = 60f,
            IndirectBounces = 0,
            IndirectGains = gains,
            IndirectSources = gains.Sources,
            Lights = lights,
            MeshDraws = [.. boxes.Select(selector: static box => Draw(mesh: Box(low: box.Low, high: box.High)))],
        };
    }
}
