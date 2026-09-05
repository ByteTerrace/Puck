using Xunit;

using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves the market's replacement primitive — an escrowed conditional transfer over ordinary keyed rows,
/// authored entirely as <see cref="WorldRule"/>s over the pre-existing generic vocabulary
/// (<see cref="ActionEffect.AddState"/>/<see cref="ActionEffect.SetState"/> with a live copy or an
/// <see cref="WorldValueExpression"/>, <see cref="ActionEffect.ScheduleState"/> for a deadline,
/// <see cref="ActionEffect.PushState"/> for an ordered history ring), with no engine-side market mechanism at all.
/// A seller's own <c>list</c> cell escrows a quantity out of their inventory into a scratch row (the "handle");
/// settle moves the escrowed quantity to the winner's cell and a fee slice into a shared reserve row; return moves
/// it back to the seller when nothing claims it. Two independent listings share one document — an English auction
/// (seat 0 selling to seats 1/2, proving escrow, outbid refund, deadline settle, and no-bid return) and a buyout
/// (seat 0 selling to seat 3, proving immediate settle and un-bought return) — to show the same handful of effects
/// generalizes across both trade shapes without a bespoke mechanism for either.</summary>
public sealed class EscrowedTransferRuleLawTests {
    private const int FeeBasisPoints = 1_000; // 10%, chosen so a fee amount is exact and easy to hand-verify.
    private const long BuyoutPrice = 300;
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldPrincipal BidderA = WorldPrincipal.Seat(slot: 1);
    private static readonly WorldPrincipal BidderB = WorldPrincipal.Seat(slot: 2);
    private static readonly WorldPrincipal Buyer = WorldPrincipal.Seat(slot: 3);

    private static WorldStateRow HolderRow(string name, bool nonNegative, params long[] balances) => new(
        Name: WorldCellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Capacity: 4,
        NonNegative: nonNegative,
        Cells: [.. balances.Select(selector: static (value, index) => new WorldStateCell(
            Key: WorldCellName.Parse(candidate: index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Value: value
        ))]
    );
    private static WorldStateRow Slot(string name, long initial, bool nonNegative = false) => new(
        Name: WorldCellName.Parse(candidate: name),
        Kind: CellKind.Int,
        NonNegative: nonNegative,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: initial)]
    );
    private static ActionEffect.SetState Set(string state, decimal value, string? key = null) => new(State: state, Value: value, Key: key);
    private static ActionEffect.SetState Copy(string state, string fromState, string? key = null) => new(State: state, FromState: fromState, Key: key);
    private static ActionEffect.AddState Add(string state, decimal value, string? key = null) => new(State: state, Value: value, Key: key);
    private static ActionEffect.AddState AddFrom(string state, string fromState, string? key = null) => new(State: state, FromState: fromState, Key: key);
    private static ActionEffect.AddState AddNegated(string state, string key, string fromState) => new(
        State: state, Key: key, Expression: new WorldValueExpression([new WorldValueToken.State(Name: fromState), new WorldValueToken.Negate()])
    );
    // The fee/net-of-fee split every settle arm needs. Both sides derive from the SAME single division so
    // fee + net always equals amount exactly: net is amount minus the one computed fee, never a second,
    // independently truncated division (amount*bps/10000 and amount*(10000-bps)/10000 can each round down, so their
    // sum can fall short of amount). "fromState" reads a live row's slot cell; "fromValue" splits a compile-time
    // authored constant instead (the buyout's fixed price).
    private static WorldValueToken AmountToken(string? fromState, long? fromValue) => (fromState, fromValue) switch {
        ({ } state, null) => new WorldValueToken.State(Name: state),
        (null, { } value) => new WorldValueToken.Constant(Value: value),
        _ => throw new System.ArgumentException("exactly one of fromState/fromValue"),
    };
    private static WorldValueExpression Fee(int bps, string? fromState = null, long? fromValue = null) => new([
        AmountToken(fromState, fromValue), new WorldValueToken.Constant(Value: bps), new WorldValueToken.Multiply(),
        new WorldValueToken.Constant(Value: 10_000m), new WorldValueToken.Divide(),
    ]);
    private static WorldValueExpression NetOfFee(int bps, string? fromState = null, long? fromValue = null) => new([
        AmountToken(fromState, fromValue),
        AmountToken(fromState, fromValue), new WorldValueToken.Constant(Value: bps), new WorldValueToken.Multiply(),
        new WorldValueToken.Constant(Value: 10_000m), new WorldValueToken.Divide(),
        new WorldValueToken.Subtract(),
    ]);
    private static ActionPredicate.CompareState Cmp(string state, ActionStateComparison comparison, decimal? value = null, string? comparandState = null, string? comparandKey = null, string? key = null) =>
        new(State: state, Comparison: comparison, Value: value, Key: key, ComparandState: comparandState, ComparandKey: comparandKey);

    private static WorldDefinition BuildDocument() {
        var goods = HolderRow(name: "goods", nonNegative: true, balances: [2, 0, 0, 0]);
        var coins = HolderRow(name: "coins", nonNegative: true, balances: [0, 500, 500, 1_000]);
        var rows = new List<WorldStateRow> {
            goods, coins,
            Slot(name: "auctionListRequest", initial: -1),
            Slot(name: "auctionActive", initial: 0),
            Slot(name: "auctionEscrowItem", initial: 0, nonNegative: true),
            Slot(name: "auctionEscrowCoin", initial: 0, nonNegative: true),
            Slot(name: "auctionCurrentBid", initial: 0, nonNegative: true),
            Slot(name: "auctionCurrentBidder", initial: -1),
            Slot(name: "auctionDeadline", initial: 0, nonNegative: true),
            Slot(name: "auctionBidRequest1", initial: -1),
            Slot(name: "auctionBidRequest2", initial: -1),
            new(Name: WorldCellName.Parse(candidate: "auctionBidHistory"), Kind: CellKind.Int, Domain: new WorldStateDomain.Ring(Capacity: 8, Empty: -1)),
            Slot(name: "feeReserve", initial: 0, nonNegative: true),
            Slot(name: "buyoutListRequest", initial: -1),
            Slot(name: "buyoutActive", initial: 0),
            Slot(name: "buyoutEscrowItem", initial: 0, nonNegative: true),
            Slot(name: "buyoutRequest3", initial: 0),
            Slot(name: "buyoutDeadline", initial: 0, nonNegative: true),
        };

        WorldRule BidRule(int seat, string request, string other) => new(
            Name: WorldCellName.Parse(candidate: $"auction-bid-seat{seat}"),
            Gate: new ActionPredicate.All([
                Cmp(state: "auctionActive", comparison: ActionStateComparison.Equal, value: 1),
                Cmp(state: request, comparison: ActionStateComparison.Greater, value: -1),
                Cmp(state: "auctionCurrentBidder", comparison: ActionStateComparison.NotEqual, value: seat),
                Cmp(state: request, comparison: ActionStateComparison.Greater, comparandState: "auctionCurrentBid"),
                Cmp(state: request, comparison: ActionStateComparison.LessOrEqual, comparandState: "coins", comparandKey: seat.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]),
            Mode: ActionTriggerMode.Edge,
            Effects: [
                // Return: whatever is currently escrowed goes back to the OTHER seat — 0 (harmless) if nobody stood
                // yet, or exactly the outbid amount if they did, since escrow always equals auctionCurrentBid.
                AddFrom(state: "coins", key: other, fromState: "auctionEscrowCoin"),
                // Escrow: debit the new bidder, mint the new escrow amount.
                AddNegated(state: "coins", key: seat.ToString(System.Globalization.CultureInfo.InvariantCulture), fromState: request),
                Copy(state: "auctionEscrowCoin", fromState: request),
                Copy(state: "auctionCurrentBid", fromState: request),
                Set(state: "auctionCurrentBidder", value: seat),
                new ActionEffect.PushState(State: "auctionBidHistory", FromState: request),
                Set(state: request, value: -1),
            ]
        );

        var document = Fixtures.BuildDocument().WithWorldState(rows: rows) with {
            Rules = [
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "auction-list"),
                    Gate: new ActionPredicate.All([
                        Cmp(state: "auctionListRequest", comparison: ActionStateComparison.Greater, value: 0),
                        Cmp(state: "auctionActive", comparison: ActionStateComparison.Equal, value: 0),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        AddNegated(state: "goods", key: "0", fromState: "auctionListRequest"),
                        AddFrom(state: "auctionEscrowItem", fromState: "auctionListRequest"),
                        Set(state: "auctionActive", value: 1),
                        Set(state: "auctionCurrentBid", value: 0),
                        Set(state: "auctionCurrentBidder", value: -1),
                        new ActionEffect.ScheduleState(State: "auctionDeadline", DelaySeconds: 0.05m),
                        Set(state: "auctionListRequest", value: -1),
                    ]
                ),
                BidRule(seat: 1, request: "auctionBidRequest1", other: "2"),
                BidRule(seat: 2, request: "auctionBidRequest2", other: "1"),
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "auction-settle-with-bid"),
                    Gate: new ActionPredicate.All([
                        Cmp(state: "auctionActive", comparison: ActionStateComparison.Equal, value: 1),
                        Cmp(state: WorldRuleFacts.Tick, comparison: ActionStateComparison.GreaterOrEqual, comparandState: "auctionDeadline"),
                        Cmp(state: "auctionCurrentBidder", comparison: ActionStateComparison.NotEqual, value: -1),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        new ActionEffect.AddState(State: "goods", Key: $"{WorldRuleFacts.CellKeyPrefix}auctionCurrentBidder:{WorldStateRow.SlotKey}", FromState: "auctionEscrowItem"),
                        Set(state: "auctionEscrowItem", value: 0),
                        new ActionEffect.AddState(State: "coins", Key: "0", Expression: NetOfFee(bps: FeeBasisPoints, fromState: "auctionEscrowCoin")),
                        new ActionEffect.AddState(State: "feeReserve", Expression: Fee(bps: FeeBasisPoints, fromState: "auctionEscrowCoin")),
                        Set(state: "auctionEscrowCoin", value: 0),
                        Set(state: "auctionCurrentBidder", value: -1),
                        Set(state: "auctionActive", value: 0),
                    ]
                ),
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "auction-expire-no-bid"),
                    Gate: new ActionPredicate.All([
                        Cmp(state: "auctionActive", comparison: ActionStateComparison.Equal, value: 1),
                        Cmp(state: WorldRuleFacts.Tick, comparison: ActionStateComparison.GreaterOrEqual, comparandState: "auctionDeadline"),
                        Cmp(state: "auctionCurrentBidder", comparison: ActionStateComparison.Equal, value: -1),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        AddFrom(state: "goods", key: "0", fromState: "auctionEscrowItem"),
                        Set(state: "auctionEscrowItem", value: 0),
                        Set(state: "auctionActive", value: 0),
                    ]
                ),
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "buyout-list"),
                    Gate: new ActionPredicate.All([
                        Cmp(state: "buyoutListRequest", comparison: ActionStateComparison.Greater, value: 0),
                        Cmp(state: "buyoutActive", comparison: ActionStateComparison.Equal, value: 0),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        AddNegated(state: "goods", key: "0", fromState: "buyoutListRequest"),
                        AddFrom(state: "buyoutEscrowItem", fromState: "buyoutListRequest"),
                        Set(state: "buyoutActive", value: 1),
                        new ActionEffect.ScheduleState(State: "buyoutDeadline", DelaySeconds: 0.05m),
                        Set(state: "buyoutListRequest", value: -1),
                    ]
                ),
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "buyout-execute"),
                    Gate: new ActionPredicate.All([
                        Cmp(state: "buyoutActive", comparison: ActionStateComparison.Equal, value: 1),
                        Cmp(state: "buyoutRequest3", comparison: ActionStateComparison.Equal, value: 1),
                        Cmp(state: "coins", comparison: ActionStateComparison.GreaterOrEqual, value: BuyoutPrice, key: "3"),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        Add(state: "coins", value: -BuyoutPrice, key: "3"),
                        new ActionEffect.AddState(State: "coins", Key: "0", Expression: NetOfFee(bps: FeeBasisPoints, fromValue: BuyoutPrice)),
                        new ActionEffect.AddState(State: "feeReserve", Expression: Fee(bps: FeeBasisPoints, fromValue: BuyoutPrice)),
                        AddFrom(state: "goods", key: "3", fromState: "buyoutEscrowItem"),
                        Set(state: "buyoutEscrowItem", value: 0),
                        Set(state: "buyoutActive", value: 0),
                        Set(state: "buyoutRequest3", value: 0),
                    ]
                ),
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "buyout-expire"),
                    Gate: new ActionPredicate.All([
                        Cmp(state: "buyoutActive", comparison: ActionStateComparison.Equal, value: 1),
                        Cmp(state: WorldRuleFacts.Tick, comparison: ActionStateComparison.GreaterOrEqual, comparandState: "buyoutDeadline"),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        AddFrom(state: "goods", key: "0", fromState: "buyoutEscrowItem"),
                        Set(state: "buyoutEscrowItem", value: 0),
                        Set(state: "buyoutActive", value: 0),
                    ]
                ),
            ],
        };

        return document;
    }

    private static long Read(WorldDefinition definition, string row, string key) {
        var found = WorldDefinitionRows.FindStateRow(rows: definition.State, name: row)!;
        var cellKey = WorldCellName.Parse(candidate: key);

        return WorldDefinitionRows.FindCell(cells: found.Cells, key: cellKey)!.Value;
    }
    private static long ReadSlot(WorldDefinition definition, string row) => Read(definition: definition, row: row, key: WorldStateRow.SlotKey.Value);
    // The value pushed most recently (age 0): slot = (cursor - 1) mod capacity, the same walk WorldServer.Patterns'
    // own ReadHistorySlot performs for a $history: read.
    private static long ReadHistoryTop(WorldDefinition definition, string row) {
        var found = WorldDefinitionRows.FindStateRow(rows: definition.State, name: row)!;
        var capacity = ((WorldStateDomain.Ring)found.EffectiveDomain).Capacity;
        var slot = (int)((found.HistoryCursor - 1L) % capacity);

        return found.Cells![slot].Value;
    }
    private static void Write(WorldFixture fixture, string row, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: WorldPrincipal.Console, Row: row, Key: WorldStateRow.SlotKey.Value, Value: value, Kind: WorldDocumentWriteKind.Set
    ));

    [Fact]
    public void EnglishAuction_OutbidThenDeadline_SettlesPayingSellerNetOfFeeAndCreditingTheWinner() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        // List one unit — escrows it out of the seller's own cell atomically with arming the deadline.
        Write(fixture: fixture, row: "auctionListRequest", value: 1);
        fixture.Step();

        Assert.Equal(expected: 1L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "0"));
        Assert.Equal(expected: 1L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionEscrowItem"));
        Assert.Equal(expected: 1L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionActive"));

        // Control: a bid below the (unset) minimum is silently refused — no state moves.
        Write(fixture: fixture, row: "auctionBidRequest1", value: 0);
        fixture.Step();
        Assert.Equal(expected: 500L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "1"));
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionCurrentBid"));

        // Seat 1 bids 100 — escrows it out of their own cell.
        Write(fixture: fixture, row: "auctionBidRequest1", value: 100);
        fixture.Step();
        Assert.Equal(expected: 400L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "1"));
        Assert.Equal(expected: 100L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionEscrowCoin"));
        Assert.Equal(expected: 1L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionCurrentBidder"));
        Assert.Equal(expected: 100L, actual: ReadHistoryTop(definition: fixture.Server.Definition, row: "auctionBidHistory"));

        // Seat 2 outbids at 150 — seat 1's escrow returns, seat 2's own bid escrows in its place.
        Write(fixture: fixture, row: "auctionBidRequest2", value: 150);
        fixture.Step();
        Assert.Equal(expected: 500L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "1"));
        Assert.Equal(expected: 350L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "2"));
        Assert.Equal(expected: 150L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionEscrowCoin"));
        Assert.Equal(expected: 2L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionCurrentBidder"));

        // Control: seat 1 cannot re-raise below seat 2's own standing bid — the attempt is silently refused, so
        // seat 1's refunded balance is untouched.
        Write(fixture: fixture, row: "auctionBidRequest1", value: 120);
        fixture.Step();
        Assert.Equal(expected: 500L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "1"));
        Assert.Equal(expected: 2L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionCurrentBidder"));

        // Control: the listing is still active well before its deadline.
        for (var index = 0; (index < 5); index++) {
            fixture.Step();
        }
        Assert.Equal(expected: 1L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionActive"));

        // Advance past the deadline — the standing bid settles: item to the winner, coin net of fee to the seller,
        // fee to the shared reserve.
        for (var index = 0; (index < 20); index++) {
            fixture.Step();
        }

        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionActive"));
        Assert.Equal(expected: -1L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionCurrentBidder"));
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionEscrowItem"));
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionEscrowCoin"));
        Assert.Equal(expected: 1L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "2")); // the winner, seat 2
        Assert.Equal(expected: 1L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "0")); // the seller's own remaining unit (unlisted, for the buyout demo)
        Assert.Equal(expected: 135L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "0")); // 150 net of 10% fee
        Assert.Equal(expected: 15L, actual: ReadSlot(definition: fixture.Server.Definition, row: "feeReserve"));
    }
    // A control at a bid the 10% fee split does not divide evenly (155): the seller's net share and the fee share
    // must still sum to exactly the escrowed amount, proving the split never destroys or manufactures a coin.
    [Fact]
    public void EnglishAuction_OddBid_SettlesWithoutLosingOrMintingACoin() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());
        long TotalCoins() => Read(definition: fixture.Server.Definition, row: "coins", key: "1")
            + Read(definition: fixture.Server.Definition, row: "coins", key: "2")
            + ReadSlot(definition: fixture.Server.Definition, row: "auctionEscrowCoin")
            + ReadSlot(definition: fixture.Server.Definition, row: "feeReserve")
            + Read(definition: fixture.Server.Definition, row: "coins", key: "0");
        var before = TotalCoins();

        Write(fixture: fixture, row: "auctionListRequest", value: 1);
        fixture.Step();
        Write(fixture: fixture, row: "auctionBidRequest1", value: 155);
        fixture.Step();

        for (var index = 0; (index < 25); index++) {
            fixture.Step();
        }

        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionActive"));
        Assert.Equal(expected: 140L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "0")); // 155 - floor(155*10%)
        Assert.Equal(expected: 15L, actual: ReadSlot(definition: fixture.Server.Definition, row: "feeReserve")); // floor(155*10%)
        Assert.Equal(expected: before, actual: TotalCoins());
    }
    [Fact]
    public void EnglishAuction_NoBidThenDeadline_ReturnsTheItemToTheSeller() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        Write(fixture: fixture, row: "auctionListRequest", value: 1);
        fixture.Step();
        Assert.Equal(expected: 1L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "0"));

        for (var index = 0; (index < 20); index++) {
            fixture.Step();
        }

        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionActive"));
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "auctionEscrowItem"));
        Assert.Equal(expected: 2L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "0")); // returned — back to its starting count
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "feeReserve"));
    }
    [Fact]
    public void Buyout_Executes_PaysTheSellerNetOfFeeAndCreditsTheBuyer() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        Write(fixture: fixture, row: "buyoutListRequest", value: 1);
        fixture.Step();
        Assert.Equal(expected: 1L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "0"));
        Assert.Equal(expected: 1L, actual: ReadSlot(definition: fixture.Server.Definition, row: "buyoutEscrowItem"));

        Write(fixture: fixture, row: "buyoutRequest3", value: 1);
        fixture.Step();

        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "buyoutActive"));
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "buyoutEscrowItem"));
        Assert.Equal(expected: 700L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "3"));
        Assert.Equal(expected: 270L, actual: Read(definition: fixture.Server.Definition, row: "coins", key: "0"));
        Assert.Equal(expected: 30L, actual: ReadSlot(definition: fixture.Server.Definition, row: "feeReserve"));
        Assert.Equal(expected: 1L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "3"));
    }
    [Fact]
    public void Buyout_NeverBought_ExpiresReturningTheItemToTheSeller() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        Write(fixture: fixture, row: "buyoutListRequest", value: 1);
        fixture.Step();

        for (var index = 0; (index < 20); index++) {
            fixture.Step();
        }

        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "buyoutActive"));
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "buyoutEscrowItem"));
        Assert.Equal(expected: 2L, actual: Read(definition: fixture.Server.Definition, row: "goods", key: "0"));
        Assert.Equal(expected: 0L, actual: ReadSlot(definition: fixture.Server.Definition, row: "feeReserve"));
    }
}
