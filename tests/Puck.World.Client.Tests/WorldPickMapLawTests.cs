using Xunit;

namespace Puck.World.Client.Tests;

public sealed class WorldPickMapLawTests {
    [Fact]
    public void ADelayedAnswerKeepsItsCapturedIdentityAcrossOrdinalReuse() {
        var builder = new WorldPickMapBuilder();
        var first = new WorldPickTarget(BodyIndex: null, Placement: "first") { Prototype = "cube" };
        var next = new WorldPickTarget(BodyIndex: null, Placement: "replacement") { Prototype = "sphere" };
        var body = new WorldPickTarget(BodyIndex: 7, Placement: null);
        WorldPickTarget?[] pool = [body];

        builder.Instances(end: 6, first: 3, target: first);
        builder.Meshes(end: 2, first: 0, target: first);
        builder.Materials(ids: [7, 9], prototype: "cube");
        var captured = builder.Snapshot(pool: pool);

        Assert.Same(expected: captured, actual: builder.Snapshot(pool: pool));
        builder.Clear();
        builder.Instances(end: 6, first: 3, target: next);
        builder.Meshes(end: 2, first: 0, target: next);
        builder.Materials(ids: [9, 7], prototype: "sphere");
        var replacement = builder.Snapshot(pool: [next]);

        foreach (var identity in new uint[] { 0x40000004, 0x40000005, 0x40000006, 0x80000000, 0x80000001 }) {
            Assert.Same(expected: first, actual: captured.Resolve(identity: identity));
            Assert.Same(expected: next, actual: replacement.Resolve(identity: identity));
        }
        Assert.Same(expected: body, actual: captured.Resolve(identity: 0x80000002));
        Assert.Same(expected: next, actual: replacement.Resolve(identity: 0x80000002));
        Assert.Equal(expected: "cube.palette[0]", actual: captured.MaterialName(identity: 0x40000004, material: 7));
        Assert.Equal(expected: "sphere.palette[1]", actual: replacement.MaterialName(identity: 0x40000004, material: 7));
        Assert.Null(@object: captured.MaterialName(identity: 0, material: 7));
        Assert.Null(@object: captured.Resolve(identity: 0));
        Assert.Null(@object: captured.Resolve(identity: 0x40000000));
        Assert.Null(@object: captured.Resolve(identity: 0x40000007));
    }
}
