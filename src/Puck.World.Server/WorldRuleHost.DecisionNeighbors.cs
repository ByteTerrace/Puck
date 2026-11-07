using System.Numerics;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>Observed work in the last ordinary rule pass; diagnostic counters do not affect simulation choices.</summary>
public readonly record struct WorldDecisionWork(long Inspected, long Scored, long SightTests, long LimitedQueries, int ImagePoints = 0, int GridBuilds = 0);
public sealed partial class WorldRuleHost {
    private DecisionPerception? m_decisionPerception;
    private WorldDecisionWork m_decisionWork;

    /// <summary>Gets bounded-neighbor and score work from the last completed ordinary rule pass.</summary>
    internal WorldDecisionWork DecisionWork { get { lock (Host.AuthorityGate) { return m_decisionWork; } } }

    private readonly record struct DecisionChoice(int Option, int Candidate, int Generation, long Score) : IComparable<DecisionChoice> {
        public int CompareTo(DecisionChoice other) => Candidate.CompareTo(value: other.Candidate);
    }
    // Derived scratch only. Rebuilt before rule effects; reconciliation during an effect must not replace this image.
    private sealed class DecisionPerception(int capacity) {
        public readonly FixedVector3[] Positions = new FixedVector3[capacity];
        public readonly FixedQuaternion[] Orientations = new FixedQuaternion[capacity];
        public readonly int[] Generations = new int[capacity];
        public readonly bool[] Active = new bool[capacity];
        public readonly FixedSpatialPoint[] Points = new FixedSpatialPoint[capacity];
        public readonly FixedSpatialNeighbor[] Scratch = new FixedSpatialNeighbor[WorldBodiesLimits.CapacityCeiling];
        public readonly Dictionary<long, Grid> Grids = [];

        public int Count;

        public sealed class Grid(int capacity, long width) {
            public readonly FixedSpatialNeighborhood Neighborhood = new(
                capacity: capacity,
                cellWidth: FixedQ4816.FromRawBits(value: width)
            );

            public bool Built;
        }
    }

    private void FreezeDecisionPerception(CompiledRule[] rules) {
        if (!rules.Any(predicate: static rule => ((rule as CompiledWorldFactsRule)?.Decision?.Options.Any(predicate: static option => (option.Neighbors is not null)) == true))) { return; }
        if (
            (m_decisionPerception is null) ||
            (m_decisionPerception.Active.Length != Host.Population.Capacity)
        ) {
            m_decisionPerception = new(capacity: Host.Population.Capacity);
        }
        var image = m_decisionPerception;

        foreach (var compiled in rules) {
            if ((compiled as CompiledWorldFactsRule)?.Decision is not { } policy) { continue; }
            foreach (var option in policy.Options) {
                if (option.Neighbors is not { } neighbors) { continue; }
                var width = neighbors.CellWidth.Value;

                if (!image.Grids.ContainsKey(key: width)) {
                    image.Grids.Add(
                    key: width,
                    value: new(
                        capacity: Host.Population.Capacity,
                        width: width
                    )
                );
                }
            }
        }
        Array.Clear(array: image.Active);
        image.Count = 0;
        for (var index = 0; (index < image.Active.Length); index++) {
            if (
                !Host.Population.IsActive(index: index) ||
                (Host.Population.EntryBody(index: index) is not { } body)
            ) { continue; }
            image.Active[index] = true;
            image.Generations[index] = Host.Population.Generation(index: index);
            image.Positions[index] = body.FixedPosition;
            image.Orientations[index] = body.FixedOrientation;
            image.Points[image.Count++] = new(
                Index: index,
                Position: body.FixedPosition
            );
        }
        foreach (var grid in image.Grids.Values) { grid.Built = false; }
        m_decisionWork = m_decisionWork with { ImagePoints = image.Count };
    }
    private bool DecisionBodyLive(int index, int generation) => ((((uint)index) < ((uint)Host.Population.Capacity)) &&
        Host.Population.IsActive(index: index) && (Host.Population.EntryBody(index: index) is not null) && (Host.Population.Generation(index: index) == generation));
    // What one option's gate decided for a candidate.
    private Verdict DecisionOptionGate(CompiledWorldFactsDecisionOption option, int key, int candidate, string ruleName) {
        var left = m_boundLeft; var right = m_boundRight;

        m_boundLeft = ((option.Neighbors is null)
            ? -1
            : key
        ); m_boundRight = candidate;
        try {
            return m_evaluator.Decide(
            gate: option.Gate,
            ruleName: ruleName
        );
        } finally { m_boundLeft = left; m_boundRight = right; }
    }
    // Whether an option's gate opened, noting a fault the decision must hold on.
    private static bool Opens(Verdict verdict, ref bool faulted) {
        faulted |= (verdict == Verdict.Faulted);

        return (verdict == Verdict.Open);
    }

    // What scoring one candidate answered. The zero value is a fault, so a result nothing set holds the decision.
    private enum DecisionScore : byte {
        Faulted = 0,
        Declined,
        Scored,
    }

    private bool DecisionPerceptible(DecisionPerception image, CompiledWorldFactsDecisionNeighbors neighbors, int observer, int candidate) {
        if (
            (candidate == observer) ||
            (((uint)candidate) >= ((uint)image.Active.Length)) ||
            !image.Active[candidate] ||
            !DecisionBodyLive(
            candidate,
            image.Generations[candidate]
        )
        ) { return false; }
        var a = image.Positions[observer]; var b = image.Positions[candidate];

        // An offset the carrier cannot hold is farther than any range.
        if (
            !FixedVector3.TrySubtract(
            difference: out var offset,
            left: b,
            right: a
        ) ||
            !offset.IsWithin(radius: neighbors.Range)
        ) { return false; }
        var forward = image.Orientations[observer].Rotate(vector: -FixedVector3.UnitZ);

        if (!offset.IsWithinCone(
            cosineHalfAngle: neighbors.MinimumDot,
            forward: forward
        )) { return false; }
        if (!neighbors.Source.RequiresLineOfSight) { return true; }
        m_decisionWork = m_decisionWork with { SightTests = (m_decisionWork.SightTests + 1) };
        return Host.Population.HasLineOfSight(
            from: a,
            fromOrientation: image.Orientations[observer],
            to: b,
            toOrientation: image.Orientations[candidate]
        );
    }
    // The choices the options offer, in the runtime's buffer. A gate or score that faulted decides nothing, so the
    // caller holds the decision.
    private int GatherDecisionChoices(DecisionRuntime runtime, int key, in DecisionBinding state, bool lostEligibility, out bool faulted) {
        var policy = runtime.Rule.Decision!;
        var ruleName = runtime.Rule.Name;
        var written = 0;

        faulted = false;

        for (var index = 0; (index < policy.Options.Length); index++) {
            var option = policy.Options[index];

            if (option.Neighbors is not { } neighbors) {
                var plain = TryScoreDecisionChoice(
                    policy,
                    index,
                    key,
                    -1,
                    0,
                    ruleName,
                    state,
                    lostEligibility,
                    out var choice
                );

                faulted |= (plain == DecisionScore.Faulted);
                if (plain == DecisionScore.Scored) {
                    runtime.Choices[written++] = choice;
                }
                continue;
            }
            var image = m_decisionPerception!;

            if (
                (((uint)key) >= ((uint)image.Active.Length)) ||
                !image.Active[key] ||
                !DecisionBodyLive(
                key,
                image.Generations[key]
            )
            ) { continue; }
            var start = written;
            var retained = 0;
            var incumbent = -1;
            var budget = neighbors.Source.CandidateBudget;

            if (
                neighbors.Source.RetainCurrent &&
                (index == state.Selected) &&
                (state.Candidate >= 0)
            ) {
                budget--;
                m_decisionWork = m_decisionWork with { Inspected = (m_decisionWork.Inspected + 1) };
                if (
                    DecisionBodyLive(
                    generation: state.CandidateGeneration,
                    index: state.Candidate
                ) &&
                    DecisionPerceptible(
                    candidate: state.Candidate,
                    image: image,
                    neighbors: neighbors,
                    observer: key
                ) &&
                    Opens(
                    faulted: ref faulted,
                    verdict: DecisionOptionGate(
                    candidate: state.Candidate,
                    key: key,
                    option: option,
                    ruleName: ruleName
                ))
                ) {
                    incumbent = state.Candidate;
                    retained++;

                    var kept = TryScoreDecisionChoice(
                        policy,
                        index,
                        key,
                        incumbent,
                        state.CandidateGeneration,
                        ruleName,
                        state,
                        lostEligibility,
                        out var choice,
                        gateAlreadyOpen: true
                    );

                    faulted |= (kept == DecisionScore.Faulted);
                    if (kept == DecisionScore.Scored) {
                        runtime.Choices[written++] = choice;
                    }
                }
            }
            if (
                (budget > 0) &&
                (retained < neighbors.Source.MaxCandidates)
            ) {
                var grid = image.Grids[neighbors.CellWidth.Value];

                if (!grid.Built) {
                    grid.Neighborhood.Rebuild(points: image.Points.AsSpan(
                        length: image.Count,
                        start: 0
                    )); grid.Built = true;
                    m_decisionWork = m_decisionWork with { GridBuilds = (m_decisionWork.GridBuilds + 1) };
                }
                var width = ((ulong)image.Active.Length).NextPowerOfTwo();
                var bits = BitOperations.Log2(value: width);
                // Visit distant portions of each population-sized phase block first, not an almost identical
                // sliding window. This permutes every phase in each block and consumes no choice RNG draws.
                var ordinal = ((bits == 0)
                    ? state.Reconsiderations
                    : state.Reconsiderations.AlignDown(alignment: width) | (state.Reconsiderations.ReverseBits() >> (64 - bits))
                );
                var phase = FixedFlockSteering.IndexPhase(
                    generation: ((uint)state.Generation),
                    index: ((uint)index),
                    key: ((uint)key),
                    ordinal: ordinal
                );
                var work = grid.Neighborhood.Query(
                    image.Positions[key],
                    neighbors.Range,
                    key,
                    budget,
                    phase,
                    image.Scratch.AsSpan(
                        length: budget,
                        start: 0
                    )
                );

                m_decisionWork = m_decisionWork with {
                    Inspected = (m_decisionWork.Inspected + work.CandidatesExamined),
                    LimitedQueries = (m_decisionWork.LimitedQueries + (work.BudgetLimited
                    ? 1
                    : 0)),
                };
                for (var n = 0; ((n < work.NeighborsWritten) && (retained < neighbors.Source.MaxCandidates)); n++) {
                    var candidate = image.Scratch[n].Index;

                    if (
                        (candidate == incumbent) ||
                        !DecisionPerceptible(
                        candidate: candidate,
                        image: image,
                        neighbors: neighbors,
                        observer: key
                    ) ||
                        !Opens(
                        faulted: ref faulted,
                        verdict: DecisionOptionGate(
                        candidate: candidate,
                        key: key,
                        option: option,
                        ruleName: ruleName
                    ))
                    ) { continue; }
                    retained++;

                    var scoredCandidate = TryScoreDecisionChoice(
                        policy,
                        index,
                        key,
                        candidate,
                        image.Generations[candidate],
                        ruleName,
                        state,
                        lostEligibility,
                        out var choice,
                        gateAlreadyOpen: true
                    );

                    faulted |= (scoredCandidate == DecisionScore.Faulted);
                    if (scoredCandidate == DecisionScore.Scored) {
                        runtime.Choices[written++] = choice;
                    }
                }
            }
            // Per-option stable body order owns ties and weighted ticket intervals, not distance or incumbent insertion order.
            runtime.Choices.AsSpan(
                length: (written - start),
                start: start
            ).Sort();
        }
        return written;
    }
    private DecisionScore TryScoreDecisionChoice(CompiledWorldFactsDecision policy, int index, int key, int candidate, int generation,
        string ruleName, in DecisionBinding state, bool lostEligibility, out DecisionChoice choice, bool gateAlreadyOpen = false) {
        choice = default;
        var option = policy.Options[index];
        var left = m_boundLeft; var right = m_boundRight;

        m_boundLeft = ((option.Neighbors is null)
            ? -1
            : key
        ); m_boundRight = candidate;
        try {
            if (!gateAlreadyOpen) {
                switch (m_evaluator.Decide(
                    gate: option.Gate,
                    ruleName: ruleName
                )) {
                    case Verdict.Faulted:
                        return DecisionScore.Faulted;
                    case Verdict.Closed:
                        return DecisionScore.Declined;
                }
            }

            m_decisionWork = m_decisionWork with { Scored = (m_decisionWork.Scored + 1) };

            var scored = m_evaluator.Score(
                kind: policy.ScoreKind,
                program: option.Score
            );

            if (scored.Case == FaultCase.Faulted) {
                return DecisionScore.Faulted;
            }
            if (
                !scored.TryFinite(raw: out var score) ||
                ((policy.Mode == WorldDecisionMode.Weighted) && (score <= 0))
            ) { return DecisionScore.Declined; }
            if (
                (index == state.Selected) &&
                (candidate == state.Candidate) &&
                (generation == state.CandidateGeneration) &&
                !lostEligibility
            ) {
                score = ((long)Int128.Min(
                    x: long.MaxValue,
                    y: (((Int128)score) + policy.IncumbentBonus)
                ));
            }
            choice = new(
                Candidate: candidate,
                Generation: generation,
                Option: index,
                Score: score
            );
            return DecisionScore.Scored;
        } finally { m_boundLeft = left; m_boundRight = right; }
    }
}
