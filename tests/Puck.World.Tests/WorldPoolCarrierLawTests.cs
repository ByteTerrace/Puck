using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a pool-backed interaction joins physical bodies to exact instance lifetimes;
/// logical enum members resolve named bodies, and reclaimed lifetimes use their own current binding values.</summary>
public sealed class WorldPoolCarrierLawTests {
    internal static CellName Name(string value) => CellName.Parse(candidate: value);
    internal static void JoinAttachedSeats(WorldFixture fixture) {
        for (var slot = 0; (slot < 2); slot++) {
            var actor = WorldPrincipal.Seat(slot: slot);

            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
                Principal: actor,
                Slot: actor.Index,
                IdentityName: null,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }
    }

    private static StateEnum BodyEnum() => new(Name(value: "BodyBinding"), [Name(value: "Detached"), Name(value: "First"), Name(value: "Second")]);
    private static WorldPoolBodyCarrier Carrier(string pool) => new(Name(value: pool), Name(value: "body"), [
        new WorldPoolBodyBinding(Name(value: "Detached")),
        new WorldPoolBodyBinding(Name(value: "First"), Seat: 0),
        new WorldPoolBodyBinding(Name(value: "Second"), Seat: 1),
    ]);

    internal static WorldDefinition Document() {
        var document = DynamicDocument();
        var record = document.StateRaw!.Records![0];

        return document with {
            StateRaw = document.StateRaw with { Records = [record with { Fields = record.Fields!.Reverse().ToArray() }] },
            Interactions = new WorldInteractionsSection(Interactions: [document.Interactions!.Interactions[0] with { Effects = [
                new ActionEffect.AddState(State: StateChannelRef.OfBindingField(binding: "left", field: "hits"), Value: 1m),
                new ActionEffect.AddState(State: StateChannelRef.OfBindingField(binding: "right", field: "hits"), Value: 10m),
            ] }]),
        };
    }

    private static WorldDefinition DynamicDocument() {
        var source = Fixtures.BuildDocument();

        return source with {
            StateRaw = source.StateRaw! with {
                Enums = [BodyEnum()],
                Records = [new StateRecord(Name: Name(value: "fighter"), Fields: [
                    new StatePoolField(Name: Name(value: "body"), Default: CellValue.Int(value: 0L), Enum: Name(value: "BodyBinding")),
                    new StatePoolField(Name: Name(value: "hits"), Default: CellValue.Int(value: 0L)),
                ])],
                Pools = [new StatePool(Name: Name(value: "fighters"), Record: Name(value: "fighter"), Capacity: 2, Initial: [
                    new StatePoolSeed(Slot: 0, Values: [new StatePoolValue(Field: Name(value: "body"), Value: CellValue.Int(value: 1L))]),
                    new StatePoolSeed(Slot: 1, Values: [new StatePoolValue(Field: Name(value: "body"), Value: CellValue.Int(value: 2L))]),
                ])],
            },
            Properties = new WorldPropertyRegistrySection(Names: [], Carriers: [
                Carrier(pool: "fighters"),
            ]),
            Interactions = new WorldInteractionsSection(Interactions: [new WorldInteraction(
                Name: Name(value: "contact"),
                Left: "fighters",
                Right: "fighters",
                CoOccurrence: WorldInteractionCoOccurrence.Distance,
                Range: 1_000_000m,
                Effects: [new ActionEffect.AddState(State: StateChannelRef.OfBindingField(binding: "left", field: "hits"), Value: 1m)],
                Mode: ActionTriggerMode.Edge
            )]),
        };
    }

    [Fact]
    public void ANullCarrierAfterAValidCarrierRefusesByName() {
        var document = DynamicDocument();

        document = document with { Properties = document.Properties! with { Carriers = [document.Properties!.Carriers![0], null!] } };
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => WorldDefinitionValidator.Validate(definition: document, neighbours: null));

        Assert.Contains("pool carrier names an undeclared pool", refusal.Message);
    }
    [InlineData("$pool_fighters_live")]
    [InlineData("$pool_fighters_generation")]
    [InlineData("$pool_fighters_field_hits")]
    [Theory]
    public void APropertyNamingGeneratedPoolStorageIsRefusedAsAnUndeclaredRow(string generated) {
        var document = DynamicDocument();
        var authored = (document with { Properties = document.Properties! with { Names = ["fightersHits"] } });

        document = document with { Properties = document.Properties! with { Names = [generated] } };

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => WorldDefinitionValidator.Validate(definition: document, neighbours: null));
        var undeclared = Assert.Throws<InvalidOperationException>(testCode: () => WorldDefinitionValidator.Validate(definition: authored, neighbours: null));

        Assert.Contains(expectedSubstring: generated, actualString: refusal.Message);
        Assert.Equal(
            actual: refusal.Message.Replace(newValue: "fightersHits", oldValue: generated),
            expected: undeclared.Message
        );
    }
    [Fact]
    public void EveryLogicalBodyMemberMustHaveAnExplicitMapping() {
        var document = DynamicDocument();
        var carrier = document.Properties!.Carriers![0];

        document = document with { Properties = document.Properties with { Carriers = [carrier with { Bindings = carrier.Bindings.Take(2).ToArray() }] } };
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => WorldDefinitionValidator.Validate(definition: document, neighbours: null));

        Assert.Contains("must map every member", refusal.Message);
    }
    [Fact]
    public void ChangingALogicalBodyMemberRebindsWithoutChangingInstanceIdentity() {
        using var fixture = Fixtures.FreshServer(DynamicDocument());

        JoinAttachedSeats(fixture: fixture);
        var arena = fixture.Server.Arena;

        Assert.True(condition: arena.Catalog.TryGetPool(name: Name(value: "fighters"), pool: out var pool));
        var first = arena.Catalog.CreateInstanceHandle(pool!.Ordinal, 0, 0);
        // Both instances select Second, so a physical body never pairs with itself.
        Assert.True(condition: arena.TryWrite(first, 0, CellValue.Int(value: 2), out _));
        fixture.Step();
        Assert.True(condition: arena.TryRead(fieldOrdinal: 1, handle: first, value: out var before));
        Assert.Equal(0L, before.AsInt);
        Assert.True(condition: arena.TryWrite(first, 0, CellValue.Int(value: 1), out _));
        fixture.Step();
        Assert.True(condition: arena.TryRead(fieldOrdinal: 1, handle: first, value: out var after));
        Assert.Equal(1L, after.AsInt);
        Assert.Equal(0L, first.Generation);
    }
    [Fact]
    public void MainWorldRulesExposePoolRegistersToClaimAndForEachFields() {
        var source = Fixtures.BuildDocument();
        var document = source with {
            StateRaw = source.StateRaw! with {
                Records = [new StateRecord(Name: Name(value: "counter"), Fields: [new StatePoolField(Name: Name(value: "value"), Default: CellValue.Int(value: 1L))])],
                Pools = [new StatePool(Name: Name(value: "counters"), Record: Name(value: "counter"), Capacity: 2)],
            },
            Rules = [
                new WorldRule(Name: Name(value: "claim"), Effects: [new ActionEffect.Claim(Pool: "counters", Binding: Name(value: "fresh"), Effects: [new ActionEffect.SetState(State: StateChannelRef.OfBindingField(binding: "fresh", field: "value"), Value: 7m)])]),
                new WorldRule(Name: Name(value: "visit"), Effects: [new ActionEffect.ForEachPool(Pool: "counters", Binding: Name(value: "item"), Effects: [new ActionEffect.AddState(State: StateChannelRef.OfBindingField(binding: "item", field: "value"), Value: 1m)])]),
            ],
        };
        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Step();

        Assert.True(condition: fixture.Server.Arena.Catalog.TryGetPool(name: Name(value: "counters"), pool: out var pool));
        var handle = Assert.Single(collection: fixture.Server.Arena.SnapshotPool(poolOrdinal: pool!.Ordinal));

        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var value));
        Assert.Equal(expected: 8L, actual: value.AsInt);
    }
    [Fact]
    public void ReleasedAndReclaimedInstanceUsesItsDetachedDefault() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        JoinAttachedSeats(fixture: fixture);
        Assert.NotNull(@object: fixture.Server.Body(index: 0));
        Assert.NotNull(@object: fixture.Server.Body(index: 1));
        fixture.Step();

        Assert.True(condition: fixture.Server.Arena.Catalog.TryGetPool(name: Name(value: "fighters"), pool: out var pool));
        var first = fixture.Server.Arena.Catalog.CreateInstanceHandle(poolOrdinal: pool!.Ordinal, slot: 0, generation: 0L);
        var other = fixture.Server.Arena.Catalog.CreateInstanceHandle(poolOrdinal: pool.Ordinal, slot: 1, generation: 0L);

        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 0, handle: first, value: out var firstHits));
        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 0, handle: other, value: out var otherHits));
        Assert.Equal(expected: 11L, actual: firstHits.AsInt);
        Assert.Equal(expected: 11L, actual: otherHits.AsInt);

        Assert.True(condition: fixture.Server.Arena.TryRelease(handle: first, reason: out var releaseReason), userMessage: releaseReason);
        Assert.True(condition: fixture.Server.Arena.TryClaim(poolOrdinal: pool.Ordinal, handle: out var replacement, reason: out var claimReason), userMessage: claimReason);
        Assert.Equal(expected: first.Slot, actual: replacement.Slot);
        Assert.Equal(expected: 1L, actual: replacement.Generation);

        fixture.Step();

        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 0, handle: replacement, value: out var replacementHits));
        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 0, handle: other, value: out var survivingHits));
        Assert.Equal(expected: 0L, actual: replacementHits.AsInt);
        Assert.Equal(expected: 11L, actual: survivingHits.AsInt);
    }
    [Fact]
    public void DynamicCarrierFieldAttachesAReclaimedLifetime() {
        using var fixture = Fixtures.FreshServer(definition: DynamicDocument());

        JoinAttachedSeats(fixture: fixture);
        Assert.True(condition: fixture.Server.Arena.Catalog.TryGetPool(name: Name(value: "fighters"), pool: out var pool));
        var first = fixture.Server.Arena.Catalog.CreateInstanceHandle(poolOrdinal: pool!.Ordinal, slot: 0, generation: 0L);

        Assert.True(condition: fixture.Server.Arena.TryRelease(handle: first, reason: out var releaseReason), userMessage: releaseReason);
        Assert.True(condition: fixture.Server.Arena.TryClaim(poolOrdinal: pool.Ordinal, handle: out var replacement, reason: out var claimReason), userMessage: claimReason);
        Assert.Equal(expected: 1L, actual: replacement.Generation);
        Assert.True(condition: fixture.Server.Arena.TryWrite(handle: replacement, fieldOrdinal: 0, value: CellValue.Int(value: 1L), reason: out var writeReason), userMessage: writeReason);

        fixture.Step();

        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 1, handle: replacement, value: out var hits));
        Assert.Equal(expected: 1L, actual: hits.AsInt);
    }
    [Fact]
    public void DynamicPairPoolCarrierReachesTheHostEvaluator() {
        var source = Fixtures.BuildDocument();
        var document = source with {
            StateRaw = source.StateRaw! with {
                Enums = [BodyEnum()],
                Records = [
                    new StateRecord(Name: Name(value: "node"), Fields: [new StatePoolField(Name: Name(value: "body"), Default: CellValue.Int(value: 0L), Enum: Name(value: "BodyBinding"))]),
                    new StateRecord(Name: Name(value: "edge"), Fields: [
                        new StatePoolField(Name: Name(value: "body"), Default: CellValue.Int(value: 0L), Enum: Name(value: "BodyBinding")),
                        new StatePoolField(Name: Name(value: "hits"), Default: CellValue.Int(value: 0L)),
                    ]),
                ],
                Pools = [new StatePool(Name: Name(value: "nodes"), Record: Name(value: "node"), Capacity: 2, Initial: [
                    new StatePoolSeed(Slot: 0, Values: [new StatePoolValue(Field: Name(value: "body"), Value: CellValue.Int(value: 1L))]),
                    new StatePoolSeed(Slot: 1, Values: [new StatePoolValue(Field: Name(value: "body"), Value: CellValue.Int(value: 2L))]),
                ])],
                PairPools = [new StatePairPool(Name: Name(value: "edges"), Record: Name(value: "edge"), LeftPool: Name(value: "nodes"), RightPool: Name(value: "nodes"), MaxLive: 2)],
            },
            Properties = new WorldPropertyRegistrySection(Names: [], Carriers: [
                Carrier(pool: "nodes"),
                Carrier(pool: "edges"),
            ]),
            Interactions = new WorldInteractionsSection(Interactions: [new WorldInteraction(
                Name: Name(value: "edgeContact"), Left: "edges", Right: "nodes",
                CoOccurrence: WorldInteractionCoOccurrence.Distance, Range: 1_000_000m,
                Effects: [new ActionEffect.AddState(State: StateChannelRef.OfBindingField(binding: "left", field: "hits"), Value: 1m)], Mode: ActionTriggerMode.Edge)]),
        };
        using var fixture = Fixtures.FreshServer(definition: document);

        JoinAttachedSeats(fixture: fixture);
        Assert.True(condition: fixture.Server.Arena.Catalog.TryGetPool(name: Name(value: "nodes"), pool: out var nodes));
        Assert.True(condition: fixture.Server.Arena.Catalog.TryGetPool(name: Name(value: "edges"), pool: out var edges));
        var left = fixture.Server.Arena.Catalog.CreateInstanceHandle(poolOrdinal: nodes!.Ordinal, slot: 0, generation: 0L);
        var right = fixture.Server.Arena.Catalog.CreateInstanceHandle(poolOrdinal: nodes.Ordinal, slot: 1, generation: 0L);

        Assert.True(condition: fixture.Server.Arena.TryClaimPair(poolOrdinal: edges!.Ordinal, leftHandle: left, rightHandle: right, handle: out var pair, reason: out var claimReason), userMessage: claimReason);
        Assert.True(condition: fixture.Server.Arena.TryWrite(handle: pair, fieldOrdinal: 0, value: CellValue.Int(value: 1L), reason: out var writeReason), userMessage: writeReason);

        fixture.Step();

        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 1, handle: pair, value: out var hits));
        Assert.Equal(expected: 1L, actual: hits.AsInt);
    }
}
