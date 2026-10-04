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
            Plan(views: views, lights: lights, owner: owner, frame: frame);
            Assert.Equal(expected: frame, actual: views.Pending);
            Assert.False(condition: views.Snapshot(index: frame).Valid);
            views.Submitted();
            Assert.True(condition: views.Snapshot(index: frame).Valid);
            Plan(views: views, lights: lights, owner: owner, frame: frame);
            Assert.Equal(expected: -1, actual: views.Pending);
        }
        var revision = views.Revision;
        for (var frame = 4; (frame < 20); frame++) {
            Plan(views: views, lights: lights, owner: owner, frame: frame);
            Assert.Equal(expected: -1, actual: views.Pending);
        }
        Assert.Equal(expected: 4UL, actual: views.Publications);
        Assert.Equal(expected: revision, actual: views.Revision);
        Assert.Equal(expected: 4, actual: views.MapCount);
        Assert.Equal(expected: ((SdfShadowSlots.MaxSlots + SdfShadowSlots.MaxFadeSlots) * 2), actual: SdfIndirectLightLayout.MaxMaps);
        views.InvalidateStorage();
        Plan(views, lights, owner, 20);
        Assert.Equal(expected: 0, actual: views.Pending);
        Assert.False(condition: views.Snapshot(index: 0).Valid);
    }

    [Fact]
    public void ExactOwnerAndAllocationIdentityInvalidateEqualIndicesAndRevisionNumbers() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();
        for (var frame = 0; (frame < 4); frame++) { Plan(views, lights, owner, frame); views.Submitted(); }
        var prior = views.Snapshot(index: 2);
        lights.ShadowSlots.SetIncomingOwner(channel: 0, owner: "replacement");
        Plan(views, lights, owner, 4);
        Assert.Equal(expected: 2, actual: views.Pending);
        Assert.False(condition: views.Snapshot(index: 2).Valid);
        Assert.True(condition: (views.Snapshot(index: 2).LightGeneration > prior.LightGeneration));
        Assert.True(condition: views.Snapshot(index: 0).Valid);
        views.Submitted();
        Plan(views, lights, new object(), 5);
        Assert.All(collection: Enumerable.Range(start: 0, count: 4), action: index => {
            Assert.False(condition: views.Snapshot(index: index).Valid);
            Assert.True(condition: (views.Snapshot(index: index).GeometryGeneration > prior.GeometryGeneration));
        });
    }

    [Fact]
    public void AReindexedOwnerStandsButAccumulatedMotionUsesTheRetainedAnchor() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();
        for (var frame = 0; (frame < 4); frame++) { Plan(views, lights, owner, frame); views.Submitted(); }
        var first = lights[0];
        lights.Set(index: 0, light: lights[1]);
        lights.Set(index: 1, light: first);
        lights.ShadowSlots.SetSlot(slot: 0, light: 1);
        lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Outgoing: 1, Incoming: 0, Slot: 0, Weight: 0.5f)]);
        lights.ShadowSlots.SetIncomingOwner(channel: 0, owner: "incoming");
        Plan(views, lights, owner, 4);
        Assert.Equal(expected: -1, actual: views.Pending);
        Assert.Equal(expected: 1, actual: views.Snapshot(index: 0).LightIndex);
        var anchor = lights[1];
        var increment = (Math.Atan(d: anchor.Param) / 16);
        for (var step = 1; (step <= 3); step++) {
            var angle = (step * increment);
            lights.Set(index: 1, light: anchor with { Direction = new Vector3(x: ((float)Math.Sin(a: angle)), y: ((float)Math.Cos(d: angle)), z: 0) });
            Plan(views, lights, owner, (4 + step));
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
        Plan(views, lights, owner, 0);
        Plan(views, lights, owner, 1);
        Assert.Equal(expected: 0, actual: views.Pending);
        Assert.False(condition: views.Snapshot(index: 0).Valid);
        views.Plan(frame: 2, geometryOwner: owner, geometry: default, lights: lights, regions: [Region, Region], casters: null, forceGeometry: false);
        Assert.Equal(expected: -1, actual: views.Pending);
        Assert.All(collection: Enumerable.Range(start: 0, count: 4), action: index => Assert.Null(@object: views.Snapshot(index: index).Projection));
        Assert.Equal(expected: 0UL, actual: views.Publications);
    }

    [Fact]
    public void TheSharedBlockPublishesSubmittedValidityAndExactGenerationsAndOffClearsThem() {
        var views = new SdfIndirectLightViews();
        Plan(views, Lights(), new object(), 0);
        var block = new byte[SdfFrameBlock.SizeBytes];
        var parameters = SdfWorldInterfaces.WorldParameters;
        int Offset(string member) => ((int)parameters.BlockOffsetOf(member: member));
        var start = Offset(SdfWorldPackage.LightMaps);
        SdfFrameBlock.WriteLightViews(block: block, views: views, depthCamera: true);
        Assert.Equal(expected: 1u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(SdfWorldPackage.LightMap)));
        Assert.Equal(expected: 4u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(SdfWorldPackage.LightMapCount)));
        Assert.Equal(expected: 0f, actual: BitConverter.ToSingle(value: block, startIndex: (start + 12)));
        Assert.True(condition: (BitConverter.ToSingle(value: block, startIndex: Offset(SdfWorldPackage.LightSweepRadius)) > 0f));
        views.Submitted();
        SdfFrameBlock.WriteLightViews(block: block, views: views, depthCamera: false);
        Assert.Equal(expected: 0u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(SdfWorldPackage.LightMap)));
        Assert.Equal(expected: 1f, actual: BitConverter.ToSingle(value: block, startIndex: (start + 12)));
        var map = views.Snapshot(index: 0);
        Assert.Equal(expected: map.LightGeneration, actual: BitConverter.ToUInt64(value: block, startIndex: (start + 96)));
        Assert.Equal(expected: map.GeometryGeneration, actual: BitConverter.ToUInt64(value: block, startIndex: (start + 104)));
        SdfFrameBlock.WriteLightViews(block: block, views: null, depthCamera: false);
        Assert.All(collection: block.AsSpan(start: start, length: (12 * 7 * 16)).ToArray(), action: value => Assert.Equal(expected: ((byte)0), actual: value));
        Assert.Equal(expected: 0u, actual: BitConverter.ToUInt32(value: block, startIndex: Offset(SdfWorldPackage.LightMapCount)));
    }

    [Fact]
    public void PinnedGeometryRejectsLaterMapsAndEqualRevisionsFromAnotherAllocation() {
        var views = new SdfIndirectLightViews();
        var lights = Lights();
        var owner = new object();
        var captured = new SdfLightGeometry(Program: 1, Poses: 2, Mesh: 3, Decals: 4);
        for (var frame = 0; frame < 4; frame++) {
            views.Plan(frame, owner, captured, lights, [Region, Region], Region, forceGeometry: false);
            views.Submitted();
        }
        var block = new byte[SdfFrameBlock.SizeBytes];
        var start = (int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(SdfWorldPackage.LightMaps);
        var count = (int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(SdfWorldPackage.LightMapCount);
        void Write(object sourceOwner, SdfLightGeometry source) =>
            SdfFrameBlock.WriteLightViews(block, views, depthCamera: false, geometryOwner: sourceOwner, geometry: source);
        void AssertFallback() {
            Assert.Equal(expected: 0u, actual: BitConverter.ToUInt32(block, count));
            Assert.All(block.AsSpan(start, SdfIndirectLightLayout.MaxMaps * SdfIndirectLightLayout.MetadataRows * 16).ToArray(),
                value => Assert.Equal(expected: (byte)0, actual: value));
        }
        Write(owner, captured);
        Assert.Equal(expected: 4u, actual: BitConverter.ToUInt32(block, count));
        Assert.Equal(expected: 1f, actual: BitConverter.ToSingle(block, start + 12));

        var live = captured with { Poses = 5 };
        views.Plan(4, owner, live, lights, [Region, Region], Region, forceGeometry: false);
        views.Submitted();
        Write(owner, captured);
        AssertFallback();
        Write(new object(), live);
        AssertFallback();
        Write(owner, live);
        Assert.Equal(expected: 4u, actual: BitConverter.ToUInt32(block, count));
        Assert.Equal(expected: 1f, actual: BitConverter.ToSingle(block, start + 12));
    }

    private static void Plan(SdfIndirectLightViews views, SdfLights lights, object owner, long frame) =>
        views.Plan(frame: frame, geometryOwner: owner, geometry: default, lights: lights, regions: [Region, Region], casters: Region, forceGeometry: false);

    private static SdfLights Lights() {
        var lights = new SdfLights { Count = 2 };
        lights.Set(index: 0, light: new SdfLight(Kind: SdfLightKind.Directional, Direction: Vector3.UnitY, Color: Vector3.One, Weight: 1, Param: 0.1f, Shadows: true));
        lights.Set(index: 1, light: lights[0]);
        lights.ShadowSlots.Configure(slots: 1, fadeCapacity: 1);
        lights.ShadowSlots.SetSlot(slot: 0, light: 0);
        lights.ShadowSlots.SetOwner(slot: 0, owner: "held");
        lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Outgoing: 0, Incoming: 1, Slot: 0, Weight: 0.5f)]);
        lights.ShadowSlots.SetIncomingOwner(channel: 0, owner: "incoming");
        return lights;
    }
}
