using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: the arena's byte total and retained key ledger reach a job only through the ceiling
/// checks its own walk meets, so a key minted in a row no job reads restarts neither a job whose walk mints nothing nor
/// one whose mints the arena still has room for, and leaves the non-minting job's position key standing; a minting job
/// restarts once the committed ledger leaves less room than its walk was admitted under.</summary>
public sealed class ArenaSearchHeadroomLawTests {
    private static readonly CellName Scratch = Name(value: "scratch");

    private static StateRow[] Rows() => [
        .. Board(
            cells: 4,
            tokens: 2
        ),
        Keyed(name: "notes"),
        Keyed(name: "diary"),
    ];
    // Two jobs over one board, each landing its own output row and judging one candidate a step, so each spans steps.
    private static SearchPlan Plan(string name, string? counts = null, string? legal = null) => new(
        Name: name,
        Tokens: "piece",
        Topology: null,
        Zones: [],
        CellCount: 4,
        Turn: "turn",
        Verdict: "verdict",
        Off: -1L,
        Nodes: 1,
        Work: SearchWork.NodeBounded(judge: 1L),
        Depth: 1,
        Best: null,
        Shapes: [new SearchShapePlan(
            Kind: SearchShapeKind.Relocate,
            Displace: false,
            Directions: [],
            CompanionIndex: -1
        )],
        Counts: counts,
        Legal: legal
    );
    // Accepts every candidate and, inside the candidate's scope, mints a cell the ledger does not hold: a mint the
    // arena's ceilings decide, rewound with the candidate.
    private static DelegateJudge Scribe(Position position) {
        var slot = position.SlotKey;
        var turn = position.Ordinal(name: "turn");
        var verdict = position.Ordinal(name: "verdict");
        var notes = position.Ordinal(name: "notes");

        return new DelegateJudge(
            arena: position.Arena,
            judge: (in ArenaSearchView view) => {
                var arena = view.Arena;

                _ = arena.TryWrite(key: slot, operand: 1L, reason: out _, rowOrdinal: verdict, write: StateWriteKind.Set);
                _ = arena.TryWrite(key: slot, operand: 1L, reason: out _, rowOrdinal: turn, write: StateWriteKind.Add);
                _ = arena.TryMint(key: out _, name: Scratch, reason: out _, rowOrdinal: notes, value: CellValue.Int(value: 1L));

                return true;
            },
            keyRows: [notes]
        );
    }
    private static ArenaSearch Build(Position position) {
        SearchPlan[] plans = [Plan(name: "moves", counts: "counts"), Plan(name: "scribe", legal: "legal")];
        var resolved = new ArenaSearchPlan[plans.Length];

        for (var index = 0; (index < plans.Length); index++) {
            Assert.True(
                condition: ArenaSearchPlan.TryResolve(catalog: position.Catalog, drawSeed: 0UL, plan: plans[index], reason: out var reason, resolved: out var plan),
                userMessage: reason
            );
            resolved[index] = plan!;
        }

        var search = new ArenaSearch(arena: position.Arena);

        Assert.True(
            condition: search.Rebuild(
                judges: [RuleJudge(position: position, rules: [AcceptEveryCandidate()]), Scribe(position: position)],
                plans: resolved,
                reason: out var rebuildReason
            ),
            userMessage: rebuildReason
        );

        return search;
    }
    // A committed key in a row neither job reads: minted and removed again, so its name stays in the ledger and moves
    // the arena's byte total and name count.
    private static void MintOrphan(Position position, string name) {
        var diary = position.Ordinal(name: "diary");

        Assert.True(condition: position.Arena.TryMint(key: out var key, name: Name(value: name), reason: out var reason, rowOrdinal: diary, value: CellValue.Int(value: 0L)), userMessage: reason);
        Assert.True(condition: position.Arena.TryRemove(key: key, reason: out reason, rowOrdinal: diary), userMessage: reason);
    }
    // Steps until both jobs have landed, naming each job's landed writes by its output row, with an orphan key minted
    // before every step when asked.
    private static (List<(string Row, string Key, long Value)> Moves, List<(string Row, string Key, long Value)> Scribe) Land(Position position, ArenaSearch search, bool churn) {
        List<(string Row, string Key, long Value)>? moves = null;
        List<(string Row, string Key, long Value)>? scribe = null;

        for (var step = 0; ((step < 256) && ((moves is null) || (scribe is null))); step++) {
            if (churn) {
                MintOrphan(position: position, name: $"orphan{step}");
            }

            _ = search.Step(
                apply: writes => {
                    var named = Normalize(catalog: position.Catalog, writes: writes);

                    if (named.Exists(match: static write => (write.Row == "counts"))) {
                        moves = named;
                    } else {
                        scribe = named;
                    }

                    return true;
                },
                engineTick: 0UL,
                tick: ((ulong)step)
            );
        }

        Assert.NotNull(@object: moves);
        Assert.NotNull(@object: scribe);

        return (moves!, scribe!);
    }

    [Fact]
    public void AKeyMintedInARowNoJobReadsRestartsNeitherAJobThatMintsNothingNorOneWithRoomForItsMints() {
        var quiet = new Position(rows: Rows());
        var churned = new Position(rows: Rows());
        var quietSearch = Build(position: quiet);
        var churnedSearch = Build(position: churned);
        var key = churnedSearch.PositionKey(index: 0);

        MintOrphan(position: churned, name: "first");
        Assert.Equal(expected: key, actual: churnedSearch.PositionKey(index: 0));

        var expected = Land(position: quiet, search: quietSearch, churn: false);
        var actual = Land(position: churned, search: churnedSearch, churn: true);

        Assert.Equal(expected: expected.Moves, actual: actual.Moves);
        Assert.Equal(expected: expected.Scribe, actual: actual.Scribe);

        foreach (var index in ((int[])[0, 1])) {
            var status = churnedSearch.Status(index: index);

            Assert.True(condition: status.Done, userMessage: status.Name);
            Assert.Equal(expected: 1L, actual: status.Restarts);
            Assert.Equal(expected: ArenaSearchRestart.Started, actual: status.LastRestart);
        }

        var checkpoint = churnedSearch.Capture();

        Assert.False(condition: checkpoint.Jobs[0].Headroom.Consulted);
        Assert.True(condition: checkpoint.Jobs[1].Headroom.Consulted);
        Assert.False(condition: checkpoint.Jobs[1].Headroom.Refused);
    }
    [Fact]
    public void AMintingJobRestartsWhenTheCommittedLedgerLeavesLessRoomThanItsWalkWasAdmittedUnder() {
        var position = new Position(rows: Rows());
        var search = Build(position: position);
        var arena = position.Arena;
        var fill = new List<CellName>(collection: arena.Keys.Names);

        // Room for exactly one more name: the scribe's mint is admitted with nothing to spare.
        for (var index = 0; (fill.Count < (StateCapacity.MaxCellKeys - 1)); index++) {
            fill.Add(item: Name(value: $"fill{index}"));
        }
        Assert.True(condition: arena.TryRestoreKeys(names: fill, reason: out var reason), userMessage: reason);

        _ = Land(position: position, search: search, churn: false);

        var admitted = search.Capture().Jobs[1].Headroom;

        Assert.True(condition: admitted.Consulted);
        Assert.False(condition: admitted.Refused);
        Assert.Equal(expected: ((long)arena.Keys.Count), actual: admitted.KeyCeiling);

        // One committed name more, in a row neither job reads.
        Assert.True(condition: arena.TryRestoreKeys(names: [.. fill, Name(value: "last")], reason: out reason), userMessage: reason);

        for (var step = 0; (step < 4); step++) {
            _ = search.Step(apply: static _ => true, engineTick: 0UL, tick: ((ulong)step));
        }

        var moves = search.Status(index: 0);
        var scribe = search.Status(index: 1);

        Assert.Equal(expected: 1L, actual: moves.Restarts);
        Assert.Equal(expected: 2L, actual: scribe.Restarts);
        Assert.Equal(expected: ArenaSearchRestart.Headroom, actual: scribe.LastRestart);
    }
}
