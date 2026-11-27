namespace Puck.SdfVm;

// A copy of a static template that moves to another template (a responding row whose holding swapped its creation)
// keeps drawing the template it drew until the one it names has a mesh to draw, and that template bakes ahead of
// everything else. A responding row's other creations are templates nothing draws until a swap shows one, so their
// groups bake only once a copy names them; without a stand-in the copy would draw nothing (a blank screen, a missing
// door) for as long as that bake waits behind the rest of the world's. No variant is baked before it is shown.
//
// The invariant: the dresser draws every copy whose previous template drew through that template, never through
// none, until each group of the template it now names draws a mesh or found none (RangeShown). Enforced by
// DressedCopiesOf, the one list the dresser reads a group's copies from (FrameAccess.CopiesOf). Demand reads the
// copies a template is named by and those still standing in on it (CopiesOf), so the template a copy names is
// measured, planned and baked as if it already drew, and the one it stands in on keeps its demand until the copy
// leaves it. Where it stops: a stand-in is kept only across a frame whose program shares the held one's field
// (SdfProgram.SharesField, which keeps every group), and only for a copy whose motion identity and scale the frame
// before named at the same index, so a recomposed program, a changed copy list or a template baked at another scale
// draws each copy as it names.
public sealed partial class SdfMeshPathBake {
    // Each copy standing in, by its index in the frame's copies, with the template range it still draws.
    private readonly Dictionary<int, SdfMeshTemplateRange> m_standIns = [];
    // The copies the dresser draws at the instances a stand-in moves (the membership index elsewhere), by instance.
    private readonly Dictionary<int, List<int>> m_drawnCopies = [];
    // The copies demand reads at the instances a stand-in holds (the membership index elsewhere), by instance.
    private readonly Dictionary<int, List<int>> m_heldCopies = [];
    // The instances of the templates a stand-in waits on, whose groups bake first (Prioritized, RaiseAwaited).
    private readonly HashSet<int> m_awaitedInstances = [];

    // Decides the stand-ins after the membership index is rebuilt: a copy at the same index and with the same motion
    // identity as before, whose template moved from one that draws to one that does not yet, stands in on the one it
    // drew (its earlier stand-in, if it stood in already). Under the gate, on the frame's thread.
    private void PlaceStandIns(SdfFrame frame, CopyLayout[] previous) {
        var held = ((m_standIns.Count == 0) ? null : new Dictionary<int, SdfMeshTemplateRange>(dictionary: m_standIns));

        m_standIns.Clear();
        if (frame.Program.SharesField(other: m_program) && (previous.Length == m_copyLayout.Length)) {
            for (var index = 0; (index < m_copyLayout.Length); index++) {
                var now = m_copyLayout[index];
                var before = previous[index];

                // The pose a copy carries scales the template it names, so it draws the one it drew only at that one's scale.
                if ((now.TemplateRange is not { } range) || (now.MotionIdentity is null) || (now.Scale != before.Scale) || !Equals(objA: now.MotionIdentity, objB: before.MotionIdentity)) { continue; }

                var drew = (((held is not null) && held.TryGetValue(key: index, value: out var standing)) ? standing : before.TemplateRange);

                if ((drew is not { } old) || (old == range) || RangeShown(range: range) || !RangeShown(range: old)) { continue; }
                m_standIns[index] = old;
            }
        }
        IndexStandIns();
    }
    // Lets each copy whose template now draws go back to it, and ranks what the rest still wait on first, at every
    // prepared frame. Under the gate, after the frame's groups have moved and before the measure reads the awaited
    // instances.
    private void ReleaseStandIns() {
        if (m_standIns.Count == 0) { return; }

        List<int>? released = null;

        foreach (var (copy, _) in m_standIns) {
            if ((m_copyLayout[copy].TemplateRange is not { } range) || RangeShown(range: range)) { (released ??= []).Add(item: copy); }
        }
        if (released is not null) {
            foreach (var copy in released) { _ = m_standIns.Remove(key: copy); }
            IndexStandIns();
            m_copyLayoutRevision++;
            m_demandCopyLayout = true;
        }
        RaiseAwaited();
    }
    // Rebuilds the drawn and held lists at the instances the stand-ins touch from the membership index.
    private void IndexStandIns() {
        m_drawnCopies.Clear();
        m_heldCopies.Clear();
        m_awaitedInstances.Clear();
        foreach (var (copy, old) in m_standIns) {
            var range = m_copyLayout[copy].TemplateRange!.Value;

            for (var instance = range.FirstInstance; (instance < (range.FirstInstance + range.InstanceCount)); instance++) {
                _ = ListAt(table: m_drawnCopies, instance: instance).Remove(item: copy);
                _ = m_awaitedInstances.Add(item: instance);
            }
            for (var instance = old.FirstInstance; (instance < (old.FirstInstance + old.InstanceCount)); instance++) {
                Insert(copies: ListAt(table: m_drawnCopies, instance: instance), copy: copy);
                Insert(copies: ListAt(table: m_heldCopies, instance: instance), copy: copy);
            }
        }

        List<int> ListAt(Dictionary<int, List<int>> table, int instance) {
            if (!table.TryGetValue(key: instance, value: out var copies)) {
                table[instance] = copies = (m_instanceCopies.TryGetValue(key: instance, value: out var named) ? [.. named] : []);
            }
            return copies;
        }
        static void Insert(List<int> copies, int copy) {
            var at = copies.BinarySearch(item: copy);

            if (at < 0) { copies.Insert(index: ~at, item: copy); }
        }
    }
    // Ranks the groups a stand-in waits on, and the leaders baking their geometry, ahead of every other group now, rather
    // than when the measure's sweep next reaches them (Prioritized keeps them there).
    private void RaiseAwaited() {
        foreach (var instance in m_awaitedInstances) {
            for (var at = m_groups.FirstOf(dynamic: false, key: instance); (at >= 0); at = m_groups.NextOf(index: at)) {
                var group = m_groups[at];

                if (group.Done || group.Retired) { continue; }
                Raise(group: group);
                if (group.Leader is { Done: false } leader) { Raise(group: leader); }
            }
        }

        static void Raise(Group group) {
            group.Priority = float.MaxValue;
            group.InView = true;
            if (group.Waiter is { } waiter) { waiter.Priority = float.MaxValue; }
        }
    }
    // Whether every listed group of a template's instances draws a mesh or found no surface.
    private bool RangeShown(SdfMeshTemplateRange range) {
        for (var instance = range.FirstInstance; (instance < (range.FirstInstance + range.InstanceCount)); instance++) {
            for (var at = m_groups.FirstOf(dynamic: false, key: instance); (at >= 0); at = m_groups.NextOf(index: at)) {
                var group = m_groups[at];

                if (group.Retired || (group.Shown is not null) || (group.Done && (group.Mesh is null))) { continue; }
                return false;
            }
        }
        return true;
    }
    // The copies at a static template's instance: those the dresser draws there, or those demand reads there (the copies
    // naming it and those standing in on it). Null where the instance is no template's.
    private IReadOnlyList<int>? StandInCopiesOf(int instance, bool drawn) =>
        (((m_standIns.Count > 0) && (drawn ? m_drawnCopies : m_heldCopies).TryGetValue(key: instance, value: out var copies))
            ? copies
            : m_instanceCopies.GetValueOrDefault(key: instance));
    // The copies the dresser draws a group's mesh for (FrameAccess.CopiesOf).
    private IReadOnlyList<int>? DressedCopiesOf(SdfMeshBakeGroup group) => (group.Dynamic
        ? CopiesOf(group: group)
        : StandInCopiesOf(instance: group.FirstInstance, drawn: true));
    // Whether a stand-in waits on a group's template.
    private bool AwaitedByStandIn(Group group) => (!group.Dynamic && (m_awaitedInstances.Count > 0) && m_awaitedInstances.Contains(item: group.FirstInstance));
}
