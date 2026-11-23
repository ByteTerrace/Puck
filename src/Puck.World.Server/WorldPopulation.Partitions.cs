using Puck.Hosting.Jobs;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World.Server;

// THE KIT ADVANCE SPLIT. A body whose step is local (WorldBody.StepIsLocal and ProducerIsLocal: it writes only its own
// motion, sections and producer-side state, emits nothing and needs no kit transaction) advances in a kit partition:
// the population's slots in fixed runs of KitPartitionSlots, each run's local bodies in ascending slot order. Every
// other body advances afterwards, serially, in index order, inside the kit transaction (AdvanceSerialBody).
//
// A tick runs four phases for the local bodies: a serial plan (sleep, cadence, goal fields, the perception grids), a
// steer fork (each due producer), a serial input pass (each moving body's admission and merged intent, which go
// through the one input runtime), and a move fork (each moving body's motion program, contacts and settlement). The
// join then applies each partition's log, outputs and counts in ascending partition order.
//
// The invariant: the arena's committed rows, the journal and every state hash after a tick are the same at any worker
// count. What enforces it:
// - The frozen image. A producer reads another body only through m_perception, frozen before the plan, so a body's
//   view of the others does not depend on which bodies stepped before it (ReadProducerSensors).
// - Partition-owned writes. A partition writes its own bodies, their entries, its ProducerWork, StepOutputs and
//   WorldBodyStepLog, and the slot notes of its own slots (the notes partition by the same KitPartitionSlots). A body
//   reaches its population only through the bound step log; BodyWoke, DisturbBody and NoteBodyFactChanged throw while
//   m_partitioned holds, as do the input runtime's entry points (ThrowIfPartitioned), so a step that is not local
//   fails loudly at one worker as at many.
// - A deterministic join order. Logs, outputs and counts merge in ascending partition order; the forks' partitions are
//   a function of the document and state, never of the worker count, and one worker runs the same partitions inline.
// - The navigation hand-off. The plan resolves every goal field a due steering body will read (PrepareGoalField), bakes
//   the domain a route search will read, and builds every perception neighbourhood, serially. The navigation runtime is
//   read-only while a fork runs (NavigationRuntime.EnterReadOnly): a goal field read, a cross-domain route, a network
//   join or a bake adoption throws there, and a domain's own path search rents a workspace of its own. A sight test
//   reads the target field, whose queries hold no mutable state.
// Where it stops: a body whose step can write state, reach another body or emit outputs stays serial; a per-partition
// kit transaction for those is the open item the engine job system plan names.
public sealed partial class WorldPopulation {
    /// <summary>The slots one kit partition holds: the body hash segment, and the run of slots one slot-notes
    /// partition holds, so a partition's section writes append only to its own notes.</summary>
    public const int KitPartitionSlots = HashSegmentSlots;

    // The producer-side scratch and statistics the serial steps use; each partition owns its own, merged in at the join.
    private readonly ProducerWork m_serialWork = new(partitioned: false);
    // The bodies this tick's plan left to the serial loop, in index order.
    private readonly List<int> m_serialKitBodies = [];
    private KitPartition?[] m_kitPartitions = [];
    // The partitions holding at least one local body this tick, in ascending order: the forks' partition list.
    private readonly List<KitPartition> m_activeKitPartitions = [];
    private KitPartitionRun? m_kitRun;
    // Whether a kit partition is running: the shared population state a partition must not write refuses while it holds.
    private bool m_partitioned;

    /// <summary>Throws while a kit partition runs: the door every shared mutator a body step could reach passes, so a
    /// step that is not local fails at once, at every worker count.</summary>
    /// <exception cref="InvalidOperationException">A kit partition is running.</exception>
    internal void ThrowIfPartitioned() {
        if (m_partitioned) {
            throw new InvalidOperationException(message: "A kit partition reached shared population state; only a serial step may write it.");
        }
    }

    // The scratch and statistics a producer run writes: a flock's candidates and neighbours, a sensed target's query
    // results, and the flock and autonomy counts. One serves the serial steps and one each kit partition, so no two
    // threads share one.
    private sealed class ProducerWork(bool partitioned) {
        public FixedSpatialNeighbor[] Neighbors = [];
        public FixedFlockNeighbor[] FlockNeighbors = [];
        public FixedSpatialNeighbor[] Sensed = [];
        public WorldFlockStatistics Flock;
        public WorldAutonomyStatistics Autonomy;

        // Whether this work belongs to a kit partition, whose reads may not build or derive a shared cache.
        public bool Partitioned { get; } = partitioned;

        public void Size(WorldPopulation population) {
            if (Neighbors.Length != population.m_flockCandidates) { Neighbors = new FixedSpatialNeighbor[population.m_flockCandidates]; }
            if (FlockNeighbors.Length != population.m_flockNeighbors) { FlockNeighbors = new FixedFlockNeighbor[population.m_flockNeighbors]; }
            if (Sensed.Length != population.m_senseCandidates) { Sensed = new FixedSpatialNeighbor[population.m_senseCandidates]; }
        }
        public void ResetStatistics() {
            Flock = default;
            Autonomy = default;
        }
        // Adds another work's counts to this one's and clears them.
        public void AbsorbStatistics(ProducerWork other) {
            var flock = other.Flock;

            Flock = new WorldFlockStatistics(
                Followers: (Flock.Followers + flock.Followers),
                Updates: (Flock.Updates + flock.Updates),
                Candidates: (Flock.Candidates + flock.Candidates),
                RetainedNeighbors: (Flock.RetainedNeighbors + flock.RetainedNeighbors),
                BudgetLimitedQueries: (Flock.BudgetLimitedQueries + flock.BudgetLimitedQueries),
                LineOfSightTests: (Flock.LineOfSightTests + flock.LineOfSightTests),
                MotionChecks: (Flock.MotionChecks + flock.MotionChecks),
                MotionRefusals: (Flock.MotionRefusals + flock.MotionRefusals),
                AffinityEvaluations: (Flock.AffinityEvaluations + flock.AffinityEvaluations),
                AffinityFailures: (Flock.AffinityFailures + flock.AffinityFailures),
                AffinityWorkUnits: (Flock.AffinityWorkUnits + flock.AffinityWorkUnits)
            );
            Autonomy = new WorldAutonomyStatistics(
                MotionUpdates: (Autonomy.MotionUpdates + other.Autonomy.MotionUpdates),
                MotionDeferred: (Autonomy.MotionDeferred + other.Autonomy.MotionDeferred),
                SteeringUpdates: (Autonomy.SteeringUpdates + other.Autonomy.SteeringUpdates)
            );
            other.ResetStatistics();
        }
    }

    // One run of KitPartitionSlots slots and the local bodies the plan found in it this tick, with everything a body's
    // step carries from one phase to the next. A body's place is its ordinal among the partition's bodies.
    private sealed class KitPartition {
        public readonly int[] Bodies = new int[KitPartitionSlots];
        public readonly AutonomyStep[] Steps = new AutonomyStep[KitPartitionSlots];
        // Whether the body moves this tick: its cadence's answer, cleared when its step does not begin.
        public readonly bool[] Moves = new bool[KitPartitionSlots];
        public readonly BodyEffectTargets[] Targets = new BodyEffectTargets[KitPartitionSlots];
        public readonly WorldBody.StepStart[] Starts = new WorldBody.StepStart[KitPartitionSlots];
        public readonly WorldBody.StepScratch[] Scratches = new WorldBody.StepScratch[KitPartitionSlots];
        public readonly ProducerWork Work = new(partitioned: true);
        public readonly WorldBody.StepOutputs Outputs = new();
        public readonly WorldBodyStepLog Log = new();
        public int Count;
        public int Steered;
        public int Moved;

        public WorldBody.StepScratch Scratch(int ordinal) => (Scratches[ordinal] ??= new WorldBody.StepScratch());
    }

    // The partitions' work for one fork: which phase runs, and what every partition of it reads.
    private sealed class KitPartitionRun(WorldPopulation owner) : IJobPartitions {
        public bool Moving;
        public ulong Tick;
        public ulong ContactFieldVersion;

        public void Run(int partition) {
            var kit = owner.m_activeKitPartitions[partition];

            if (Moving) { owner.MoveKitPartition(contactFieldVersion: ContactFieldVersion, partition: kit, tick: Tick); } else { owner.SteerKitPartition(partition: kit); }
        }
    }

    // Whether a body's producer reads only what a partition may read: the frozen image, its own entry, a curve, a sight
    // test, a shared domain's resident goal field and a domain's own path search. A flock affinity (an authority
    // expression over the arena) stays serial.
    private bool ProducerIsLocal(Entry entry, WorldBody body, int index) =>
        (body.Source.ProducerName is not { } name) ||
        (ResolveProducer(entry: entry, name: name, table: m_kits[ResolveKitIndex(index: index)].Producers) is not { } producer) ||
        (producer.Flock is not { } flock) ||
        ((flock.Source.CohesionAffinity is null) && (flock.Source.AlignmentAffinity is null));

    private KitPartition KitPartitionOf(int index) {
        var ordinal = (index / KitPartitionSlots);

        if (m_kitPartitions.Length != Capacity.CeilingDivide(divisor: KitPartitionSlots)) {
            m_kitPartitions = new KitPartition?[Capacity.CeilingDivide(divisor: KitPartitionSlots)];
        }
        return (m_kitPartitions[ordinal] ??= new KitPartition());
    }

    // The serial plan: walks the tick's bodies in index order, leaves every body whose step is not local to the serial
    // loop, and for each local one settles its sleep, field inputs and cadence (the same rules AdvanceSerialBody
    // applies), resolves the goal field its producer will read and places it in its partition.
    private void PlanKitStep(ReadOnlySpan<int> stepBodies, ulong stepTicks, ulong stepStartEngineTick, ulong contactFieldVersion) {
        m_serialKitBodies.Clear();
        foreach (var partition in m_activeKitPartitions) {
            partition.Count = 0;
            partition.Steered = 0;
            partition.Moved = 0;
        }
        m_activeKitPartitions.Clear();
        var perceives = false;

        foreach (var index in stepBodies) {
            var entry = m_entries[index];

            if (
                !entry.Active ||
                (entry.Kind == PopulationKind.LocalSeat) ||
                (entry.Body is not { } player)
            ) {
                continue;
            }
            SimulatedBodyVisits++;
            if (!player.StepIsLocal || !ProducerIsLocal(entry: entry, body: player, index: index)) {
                m_serialKitBodies.Add(item: index);
                continue;
            }
            if (
                !TryPlanAutonomy(contactFieldVersion: contactFieldVersion, entry: entry, index: index, player: player, step: out var step, stepStartEngineTick: stepStartEngineTick, stepTicks: stepTicks) ||
                (!step.SteeringDue && !step.Moves)
            ) {
                continue;
            }
            if (step.SteeringDue && (entry.ProducerLookup is { } producer) && (player.Source.ProducerName is not null)) {
                perceives |= ((producer.Flock is not null) || (producer.Target?.Source is BodyTargetSource.Sensed));
                if (producer.Flock is { MovementDomainIndex: >= 0 } flock) { m_navigation[flock.MovementDomainIndex].EnsureBaked(); }
                // A route of its own reads one domain: bake it here, where a bake may be adopted.
                if ((producer.Target is { Source: BodyTargetSource.Navigated } routed) && (((uint)routed.NavigationDomainIndex) < ((uint)m_navigation.Count))) {
                    m_navigation[routed.NavigationDomainIndex].EnsureBaked();
                }
                PrepareGoalField(entry: entry, producer: producer, self: player.FixedPosition);
            }

            var partition = KitPartitionOf(index: index);

            if (partition.Count == 0) { m_activeKitPartitions.Add(item: partition); }
            var at = partition.Count++;

            partition.Bodies[at] = index;
            partition.Steps[at] = step;
            partition.Moves[at] = step.Moves;
        }
        if (perceives) { m_perception?.BuildDeclared(); }
    }

    // Runs the planned partitions: the steer fork, the serial input pass, the move fork, then the join.
    private void RunKitPartitions(JobPool? jobs, ulong tick, ulong contactFieldVersion) {
        if (m_activeKitPartitions.Count == 0) {
            return;
        }
        var run = (m_kitRun ??= new KitPartitionRun(owner: this));

        run.Tick = tick;
        run.ContactFieldVersion = contactFieldVersion;
        foreach (var partition in m_activeKitPartitions) { partition.Work.Size(population: this); }
        run.Moving = false;
        ForkKitPartitions(jobs: jobs, run: run);

        // The input pass: admission and the merged intent go through the one input runtime, in index order.
        foreach (var partition in m_activeKitPartitions) {
            for (var at = 0; (at < partition.Count); at++) {
                if (partition.Moves[at]) { BeginLocalStep(at: at, partition: partition, tick: tick); }
            }
        }
        run.Moving = true;
        ForkKitPartitions(jobs: jobs, run: run);

        var steered = 0;
        var moved = 0;

        foreach (var partition in m_activeKitPartitions) {
            foreach (var (index, woke) in partition.Log.Entries) {
                if (woke) { BodyWoke(index: index); } else { NoteBodyFactChanged(index: index); }
            }
            partition.Log.Clear();
            m_outputs.Absorb(other: partition.Outputs);
            m_serialWork.AbsorbStatistics(other: partition.Work);
            steered += partition.Steered;
            moved += partition.Moved;
        }
        WorldBodyWork.Process.Add(amount: moved, kind: WorldBodyWork.Advances);
        WorldBodyWork.Process.Add(amount: moved, kind: WorldBodyWork.LocalAdvances);
        WorldBodyWork.Process.Add(amount: steered, kind: WorldBodyWork.LocalSteers);
        WorldBodyWork.Process.Add(amount: m_activeKitPartitions.Count, kind: WorldBodyWork.KitPartitions);
    }
    // One fork over the active partitions; with no pool, or a serial one, the same partitions run here in order.
    private void ForkKitPartitions(JobPool? jobs, KitPartitionRun run) {
        m_partitioned = true;
        using var navigation = m_navigation.EnterReadOnly();
        try {
            if (jobs is null) {
                for (var partition = 0; (partition < m_activeKitPartitions.Count); partition++) { run.Run(partition: partition); }
            } else {
                jobs.Fork(body: run, jobClass: JobClass.Tick, partitions: m_activeKitPartitions.Count);
            }
        } finally {
            m_partitioned = false;
        }
    }
    private void SteerKitPartition(KitPartition partition) {
        for (var at = 0; (at < partition.Count); at++) {
            if (!partition.Steps[at].SteeringDue) { continue; }
            var index = partition.Bodies[at];
            var entry = m_entries[index];
            var body = entry.Body!;

            StageProducer(body: body, entry: entry, index: index, stepTicks: partition.Steps[at].SteeringTicks, work: partition.Work);
            SeedSteering(entry: entry, motionPeriod: partition.Steps[at].MotionPeriod, player: body, steeringPeriod: partition.Steps[at].SteeringPeriod, work: partition.Work);
            partition.Steered++;
        }
    }
    private void BeginLocalStep(KitPartition partition, int at, ulong tick) {
        var index = partition.Bodies[at];
        var entry = m_entries[index];
        var body = entry.Body!;
        ref var autonomy = ref entry.AutonomyState;

        if (!partition.Steps[at].SteeringDue && autonomy.SteeringSeeded) {
            body.StageProducerIntent(intent: in autonomy.SteeringIntent);
        }
        partition.Targets[at] = ReadEffectTargets(entry: entry, selfIndex: index, self: body.FixedPosition);
        partition.Moves[at] = body.TryBeginStep(
            engageProbeOrdinal: null,
            entityIndex: index,
            outputs: partition.Outputs,
            scratch: partition.Scratch(ordinal: at),
            start: out partition.Starts[at],
            stepTicks: partition.Steps[at].MotionTicks,
            tick: tick,
            transactional: false
        );
    }
    private void MoveKitPartition(KitPartition partition, ulong tick, ulong contactFieldVersion) {
        for (var at = 0; (at < partition.Count); at++) {
            if (!partition.Moves[at]) { continue; }
            var index = partition.Bodies[at];
            var body = m_entries[index].Body!;

            body.BindStepLog(log: partition.Log);
            try {
                body.MoveStep(effectTargets: partition.Targets[at], entityIndex: index, outputs: partition.Outputs, start: ref partition.Starts[at], stepTicks: partition.Steps[at].MotionTicks);
                _ = body.EndStep(contactFieldVersion: contactFieldVersion, outputs: partition.Outputs, scratch: partition.Scratches[at], sleepAfterTicks: m_sleepAfterTicks,
                    start: in partition.Starts[at], stepTicks: partition.Steps[at].MotionTicks, tick: tick);
            } finally {
                body.BindStepLog(log: null);
            }
            RecordFlockMotion(body: body, work: partition.Work);
            partition.Moved++;
        }
    }
}
