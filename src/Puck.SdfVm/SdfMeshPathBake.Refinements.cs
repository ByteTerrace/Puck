using System.Diagnostics;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // The refinement queue. A group's admission promises only what its final lattice holds while its slabs are sampled
    // (SampledPeakOf), which is frames of GPU work; its refinement (Prepare) is the stage that allocates the fine samples,
    // whose bound (PeakOf) is many times more, so a sampled lattice waits here, holding its lattice and no worker, until the
    // account has room for that bound. Admissions are paced by this queue's depth (Refinement.Waiting, read by the take),
    // never by a worker waiting for room.
    //
    // Invariant: a refinement starts only with its peak reservation held, and no bake worker waits on the memory account
    // for it. The one door is Refinement.Start, the queue's dequeue: the only code that constructs a started refinement,
    // which Prepare refuses otherwise, and it asks the account without waiting (Owner.TryReserve, TryReserveForced), so a
    // refused head stays queued and the caller carries on. Workers never wait on the account anywhere else either: the
    // pool's threads are marked so the account's one waiting door answers at once (MemoryAccount.NeverWaitOnThisThread).
    //
    // The queue is one for every bake in the process, served in arrival order, and a refused head holds its place: the
    // room the account gives back goes to it before any refinement queued behind it, so a large refinement never starves
    // behind a stream of small ones, and the residencies' bakes draw on one room in one order. A refinement whose head
    // finds no room starts anyway as the process's one forced job (TryReserveForced) when no refinement holds its peak
    // anywhere, so the bake always progresses.
    private sealed class Refinement {
        private static readonly Lock Gate = new();
        // Under Gate: the refinements waiting, in arrival order; the refinements started whose peak is not yet narrowed to
        // what they measured (every bake's).
        private static readonly List<Refinement> Queue = [];
        private static int RefiningCount;
        // The queue's depth, readable without the gate.
        private static int WaitingCount;

        private Refinement(SdfMeshPathBake bake, Group group, FinalLattice final) {
            Bake = bake;
            Group = group;
            Final = final;
        }

        public SdfMeshPathBake Bake { get; }
        public Group Group { get; }
        public FinalLattice Final { get; }
        // Whether the door has reserved its peak and handed it to a worker: set only by Start.
        public bool Started { get; private set; }

        private bool m_reported;
        private int m_finished;

        // Gets the refinements waiting for room, every bake's.
        public static int Waiting => Volatile.Read(location: ref WaitingCount);

        // Queues a group's refinement, its final lattice sampled whole, and runs the door. The group's job count is taken
        // here and held while it waits, so its workspace stays until its refinement runs or the queue drops it. Off any
        // bake's gate.
        public static void Enqueue(SdfMeshPathBake bake, Group group, FinalLattice final) {
            if (!group.QueueWork()) { return; }
            lock (Gate) {
                Queue.Add(item: new Refinement(bake: bake, final: final, group: group));
                Volatile.Write(location: ref WaitingCount, value: Queue.Count);
            }
            Start();
        }
        // The door: starts the queue's head while the account has room for its peak, each on the bake's pool ahead of every
        // queued stage, and drops the refinements of groups their bakes no longer hold. Asks the account without waiting and
        // returns once the head is refused or the queue is empty. Off any bake's gate: a refusal can make the account
        // collect (MemoryAccount reclaims for authoring), which a bake's gate must not be held across.
        public static void Start() {
            if (Waiting == 0) { return; }

            List<Refinement>? dropped = null;
            List<Refinement>? started = null;

            lock (Gate) {
                while (Queue.Count > 0) {
                    var next = Queue[0];
                    var group = next.Group;

                    if (group.Retired || !ReferenceEquals(objA: group.Final, objB: next.Final)) {
                        Queue.RemoveAt(index: 0);
                        (dropped ??= []).Add(item: next);
                        continue;
                    }

                    var peak = Math.Max(val1: PeakOf(final: next.Final), val2: group.PeakFloor);

                    if (!group.Memory.TryReserve(peak: peak) && ((RefiningCount > 0) || !group.Memory.TryReserveForced(peak: peak))) {
                        if (!next.m_reported) {
                            next.m_reported = true;
                            _ = Interlocked.Increment(location: ref next.Bake.m_accounts.RefinementWaits);
                            SdfMeshBakeReport.RefinementDeferred(peak: peak, refining: RefiningCount, residency: next.Bake.Residency, selector: group.Selector, waiting: Queue.Count);
                        }
                        break;
                    }
                    Queue.RemoveAt(index: 0);
                    RefiningCount++;
                    next.Started = true;
                    (started ??= []).Add(item: next);
                }
                Volatile.Write(location: ref WaitingCount, value: Queue.Count);
            }
            if (dropped is not null) {
                foreach (var refinement in dropped) { refinement.Group.EndWork(); }
            }
            if (started is not null) {
                foreach (var refinement in started) { refinement.Bake.Run(refinement: refinement); }
            }
        }
        // Drops a bake's waiting refinements, for its residency released. Off the bake's gate.
        public static void Drop(SdfMeshPathBake bake) {
            List<Refinement>? dropped = null;

            lock (Gate) {
                for (var index = (Queue.Count - 1); (index >= 0); index--) {
                    if (!ReferenceEquals(objA: Queue[index].Bake, objB: bake)) { continue; }
                    (dropped ??= []).Add(item: Queue[index]);
                    Queue.RemoveAt(index: index);
                }
                Volatile.Write(location: ref WaitingCount, value: Queue.Count);
            }
            if (dropped is null) { return; }
            foreach (var refinement in dropped) { refinement.Group.EndWork(); }
        }
        // Ends a started refinement's hold on the peak count once, after its peak narrowed or its stage ended, and runs the
        // door, since the room it gave back may start the head. On the refinement's worker, off any gate.
        public void Finish() {
            if (Interlocked.Exchange(location1: ref m_finished, value: 1) != 0) { return; }
            lock (Gate) { RefiningCount--; }
            Start();
        }
    }

    // Hands a started refinement to the pool ahead of every queued stage, under the job its queueing took; a group the bake
    // retired meanwhile only lets its job go.
    private void Run(Refinement refinement) => Enqueue(group: refinement.Group, urgent: true, work: () => {
        try {
            if (!refinement.Group.Retired) { Prepare(refinement: refinement); }
        } finally {
            refinement.Finish();
        }
    });

    // What a final lattice holds while its slabs are sampled, before Prepare builds anything from them: a value and a
    // material a sample (FinalLattice's two buffers), which is what a group's admission promises.
    private static long SampledPeakOf(FinalLattice final) => checked(((long)final.Lattice.Samples) * (sizeof(float) + sizeof(int)));

    // Ends a group's lead (SdfMeshBakeBacklog): the time from its admission to its first stage queued, or to its
    // refinement's queueing, so the time a refinement waits for room never reads as the pool's.
    private static void EndLead(Group group) {
        if (group.TakeAdmittedAt() is var admittedAt and not 0L) { PoolBacklog.ObserveLead(seconds: Stopwatch.GetElapsedTime(startingTimestamp: admittedAt).TotalSeconds); }
    }
}
