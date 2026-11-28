using System.Globalization;
using System.Text;

namespace Puck.SdfVm;

// An owner with several forms (a body that can switch looks: its own body, or that body in a suit) rides every form in
// the program at once, each on dynamic slots of its own (SdfFrame.MeshForms). The bake draws one form of each owner and
// keeps the others dormant: planned, baked and measured where their slots stand (the body's root, so at the tier its
// demand asks), named ahead of the draws so the mesh region keeps their words, and drawn by no row.
//
// The invariant: a frame draws an owner's named form only once every group of it shows what a settled bake of it shows
// (ShowsProduct: its own product past the floor an arrival plans first, or no surface) and every piece of every such
// mesh stands in the mesh region; until then it draws the form it drew before. So a body never draws a look whose groups
// have not all landed. Enforced by DressedCopiesOf, the one list the dresser reads a group's copies from
// (FrameAccess.CopiesOf), which answers no copies for a group riding a dormant form's slots; the dormant set is derived
// only by PlaceForms from the forms' Drawn flags, and only DecideForms hands a named form the draws, on the frame's
// thread with the residency's tables in hand (an offline bake, which lands nothing, judges its groups alone). The
// composition hands the bake the forms its held program was emitted with (SdfCompositionFrameSource keeps them while a
// successor builds), so a form is never judged by another form's groups at the same slots. Where it stops: an owner whose drawn form leaves the frame (its producer
// dropped it, or a recomposed program no longer holds it) draws its named form as it stands, since nothing is left to
// hold; a named form with no group in the program yet is never handed the draws; and a piece the region refused beside
// the meshes the draws read (SdfWorldTables.RefusesAhead) counts as standing, as a refused product takes its draws, so
// under that pressure the piece lands out of the frame as a first mesh does. A form its producer marks lingering
// (SdfMeshFormRange.Lingers, a form playing its transition out) is neither dormant nor held: it draws beside its owner's
// drawn form until the producer publishes it without the mark, which re-places the forms (SameForms compares the mark).
public sealed partial class SdfMeshPathBake {
    // The forms the latest prepared frame listed, by value; the form each owner draws, by its place in m_forms; the slots
    // of every form drawn by none of its owner's rows; and the slots of each named form still held, whose groups bake
    // first (AwaitedByStandIn). PlaceForms writes them, before the frame's measure is posted.
    private readonly List<SdfMeshFormRange> m_forms = [];
    private readonly Dictionary<object, int> m_formDrawn = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> m_dormantSlots = [];
    private readonly HashSet<int> m_heldSlots = [];
    private readonly HashSet<int> m_placedDormant = [];
    // Whether DecideForms handed a form the draws since PlaceForms last ran, the owners whose named form is held, and the
    // forms handed the draws and the decisions that held one, since the bake began.
    private bool m_formsDecided;
    private int m_formsHeld;
    private long m_formsHanded;
    private long m_formHolds;
    // One more each time the dormant set changes, and its value and the product revision when ServeAhead last named the
    // dormant groups' meshes ahead.
    private long m_dormantRevision;
    private long m_dormantNamed = -1L;
    private long m_dormantNamedProduct = -1L;

    // Takes the frame's forms and places them again where they or a decision moved. A bake no tables observe (an offline
    // bake) lands nothing, so it hands a named form the draws here, as soon as its groups show their product, as it draws
    // every product at once. Under the gate, on the frame's thread, before the frame's measure is posted.
    private void TakeForms(SdfFrame frame) {
        var forms = frame.MeshForms;

        if (m_formsDecided || !SameForms(forms: forms)) {
            m_formsDecided = false;
            m_forms.Clear();
            m_forms.AddRange(collection: forms);
            PlaceForms();
        }
        if (LandsAhead || (m_formsHeld == 0)) { return; }
        DecideForms(tables: null);
        if (!m_formsDecided) { return; }
        m_formsDecided = false;
        PlaceForms();
    }
    private bool SameForms(IReadOnlyList<SdfMeshFormRange> forms) {
        if (forms.Count != m_forms.Count) { return false; }
        for (var index = 0; (index < forms.Count); index++) {
            if (forms[index] != m_forms[index]) { return false; }
        }
        return true;
    }
    // Derives each owner's drawn form from the forms' Drawn flags (the named form where none is drawn, which has nothing to
    // hold), clears every other form's flag, and rebuilds the dormant and held slots from them. A changed dormant set
    // moves the copy layout, so the dresser reads every group again.
    private void PlaceForms() {
        m_formDrawn.Clear();
        for (var index = 0; (index < m_forms.Count); index++) {
            var range = m_forms[index];

            if (!range.Form.Drawn) { continue; }
            if (!m_formDrawn.TryGetValue(key: range.Form.Owner, value: out var drawn) || (range.Named && !m_forms[drawn].Named)) { m_formDrawn[range.Form.Owner] = index; }
        }
        for (var index = 0; (index < m_forms.Count); index++) {
            var range = m_forms[index];

            if (range.Named && m_formDrawn.TryAdd(key: range.Form.Owner, value: index)) { range.Form.Drawn = true; }
        }
        m_placedDormant.Clear();
        m_heldSlots.Clear();
        m_formsHeld = 0;
        for (var index = 0; (index < m_forms.Count); index++) {
            var range = m_forms[index];

            if (m_formDrawn.TryGetValue(key: range.Form.Owner, value: out var drawn) && (drawn == index)) { continue; }
            range.Form.Drawn = false;
            // A form the producer keeps while it plays its transition out draws beside the drawn form: not dormant, not
            // held. Its groups already stood when it last held the draws.
            if (range.Lingers && !range.Named) { continue; }
            for (var slot = range.FirstSlot; (slot < (range.FirstSlot + range.SlotCount)); slot++) { _ = m_placedDormant.Add(item: slot); }
            if (!range.Named) { continue; }
            m_formsHeld++;
            for (var slot = range.FirstSlot; (slot < (range.FirstSlot + range.SlotCount)); slot++) {
                _ = m_heldSlots.Add(item: slot);
                for (var at = m_groups.FirstOf(dynamic: true, key: slot); (at >= 0); at = m_groups.NextOf(index: at)) {
                    var group = m_groups[at];

                    if (group.Done || group.Retired) { continue; }
                    RaiseFirst(group: group);
                    if (group.Leader is { Done: false } leader) { RaiseFirst(group: leader); }
                }
            }
        }
        if (m_placedDormant.SetEquals(other: m_dormantSlots)) { return; }
        m_dormantSlots.Clear();
        m_dormantSlots.UnionWith(other: m_placedDormant);
        m_dormantRevision++;
        m_copyLayoutRevision++;
    }
    // Hands each held named form the draws once it stands (FormStands): its flag is set and its owner's drawn form's
    // cleared, and the next prepared frame places the forms again. Under the gate, on the frame's thread, with the
    // residency's tables in hand (none for an offline bake); it writes the forms' flags alone, since the frame's measure
    // may be reading the bake.
    private void DecideForms(SdfWorldTables? tables) {
        if (m_formsHeld == 0) { return; }
        for (var index = 0; (index < m_forms.Count); index++) {
            var range = m_forms[index];

            if (!range.Named || range.Form.Drawn || !m_formDrawn.TryGetValue(key: range.Form.Owner, value: out var drawn) || (drawn == index)) { continue; }
            if (!FormStands(range: range, tables: tables)) {
                m_formHolds++;
                continue;
            }
            m_forms[drawn].Form.Drawn = false;
            range.Form.Drawn = true;
            m_formDrawn[range.Form.Owner] = index;
            m_formsDecided = true;
            m_formsHanded++;
        }
    }
    // Whether every group riding a form's slots shows what a settled bake of it shows and every piece of what it shows
    // stands in the mesh region (or was refused there, see the invariant; an offline bake has no region), and the program
    // holds at least one such group.
    private bool FormStands(SdfMeshFormRange range, SdfWorldTables? tables) {
        var groups = 0;

        for (var slot = range.FirstSlot; (slot < (range.FirstSlot + range.SlotCount)); slot++) {
            for (var at = m_groups.FirstOf(dynamic: true, key: slot); (at >= 0); at = m_groups.NextOf(index: at)) {
                var group = m_groups[at];

                if (group.Retired) { continue; }
                if (!ShowsProduct(group: group)) { return false; }
                if ((tables is not null) && (group.Shown is { } shown)) {
                    for (var piece = 0; (piece < shown.PieceCount); piece++) {
                        var part = shown.Piece(index: piece);

                        if (!tables.MeshStands(mesh: part) && !tables.RefusesAhead(mesh: part)) { return false; }
                    }
                }
                groups++;
            }
        }
        return (groups > 0);
    }
    // Whether a group rides a form no row of its owner draws.
    private bool Dormant(SdfMeshBakeGroup group) => (group.Dynamic && (m_dormantSlots.Count > 0) && m_dormantSlots.Contains(item: group.Slot));
    // Whether a group rides a named form its owner still holds, which bakes ahead of everything else.
    private bool HeldForm(Group group) => (group.Dynamic && (m_heldSlots.Count > 0) && m_heldSlots.Contains(item: group.Slot));
    // Whether the dormant groups' meshes must be named ahead again: the set moved, or a group's product did while any is
    // dormant.
    private bool DormantNamesMoved() => ((m_dormantNamed != m_dormantRevision) || ((m_dormantSlots.Count > 0) && (m_dormantNamedProduct != m_productRevision)));
    // Appends the pieces of every dormant group's shown mesh, so the region keeps a form that draws nothing landed for
    // the switch to it.
    private void AppendDormantMeshes(List<SdfMesh> meshes) {
        m_dormantNamed = m_dormantRevision;
        m_dormantNamedProduct = m_productRevision;
        if (m_dormantSlots.Count == 0) { return; }
        foreach (var slot in m_dormantSlots) {
            for (var at = m_groups.FirstOf(dynamic: true, key: slot); (at >= 0); at = m_groups.NextOf(index: at)) {
                var group = m_groups[at];

                if (group.Retired || (group.Shown is not { } shown)) { continue; }
                for (var piece = 0; (piece < shown.PieceCount); piece++) { meshes.Add(item: shown.Piece(index: piece)); }
            }
        }
    }
    // Describes the forms: the owners listed, those holding their named form, the hand-overs and holding decisions, and
    // the dormant groups with the distinct meshes they keep resident and those meshes' bytes, an upper bound of what the
    // dormant forms add, since a mesh a drawn group shares costs nothing more. Under the gate.
    private void DescribeForms(StringBuilder line) {
        var dormantGroups = 0;
        var dormantBytes = 0L;
        var meshes = new HashSet<SdfMesh>(comparer: ReferenceEqualityComparer.Instance);

        foreach (var slot in m_dormantSlots) {
            for (var at = m_groups.FirstOf(dynamic: true, key: slot); (at >= 0); at = m_groups.NextOf(index: at)) {
                var group = m_groups[at];

                if (group.Retired) { continue; }
                dormantGroups++;
                if ((group.Shown is { } shown) && meshes.Add(item: shown)) { dormantBytes += SdfMeshRegion.PlacedBytesOf(mesh: shown); }
            }
        }
        _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" forms={m_formDrawn.Count} forms-held={m_formsHeld} forms-handed={m_formsHanded} form-holds={m_formHolds} forms-dormant-groups={dormantGroups} forms-dormant-meshes={meshes.Count} forms-dormant-bytes={dormantBytes}B");
    }
}
