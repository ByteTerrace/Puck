using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    private static TemporalRig ShadowHistoryRig(int fadeCapacity = 0, bool cadence = false) {
        var lights = SdfLights.Default();

        lights.Count = 3;
        var light = new SdfLight(Kind: SdfLightKind.Directional, Direction: Vector3.UnitY,
            Color: Vector3.One, Weight: 1f, Param: 0.1f, Shadows: true);

        lights.Set(index: 1, light: light);
        lights.Set(index: 2, light: light);
        lights.ShadowSlots.Configure(fadeCapacity: fadeCapacity, slots: 2);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        lights.ShadowSlots.SetOwner(owner: "first", slot: 0);
        lights.ShadowSlots.SetSlot(light: 1, slot: 1);
        lights.ShadowSlots.SetOwner(owner: "second", slot: 1);
        var rig = new TemporalRig(views: 1, temporal: true, lights: lights, amortize: true, cadence: cadence);

        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        return rig;
    }

    [Fact]
    public void ShadowHistoryPortsFollowTheLastWriterAcrossSkippedShadowPasses() {
        using var rig = ShadowHistoryRig();
        var previous = rig.ShadowPorts().Write;

        rig.ShadowsEnabled = false;
        rig.Produce();
        rig.Produce();
        rig.ShadowsEnabled = true;
        for (var frame = 0; (frame < 6); frame++) {
            var ports = rig.ShadowPorts();

            Assert.NotEqual(actual: ports.Write, expected: ports.Read);
            Assert.Equal(actual: ports.Read, expected: previous);
            Assert.Equal(actual: ports.ViewsRead, expected: ports.Write);
            previous = ports.Write;
        }
    }
    [Fact]
    public void ShadowHistoryOwnerNameAloneWakesASettledView() {
        using var rig = ShadowHistoryRig(cadence: true);

        for (var frame = 0; (frame < 10); frame++) {
            rig.Produce();
        }
        Assert.True(condition: rig.Stood());
        rig.Lights.ShadowSlots.SetOwner(owner: "same-values-new-owner", slot: 1);
        rig.Produce();
        Assert.False(condition: rig.Stood());
    }
    [Fact]
    public void ShadowHistoryUsesNamesThroughReordersAndRejectsTableIndexReuse() {
        using var rig = ShadowHistoryRig();

        rig.Lights.ShadowSlots.SetSlot(light: 2, slot: 1);
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        rig.Lights.ShadowSlots.SetOwner(owner: "replacement", slot: 1);
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
    }
    [Fact]
    public void ShadowHistoryRetainsItsLightAnchorAcrossPartialFramesAndRejectsSlowDrift() {
        using var rig = ShadowHistoryRig();

        void Turn(float angle) => rig.Lights.Set(index: 1, light: rig.Lights[1] with {
            Direction = new Vector3(x: MathF.Sin(x: angle), y: MathF.Cos(x: angle), z: 0f),
        });
        Turn(angle: 0.006f);
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowLightReject));
        Turn(angle: 0.014f);
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowLightReject));
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowLightReject));
        rig.Lights.Set(index: 1, light: rig.Lights[1] with { Param = 0.08f });
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowLightReject));
    }
    [Fact]
    public void ShadowHistoryCommitsNoOwnerOnAFailedSubmission() {
        using var rig = ShadowHistoryRig();

        rig.Lights.ShadowSlots.SetOwner(owner: "accepted-owner", slot: 1);
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        rig.Lights.ShadowSlots.SetOwner(owner: "failed-owner", slot: 1);
        rig.RefuseRenderSubmission();
        Assert.Throws<InvalidOperationException>(testCode: () => rig.Produce());
        rig.Lights.ShadowSlots.SetOwner(owner: "accepted-owner", slot: 1);
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
    }
    [Fact]
    public void ShadowHistoryMarchesFadingSlotsUntilTheFirstNonfadingRebuildSubmits() {
        using var rig = ShadowHistoryRig(fadeCapacity: 1);

        rig.Lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Incoming: 2, Outgoing: 1, Slot: 1, Weight: 0.5f)]);
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        rig.Lights.ShadowSlots.SetHandoffs(handoffs: []);
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
    }
    [Fact]
    public void ShadowHistoryDropsUnconfiguredOwnersAndTheLeverWritesTheLivePass() {
        using var rig = ShadowHistoryRig();

        rig.ShadowAmortize = false;
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowAmortize));
        rig.ShadowAmortize = true;
        Assert.Equal(1u, rig.ShadowValue(member: SdfWorldPackage.ShadowAmortize));
        rig.Lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 1);
        rig.Lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        rig.Lights.ShadowSlots.SetOwner(owner: "first", slot: 0);
        Assert.Equal(0u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
        rig.Lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 2);
        rig.Lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        rig.Lights.ShadowSlots.SetOwner(owner: "first", slot: 0);
        rig.Lights.ShadowSlots.SetSlot(light: 1, slot: 1);
        rig.Lights.ShadowSlots.SetOwner(owner: "second", slot: 1);
        Assert.Equal(2u, rig.ShadowValue(member: SdfWorldPackage.ShadowOwnershipReject));
    }
}
