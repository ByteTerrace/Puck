using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// Receiver proofs are world-space and geometry-static: a still view proves each receiver once, a frame issues at most
// its allowance, a receiver past the allowance reads no light rather than unproven light, and a cached proof is reused
// only where the receiver's certified ball meets the proof's anchor, so a sealed pocket beside a proven anchor stays
// dark.
public sealed class IrradianceProofLawTests {
    private static readonly IrradianceLevel Room = new(Name: "room", Radius: 0.0, Reach: 0.0, Spacing: 1.5, Strata: 2);
    private static readonly Double3 Up = new(X: 0.0, Y: 1.0, Z: 0.0);

    [Fact]
    public void AStillViewProvesEachReceiverOnce() {
        var model = SealedRoom(allowance: int.MaxValue);
        var receivers = Receivers();

        model.BeginFrame();

        var first = receivers.Select(selector: receiver => model.Irradiance(normal: receiver.Normal, surface: receiver.Point)).ToArray();
        var issued = model.ProofsIssued;

        model.BeginFrame();

        var second = receivers.Select(selector: receiver => model.Irradiance(normal: receiver.Normal, surface: receiver.Point)).ToArray();

        Assert.True(condition: (issued > 0));
        Assert.Equal(expected: issued, actual: model.ProofsIssued);
        Assert.Equal(actual: second, expected: first);
        Assert.True(condition: (model.ProofsReused >= receivers.Length));
    }
    [Fact]
    public void AFrameIssuesAtMostItsAllowanceAndTheRestDarken() {
        var model = SealedRoom(allowance: 2);
        var full = SealedRoom(allowance: int.MaxValue);
        var receivers = Receivers();

        full.BeginFrame();

        var expected = receivers.Select(selector: receiver => full.Irradiance(normal: receiver.Normal, surface: receiver.Point)!.Value.X).ToArray();
        var frames = 0;

        while (true) {
            model.BeginFrame();
            frames++;

            var before = model.ProofsIssued;
            var values = receivers.Select(selector: receiver => model.Irradiance(normal: receiver.Normal, surface: receiver.Point)!.Value.X).ToArray();

            Assert.True(condition: ((model.ProofsIssued - before) <= 2));

            for (var index = 0; (index < values.Length); index++) {
                // A receiver reads either its proven light or nothing, never more than its proven light.
                Assert.True(condition: ((values[index] == expected[index]) || (values[index] == 0.0)));
            }

            if (values.SequenceEqual(second: expected)) {
                break;
            }

            Assert.True(condition: (frames < 20));
        }

        Assert.True(condition: (frames > 1));
        Assert.True(condition: (model.LookupsDeferred > 0));
    }
    [Fact]
    public void AProvenAnchorBesideASealedPocketDoesNotLightIt() {
        // A tiny sealed shell (inner radius 0.08, wall 0.02) inside one cell, in a hall lit by its sky. A receiver on
        // its outer face is proven first; one on its inner face, in the same proof slot, must not reuse that proof.
        var center = new Double3(X: 0.3, Y: 0.3, Z: 0.3);
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.SphereRoom(center: center, radius: 0.08, thickness: 0.02),
            levels: [Room],
            options: new IrradianceModelOptions(ExitDistance: 60.0),
            surfaces: IrradianceScenes.Uniform(albedo: 0.5, emission: 0.0, sky: 1.0)
        );

        model.Allocate(level: 0, max: new Double3(X: 4.0, Y: 4.0, Z: 4.0), min: new Double3(X: -4.0, Y: -4.0, Z: -4.0));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 1);
        model.BeginFrame();

        // The outer receiver faces away along (-1, -1, -1), so its launched point lands in the same proof slot as the
        // inner receiver's, beside the pocket: the two balls do not meet, so the inner receiver proves its own way.
        var away = new Double3(X: -1.0, Y: -1.0, Z: -1.0).Normalize();
        var outside = model.Irradiance(normal: away, surface: (center + (away * 0.1)));
        var inside = model.Irradiance(normal: -Up, surface: (center + new Double3(X: 0.0, Y: 0.08, Z: 0.0)));

        Assert.True(condition: (outside!.Value.X > 0.5));
        Assert.True(condition: ((inside is null) || (inside.Value.X == 0.0)));

        // Red leg: a model without the partition reads the outside corners from inside the pocket.
        var leaky = new IrradianceCacheModel(
            field: IrradianceScenes.SphereRoom(center: center, radius: 0.08, thickness: 0.02),
            levels: [Room],
            options: new IrradianceModelOptions(ExitDistance: 60.0, Partition: false),
            surfaces: IrradianceScenes.Uniform(albedo: 0.5, emission: 0.0, sky: 1.0)
        );

        leaky.Allocate(level: 0, max: new Double3(X: 4.0, Y: 4.0, Z: 4.0), min: new Double3(X: -4.0, Y: -4.0, Z: -4.0));
        leaky.Classify();
        leaky.Trace();
        leaky.Solve(bounces: 1);
        leaky.BeginFrame();

        Assert.True(condition: (leaky.Irradiance(normal: -Up, surface: (center + new Double3(X: 0.0, Y: 0.08, Z: 0.0)))!.Value.X > 0.5));
    }

    // A sealed room beside a lit exterior, its receivers on both faces of its walls.
    private static IrradianceCacheModel SealedRoom(int allowance) {
        var center = new Double3(X: 0.3, Y: 1.7, Z: 0.2);
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.Room(center: center, interiorHalf: new Double3(X: 2.0, Y: 1.5, Z: 2.0), thickness: 0.05),
            levels: [Room],
            options: new IrradianceModelOptions(ExitDistance: 60.0, ProofAllowance: allowance),
            surfaces: IrradianceScenes.Uniform(albedo: 0.5, emission: 0.0, sky: 1.0)
        );

        model.Allocate(level: 0, max: (center + new Double3(X: 5.0, Y: 5.0, Z: 5.0)), min: (center - new Double3(X: 5.0, Y: 5.0, Z: 5.0)));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 1);

        return model;
    }
    private static (Double3 Point, Double3 Normal)[] Receivers() {
        var center = new Double3(X: 0.3, Y: 1.7, Z: 0.2);

        return [
            ((center + new Double3(X: -1.99, Y: -1.5, Z: 0.3)), Up),
            ((center + new Double3(X: 0.2, Y: -1.5, Z: -0.4)), Up),
            ((center + new Double3(X: -2.0, Y: 0.1, Z: 0.7)), new Double3(X: 1.0, Y: 0.0, Z: 0.0)),
            ((center + new Double3(X: -2.05, Y: 0.0, Z: 0.0)), new Double3(X: -1.0, Y: 0.0, Z: 0.0)),
            ((center + new Double3(X: 2.05, Y: 0.3, Z: -1.0)), new Double3(X: 1.0, Y: 0.0, Z: 0.0)),
        ];
    }
}
