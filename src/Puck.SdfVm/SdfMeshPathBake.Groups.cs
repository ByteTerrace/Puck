using System.Collections;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // The bake's group list, which is also the one record of whether a group stands in it: a group is listed exactly
    // while it stands at one of the list's indices. Every gated stage asks that before it publishes its group's next step
    // (a group a restart or a promotion took out of the list publishes nothing), and the answer is a flag on the group
    // rather than a search of the list. Only Set and Clear change the list, and each moves the flags with it; Plan adds
    // groups unlisted, which Restart then sets into their places. Read and written under the bake's gate.
    private sealed class GroupList : IReadOnlyList<Group> {
        private readonly List<Group> m_items = [];

        public int Count => m_items.Count;

        public Group this[int index] => m_items[index];

        // Moves with every change of which group stands at which index, so an order of the indices (Order) knows it
        // still names the groups it was taken over.
        public int Version { get; private set; }

        // Whether the group stands at an index of the list.
        public static bool Holds(Group group) => group.Listed;

        // Puts a group at an index, taking the group it displaces out of the list.
        public void Set(int index, Group group) {
            var displaced = m_items[index];

            if (!ReferenceEquals(objA: displaced, objB: group)) { displaced.Listed = false; }
            group.Listed = true;
            m_items[index] = group;
            Version++;
        }
        // Takes every group out of the list.
        public void Clear() {
            foreach (var group in m_items) { group.Listed = false; }
            m_items.Clear();
            Version++;
        }
        // Appends planned groups, unlisted until Set puts each into its place.
        public void Plan(IEnumerable<Group> groups) {
            var first = m_items.Count;

            m_items.AddRange(collection: groups);
            for (var index = first; (index < m_items.Count); index++) { m_items[index].Listed = false; }
            Version++;
        }

        public List<Group>.Enumerator GetEnumerator() => m_items.GetEnumerator();

        IEnumerator<Group> IEnumerable<Group>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // What each listed group still baking adds to the counts a take reads (TryTake): how many fly in a dispatch, run a
    // stage, hold an admission, wait for their first stage to reach the pool, and hold a lattice no dispatch has taken. A
    // group's contribution is a function of its own state (Group.TallyOf), and Settle is the one place it moves a count:
    // it replaces the group's recorded contribution with the present one under the tally's lock, so a count is exactly
    // the sum of the groups' recorded contributions whatever order or thread the changes arrive in. Every setter of a
    // state the contribution reads settles the group (Group.Done, Listed, InFlight, Contouring, Admitted, Next and the
    // admission stamp); a state read there that gained a setter without one would leave its group's contribution stale
    // until its next change.
    private sealed class GroupTally {
        private const int Flying = 1;
        private const int Working = 2;
        private const int Admitted = 4;
        private const int Awaiting = 8;
        private const int Offering = 16;

        private readonly Lock m_gate = new();

        private int m_admitted;
        private int m_awaiting;
        private int m_flying;
        private int m_offering;
        private int m_working;

        public (int Flying, int Working, int Admitted, int Awaiting, int Offering) Read() {
            lock (m_gate) { return (m_flying, m_working, m_admitted, m_awaiting, m_offering); }
        }
        public static int Of(bool done, bool listed, bool inFlight, bool contouring, bool admitted, bool awaiting, bool hasNext) =>
            ((!listed || done)
                ? 0
                : ((inFlight ? Flying : 0) | (contouring ? Working : 0) | (admitted ? Admitted : 0) | (awaiting ? Awaiting : 0) | ((hasNext && !inFlight && !contouring) ? Offering : 0)));
        public void Settle(Group group) {
            lock (m_gate) {
                var now = group.TallyOf();
                var before = group.Tallied;

                if (now == before) { return; }
                Move(bits: before, sign: -1);
                Move(bits: now, sign: 1);
                group.Tallied = now;
            }
        }
        private void Move(int bits, int sign) {
            if ((bits & Flying) != 0) { m_flying += sign; }
            if ((bits & Working) != 0) { m_working += sign; }
            if ((bits & Admitted) != 0) { m_admitted += sign; }
            if ((bits & Awaiting) != 0) { m_awaiting += sign; }
            if ((bits & Offering) != 0) { m_offering += sign; }
        }
    }
}
