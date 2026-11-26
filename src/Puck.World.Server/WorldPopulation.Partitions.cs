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
// A tick runs five phases for the local bodies: a plan fork (which bodies are local, their sleep, field inputs and
// cadence), a serial settlement of the plan (the wakes it logged, the goal fields its bodies will read, the perception
// grids), a steer fork (each due producer), a serial input pass (each moving body's producer observation, admitted
// through the one input runtime), and a move fork (each moving body's capture, merged intent, motion program, contacts
// and settlement). The join then applies each partition's log, outputs and counts in ascending partition order.
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
// - The navigation hand-off. The plan's settlement resolves every goal field a due steering body will read
//   (PrepareGoalField), bakes the domain a route search will read, and builds every perception neighbourhood, serially,
//   in partition order, which is the order one thread walking the slots would ask in. The navigation runtime is
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
    // The partitions holding at least one of the tick's step bodies, in ascending order: the plan fork's list.
    private readonly List<KitPartition> m_planKitPartitions = [];
    // The partitions holding at least one local body this tick, in ascending order: the steer and move forks' list.
    private readonly List<KitPartition> m_activeKitPartitions = [];
    private KitPartitionRun? m_kitRun;
    // How many local bodies this tick's plan has steer and move: what decides whether a phase forks.
    private int m_kitSteers;
    private int m_kitMoves;
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
        // The occupied bodies a far-cadence query found near a body (NearestOccupiedWith).
        public int[] Occupied = [];
        public WorldFlockStatistics Flock;
        public WorldAutonomyStatistics Autonomy;

        // Whether this work belongs to a kit partition, whose reads may not build or derive a shared cache.
        public bool Partitioned { get; } = partitioned;

        public void Size(WorldPopulation population) {
            if (Neighbors.Length != population.m_flockCandidates) { Neighbors = new FixedSpatialNeighbor[population.m_flockCandidates]; }
            if (FlockNeighbors.Length != population.m_flockNeighbors) { FlockNeighbors = new FixedFlockNeighbor[population.m_flockNeighbors]; }
            if (Sensed.Length != population.m_senseCandidates) { Sensed = new FixedSpatialNeighbor[population.m_senseCandidates]; }
            if (Occupied.Length < population.m_occupiedCount) { Occupied = new int[population.m_occupiedCount]; }
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
        // Whether the body moves this tick: its cadence's answer.
        public readonly bool[] Moves = new bool[KitPartitionSlots];
        // The one step scratch the partition's bodies capture into, each step in turn: a step begins and ends within the
        // move fork, so no two of the partition's steps are open at once.
        public readonly WorldBody.StepScratch Scratch = new();
        public readonly ProducerWork Work = new(partitioned: true);
        public readonly WorldBody.StepOutputs Outputs = new();
        public readonly WorldBodyStepLog Log = new();
        public int Count;
        public int Steered;
        public int Moved;
        // The plan's share of the tick: the run of the step bodies whose slots are this partition's, the bodies it
        // visited, those it left to the serial loop, and those whose goal field its settlement requests (ahead of the
        // read for a body not due to steer).
        public int PlanStart;
        public int PlanCount;
        public int Visits;
        public readonly List<int> Serial = [];
        public readonly List<(int Index, bool Ahead)> GoalRequests = [];
        public bool Perceives;
        // The sections the partition's steps copied aside this tick, and their bytes (WorldBodyWork.StepSaves).
        public long Saves;
        public long SavedBytes;
    }

    // The work of one fork: which phase runs, what every partition of it reads, and the jobs the fork hands out. A job is
    // a run of consecutive active partitions holding about an equal share of the bodies, so a fork costs a few wakes,
    // not one per partition. Which job runs a partition changes nothing a partition writes: every partition owns its
    // outputs, and the join reads them in partition order.
    private sealed class KitPartitionRun(WorldPopulation owner) : IJobPartitions {
        public KitPhase Phase;
        // The partitions the fork runs: the plan's for the plan, the active ones for the steer and the move.
        public List<KitPartition> Partitions = [];
        public ulong Tick;
        public ulong StepTicks;
        public ulong StepStartEngineTick;
        public ulong ContactFieldVersion;
        // Job j runs partitions [JobStarts[j], JobStarts[j + 1]) of the fork's list.
        public int[] JobStarts = new int[1];
        public int Jobs;

        public void Run(int partition) {
            for (var at = JobStarts[partition]; (at < JobStarts[(partition + 1)]); at++) {
                var kit = Partitions[at];

                switch (Phase) {
                    case KitPhase.Plan:
                        owner.PlanKitPartition(contactFieldVersion: ContactFieldVersion, partition: kit, stepStartEngineTick: StepStartEngineTick, stepTicks: StepTicks);
                        break;
                    case KitPhase.Steer:
                        owner.SteerKitPartition(partition: kit);
                        break;
                    default:
                        owner.MoveKitPartition(contactFieldVersion: ContactFieldVersion, partition: kit, tick: Tick);
                        break;
                }
            }
        }
        // Cuts the fork's partitions into at most jobs runs of about equal body counts.
        public void Plan(int jobs) {
            var partitions = Partitions;
            var bodies = 0;

            foreach (var partition in partitions) { bodies += Bodies(partition: partition); }
            jobs = Math.Clamp(value: jobs, min: 1, max: Math.Max(1, partitions.Count));
            if (JobStarts.Length < (jobs + 1)) { JobStarts = new int[(jobs + 1)]; }
            var job = 0;
            var seen = 0;

            JobStarts[0] = 0;
            for (var at = 0; (at < partitions.Count); at++) {
                seen += Bodies(partition: partitions[at]);
                // Close the job once it holds its share, leaving at least one partition for every later job.
                if ((job < (jobs - 1)) && (((long)seen * jobs) >= ((long)bodies * (job + 1))) && ((partitions.Count - (at + 1)) >= (jobs - (job + 1)))) {
                    JobStarts[++job] = (at + 1);
                }
            }
            Jobs = (job + 1);
            JobStarts[Jobs] = partitions.Count;
        }
        // The bodies a partition brings to the fork's phase.
        private int Bodies(KitPartition partition) => ((Phase == KitPhase.Plan) ? partition.PlanCount : partition.Count);
    }
    // The three forks of a tick's kit advance.
    private enum KitPhase : byte {
        Plan,
        Steer,
        Move,
    }

    // Whether a body's producer reads only what a partition may read: the frozen image, its own entry, a curve, a sight
    // test, a shared domain's resident goal field and a domain's own path search. A flock affinity (an authority
    // expression over the arena) stays serial. A producer that stands down runs none of its program, as a body naming
    // none does.
    private bool ProducerIsLocal(Entry entry, WorldBody body, int index) =>
        (entry.ProducerState.StandDown != BodyProducerStandDown.None) ||
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

    // The plan. A fork walks each partition's share of the tick's step bodies: it leaves every body whose step is not
    // local to the serial loop, and for each local one settles its sleep, field inputs and cadence (the same rules
    // AdvanceSerialBody applies) and places it in its partition. The settlement then runs here, partitions in ascending
    // order: the wakes the plan logged, the bodies left to the serial loop, the goal field each due producer will read
    // and the perception grids, which read and write what the partitions share.
    //
    // The invariant: the plan leaves every body, entry and shared set as one thread walking the slots in order would.
    // What enforces it: a partition's plan writes only its own bodies, their entries and its own lists; whatever would
    // write shared state is logged or listed and applied by the settlement in slot order, and the shared mutators throw
    // while a partition runs (ThrowIfPartitioned), so a plan step that reached one fails at one worker as at many.
    private void PlanKitStep(ReadOnlySpan<int> stepBodies, ulong stepTicks, ulong stepStartEngineTick, ulong contactFieldVersion, JobPool? jobs) {
        m_serialKitBodies.Clear();
        foreach (var partition in m_planKitPartitions) {
            partition.Count = 0;
            partition.Steered = 0;
            partition.Moved = 0;
            partition.Saves = 0;
            partition.SavedBytes = 0;
            partition.PlanCount = 0;
            partition.Visits = 0;
            partition.Perceives = false;
        }
        m_planKitPartitions.Clear();
        m_activeKitPartitions.Clear();
        m_kitSteers = 0;
        m_kitMoves = 0;

        // The step bodies are in slot order, so each partition's share is one run of them.
        KitPartition? share = null;
        var shareOrdinal = -1;

        for (var at = 0; (at < stepBodies.Length); at++) {
            var ordinal = (stepBodies[at] / KitPartitionSlots);

            if (ordinal != shareOrdinal) {
                share = KitPartitionOf(index: stepBodies[at]);
                share.PlanStart = at;
                shareOrdinal = ordinal;
                m_planKitPartitions.Add(item: share);
            }
            share!.PlanCount++;
        }
        if (m_planKitPartitions.Count == 0) {
            return;
        }
        var run = (m_kitRun ??= new KitPartitionRun(owner: this));

        run.ContactFieldVersion = contactFieldVersion;
        run.StepStartEngineTick = stepStartEngineTick;
        run.StepTicks = stepTicks;
        foreach (var partition in m_planKitPartitions) { partition.Work.Size(population: this); }
        ForkKitPartitions(bodies: stepBodies.Length, jobs: jobs, partitions: m_planKitPartitions, phase: KitPhase.Plan, run: run);

        var perceives = false;

        foreach (var partition in m_planKitPartitions) {
            ApplyStepLog(partition: partition);
            m_serialWork.AbsorbStatistics(other: partition.Work);
            SimulatedBodyVisits += partition.Visits;
            m_serialKitBodies.AddRange(collection: partition.Serial);
            partition.Serial.Clear();
            perceives |= partition.Perceives;
            foreach (var (index, ahead) in partition.GoalRequests) {
                var entry = m_entries[index];
                var producer = entry.ProducerLookup!;
                var self = entry.Body!.FixedPosition;

                if (ahead) {
                    PrefetchGoalField(entry: entry, producer: producer, self: in self);
                    continue;
                }
                if (producer.Flock is { MovementDomainIndex: >= 0 } flock) { m_navigation[flock.MovementDomainIndex].EnsureBaked(); }
                // A route of its own reads one domain, and its cells' area types under a profile that prices them: bake
                // and derive them here, where a bake may be adopted.
                if ((producer.Target is { Source: BodyTargetSource.Navigated } routed) && (((uint)routed.NavigationDomainIndex) < ((uint)m_navigation.Count))) {
                    if (NavigationProfile(index: routed.NavigationProfileIndex).IsNeutral) {
                        m_navigation[routed.NavigationDomainIndex].EnsureBaked();
                    } else {
                        m_navigation[routed.NavigationDomainIndex].EnsureAreas();
                    }
                }
                PrepareGoalField(entry: entry, producer: producer, self: in self);
            }
            partition.GoalRequests.Clear();
            if (partition.Count == 0) {
                continue;
            }
            m_activeKitPartitions.Add(item: partition);
            for (var at = 0; (at < partition.Count); at++) {
                m_kitSteers += (partition.Steps[at].SteeringDue ? 1 : 0);
                m_kitMoves += (partition.Moves[at] ? 1 : 0);
            }
        }
        ResolveGoalFields();
        if (perceives) { m_perception?.BuildDeclared(); }
    }
    // One partition's plan: its share of the tick's step bodies, in slot order.
    private void PlanKitPartition(KitPartition partition, ulong stepTicks, ulong stepStartEngineTick, ulong contactFieldVersion) {
        var bodies = m_stepBodies.AsSpan(length: partition.PlanCount, start: partition.PlanStart);

        foreach (var index in bodies) {
            var entry = m_entries[index];

            if (
                !entry.Active ||
                (entry.Kind == PopulationKind.LocalSeat) ||
                (entry.Body is not { } player)
            ) {
                continue;
            }
            partition.Visits++;
            // A sleeper nothing wakes takes no step whichever way its step would run, so it is neither classed nor
            // planned: an idle town costs its plan one read a body.
            if (player.SleepsThrough(contactFieldVersion: contactFieldVersion)) {
                continue;
            }
            if (!player.StepIsLocal || !ProducerIsLocal(entry: entry, body: player, index: index)) {
                partition.Serial.Add(item: index);
                continue;
            }
            bool planned;
            AutonomyStep step;

            // A sleeper the plan wakes reports to its population through the partition's log.
            player.BindStepLog(log: partition.Log);
            try {
                planned = TryPlanAutonomy(contactFieldVersion: contactFieldVersion, entry: entry, index: index, player: player, step: out step, stepStartEngineTick: stepStartEngineTick, stepTicks: stepTicks, work: partition.Work);
            } finally {
                player.BindStepLog(log: null);
            }
            if (!planned) {
                continue;
            }
            var producer = (((entry.ProducerState.StandDown == BodyProducerStandDown.None) && (player.Source.ProducerName is not null)) ? entry.ProducerLookup : null);

            if (!step.SteeringDue && (producer is not null) && GoalFieldMoved(entry: entry, producer: producer)) {
                partition.GoalRequests.Add(item: (index, true));
            }
            if (!step.SteeringDue && !step.Moves) {
                continue;
            }
            if (step.SteeringDue && (producer is not null)) {
                partition.Perceives |= ((producer.Flock is not null) || (producer.Target?.Source is BodyTargetSource.Sensed));
                partition.GoalRequests.Add(item: (index, false));
            }

            var at = partition.Count++;

            partition.Bodies[at] = index;
            partition.Steps[at] = step;
            partition.Moves[at] = step.Moves;
        }
    }
    // Whether a body not due to steer names a shared navigated goal other than the one its field was last requested
    // for, so the settlement requests the new goal's field ahead of the read (PrefetchGoalField, which decides again).
    private bool GoalFieldMoved(Entry entry, CompiledBodyProducer producer) => (
        (producer.Target is { Source: BodyTargetSource.Navigated } target) &&
        (((uint)target.NavigationDomainIndex) < ((uint)m_navigation.Count)) &&
        m_navigation[target.NavigationDomainIndex].Tuning.Shared &&
        TryNavigationGoal(entry: entry, goal: out var goal, target: in target, targetIndex: out _) &&
        (entry.PrefetchedGoal != goal)
    );
    // Applies what a partition's bodies reported while it ran, in the order they reported it.
    private void ApplyStepLog(KitPartition partition) {
        foreach (var (index, woke) in partition.Log.Entries) {
            if (woke) { BodyWoke(index: index); } else { NoteBodyFactChanged(index: index); }
        }
        partition.Log.Clear();
    }

    // Runs the planned partitions: the steer fork, the serial input pass, the move fork, then the join.
    private void RunKitPartitions(JobPool? jobs, ulong tick, ulong contactFieldVersion) {
        if (m_activeKitPartitions.Count == 0) {
            return;
        }
        var run = (m_kitRun ??= new KitPartitionRun(owner: this));

        run.Tick = tick;
        run.ContactFieldVersion = contactFieldVersion;
        using (Puck.Hosting.HostFrameWork.Measure(Puck.Hosting.HostFrameWorkPart.TickProducers)) {
            ForkKitPartitions(bodies: m_kitSteers, jobs: jobs, partitions: m_activeKitPartitions, phase: KitPhase.Steer, run: run);
        }

        // The input pass: each moving body's producer observation goes through the one input runtime, in index order.
        using (Puck.Hosting.HostFrameWork.Measure(Puck.Hosting.HostFrameWorkPart.TickAdmission)) {
            foreach (var partition in m_activeKitPartitions) {
                for (var at = 0; (at < partition.Count); at++) {
                    if (partition.Moves[at]) { AdmitLocalObservation(at: at, partition: partition, tick: tick); }
                }
            }
        }
        ForkKitPartitions(bodies: m_kitMoves, jobs: jobs, partitions: m_activeKitPartitions, phase: KitPhase.Move, run: run);

        var steered = 0;
        var moved = 0;
        var saves = 0L;
        var savedBytes = 0L;

        foreach (var partition in m_activeKitPartitions) {
            ApplyStepLog(partition: partition);
            m_outputs.Absorb(other: partition.Outputs);
            m_serialWork.AbsorbStatistics(other: partition.Work);
            steered += partition.Steered;
            moved += partition.Moved;
            saves += partition.Saves;
            savedBytes += partition.SavedBytes;
        }
        WorldBodyWork.Process.Add(amount: saves, kind: WorldBodyWork.StepSaves);
        WorldBodyWork.Process.Add(amount: savedBytes, kind: WorldBodyWork.StepSavedBytes);
        BodyAdvances += moved;
        BodySteers += steered;
        WorldBodyWork.Process.Add(amount: moved, kind: WorldBodyWork.Advances);
        WorldBodyWork.Process.Add(amount: moved, kind: WorldBodyWork.LocalAdvances);
        WorldBodyWork.Process.Add(amount: steered, kind: WorldBodyWork.LocalSteers);
        WorldBodyWork.Process.Add(amount: m_activeKitPartitions.Count, kind: WorldBodyWork.KitPartitions);
    }
    // The bodies below which a phase runs its partitions on this thread: fewer than that, waking a worker and joining it
    // costs as much as the steps it would take away.
    private const int KitForkBodies = 64;
    // How many jobs a fork hands out per worker, so a slow job leaves others to balance against.
    private const int KitJobsPerWorker = 2;

    // One fork of a phase over its partitions, cut into a few jobs; with no pool, a serial one or too few bodies, every
    // partition runs here in order. Either way each partition runs once, writing only what it owns.
    private void ForkKitPartitions(JobPool? jobs, KitPartitionRun run, KitPhase phase, List<KitPartition> partitions, int bodies) {
        run.Phase = phase;
        run.Partitions = partitions;
        m_partitioned = true;
        using var navigation = m_navigation.EnterReadOnly();
        try {
            if ((jobs is null) || jobs.Serial || (bodies < KitForkBodies)) {
                run.Plan(jobs: 1);
                run.Run(partition: 0);
            } else {
                run.Plan(jobs: (jobs.Workers.Count * KitJobsPerWorker));
                jobs.Fork(body: run, jobClass: JobClass.Tick, partitions: run.Jobs);
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

            var ran = StageProducer(body: body, entry: entry, index: index, stepTicks: partition.Steps[at].SteeringTicks, work: partition.Work);

            SeedSteering(entry: entry, motionPeriod: partition.Steps[at].MotionPeriod, player: body, steeringPeriod: partition.Steps[at].SteeringPeriod, work: partition.Work);
            if (ran) { partition.Steered++; }
        }
    }
    // A moving body's part of the input pass: the staged steering a batched body keeps between refreshes, then the
    // admission of its producer observation through the one input runtime.
    private void AdmitLocalObservation(KitPartition partition, int at, ulong tick) {
        var index = partition.Bodies[at];
        var entry = m_entries[index];
        var body = entry.Body!;
        ref var autonomy = ref entry.AutonomyState;

        if (!partition.Steps[at].SteeringDue && autonomy.SteeringSeeded) {
            body.StageProducerIntent(intent: in autonomy.SteeringIntent);
        }
        body.AdmitProducerObservation(entityIndex: index, tick: tick);
    }
    private void MoveKitPartition(KitPartition partition, ulong tick, ulong contactFieldVersion) {
        for (var at = 0; (at < partition.Count); at++) {
            if (!partition.Moves[at]) { continue; }
            var index = partition.Bodies[at];
            var entry = m_entries[index];
            var body = entry.Body!;
            var stepTicks = partition.Steps[at].MotionTicks;
            var targets = ReadEffectTargets(entry: entry, selfIndex: index, self: body.FixedPosition);
            var (savesBefore, bytesBefore) = body.RestoreSaves;

            body.BindStepLog(log: partition.Log);
            try {
                body.BeginStepCore(engageProbeOrdinal: null, entityIndex: index, outputs: partition.Outputs, scratch: partition.Scratch, start: out var start, stepTicks: stepTicks, tick: tick, transactional: false);
                body.MoveStep(effectTargets: targets, entityIndex: index, outputs: partition.Outputs, start: ref start, stepTicks: stepTicks);
                _ = body.EndStep(contactFieldVersion: contactFieldVersion, outputs: partition.Outputs, scratch: partition.Scratch, sleepAfterTicks: m_sleepAfterTicks,
                    start: in start, stepTicks: stepTicks, tick: tick);
            } finally {
                body.BindStepLog(log: null);
            }
            var (savesAfter, bytesAfter) = body.RestoreSaves;

            partition.Saves += (savesAfter - savesBefore);
            partition.SavedBytes += (bytesAfter - bytesBefore);
            RecordFlockMotion(body: body, work: partition.Work);
            partition.Moved++;
        }
    }
}
