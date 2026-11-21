using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;
using Puck.World.Protocol;

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
    // The bodies as they stood before this step's first advance, or null for a world no producer perceives through:
    // every producer with a sensed target or a flock reads it, so their sensing is one spatial query of one instant
    // that no earlier body's advance in the step can move. Power-of-two widths make a small-range observer's sampling
    // independent of unrelated wide-range profiles. Only levels used this step are rebuilt; all levels share the one
    // frozen image.
    private WorldPerceptionImage? m_perception;
    private FixedSpatialNeighbor[] m_neighborScratch = [];
    private FixedFlockNeighbor[] m_flockScratch = [];
    // A plain sensed target's exhaustive query results, one per slot, or empty for a world that authors none.
    private FixedSpatialNeighbor[] m_senseScratch = [];

    /// <summary>Gets bounded local steering work from the most recent step.</summary>
    public WorldFlockStatistics FlockStatistics { get; private set; }

    /// <summary>Describes authored perception limits and last-step work without changing simulation state.</summary>
    public string DescribeFlocks() {
        var rows = new List<string>();

        for (var kit = 0; (kit < m_kits.Length); kit++) {
            foreach (var (name, producer) in m_kits[kit].Producers) {
                if (producer.Flock is not { } flock) { continue; }
                rows.Add(item: FormattableString.Invariant(formattable: $"kit {kit}/{name}: {flock.Source.Space}, range {flock.Source.Range}, candidates {flock.Source.CandidateBudget}, neighbors {flock.Source.MaxNeighbors}, period {flock.PeriodEngineTicks} engine ticks, cone {flock.Source.HalfAngleDegrees}, sight {flock.Source.RequiresLineOfSight}, movementDomain {(flock.Source.MovementDomain ?? "none")}, weights separation={flock.Source.Separation} alignment={flock.Source.Alignment} cohesion={flock.Source.Cohesion} goal={flock.Source.Goal} inertia={flock.Source.Inertia}"));
                if (flock.Source.CohesionAffinity is { } cohesion) {
                    rows.Add(item: $"cohesionAffinity={System.Text.Json.JsonSerializer.Serialize(
                        cohesion,
                        WorldJsonContext.Default.ExpressionProgram
                    )}");
                }
                if (flock.Source.AlignmentAffinity is { } alignment) {
                    rows.Add(item: $"alignmentAffinity={System.Text.Json.JsonSerializer.Serialize(
                        alignment,
                        WorldJsonContext.Default.ExpressionProgram
                    )}");
                }
            }
        }
        return $"[world.flock: {((rows.Count == 0)
            ? "none"
            : string.Join(
                separator: "; ",
                values: rows
            ))} | {DescribeFlockWork()}]";
    }
    /// <summary>Describes the frozen-image capacity, per-follower bounds, and measured structural work.</summary>
    public string DescribeFlockWork() {
        var work = FlockStatistics;

        return $"flock grid {((m_perception is null)
            ? 0
            : Capacity)} point(s) at {(m_perception?.Scales ?? 0)} scale(s), scratch {m_neighborScratch.Length} candidate(s)/{m_flockScratch.Length} neighbor(s), last {work.Followers} follower(s), {work.Updates} update(s), {work.Candidates} candidate(s), {work.RetainedNeighbors} retained, {work.BudgetLimitedQueries} budget-limited, {work.LineOfSightTests} sight test(s), {work.MotionChecks} movement check(s)/{work.MotionRefusals} refused, {work.AffinityEvaluations} affinity evaluation(s)/{work.AffinityFailures} failed, {work.AffinityWorkUnits}/{m_flockAffinityCeiling} affinity work units";
    }

    private void CompileFlocks() {
        m_perception = null;
        m_senseScratch = [];
        var candidates = 0;
        var neighbors = 0;

        foreach (var kit in m_kits) {
            foreach (var producer in kit.Producers.Values) {
                if (producer.Flock is not { } flock) {
                    if (producer.Target is { Source: BodyTargetSource.Sensed } sensed) {
                        (m_perception ??= new WorldPerceptionImage(capacity: Capacity)).Declare(cellWidthRaw: FlockGridWidth(range: sensed.Range));
                        if (m_senseScratch.Length != Capacity) { m_senseScratch = new FixedSpatialNeighbor[Capacity]; }
                    }
                    continue;
                }
                (m_perception ??= new WorldPerceptionImage(capacity: Capacity)).Declare(cellWidthRaw: FlockGridWidth(range: FlockQueryRange(producer: producer)));
                candidates = Math.Max(
                    val1: candidates,
                    val2: flock.Source.CandidateBudget
                );
                neighbors = Math.Max(
                    val1: neighbors,
                    val2: flock.Source.MaxNeighbors
                );
            }
        }

        m_neighborScratch = new FixedSpatialNeighbor[candidates];
        m_flockScratch = new FixedFlockNeighbor[neighbors];
    }
    private static FixedQ4816 FlockQueryRange(CompiledBodyProducer producer) => ((producer.Target is { Source: BodyTargetSource.Sensed } target)
        ? FixedQ4816.Max(
            x: producer.Flock!.Range,
            y: target.Range
        )
        : producer.Flock!.Range
    );
    private static long FlockGridWidth(FixedQ4816 range) => checked((long)((ulong)range.Value).NextPowerOfTwo());
    private void FreezeFlockImage() {
        FlockStatistics = default;
        m_perception?.Freeze(population: this);
    }
    private void RefreshFlockPerception(int index, Entry entry, CompiledBodyProducer producer, ulong stepTicks) {
        var flock = producer.Flock!;
        ref var state = ref entry.ProducerState;

        if (
            (state.FlockBinding?.Flock?.Source != flock.Source) ||
            (state.FlockBinding?.Target?.Source != producer.Target?.Source)
        ) {
            state.FlockSeeded = false;
        }
        state.FlockBinding = producer;
        if (state.FlockGeneration != entry.Generation) {
            state.FlockSeeded = false;
            state.FlockSampleOrdinal = 0;
            state.FlockGeneration = entry.Generation;
        }
        FlockStatistics = FlockStatistics with { Followers = (FlockStatistics.Followers + 1) };
        if (
            state.FlockSeeded &&
            (state.FlockRemainingTicks > stepTicks)
        ) {
            state.FlockRemainingTicks -= stepTicks;
            return;
        }
        var first = !state.FlockSeeded;
        var overdue = (first
            ? 0
            : (stepTicks - state.FlockRemainingTicks)
        );

        state.FlockRemainingTicks = ((flock.PeriodEngineTicks == 0)
            ? 0
            : (flock.PeriodEngineTicks - (overdue % flock.PeriodEngineTicks))
        );
        if (
            first &&
            (flock.PeriodEngineTicks != 0)
        ) {
            // Initial perception is immediate. Spread subsequent updates deterministically through the period.
            state.FlockRemainingTicks += ((flock.PeriodEngineTicks * ((ulong)index)) / ((ulong)Math.Max(
                val1: 1,
                val2: Capacity
            )));
        }
        var image = m_perception!;
        var self = image.Position(index: index);
        var forward = image.Orientation(index: index).Rotate(vector: LocalForward);
        var sensedTarget = ((producer.Target is { Source: BodyTargetSource.Sensed })
            ? producer.Target
            : null
        );
        var range = FlockQueryRange(producer: producer);
        var work = image.Query(
            FlockGridWidth(range: range),
            self,
            range,
            index,
            flock.Source.CandidateBudget,
            FixedFlockSteering.IndexPhase(
                generation: ((uint)entry.Generation),
                index: 0U,
                key: ((uint)index),
                ordinal: state.FlockSampleOrdinal++
            ),
            m_neighborScratch.AsSpan(
                0,
                flock.Source.CandidateBudget
            )
        );
        var count = 0;
        var sightTests = 0;
        CompiledWorldFlockAffinities? affinities = null;

        if (
            (flock.Source.CohesionAffinity is not null) ||
            (flock.Source.AlignmentAffinity is not null)
        ) {
            if (!m_flockAffinities.TryGetValue(
                key: (m_kitRows[KitIndex(index: entry.Index)].Name, producer.Program.Name),
                value: out affinities
            )) {
                throw new InvalidOperationException(message: "Flock affinity expressions require an authority binding for the current definition.");
            }
        }
        state.FlockTarget = null;

        for (var candidate = 0; (candidate < work.NeighborsWritten); candidate++) {
            var other = m_neighborScratch[candidate].Index;
            var offset = (m_perception!.Position(index: other) - self);
            var neighbor = ((count < flock.Source.MaxNeighbors) && BodyTargetConeSense.ContainsOffset(
                forward: in forward,
                minimumDot: flock.MinimumDot,
                offset: in offset,
                range: flock.Range
            ));
            var target = ((state.FlockTarget is null) && (sensedTarget is { Source: BodyTargetSource.Sensed sensed } source) &&
                SensedAdmits(other: other, sensed: sensed, source: in source, self: in self, forward: in forward));
            var targetNeedsSight = (target && ((BodyTargetSource.Sensed)sensedTarget!.Value.Source).RequiresLineOfSight);

            if (
                (neighbor && flock.Source.RequiresLineOfSight) ||
                targetNeedsSight
            ) {
                sightTests++;
                if (!HasLineOfSight(
                    from: self,
                    fromOrientation: m_perception!.Orientation(index: index),
                    to: m_perception!.Position(index: other),
                    toOrientation: m_perception!.Orientation(index: other)
                )) {
                    neighbor &= !flock.Source.RequiresLineOfSight;
                    target &= !targetNeedsSight;
                }
            }
            if (target) {
                state.FlockTarget = new WorldFlockObservation(
                other,
                m_entries[other].Generation,
                m_perception!.Position(index: other)
            );
            }
            if (neighbor) {
                m_flockScratch[count++] = new FixedFlockNeighbor(
                    other,
                    offset,
                    m_perception!.Travel(index: other),
                    ReadFlockAffinity(
                        affinities?.Cohesion,
                        index,
                        other
                    ),
                    ReadFlockAffinity(
                        affinities?.Alignment,
                        index,
                        other
                    )
                );
            }
            if (
                (count == flock.Source.MaxNeighbors) &&
                ((sensedTarget is null) || (state.FlockTarget is not null))
            ) { break; }
        }
        var normal = ((flock.Source.Space == WorldFlockSpace.Tangent)
            ? entry.Body!.FixedUp
            : FixedVector3.Zero
        );
        var components = FixedFlockSteering.Evaluate(
            index,
            FixedVector3.Zero,
            FixedVector3.Zero,
            normal,
            m_flockScratch.AsSpan(
                length: count,
                start: 0
            ),
            flock.Weights
        );

        state.FlockDesired = (((components.Separation * flock.Weights.Separation)
            + (components.Alignment * flock.Weights.Alignment)) + (components.Cohesion * flock.Weights.Cohesion));
        state.FlockSeeded = true;
        FlockStatistics = FlockStatistics with {
            Updates = (FlockStatistics.Updates + 1),
            Candidates = (FlockStatistics.Candidates + work.CandidatesExamined),
            RetainedNeighbors = (FlockStatistics.RetainedNeighbors + count),
            BudgetLimitedQueries = (FlockStatistics.BudgetLimitedQueries + (work.BudgetLimited
            ? 1
            : 0)),
            LineOfSightTests = (FlockStatistics.LineOfSightTests + sightTests),
            AffinityWorkUnits = (FlockStatistics.AffinityWorkUnits + (count * (affinities?.WorkUnitsPerNeighbor.Units ?? 0L))),
        };
    }
    // Whether a frozen body answers a sensed target's seat scope and cone from an observer's frozen pose. Its sight
    // line is the caller's to test, so a flock pays one test for a candidate that is both a neighbour and a target.
    private bool SensedAdmits(int other, BodyTargetSource.Sensed sensed, in FixedBodyTargetSource source, in FixedVector3 self, in FixedVector3 forward) =>
        ((sensed.Scope != BodyTargetScope.Seats) || (m_entries[other].Kind == PopulationKind.LocalSeat)) &&
        BodyTargetConeSense.Contains(self, forward, m_perception!.Position(index: other), source.Range, source.MinimumDot, out _);
    // The nearest body a plain (unflocked) sensed target admits: one exhaustive query of the frozen image at the
    // target's range, nearest first, so the first result that passes scope, cone and sight is the answer and only the
    // bodies nearer than it pay a sight test. Perception reads the frozen instant; steering reads the chosen body's
    // live position, as it reads the held target's.
    private BodySensorTarget SenseNearest(int selfIndex, BodyTargetSource.Sensed sensed, in FixedBodyTargetSource source, in FixedVector3 self) {
        var image = m_perception!;

        // A body admitted after this step's freeze has no frozen pose to sense from until the next step.
        if (!image.Present(index: selfIndex)) { return BodySensorTarget.None; }
        var origin = image.Position(index: selfIndex);
        var orientation = image.Orientation(index: selfIndex);
        var forward = orientation.Rotate(vector: LocalForward);
        var work = image.Query(FlockGridWidth(range: source.Range), origin, source.Range, selfIndex, Capacity, 0UL, m_senseScratch);

        for (var found = 0; (found < work.NeighborsWritten); found++) {
            var other = m_senseScratch[found].Index;

            if (
                !SensedAdmits(other: other, sensed: sensed, source: in source, self: in origin, forward: in forward) ||
                (sensed.RequiresLineOfSight && !HasLineOfSight(from: origin, fromOrientation: orientation, to: image.Position(index: other), toOrientation: image.Orientation(index: other))) ||
                !m_entries[other].Active || (m_entries[other].Body is not { } body)
            ) { continue; }
            var position = body.FixedPosition;

            return new BodySensorTarget(Index: other, Position: position, DistanceSquared: (position - self).LengthSquared);
        }
        return BodySensorTarget.None;
    }
    private BodySensorTarget ReadFlockTarget(Entry entry, in FixedVector3 self) {
        if (
            (entry.ProducerState.FlockTarget is not { } observed) ||
            !m_entries[observed.Index].Active ||
            (m_entries[observed.Index].Generation != observed.Generation)
        ) { return BodySensorTarget.None; }
        return new BodySensorTarget(
            observed.Index,
            observed.Position,
            (observed.Position - self).LengthSquared
        );
    }
    private void RecordFlockMotion(WorldBody body) {
        FlockStatistics = FlockStatistics with {
            MotionChecks = (FlockStatistics.MotionChecks + (body.FlockMotionChecked
            ? 1
            : 0)),
            MotionRefusals = (FlockStatistics.MotionRefusals + (body.FlockMotionRefused
            ? 1
            : 0)),
        };
    }
    private FixedVector3 BlendFlockPreference(int index, Entry entry, FixedWorldFlockProfile flock, BodySensorTarget goal) {
        var direction = ((goal.Exists && (goal.DistanceSquared > (flock.ArrivalDistance * flock.ArrivalDistance)))
            ? (goal.Position - m_perception!.Position(index: index))
            : FixedVector3.Zero
        );
        var heading = ((m_perception!.Travel(index: index) == FixedVector3.Zero)
            ? m_perception!.Orientation(index: index).Rotate(vector: LocalForward)
            : m_perception!.Travel(index: index)
        );
        var normal = ((flock.Source.Space == WorldFlockSpace.Tangent)
            ? entry.Body!.FixedUp
            : FixedVector3.Zero
        );

        return FixedFlockSteering.BlendPreference(
            entry.ProducerState.FlockDesired,
            heading,
            direction,
            normal,
            flock.Weights.Goal,
            flock.Weights.Inertia
        );
    }

    internal void AppendFlockStateHash(ref Fnv1aHash hash) {
        foreach (var entry in m_entries) {
            hash.Add(value: entry.Generation);
            if (
                !entry.Active ||
                (entry.Body is not { } body)
            ) { continue; }
            ref var state = ref entry.ProducerState;

            hash.Add(value: ((byte)(state.FlockSeeded
                ? 1
                : 0)));
            hash.Add(value: state.FlockGeneration);
            hash.Add(value: state.FlockRemainingTicks);
            hash.Add(value: state.FlockSampleOrdinal);
            hash.Add(value: state.FlockDesired.X.Value);
            hash.Add(value: state.FlockDesired.Y.Value);
            hash.Add(value: state.FlockDesired.Z.Value);
            hash.Add(value: ((byte)((state.FlockTarget is null)
                ? 0
                : 1)));
            if (state.FlockTarget is { } observed) {
                hash.Add(value: observed.Index);
                hash.Add(value: observed.Generation);
                hash.Add(value: observed.Position.X.Value);
                hash.Add(value: observed.Position.Y.Value);
                hash.Add(value: observed.Position.Z.Value);
            }
            ref var autonomy = ref entry.AutonomyState;

            if (
                (autonomy.MotionPeriodTicks != 0UL) ||
                (autonomy.MotionElapsedTicks != 0UL) ||
                (autonomy.MotionRemainingTicks != 0UL) ||
                (autonomy.SteeringPeriodTicks != 0UL) ||
                (autonomy.SteeringElapsedTicks != 0UL) ||
                (autonomy.SteeringRemainingTicks != 0UL) ||
                autonomy.SteeringSeeded
            ) {
                hash.Add(value: 0x4155544FUL); // "AUTO" — absent full-rate cadence contributes no legacy-hash bytes.
                hash.Add(value: autonomy.MotionPeriodTicks);
                hash.Add(value: autonomy.MotionElapsedTicks);
                hash.Add(value: autonomy.MotionRemainingTicks);
                hash.Add(value: autonomy.SteeringPeriodTicks);
                hash.Add(value: autonomy.SteeringElapsedTicks);
                hash.Add(value: autonomy.SteeringRemainingTicks);
                hash.Add(value: ((byte)(autonomy.SteeringSeeded
                    ? 1
                    : 0)));
                for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                    hash.Add(value: autonomy.SteeringIntent[ordinal].Value);
                }
            }
            hash.Add(value: body.FixedPreviousPosition.X.Value);
            hash.Add(value: body.FixedPreviousPosition.Y.Value);
            hash.Add(value: body.FixedPreviousPosition.Z.Value);
        }
    }
}
