using System.Numerics;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>Last-step deterministic flock work; these counters do not affect decisions.</summary>
public readonly record struct WorldFlockStatistics(int Followers, int Updates, int Candidates, int RetainedNeighbors,
    int BudgetLimitedQueries, int LineOfSightTests, int MotionChecks = 0, int MotionRefusals = 0,
    int AffinityEvaluations = 0, int AffinityFailures = 0, long AffinityWorkUnits = 0);

/// <summary>A body observed during a bounded flock sample. Position remains the last observed position.</summary>
/// <param name="Index">Observed population slot.</param>
/// <param name="Generation">Observed occupant generation, preventing reuse from transferring the observation.</param>
/// <param name="Position">Position in the frozen image at observation time.</param>
public readonly record struct WorldFlockObservation(int Index, int Generation, FixedVector3 Position);

public sealed partial class WorldPopulation {
    private sealed class FlockGrid(int capacity, long widthRaw) {
        public readonly FixedSpatialNeighborhood Neighborhood = new(capacity, new FixedQ4816(widthRaw));
        public bool Frozen;
    }
    // Power-of-two widths make a small-range observer's sampling independent of unrelated wide-range profiles.
    // Only levels used this step are rebuilt; all levels share one frozen point image.
    private readonly Dictionary<long, FlockGrid> m_flockGrids = [];
    private int m_flockPointCount;
    private FixedSpatialPoint[] m_flockPoints = [];
    private FixedVector3[] m_flockPositions = [];
    private FixedVector3[] m_flockTravel = [];
    private FixedQuaternion[] m_flockOrientations = [];
    private FixedSpatialNeighbor[] m_neighborScratch = [];
    private FixedFlockNeighbor[] m_flockScratch = [];

    /// <summary>Gets bounded local steering work from the most recent step.</summary>
    public WorldFlockStatistics FlockStatistics { get; private set; }

    /// <summary>Describes authored perception limits and last-step work without changing simulation state.</summary>
    public string DescribeFlocks() {
        var rows = new List<string>();
        for (var kit = 0; kit < m_kits.Length; kit++) {
            foreach (var (name, producer) in m_kits[kit].Producers) {
                if (producer.Flock is not { } flock) { continue; }
                rows.Add(FormattableString.Invariant($"kit {kit}/{name}: {flock.Source.Space}, range {flock.Source.Range}, candidates {flock.Source.CandidateBudget}, neighbors {flock.Source.MaxNeighbors}, period {flock.PeriodEngineTicks} engine ticks, cone {flock.Source.HalfAngleDegrees}, sight {flock.Source.RequiresLineOfSight}, movementDomain {flock.Source.MovementDomain ?? "none"}, weights separation={flock.Source.Separation} alignment={flock.Source.Alignment} cohesion={flock.Source.Cohesion} goal={flock.Source.Goal} inertia={flock.Source.Inertia}"));
                if (flock.Source.CohesionAffinity is { } cohesion) {
                    rows.Add($"cohesionAffinity={System.Text.Json.JsonSerializer.Serialize(cohesion, WorldJsonContext.Default.WorldValueExpression)}");
                }
                if (flock.Source.AlignmentAffinity is { } alignment) {
                    rows.Add($"alignmentAffinity={System.Text.Json.JsonSerializer.Serialize(alignment, WorldJsonContext.Default.WorldValueExpression)}");
                }
            }
        }
        return $"[world.flock: {(rows.Count == 0 ? "none" : string.Join("; ", rows))} | {DescribeFlockWork()}]";
    }

    /// <summary>Describes the frozen-image capacity, per-follower bounds, and measured structural work.</summary>
    public string DescribeFlockWork() {
        var work = FlockStatistics;
        return $"flock grid {(m_flockGrids.Count == 0 ? 0 : Capacity)} point(s) at {m_flockGrids.Count} scale(s), scratch {m_neighborScratch.Length} candidate(s)/{m_flockScratch.Length} neighbor(s), last {work.Followers} follower(s), {work.Updates} update(s), {work.Candidates} candidate(s), {work.RetainedNeighbors} retained, {work.BudgetLimitedQueries} budget-limited, {work.LineOfSightTests} sight test(s), {work.MotionChecks} movement check(s)/{work.MotionRefusals} refused, {work.AffinityEvaluations} affinity evaluation(s)/{work.AffinityFailures} failed, {work.AffinityWorkUnits}/{m_flockAffinityCeiling} affinity work units";
    }

    private void CompileFlocks() {
        m_flockGrids.Clear();
        var candidates = 0;
        var neighbors = 0;
        foreach (var kit in m_kits) {
            foreach (var producer in kit.Producers.Values) {
                if (producer.Flock is not { } flock) { continue; }
                var width = FlockGridWidth(FlockQueryRange(producer));
                if (!m_flockGrids.ContainsKey(width)) { m_flockGrids.Add(width, new FlockGrid(Capacity, width)); }
                candidates = Math.Max(candidates, flock.Source.CandidateBudget);
                neighbors = Math.Max(neighbors, flock.Source.MaxNeighbors);
            }
        }
        m_flockPoints = m_flockGrids.Count != 0 ? new FixedSpatialPoint[Capacity] : [];
        m_flockPositions = m_flockGrids.Count != 0 ? new FixedVector3[Capacity] : [];
        m_flockTravel = m_flockGrids.Count != 0 ? new FixedVector3[Capacity] : [];
        m_flockOrientations = m_flockGrids.Count != 0 ? new FixedQuaternion[Capacity] : [];
        m_neighborScratch = new FixedSpatialNeighbor[candidates];
        m_flockScratch = new FixedFlockNeighbor[neighbors];
    }

    private static FixedQ4816 FlockQueryRange(CompiledBodyProducer producer) => producer.Target is { Source: BodyTargetSource.Sensed } target
        ? FixedQ4816.Max(producer.Flock!.Range, target.Range) : producer.Flock!.Range;
    private static long FlockGridWidth(FixedQ4816 range) => checked((long)BitOperations.RoundUpToPowerOf2((ulong)range.Value));

    private void FreezeFlockImage() {
        FlockStatistics = default;
        if (m_flockGrids.Count == 0) { return; }
        var count = 0;
        for (var index = 0; index < Capacity; index++) {
            if (m_entries[index] is not { Active: true, Body: { } body }) { continue; }
            m_flockPositions[index] = body.FixedPosition;
            // Displacement carries actual movement (including free flight and contact corrections). Its common
            // previous-step time scale cancels when the flock kernel takes the weighted mean direction.
            m_flockTravel[index] = body.FixedPosition - body.FixedPreviousPosition;
            m_flockOrientations[index] = body.FixedOrientation;
            m_flockPoints[count++] = new FixedSpatialPoint(index, body.FixedPosition);
        }
        m_flockPointCount = count;
        foreach (var grid in m_flockGrids.Values) { grid.Frozen = false; }
    }

    private void RefreshFlockPerception(int index, Entry entry, CompiledBodyProducer producer, ulong stepTicks) {
        var flock = producer.Flock!;
        ref var state = ref entry.ProducerState;
        if (state.FlockBinding?.Flock?.Source != flock.Source || state.FlockBinding?.Target?.Source != producer.Target?.Source) {
            state.FlockSeeded = false;
        }
        state.FlockBinding = producer;
        if (state.FlockGeneration != entry.Generation) {
            state.FlockSeeded = false;
            state.FlockSampleOrdinal = 0;
            state.FlockGeneration = entry.Generation;
        }
        FlockStatistics = FlockStatistics with { Followers = FlockStatistics.Followers + 1 };
        if (state.FlockSeeded && state.FlockRemainingTicks > stepTicks) {
            state.FlockRemainingTicks -= stepTicks;
            return;
        }
        var first = !state.FlockSeeded;
        var overdue = first ? 0 : stepTicks - state.FlockRemainingTicks;
        state.FlockRemainingTicks = flock.PeriodEngineTicks == 0 ? 0 :
            flock.PeriodEngineTicks - overdue % flock.PeriodEngineTicks;
        if (first && flock.PeriodEngineTicks != 0) {
            // Initial perception is immediate. Spread subsequent updates deterministically through the period.
            state.FlockRemainingTicks += flock.PeriodEngineTicks * (ulong)index / (ulong)Math.Max(1, Capacity);
        }
        var self = m_flockPositions[index];
        var forward = m_flockOrientations[index].Rotate(LocalForward);
        var sensedTarget = producer.Target is { Source: BodyTargetSource.Sensed } ? producer.Target : null;
        var range = FlockQueryRange(producer);
        var grid = m_flockGrids[FlockGridWidth(range)];
        if (!grid.Frozen) {
            grid.Neighborhood.Rebuild(m_flockPoints.AsSpan(0, m_flockPointCount));
            grid.Frozen = true;
        }
        var work = grid.Neighborhood.Query(self, range, index, flock.Source.CandidateBudget,
            unchecked(state.FlockSampleOrdinal++ + (ulong)index * 0x9E3779B9UL + ((ulong)(uint)entry.Generation << 32)),
            m_neighborScratch.AsSpan(0, flock.Source.CandidateBudget));
        var count = 0;
        var sightTests = 0;
        CompiledWorldFlockAffinities? affinities = null;
        if (flock.Source.CohesionAffinity is not null || flock.Source.AlignmentAffinity is not null) {
            if (!m_flockAffinities.TryGetValue((m_kitRows[entry.KitIndex].Name, producer.Program.Name), out affinities)) {
                throw new InvalidOperationException("Flock affinity expressions require an authority binding for the current definition.");
            }
        }
        state.FlockTarget = null;
        var flockRangeSquaredRaw = (UInt128)(ulong)flock.Range.Value * (ulong)flock.Range.Value;
        for (var candidate = 0; candidate < work.NeighborsWritten; candidate++) {
            var other = m_neighborScratch[candidate].Index;
            var offset = m_flockPositions[other] - self;
            var neighbor = count < flock.Source.MaxNeighbors && m_neighborScratch[candidate].SquaredDistanceRaw <= flockRangeSquaredRaw &&
                (offset == FixedVector3.Zero || FixedVector3.Dot(forward, offset.Normalize()) >= flock.MinimumDot);
            var target = state.FlockTarget is null && sensedTarget is { Source: BodyTargetSource.Sensed sensed } source &&
                (sensed.Scope != BodyTargetScope.Seats || m_entries[other].Kind == PopulationKind.LocalSeat) &&
                BodyTargetConeSense.Contains(self, forward, m_flockPositions[other], source.Range, source.MinimumDot, out _);
            var targetNeedsSight = target && ((BodyTargetSource.Sensed)sensedTarget!.Value.Source).RequiresLineOfSight;
            if ((neighbor && flock.Source.RequiresLineOfSight) || targetNeedsSight) {
                sightTests++;
                if (!HasLineOfSight(self, m_flockOrientations[index], m_flockPositions[other], m_flockOrientations[other])) {
                    neighbor &= !flock.Source.RequiresLineOfSight;
                    target &= !targetNeedsSight;
                }
            }
            if (target) { state.FlockTarget = new WorldFlockObservation(other, m_entries[other].Generation, m_flockPositions[other]); }
            if (neighbor) {
                m_flockScratch[count++] = new FixedFlockNeighbor(other, offset, m_flockTravel[other],
                    ReadFlockAffinity(affinities?.Cohesion, index, other), ReadFlockAffinity(affinities?.Alignment, index, other));
            }
            if (count == flock.Source.MaxNeighbors && (sensedTarget is null || state.FlockTarget is not null)) { break; }
        }
        var normal = flock.Source.Space == WorldFlockSpace.Tangent ? entry.Body!.FixedUp : FixedVector3.Zero;
        var components = FixedFlockSteering.Evaluate(index, FixedVector3.Zero, FixedVector3.Zero, normal,
            m_flockScratch.AsSpan(0, count), flock.Weights);
        state.FlockDesired = components.Separation * flock.Weights.Separation
            + components.Alignment * flock.Weights.Alignment + components.Cohesion * flock.Weights.Cohesion;
        state.FlockSeeded = true;
        FlockStatistics = FlockStatistics with {
            Updates = FlockStatistics.Updates + 1,
            Candidates = FlockStatistics.Candidates + work.CandidatesExamined,
            RetainedNeighbors = FlockStatistics.RetainedNeighbors + count,
            BudgetLimitedQueries = FlockStatistics.BudgetLimitedQueries + (work.BudgetLimited ? 1 : 0),
            LineOfSightTests = FlockStatistics.LineOfSightTests + sightTests,
            AffinityWorkUnits = FlockStatistics.AffinityWorkUnits + count * (affinities?.WorkUnitsPerNeighbor ?? 0),
        };
    }

    private BodySensorTarget ReadFlockTarget(Entry entry, in FixedVector3 self) {
        if (entry.ProducerState.FlockTarget is not { } observed || !m_entries[observed.Index].Active ||
            m_entries[observed.Index].Generation != observed.Generation) { return BodySensorTarget.None; }
        return new BodySensorTarget(observed.Index, observed.Position, (observed.Position - self).LengthSquared);
    }

    private void RecordFlockMotion(WorldBody body) {
        FlockStatistics = FlockStatistics with {
            MotionChecks = FlockStatistics.MotionChecks + (body.FlockMotionChecked ? 1 : 0),
            MotionRefusals = FlockStatistics.MotionRefusals + (body.FlockMotionRefused ? 1 : 0),
        };
    }

    private FixedVector3 BlendFlockPreference(int index, Entry entry, FixedWorldFlockProfile flock, BodySensorTarget goal) {
        var direction = goal.Exists && goal.DistanceSquared > flock.ArrivalDistance * flock.ArrivalDistance
            ? goal.Position - m_flockPositions[index] : FixedVector3.Zero;
        var heading = m_flockTravel[index] == FixedVector3.Zero
            ? m_flockOrientations[index].Rotate(LocalForward) : m_flockTravel[index];
        var normal = flock.Source.Space == WorldFlockSpace.Tangent ? entry.Body!.FixedUp : FixedVector3.Zero;
        return FixedFlockSteering.BlendPreference(entry.ProducerState.FlockDesired, heading, direction, normal,
            flock.Weights.Goal, flock.Weights.Inertia);
    }

    internal void AppendFlockStateHash(ref Fnv1aHash hash) {
        foreach (var entry in m_entries) {
            hash.Add(entry.Generation);
            if (!entry.Active || entry.Body is not { } body) { continue; }
            ref var state = ref entry.ProducerState;
            hash.Add((byte)(state.FlockSeeded ? 1 : 0));
            hash.Add(state.FlockGeneration);
            hash.Add(state.FlockRemainingTicks);
            hash.Add(state.FlockSampleOrdinal);
            hash.Add(state.FlockDesired.X.Value);
            hash.Add(state.FlockDesired.Y.Value);
            hash.Add(state.FlockDesired.Z.Value);
            hash.Add((byte)(state.FlockTarget is null ? 0 : 1));
            if (state.FlockTarget is { } observed) {
                hash.Add(observed.Index);
                hash.Add(observed.Generation);
                hash.Add(observed.Position.X.Value);
                hash.Add(observed.Position.Y.Value);
                hash.Add(observed.Position.Z.Value);
            }
            hash.Add(body.FixedPreviousPosition.X.Value);
            hash.Add(body.FixedPreviousPosition.Y.Value);
            hash.Add(body.FixedPreviousPosition.Z.Value);
        }
    }
}
