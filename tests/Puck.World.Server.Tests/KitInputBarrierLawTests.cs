using Puck.Commands;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class KitInputBarrierLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ForeignSelectorWritesCommitAsOneFiringAndARefusedSourceSweepRewindsThem(bool refuseSweep) {
        var basis = (refuseSweep ? BodySweepRefusalLawTests.LatticeWorld() : Fixtures.BuildDocument());
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("transition", ChannelShape.Binary, Composition: true),
                new("targetHold", ChannelShape.Binary, Composition: true)],
            StateRaw = (basis.StateRaw ?? new WorldStateSection()) with {
                Body = [.. (basis.StateRaw?.Body ?? []),
                    new("firstMode", ActionStateKind.Counter, Initial: 0, Envelope: new ActionStateEnvelope.Set(Values: [0, 1])),
                    new("secondMode", ActionStateKind.Counter, Initial: 0, Envelope: new ActionStateEnvelope.Set(Values: [0, 1]))],
            },
            KitRowsRaw = basis.Kits.Select(selector: kit => kit with {
                ActionsRaw = new Dictionary<string, ActionSpec> {
                    ["transition"] = new(OnPress: new(Effects: [
                        new ActionEffect.SetState("firstMode", Value: 1, Target: ActionTarget.AffectingSubject),
                        new ActionEffect.SetState("secondMode", Value: 1, Target: ActionTarget.AffectingSubject),
                    ])),
                },
            }).ToArray(),
            InputModes = new([.. basis.Channels.Select(selector: row => row.Name), "transition", "targetHold"], [
                new("first", new(Kind: WorldInputModeSelectorKind.BodyState, Name: "firstMode"), "before", [
                    new("before", [], ["0"]), new("after", [], ["1"]),
                ]),
                new("second", new(Kind: WorldInputModeSelectorKind.BodyState, Name: "secondMode"), "before", [
                    new("before", [], ["0"]), new("after", [], ["1"]),
                ]),
            ], [new Dictionary<string, string> { ["first"] = "before", ["second"] = "before" },
                new Dictionary<string, string> { ["first"] = "after", ["second"] = "after" }]),
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat(slot: 0);
        fixture.JoinSeat(slot: 1);
        fixture.Step();
        var sourceBody = fixture.Server.Body(index: 0)!;

        sourceBody.ApplyIntegrationResidue(sourceBody.CaptureIntegrationResidue() with { AffectingSubject = 1 });
        if (refuseSweep) {
            sourceBody.Pose(new(X: FixedQ4816.One, Y: (FixedQ4816.MaxValue - FixedQ4816.FromDouble(value: 0.25)), Z: FixedQ4816.One),
                FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        }
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var transition, name: "transition"));
        Assert.True(condition: catalog.TryGetLogicalId(id: out var hold, name: "targetHold"));
        var tick = fixture.Server.NextInputTick;
        WorldInputOwner? targetOwner = null;

        for (var seat = 0; (seat < 2); seat++) {
            WorldInputSource[] profile = [new(0, ((seat == 0) ? transition : hold), WorldInputContributionKind.Level, "key")];
            var principal = Principal.Seat(slot: seat);
            var owner = fixture.Server.Input.Admit(bodyIndex: seat, principal: principal, sources: profile);
            var producer = new WorldInputProducer(catalog, owner.Mapping, profile, 128, owner.Revision, owner.Epoch);

            Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, ((ulong)(seat + 1))));
            fixture.Server.EnqueueIntent(submission: new(tick, seat, default, principal, Input: producer.Publish(tick)));
            if (seat == 1) { targetOwner = owner; }
        }
        var epoch = targetOwner!.Epoch;

        fixture.Step();
        foreach (var state in new[] { "firstMode", "secondMode" }) {
            Assert.True(condition: WorldBodyStateReads.TryResolveSlot(definition: definition, kind: out _, name: state, slot: out var slot));
            Assert.Equal((refuseSweep ? 0 : FixedQ4816.One.Value), fixture.Server.Population.ActionStateLane.Read(ordinal: 1, slot: slot));
        }
        Assert.Equal((refuseSweep ? epoch : (epoch + 1)), targetOwner.Epoch);
        Assert.Equal(refuseSweep, targetOwner.TryHeld(channel: hold, hold: out _));
        if (!refuseSweep) { Assert.True(condition: targetOwner.TryEvent(channel: hold, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out _)); } else { Assert.Equal(ContactRefusal.UnrepresentableSweep, sourceBody.SweepRefusal); }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AKitFiringInstallsItsBarrierBeforeTheNextActionAndARefusedSweepRewindsIt(bool refuseSweep) {
        var basis = (refuseSweep ? BodySweepRefusalLawTests.LatticeWorld() : Fixtures.BuildDocument());
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("aTransition", ChannelShape.Binary, Composition: true),
                new("bOldAction", ChannelShape.Binary, Composition: true)],
            StateRaw = (basis.StateRaw ?? new WorldStateSection()) with {
                Body = [.. (basis.StateRaw?.Body ?? []),
                    new("inputMode", ActionStateKind.Counter, Initial: 0, Envelope: new ActionStateEnvelope.Set(Values: [0, 1])),
                    new("oldActionFired", ActionStateKind.Counter, Initial: 0)],
            },
            KitRowsRaw = basis.Kits.Select(selector: kit => kit with {
                ActionsRaw = new Dictionary<string, ActionSpec> {
                    ["aTransition"] = new(OnPress: new(Effects: [new ActionEffect.SetState("inputMode", Value: 1)])),
                    ["bOldAction"] = new(OnPress: new(Effects: [new ActionEffect.SetState("oldActionFired", Value: 1)])),
                },
            }).ToArray(),
            InputModes = new([.. basis.Channels.Select(selector: row => row.Name), "aTransition", "bOldAction"], [
                new("mode", new(Kind: WorldInputModeSelectorKind.BodyState, Name: "inputMode"), "before", [
                    new("before", [], ["0"]), new("after", [], ["1"]),
                ], Transitions: [new("before", "after", ["aTransition"])]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat();
        fixture.Step();
        if (refuseSweep) {
            fixture.Server.Body(index: 0)!.Pose(new(X: FixedQ4816.One, Y: (FixedQ4816.MaxValue - FixedQ4816.FromDouble(value: 0.25)), Z: FixedQ4816.One),
                FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        }
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var transition, name: "aTransition"));
        Assert.True(condition: catalog.TryGetLogicalId(id: out var old, name: "bOldAction"));
        WorldInputSource[] sources = [new(0, transition, WorldInputContributionKind.Level, "first"), new(1, old, WorldInputContributionKind.Level, "second")];
        var principal = Principal.Seat(slot: 0);
        var owner = fixture.Server.Input.Admit(bodyIndex: 0, principal: principal, sources: sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.NextInputTick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 101));
        Assert.True(condition: producer.Observe(1, FixedQ4816.One.Value, tick, default, 202));
        fixture.Server.EnqueueIntent(submission: new(tick, 0, default, principal, Input: producer.Publish(tick)));
        var epoch = owner.Epoch;

        fixture.Step();
        Assert.True(condition: owner.TryHeld(channel: transition, hold: out var kept));
        if (refuseSweep) {
            Assert.Equal(ContactRefusal.UnrepresentableSweep, fixture.Server.Body(index: 0)!.SweepRefusal);
            Assert.Equal(epoch, owner.Epoch);
            Assert.False(condition: kept.Consumed);
            Assert.True(condition: owner.TryHeld(channel: old, hold: out _));
            Assert.Contains("before", owner.Mapping.Name);
            return;
        }
        Assert.True(condition: kept.Consumed);
        Assert.Equal(new ulong[] { 101 }, owner.LastSwitch.ConsumedActivations.ToArray());
        Assert.False(condition: owner.TryHeld(channel: old, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: old, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out _));
        Assert.True(condition: WorldBodyStateReads.TryResolveSlot(fixture.Server.Definition, "oldActionFired", out var slot, out _));
        Assert.Equal(0, fixture.Server.Population.ActionStateLane.Read(ordinal: 0, slot: slot));
        Assert.Contains("after", owner.Mapping.Name);
    }
}
