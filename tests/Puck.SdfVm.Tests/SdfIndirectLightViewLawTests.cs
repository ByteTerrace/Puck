using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfIndirectLightViewLawTests {
    private static readonly SdfLightRegion Region = new(Min: new Double3(X: -4, Y: -1, Z: -4), Max: new Double3(X: 4, Y: 3, Z: 4));

    [Fact]
    public void HeldAndIncomingRegionsPublishOnceAndUnchangedFramesScheduleNothing() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();

        for (var frame = 0; (frame < 4); frame++) {
            Plan(frame: frame, lights: lights, owner: owner, views: views);
            Assert.Equal(expected: frame, actual: views.Pending);
            Assert.False(condition: views.Snapshot(index: frame).Valid);
            views.Submitted();
            Assert.True(condition: views.Snapshot(index: frame).Valid);
            Plan(frame: frame, lights: lights, owner: owner, views: views);
            Assert.Equal(expected: -1, actual: views.Pending);
        }
        var revision = views.Revision;

        for (var frame = 4; (frame < 20); frame++) {
            Plan(frame: frame, lights: lights, owner: owner, views: views);
            Assert.Equal(expected: -1, actual: views.Pending);
        }
        Assert.Equal(expected: 4UL, actual: views.Publications);
        Assert.Equal(expected: revision, actual: views.Revision);
        Assert.Equal(expected: 4, actual: views.MapCount);
        Assert.Equal(actual: SdfIndirectLightLayout.MaxMaps, expected: ((SdfShadowSlots.MaxSlots + SdfShadowSlots.MaxFadeSlots) * 2));
        lights.Set(index: 0, light: lights[0] with { Color = Vector3.UnitX });
        lights.Set(index: 1, light: lights[1] with { Color = Vector3.UnitZ });
        Plan(frame: 20, lights: lights, owner: owner, views: views);
        Assert.Equal(-1, views.Pending);
        Assert.Equal(revision, views.Revision);
        Assert.All(Enumerable.Range(count: 4, start: 0), index => Assert.True(condition: views.Snapshot(index: index).Valid));
        views.InvalidateStorage();
        Plan(frame: 21, lights: lights, owner: owner, views: views);
        Assert.Equal(expected: 0, actual: views.Pending);
        Assert.False(condition: views.Snapshot(index: 0).Valid);
    }
    [Fact]
    public void ExactOwnerAndAllocationIdentityInvalidateEqualIndicesAndRevisionNumbers() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();

        for (var frame = 0; (frame < 4); frame++) { Plan(frame: frame, lights: lights, owner: owner, views: views); views.Submitted(); }
        var prior = views.Snapshot(index: 2);

        lights.ShadowSlots.SetIncomingOwner(channel: 0, owner: "replacement");
        Plan(frame: 4, lights: lights, owner: owner, views: views);
        Assert.Equal(expected: 2, actual: views.Pending);
        Assert.False(condition: views.Snapshot(index: 2).Valid);
        Assert.True(condition: (views.Snapshot(index: 2).LightGeneration > prior.LightGeneration));
        Assert.True(condition: views.Snapshot(index: 0).Valid);
        views.Submitted();
        Plan(views, lights, new object(), 5);
        Assert.All(collection: Enumerable.Range(count: 4, start: 0), action: index => {
            Assert.False(condition: views.Snapshot(index: index).Valid);
            Assert.True(condition: (views.Snapshot(index: index).GeometryGeneration > prior.GeometryGeneration));
        });
    }
    [Fact]
    public void AReindexedOwnerStandsButAccumulatedMotionUsesTheRetainedAnchor() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();

        for (var frame = 0; (frame < 4); frame++) { Plan(frame: frame, lights: lights, owner: owner, views: views); views.Submitted(); }
        var first = lights[0];

        lights.Set(index: 0, light: lights[1]);
        lights.Set(index: 1, light: first);
        lights.ShadowSlots.SetSlot(light: 1, slot: 0);
        lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Incoming: 0, Outgoing: 1, Slot: 0, Weight: 0.5f)]);
        lights.ShadowSlots.SetIncomingOwner(channel: 0, owner: "incoming");
        Plan(frame: 4, lights: lights, owner: owner, views: views);
        Assert.Equal(expected: -1, actual: views.Pending);
        Assert.Equal(expected: 1, actual: views.Snapshot(index: 0).LightIndex);
        var anchor = lights[1];
        var increment = (Math.Atan(d: anchor.Param) / 16);

        for (var step = 1; (step <= 3); step++) {
            var angle = (step * increment);

            lights.Set(index: 1, light: anchor with { Direction = new Vector3(x: ((float)Math.Sin(a: angle)), y: ((float)Math.Cos(d: angle)), z: 0) });
            Plan(frame: (4 + step), lights: lights, owner: owner, views: views);
            if (step == 1) { Assert.Equal(expected: -1, actual: views.Pending); }
        }
        Assert.Equal(expected: 0, actual: views.Pending);
        Assert.False(condition: views.Snapshot(index: 0).Valid);
        Assert.True(condition: views.Snapshot(index: 2).Valid);
    }
    [Fact]
    public void UnboundedCastersAndUnsubmittedRegionsNeverPublishValidity() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();

        Plan(frame: 0, lights: lights, owner: owner, views: views);
        Plan(frame: 1, lights: lights, owner: owner, views: views);
        Assert.Equal(expected: 0, actual: views.Pending);
        Assert.False(condition: views.Snapshot(index: 0).Valid);
        views.Plan(casters: null, forceGeometry: false, frame: 2, geometry: default, geometryOwner: owner, lights: lights, regions: [Region, Region]);
        Assert.Equal(expected: -1, actual: views.Pending);
        Assert.All(collection: Enumerable.Range(count: 4, start: 0), action: index => Assert.Null(@object: views.Snapshot(index: index).Projection));
        Assert.Equal(expected: 0UL, actual: views.Publications);
    }
    [Fact]
    public void TheSharedBlockPublishesSubmittedValidityAndExactGenerationsAndOffClearsThem() {
        var views = new SdfIndirectLightViews();

        Plan(views, Lights(), new object(), 0);
        var block = new byte[SdfFrameBlock.SizeBytes];
        var parameters = SdfWorldInterfaces.WorldParameters;

        int Offset(string member) => ((int)parameters.BlockOffsetOf(member: member));
        var start = Offset(member: SdfWorldPackage.LightMaps);

        SdfFrameBlock.WriteLightViews(block: block, views: views, depthCamera: true);
        Assert.Equal(expected: 1u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(member: SdfWorldPackage.LightMap)));
        Assert.Equal(expected: 4u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(member: SdfWorldPackage.LightMapCount)));
        Assert.Equal(expected: 0f, actual: BitConverter.ToSingle(startIndex: (start + 12), value: block));
        Assert.True(condition: (BitConverter.ToSingle(value: block, startIndex: Offset(member: SdfWorldPackage.LightSweepRadius)) > 0f));
        views.Submitted();
        SdfFrameBlock.WriteLightViews(block: block, views: views, depthCamera: false);
        Assert.Equal(expected: 0u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(member: SdfWorldPackage.LightMap)));
        Assert.Equal(expected: 1f, actual: BitConverter.ToSingle(startIndex: (start + 12), value: block));
        var map = views.Snapshot(index: 0);

        Assert.Equal(expected: map.LightGeneration, actual: BitConverter.ToUInt64(startIndex: (start + 96), value: block));
        Assert.Equal(expected: map.GeometryGeneration, actual: BitConverter.ToUInt64(startIndex: (start + 104), value: block));
        SdfFrameBlock.WriteLightViews(block: block, views: null, depthCamera: false);
        Assert.All(collection: block.AsSpan(length: ((12 * 7) * 16), start: start).ToArray(), action: value => Assert.Equal(actual: value, expected: ((byte)0)));
        Assert.Equal(expected: 0u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(member: SdfWorldPackage.LightMapCount)));
    }
    [Fact]
    public void PinnedGeometryRejectsLaterMapsAndEqualRevisionsFromAnotherAllocation() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();
        var captured = new SdfLightGeometry(Program: 1, Poses: 2, Mesh: 3, Decals: 4);

        for (var frame = 0; (frame < 4); frame++) {
            views.Plan(frame, owner, captured, lights, [Region, Region], Region, forceGeometry: false);
            views.Submitted();
        }
        var block = new byte[SdfFrameBlock.SizeBytes];
        var start = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.LightMaps));
        var count = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.LightMapCount));

        void Write(object sourceOwner, SdfLightGeometry source) =>
            SdfFrameBlock.WriteLightViews(block, views, depthCamera: false, geometryOwner: sourceOwner, geometry: source);
        void AssertFallback() {
            Assert.Equal(expected: 0u, actual: BitConverter.ToUInt32(startIndex: count, value: block));
            Assert.All(block.AsSpan(length: ((SdfIndirectLightLayout.MaxMaps * SdfIndirectLightLayout.MetadataRows) * 16), start: start).ToArray(),
                value => Assert.Equal(actual: value, expected: ((byte)0)));
        }
        Write(source: captured, sourceOwner: owner);
        Assert.Equal(expected: 4u, actual: BitConverter.ToUInt32(startIndex: count, value: block));
        Assert.Equal(expected: 1f, actual: BitConverter.ToSingle(startIndex: (start + 12), value: block));

        var live = captured with { Poses = 5 };

        views.Plan(4, owner, live, lights, [Region, Region], Region, forceGeometry: false);
        views.Submitted();
        Write(source: captured, sourceOwner: owner);
        AssertFallback();
        Write(new object(), live);
        AssertFallback();
        Write(source: live, sourceOwner: owner);
        Assert.Equal(expected: 4u, actual: BitConverter.ToUInt32(startIndex: count, value: block));
        Assert.Equal(expected: 1f, actual: BitConverter.ToSingle(startIndex: (start + 12), value: block));
    }

    private static void Plan(SdfIndirectLightViews views, SdfLights lights, object owner, long frame) =>
        views.Plan(casters: Region, forceGeometry: false, frame: frame, geometry: default, geometryOwner: owner, lights: lights, regions: [Region, Region]);
    private static SdfLights Lights() {
        var lights = new SdfLights { Count = 2 };

        lights.Set(index: 0, light: new SdfLight(Kind: SdfLightKind.Directional, Direction: Vector3.UnitY, Color: Vector3.One, Weight: 1, Param: 0.1f, Shadows: true));
        lights.Set(index: 1, light: lights[0]);
        lights.ShadowSlots.Configure(fadeCapacity: 1, slots: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        lights.ShadowSlots.SetOwner(owner: "held", slot: 0);
        lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Incoming: 1, Outgoing: 0, Slot: 0, Weight: 0.5f)]);
        lights.ShadowSlots.SetIncomingOwner(channel: 0, owner: "incoming");
        return lights;
    }
}
