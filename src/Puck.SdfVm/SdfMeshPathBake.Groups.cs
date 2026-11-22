using Puck.Abstractions.Gpu;
using Puck.Maths;
using System.Collections;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // The bake's group list, which is also the one record of whether a group stands in it: a group is listed exactly
    // while it stands at one of the list's indices. Every gated stage asks that before it publishes its group's next step
    // (a group a restart or a promotion took out of the list publishes nothing), and the answer is a flag on the group
    // rather than a search of the list. Only Set and Clear change the list, and each moves the flags with it; Plan adds
    // groups unlisted, which Restart then sets into their places. Each group also knows the index it stands at
    // (Group.Index), which the order of a take breaks ties by. Read and written under the bake's gate.
    //
    // The list also keeps each listed group's index under the key its demand box moves by (OwnerKey): a dynamic group's
    // slot, a static group's first instance, so a moved slot or copy finds the groups it moves without a walk; and the
    // groups set into a place since the demand last measured (TakeFresh), each of which the demand measures before its
    // next tier decision (MeasureDemands). Set being the one door into the list is what keeps both whole.
    private sealed class GroupList : IReadOnlyList<Group> {
        private readonly List<Group> m_items = [];
        private readonly GpuRowIndex<(bool Dynamic, int Key)> m_byOwner = new();
        private List<Group> m_fresh = [];
        private List<Group> m_freshSpare = [];

        public int Count => m_items.Count;

        public Group this[int index] => m_items[index];

        // Whether the group stands at an index of the list.
        public static bool Holds(Group group) => group.Listed;

        // The key a group's demand box moves by: its slot's transform or its copies (a dynamic group), or its first
        // instance's copies (a static one).
        public static (bool Dynamic, int Key) OwnerKey(SdfMeshBakeGroup group) => (group.Dynamic, (group.Dynamic ? group.Slot : group.FirstInstance));

        // The first index whose group has the owner key, and the next on its chain; -1 past the last.
        public int FirstOf(bool dynamic, int key) => m_byOwner.First(key: (dynamic, key));
        public int NextOf(int index) => m_byOwner.Next(row: index);

        // Puts a group at an index, taking the group it displaces out of the list.
        public void Set(int index, Group group) {
            var displaced = m_items[index];

            if (!ReferenceEquals(objA: displaced, objB: group)) { displaced.Listed = false; }
            group.Index = index;
            group.Listed = true;
            m_items[index] = group;
            m_byOwner.Set(key: OwnerKey(group: group), row: index);
            if (double.IsNaN(d: group.DemandDueAt) && !group.DemandFresh) {
                group.DemandFresh = true;
                m_fresh.Add(item: group);
            }
        }
        // Takes every group out of the list.
        public void Clear() {
            foreach (var group in m_items) { group.Listed = false; }
            m_items.Clear();
            m_byOwner.Clear();
        }
        // Hands over the groups set into a place since the last hand-over, some of which may have left the list since.
        public List<Group> TakeFresh() {
            var taken = m_fresh;

            m_fresh = m_freshSpare;
            m_fresh.Clear();
            m_freshSpare = taken;
            foreach (var group in taken) { group.DemandFresh = false; }

            return taken;
        }
        // Appends planned groups, unlisted until Set puts each into its place.
        public void Plan(IEnumerable<Group> groups) {
            var first = m_items.Count;

            m_items.AddRange(collection: groups);
            for (var index = first; (index < m_items.Count); index++) {
                m_items[index].Index = index;
                m_items[index].Listed = false;
            }
        }

        public List<Group>.Enumerator GetEnumerator() => m_items.GetEnumerator();

        IEnumerator<Group> IEnumerable<Group>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // What each listed group still baking adds to the counts a take reads (TryTake): how many fly in a dispatch, run a
    // stage, hold an admission, wait for their first stage to reach the pool, and hold a lattice no dispatch has taken,
    // and which groups hold one. A group's contribution is a function of its own state (Group.TallyOf), and Settle is
    // the one place it moves a count or a set: it replaces the group's recorded contribution with the present one under
    // the tally's lock, so a count is exactly the sum of the groups' recorded contributions whatever order or thread the
    // changes arrive in. Every setter of a state the contribution reads settles the group (Group.Done, Listed, InFlight,
    // Contouring, Admitted, Next and the admission stamp); a state read there that gained a setter without one would
    // leave its group's contribution stale until its next change.
    //
    // The groups that hold a lattice are two sets, the ready (a lattice any take may dispatch) and the wanting (a first
    // slab of a final lattice, which only a group the process memory account admits may dispatch), each kept by the
    // group's place in it (Group.OfferedAt) so a group joins and leaves in constant time. A take walks these and no
    // other group: a bake with no group holding a lattice takes nothing and walks nothing, and one whose admissions are
    // closed never reads the wanting.
    private sealed class GroupTally {
        private const int Flying = 1;
        private const int Working = 2;
        private const int Admitted = 4;
        private const int Awaiting = 8;
        private const int Offering = 16;
        private const int Wanting = 32;

        private readonly List<Group> m_ready = [];
        private readonly List<Group> m_wanting = [];
        private readonly Lock m_gate = new();

        private int m_admitted;
        private int m_awaiting;
        private int m_flying;
        private int m_working;

        // The counts; `Offering` is every group holding a lattice, the ready and the wanting.
        public (int Flying, int Working, int Admitted, int Awaiting, int Offering) Read() {
            lock (m_gate) { return (m_flying, m_working, m_admitted, m_awaiting, (m_ready.Count + m_wanting.Count)); }
        }
        // Copies the groups holding a lattice into the lists, which it clears first: the wanting only when asked.
        public void Offered(List<Group> ready, List<Group> wanting, bool includeWanting) {
            ready.Clear();
            wanting.Clear();
            lock (m_gate) {
                ready.AddRange(collection: m_ready);
                if (includeWanting) { wanting.AddRange(collection: m_wanting); }
            }
        }
        public static int Of(bool done, bool listed, bool inFlight, bool contouring, bool admitted, bool awaiting, bool hasNext, bool firstSlab) {
            if (!listed || done) { return 0; }

            var offers = (hasNext && !inFlight && !contouring);

            return ((inFlight ? Flying : 0) | (contouring ? Working : 0) | (admitted ? Admitted : 0) | (awaiting ? Awaiting : 0) | (offers ? ((firstSlab && !admitted) ? Wanting : Offering) : 0));
        }
        public void Settle(Group group) {
            lock (m_gate) {
                var now = group.TallyOf();
                var before = group.Tallied;

                if (now == before) { return; }
                Move(bits: before, group: group, sign: -1);
                Move(bits: now, group: group, sign: 1);
                group.Tallied = now;
            }
        }
        private void Move(int bits, Group group, int sign) {
            if ((bits & Flying) != 0) { m_flying += sign; }
            if ((bits & Working) != 0) { m_working += sign; }
            if ((bits & Admitted) != 0) { m_admitted += sign; }
            if ((bits & Awaiting) != 0) { m_awaiting += sign; }
            if ((bits & Offering) != 0) { Offer(group: group, list: m_ready, sign: sign); }
            if ((bits & Wanting) != 0) { Offer(group: group, list: m_wanting, sign: sign); }
        }
        // Puts the group in a set or takes it out, the last member filling its place.
        private static void Offer(Group group, List<Group> list, int sign) {
            if (sign > 0) {
                group.OfferedAt = list.Count;
                list.Add(item: group);
                return;
            }

            var last = list[(list.Count - 1)];

            list[group.OfferedAt] = last;
            last.OfferedAt = group.OfferedAt;
            list.RemoveAt(index: (list.Count - 1));
        }
    }

    // A sweep that spreads one measure of every group over SdfMeshTier.MeasureFrames frames: the group the next frame's
    // share starts at, the boxes the sweep under way has read, and those the last whole sweep read, which a frame's share
    // is taken of, so a frame pays that share of the resident world whatever its size (MeasureDemands, Prioritize). The
    // caller charges each visit at least one box, so a world of landed groups is not walked whole each frame.
    private struct GroupSweep {
        private int m_at;
        private long m_boxes;
        private long m_sweep;

        // The boxes the last whole sweep read.
        public readonly long Boxes => m_boxes;
        // The boxes the next frame's share reads at most.
        public readonly long Share => Math.Max(val1: SdfMeshTier.MeasureBoxes, val2: m_boxes.CeilingDivide(divisor: ((long)SdfMeshTier.MeasureFrames)));

        // The group the share starts at, among `count`.
        public readonly int Start(int count) => ((m_at < count) ? m_at : 0);
        // Counts the boxes a visit was charged, and moves on from the group `at`: its successor, which wraps to the first
        // group with the sweep's boxes taken as the last whole sweep's.
        public int Visited(int at, int boxes, int count) {
            m_sweep += boxes;
            if (++at < count) { return at; }
            m_boxes = m_sweep;
            m_sweep = 0L;

            return 0;
        }
        // Remembers where the share ended.
        public void Stop(int at) => m_at = at;
    }
}
