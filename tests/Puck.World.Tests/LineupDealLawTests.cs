using Puck.Commands;
using System.Globalization;
using Puck.Abstractions.Counting;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the placements lineup deals from each seat's hidden gallery rows, through a real server: a
/// remote peer is handed the galleries as its own disclosure deals them, one tick's deal is one mutation, and the
/// World's own deal is echoed to no session.</summary>
public sealed class LineupDealLawTests(ITestOutputHelper output) {
    private const int Busts = 24;

    private static readonly string[] Layers = ["Face", "Hair", "Eyes", "Glasses", "Hat", "Beard", "Earrings", "Nose"];
    private static readonly Lazy<WorldDefinition> Source = new(valueFactory: () => AuthoredGameFixtures.Load("worlds/parlor/lineup.puck"));

    private static string Key(int value) => value.ToString(provider: CultureInfo.InvariantCulture);
    private static void Set(WorldFixture fixture, string row, string key, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: Principal.Console,
        Row: row,
        Key: key,
        Value: value,
        Kind: WorldDocumentWriteKind.Set
    ));
    // Boots lineup with neither seat the computer and runs its deal: both secrets drawn, both galleries standing whole.
    private static WorldFixture Dealt() {
        var fixture = Fixtures.FreshServer(definition: Source.Value);

        Set(fixture: fixture, key: "cpuMask", row: "lineupOptions", value: 0);
        Steps(fixture: fixture, ticks: 10);

        return fixture;
    }
    private static void Steps(WorldFixture fixture, int ticks) {
        for (var tick = 0; (tick < ticks); tick++) {
            fixture.Step();
        }
    }
    // One seat asks one question through the act door, the way its bound keys do.
    private static void Ask(WorldFixture fixture, int seat, int question, long request) {
        Set(fixture: fixture, key: "seat", row: "lineupAct", value: seat);
        Set(fixture: fixture, key: "kind", row: "lineupAct", value: 1);
        Set(fixture: fixture, key: "value", row: "lineupAct", value: question);
        Set(fixture: fixture, key: "request", row: "lineupAct", value: request);
    }
    private static string Prototype(IReadOnlyList<WorldPlacement> placements, int seat, string layer, int bust) =>
        WorldDefinitionRows.FindPlacement(id: $"lineupGallery{seat}{layer}/{Key(value: bust)}", placements: placements)!.PrototypeId;
    // What a presentation-tier peer serving one seat is handed on the wire: the document encoded for that recipient
    // and decoded as the peer decodes it.
    private static WorldDefinition Served(WorldFixture fixture, Principal recipient) {
        var bytes = WorldFederationCodec.EncodeDocument(
            authority: "boot",
            definition: fixture.Server.Definition,
            recipient: recipient,
            revision: 1,
            tier: WorldDisclosureTier.Presentation
        );

        Assert.True(condition: WorldFederationCodec.TryDecodeDocument(
            body: bytes,
            definition: out var served,
            failure: out var failure,
            tier: out var tier
        ), userMessage: $"the served document did not decode: {failure}");
        Assert.Equal(actual: tier, expected: WorldDisclosureTier.Presentation);

        return served!;
    }

    // Seat 1 has ruled busts out, so its gallery's parts spell its hidden standing set. A presentation peer serving
    // seat 2 is handed seat 1's gallery as seat 2's disclosure deals it — every part the template's own blank, since
    // seat 2 reads none of seat 1's gallery rows — and its own gallery exactly as the server holds it; a peer serving
    // seat 1 is handed seat 1's gallery whole. The console's placement census answers each seat the same way.
    [Fact]
    public void APeerServingSeatTwoCarriesNoneOfSeatOnesGalleryVariants() {
        using var fixture = Dealt();

        Ask(fixture: fixture, question: 9, request: 1, seat: 1);
        Steps(fixture: fixture, ticks: 10);

        var live = fixture.Server.Definition.Placements;
        var forSeat2 = Served(fixture: fixture, recipient: Principal.Seat(slot: 1)).Placements;
        var forSeat1 = Served(fixture: fixture, recipient: Principal.Seat(slot: 0)).Placements;
        var down = 0;

        for (var bust = 0; (bust < Busts); bust++) {
            down += ((Prototype(bust: bust, layer: "Face", placements: live, seat: 1) == "lineupBustDown") ? 1 : 0);

            foreach (var layer in Layers) {
                Assert.Equal(expected: "lineupBlank", actual: Prototype(bust: bust, layer: layer, placements: forSeat2, seat: 1));
                Assert.Equal(expected: Prototype(bust: bust, layer: layer, placements: live, seat: 2), actual: Prototype(bust: bust, layer: layer, placements: forSeat2, seat: 2));
                Assert.Equal(expected: Prototype(bust: bust, layer: layer, placements: live, seat: 1), actual: Prototype(bust: bust, layer: layer, placements: forSeat1, seat: 1));
            }
        }

        // The control: seat 1's live gallery does spell its standing set, so a peer handed it verbatim would read it.
        Assert.InRange(actual: down, high: (Busts - 1), low: 1);
        Assert.Equal(expected: live.Count, actual: forSeat2.Count);

        var census = fixture.Server.DescribePlacements(view: WorldStateReadView.Of(reader: Principal.Seat(slot: 1), server: fixture.Server));

        Assert.DoesNotContain(actualString: census, expectedSubstring: "lineupBustDown");
        Assert.Contains(actualString: census, expectedSubstring: "'lineupGallery1Face/0' prototype=lineupBlank");
        Assert.Contains(expectedSubstring: "lineupBustDown", actualString: fixture.Server.DescribePlacements(view: WorldStateReadView.Of(reader: Principal.Seat(slot: 0), server: fixture.Server)));
    }
    // A turn moves one seat's standing set, and the gallery redraws all eight of that seat's layer rows in one tick;
    // the deal re-syncs every layer's template together as one mutation, validated once. Allocation is counted, never
    // timed.
    [Fact]
    public void ATurnDealsAsOneMutationATickUnderAnAllocationCeiling() {
        using var fixture = Dealt();
        var dealt = new List<(ulong Tick, WorldMutation Mutation)>();

        fixture.Server.MutationJournalTap = (tick, _, mutation) => {
            if (mutation.Principal == Principal.World) {
                dealt.Add(item: (tick, mutation));
            }
        };
        Steps(fixture: fixture, ticks: 40);

        var idle = AllocationWindow.Total(window: () => fixture.Step());
        var turns = new long[4];

        for (var turn = 0; (turn < turns.Length); turn++) {
            var asked = turn;

            turns[turn] = AllocationWindow.Total(window: () => {
                Ask(fixture: fixture, question: (asked + 2), request: (asked + 1), seat: ((asked % 2) + 1));
                Steps(fixture: fixture, ticks: 10);
            });
        }

        output.WriteLine(message: $"lineup: idle tick {idle:N0} bytes; turns {string.Join(separator: ", ", values: turns.Select(selector: bytes => bytes.ToString(format: "N0", provider: CultureInfo.InvariantCulture)))} bytes; {dealt.Count} world mutation(s) over {dealt.Select(selector: entry => entry.Tick).Distinct().Count()} tick(s)");
        Assert.NotEmpty(collection: dealt);
        Assert.All(collection: dealt.GroupBy(keySelector: entry => entry.Tick), action: tick => Assert.Single(collection: tick));
        Assert.All(collection: dealt, action: entry => Assert.IsType<WorldMutation.Batch>(@object: entry.Mutation));
        Assert.True(condition: (idle < 1024L), userMessage: $"idle tick {idle:N0} bytes");
        Assert.All(collection: turns, action: bytes => Assert.True(condition: (bytes < ((8L * 1024L) * 1024L)), userMessage: $"a turn allocated {bytes:N0} bytes"));
    }
    // The deal is the World's own work: it journals and narrates, but no session submitted it, so it raises no edit
    // echo — the echo is what reaches the toast, the console tape and the edit cue lane. The console's own writes that
    // drove it still echo, so the tap is live.
    [Fact]
    public void TheWorldsOwnDealRaisesNoEditEcho() {
        using var fixture = Dealt();
        var echoes = new List<WorldEditEcho>();
        var journaled = 0;

        fixture.Server.EchoTap = echo => echoes.Add(item: echo);
        fixture.Server.MutationJournalTap = (_, _, mutation) => journaled += ((mutation.Principal == Principal.World) ? 1 : 0);
        Ask(fixture: fixture, question: 9, request: 1, seat: 1);
        Steps(fixture: fixture, ticks: 10);

        Assert.True(condition: (journaled > 0), userMessage: "the turn dealt nothing");
        Assert.Equal(expected: 4, actual: echoes.Count(predicate: echo => (echo.Mutation?.Principal == Principal.Console)));
        Assert.DoesNotContain(collection: echoes, filter: echo => (echo.Mutation?.Principal == Principal.World));
    }
}
