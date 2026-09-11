namespace Puck.GamingBricks.Tests;

/// <summary>Pins the shared furthest-behind interleave against an independently written oracle for 2, 3, and 4
/// machines running at unequal rates. Both machine families' link sessions pace through this one loop, and the order
/// it steps them in decides which console writes a cable word first, so the recorded sequence — not just the totals —
/// is the contract a link replay depends on.</summary>
public sealed class LinkPacerLawTests {
    [Theory]
    // Two machines, coprime rates: no step ever lands on a shared boundary.
    [InlineData(new long[] { 4L, 7L })]
    // Two machines at the same rate: every round is a tie, so the sequence is the tie-break rule alone.
    [InlineData(new long[] { 5L, 5L })]
    // Three machines, one of them stepping a single cycle at a time.
    [InlineData(new long[] { 4L, 7L, 1L })]
    // Four machines with a repeated rate, so ties appear between a pair that is not the first pair.
    [InlineData(new long[] { 3L, 3L, 5L, 8L })]
    // Four machines whose rates share a factor, so several reach their targets on the same round.
    [InlineData(new long[] { 2L, 4L, 8L, 16L })]
    public void TheInterleaveMatchesTheOracleAcrossConsecutiveBudgets(long[] rates) {
        var budgets = new long[] { 200L, 37L, 1L, 512L, 3L };
        var machines = TestMachine.Fleet(rates: rates);
        var pacedTargets = new long[rates.Length];
        var oracleCycles = new long[rates.Length];
        var oracleTargets = new long[rates.Length];
        var oracleOrder = new List<int>();

        foreach (var budget in budgets) {
            for (var index = 0; (index < rates.Length); ++index) {
                pacedTargets[index] += budget;
                oracleTargets[index] += budget;
            }

            LinkPacer.Run(participants: new Fleet(
                machines: machines,
                targets: pacedTargets
            ));
            oracleOrder.AddRange(collection: OracleOrder(
                cycles: oracleCycles,
                rates: rates,
                targets: oracleTargets
            ));
        }

        Assert.Equal(
            actual: TestMachine.MergeByRound(machines: machines),
            expected: oracleOrder
        );

        for (var index = 0; (index < rates.Length); ++index) {
            Assert.Equal(
                actual: machines[index].Cycles,
                expected: oracleCycles[index]
            );
            // One step at a time: a machine stops as soon as it reaches its target, so it never overshoots by more
            // than a single step's worth of cycles.
            Assert.InRange(
                actual: machines[index].Cycles,
                high: ((pacedTargets[index] + rates[index]) - 1L),
                low: pacedTargets[index]
            );
        }
    }
    [Fact]
    public void EquallyBehindMachinesStepInCableOrder() {
        var machines = TestMachine.Fleet(rates: [1L, 1L, 1L, 1L]);

        LinkPacer.Run(participants: new Fleet(
            machines: machines,
            targets: [3L, 3L, 3L, 3L]
        ));

        Assert.Equal(
            actual: TestMachine.MergeByRound(machines: machines),
            expected: new List<int> { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 }
        );
    }
    [Fact]
    public void AMachineAlreadyPastItsTargetIsNeverStepped() {
        var machines = TestMachine.Fleet(rates: [1L, 1L]);

        machines[0].Cycles = 10L;

        LinkPacer.Run(participants: new Fleet(
            machines: machines,
            targets: [4L, 4L]
        ));

        Assert.Equal(
            actual: TestMachine.MergeByRound(machines: machines),
            expected: new List<int> { 1, 1, 1, 1 }
        );
        Assert.Equal(
            actual: machines[0].Cycles,
            expected: 10L
        );
    }

    // The oracle: at each round rank every machine that is still behind by remainder descending, then by cable
    // position, and step the winner. Deliberately expressed as an ordering rather than as a running maximum, so it
    // agrees with the implementation only when the tie-break really is "lowest index wins".
    private static List<int> OracleOrder(long[] rates, long[] targets, long[] cycles) {
        var order = new List<int>();

        while (true) {
            var behind = Enumerable
                .Range(
                    count: rates.Length,
                    start: 0
                )
                .Select(selector: index => (Index: index, Remaining: (targets[index] - cycles[index])))
                .Where(predicate: candidate => (candidate.Remaining > 0L))
                .OrderByDescending(keySelector: candidate => candidate.Remaining)
                .ThenBy(keySelector: candidate => candidate.Index)
                .ToList();

            if (behind.Count == 0) {
                return order;
            }

            var winner = behind[0].Index;

            cycles[winner] += rates[winner];

            order.Add(item: winner);
        }
    }

    private sealed class RoundCounter {
        private long m_next;

        public long Next() =>
            m_next++;
    }
    private sealed class TestMachine {
        private readonly RoundCounter m_rounds;

        public TestMachine(int index, long rate, RoundCounter rounds) {
            Index = index;
            Rate = rate;
            m_rounds = rounds;
        }

        public long Cycles { get; set; }
        public int Index { get; }
        public long Rate { get; }
        public List<long> StepRounds { get; } = [];

        public static TestMachine[] Fleet(long[] rates) {
            var machines = new TestMachine[rates.Length];
            var rounds = new RoundCounter();

            for (var index = 0; (index < rates.Length); ++index) {
                machines[index] = new TestMachine(
                    index: index,
                    rate: rates[index],
                    rounds: rounds
                );
            }

            return machines;
        }
        // Rebuilds the global step order from the per-machine round stamps, so the recorded sequence comes from the
        // machines the pacer actually touched rather than from a list the pacer's own caller kept.
        public static List<int> MergeByRound(TestMachine[] machines) =>
            [.. machines
                .SelectMany(selector: machine => machine.StepRounds.Select(selector: round => (machine.Index, Round: round)))
                .OrderBy(keySelector: step => step.Round)
                .Select(selector: step => step.Index)];
        public void Step() {
            Cycles += Rate;

            StepRounds.Add(item: m_rounds.Next());
        }
    }
    private readonly struct Fleet(TestMachine[] machines, long[] targets) : ILinkPacerParticipants {
        public int Count =>
            machines.Length;

        public long GetRemaining(int index) =>
            (targets[index] - machines[index].Cycles);
        public void StepOnce(int index) =>
            machines[index].Step();
    }
}
