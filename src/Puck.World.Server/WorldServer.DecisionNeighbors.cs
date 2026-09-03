using System.Numerics;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>Observed work in the last ordinary rule pass; diagnostic counters do not affect simulation choices.</summary>
public readonly record struct WorldDecisionWork(long Inspected, long Scored, long SightTests, long LimitedQueries, int ImagePoints = 0, int GridBuilds = 0);

public sealed partial class WorldServer {
    private DecisionPerception? m_decisionPerception;
    private WorldDecisionWork m_decisionWork;

    /// <summary>Gets bounded-neighbor and score work from the last completed ordinary rule pass.</summary>
    public WorldDecisionWork DecisionWork { get { lock (m_authorityGate) { return m_decisionWork; } } }

    private readonly record struct DecisionChoice(int Option, int Candidate, int Generation, long Score) : IComparable<DecisionChoice> {
        public int CompareTo(DecisionChoice other) => Candidate.CompareTo(other.Candidate);
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
            public readonly FixedSpatialNeighborhood Neighborhood = new(capacity, FixedQ4816.FromRawBits(width));
            public bool Built;
        }
    }

    private void FreezeDecisionPerception(CompiledWorldRule[] rules) {
        if (!rules.Any(static rule => rule.Decision?.Options.Any(static option => option.Neighbors is not null) == true)) { return; }
        if (m_decisionPerception is null || m_decisionPerception.Active.Length != m_population.Capacity) {
            m_decisionPerception = new(m_population.Capacity);
        }
        var image = m_decisionPerception;
        foreach (var rule in rules) {
            if (rule.Decision is not { } policy) { continue; }
            foreach (var option in policy.Options) {
                if (option.Neighbors is not { } neighbors) { continue; }
                var width = neighbors.CellWidth.Value;
                if (!image.Grids.ContainsKey(width)) { image.Grids.Add(width, new(m_population.Capacity, width)); }
            }
        }
        Array.Clear(image.Active);
        image.Count = 0;
        for (var index = 0; index < image.Active.Length; index++) {
            if (!m_population.IsActive(index) || m_population.EntryBody(index) is not { } body) { continue; }
            image.Active[index] = true;
            image.Generations[index] = m_population.Generation(index);
            image.Positions[index] = body.FixedPosition;
            image.Orientations[index] = body.FixedOrientation;
            image.Points[image.Count++] = new(index, body.FixedPosition);
        }
        foreach (var grid in image.Grids.Values) { grid.Built = false; }
        m_decisionWork = m_decisionWork with { ImagePoints = image.Count };
    }

    private bool DecisionBodyLive(int index, int generation) => (uint)index < (uint)m_population.Capacity &&
        m_population.IsActive(index) && m_population.EntryBody(index) is not null && m_population.Generation(index) == generation;

    private bool DecisionOptionGate(CompiledWorldDecisionOption option, int observer, int candidate, ulong tick) {
        var left = m_boundLeft; var right = m_boundRight;
        m_boundLeft = option.Neighbors is null ? -1 : observer; m_boundRight = candidate;
        try { return RuleGateOpen(option.Gate, tick); }
        finally { m_boundLeft = left; m_boundRight = right; }
    }

    private bool DecisionPerceptible(DecisionPerception image, CompiledWorldDecisionNeighbors neighbors, int observer, int candidate) {
        if (candidate == observer || (uint)candidate >= (uint)image.Active.Length || !image.Active[candidate] ||
            !DecisionBodyLive(candidate, image.Generations[candidate])) { return false; }
        var a = image.Positions[observer]; var b = image.Positions[candidate];
        // Reject far/extreme coordinates before subtraction or fixed-point norms can overflow.
        var radius = (UInt128)(ulong)neighbors.Range.Value;
        var x = (UInt128)Int128.Abs((Int128)a.X.Value - b.X.Value);
        var y = (UInt128)Int128.Abs((Int128)a.Y.Value - b.Y.Value);
        var z = (UInt128)Int128.Abs((Int128)a.Z.Value - b.Z.Value);
        if (x > radius || y > radius || z > radius || x * x + y * y + z * z > radius * radius) { return false; }
        var offset = b - a;
        var forward = image.Orientations[observer].Rotate(new(FixedQ4816.Zero, FixedQ4816.Zero, -FixedQ4816.One));
        if (offset != FixedVector3.Zero && FixedVector3.Dot(forward, offset.Normalize()) < neighbors.MinimumDot) { return false; }
        if (!neighbors.Source.RequiresLineOfSight) { return true; }
        m_decisionWork = m_decisionWork with { SightTests = m_decisionWork.SightTests + 1 };
        return m_population.HasLineOfSight(a, image.Orientations[observer], b, image.Orientations[candidate]);
    }

    private int GatherDecisionChoices(DecisionRuntime runtime, int key, ulong tick, in DecisionBinding state, bool lostEligibility) {
        var policy = runtime.Rule.Decision!;
        var written = 0;
        for (var index = 0; index < policy.Options.Length; index++) {
            var option = policy.Options[index];
            if (option.Neighbors is not { } neighbors) {
                if (TryScoreDecisionChoice(policy, index, key, -1, 0, tick, state, lostEligibility, out var choice)) {
                    runtime.Choices[written++] = choice;
                }
                continue;
            }
            var image = m_decisionPerception!;
            if ((uint)key >= (uint)image.Active.Length || !image.Active[key] || !DecisionBodyLive(key, image.Generations[key])) { continue; }
            var start = written;
            var retained = 0;
            var incumbent = -1;
            var budget = neighbors.Source.CandidateBudget;
            if (neighbors.Source.RetainCurrent && index == state.Selected && state.Candidate >= 0) {
                budget--;
                m_decisionWork = m_decisionWork with { Inspected = m_decisionWork.Inspected + 1 };
                if (DecisionBodyLive(state.Candidate, state.CandidateGeneration) &&
                    DecisionPerceptible(image, neighbors, key, state.Candidate) && DecisionOptionGate(option, key, state.Candidate, tick)) {
                    incumbent = state.Candidate;
                    retained++;
                    if (TryScoreDecisionChoice(policy, index, key, incumbent, state.CandidateGeneration, tick, state, lostEligibility, out var choice, gateAlreadyOpen: true)) {
                        runtime.Choices[written++] = choice;
                    }
                }
            }
            if (budget > 0 && retained < neighbors.Source.MaxCandidates) {
                var grid = image.Grids[neighbors.CellWidth.Value];
                if (!grid.Built) {
                    grid.Neighborhood.Rebuild(image.Points.AsSpan(0, image.Count)); grid.Built = true;
                    m_decisionWork = m_decisionWork with { GridBuilds = m_decisionWork.GridBuilds + 1 };
                }
                var width = BitOperations.RoundUpToPowerOf2((ulong)image.Active.Length);
                var bits = BitOperations.Log2(width);
                // Visit distant portions of each population-sized phase block first, not an almost identical
                // sliding window. This permutes every phase in each block and consumes no choice RNG draws.
                var ordinal = bits == 0 ? state.Reconsiderations :
                    (state.Reconsiderations & ~(width - 1)) | (state.Reconsiderations.ReverseBits() >> (64 - bits));
                var phase = unchecked(ordinal + (ulong)(uint)key * 0x9E3779B9UL +
                    ((ulong)(uint)state.Generation << 32) + (ulong)index * 0x85EBCA6BUL);
                var work = grid.Neighborhood.Query(image.Positions[key], neighbors.Range, key, budget, phase, image.Scratch.AsSpan(0, budget));
                m_decisionWork = m_decisionWork with {
                    Inspected = m_decisionWork.Inspected + work.CandidatesExamined,
                    LimitedQueries = m_decisionWork.LimitedQueries + (work.BudgetLimited ? 1 : 0),
                };
                for (var n = 0; n < work.NeighborsWritten && retained < neighbors.Source.MaxCandidates; n++) {
                    var candidate = image.Scratch[n].Index;
                    if (candidate == incumbent || !DecisionPerceptible(image, neighbors, key, candidate) || !DecisionOptionGate(option, key, candidate, tick)) { continue; }
                    retained++;
                    if (TryScoreDecisionChoice(policy, index, key, candidate, image.Generations[candidate], tick, state, lostEligibility, out var choice, gateAlreadyOpen: true)) {
                        runtime.Choices[written++] = choice;
                    }
                }
            }
            // Per-option stable body order owns ties and weighted ticket intervals, not distance or incumbent insertion order.
            runtime.Choices.AsSpan(start, written - start).Sort();
        }
        return written;
    }

    private bool TryScoreDecisionChoice(CompiledWorldDecision policy, int index, int key, int candidate, int generation,
        ulong tick, in DecisionBinding state, bool lostEligibility, out DecisionChoice choice, bool gateAlreadyOpen = false) {
        choice = default;
        var option = policy.Options[index];
        var left = m_boundLeft; var right = m_boundRight;
        m_boundLeft = option.Neighbors is null ? -1 : key; m_boundRight = candidate;
        try {
            if (!gateAlreadyOpen && !RuleGateOpen(option.Gate, tick)) { return false; }
            m_decisionWork = m_decisionWork with { Scored = m_decisionWork.Scored + 1 };
            if (!TryEvaluateExpression(option.Score, policy.ScoreKind, tick, out var score) ||
                (policy.Mode == WorldDecisionMode.Weighted && score <= 0)) { return false; }
            if (index == state.Selected && candidate == state.Candidate && generation == state.CandidateGeneration && !lostEligibility) {
                score = (long)Int128.Min(long.MaxValue, (Int128)score + policy.IncumbentBonus);
            }
            choice = new(index, candidate, generation, score);
            return true;
        } finally { m_boundLeft = left; m_boundRight = right; }
    }
}
