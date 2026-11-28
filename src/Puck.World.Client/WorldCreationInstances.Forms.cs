using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

// A body's forms: the creations its look can wear (its own body, or that body in a suit), each a registration of its
// own on slots of its own, all of one body naming one SdfMeshForm owner. The registration its look wears is named
// (m_bodySlots); the bake draws one form of the owner (SdfMeshPathBake's forms: the named one once every group of it has
// landed, the one it drew until then) and answers which through SdfMeshForm.Drawn.
//
// Which forms a body keeps (ReconcileBodies): the named one; every form the bake still draws; and, while the body is near
// a view (m_formsWanted), every other form its look's selectors can choose, so a switch finds the new form baked and
// resident at the tier the body's demand asks and draws it on the next frame. A form neither named, drawn nor wanted
// leaves at the next reconcile, which TryTakeFormsMove asks for.
//
// The prewarm gate reads the views the pool rates its bodies by (RateBy): a body takes its other forms while it would
// pose every frame at a stride's part speed in some view (SdfUpdateRateTiers.BodyHertz at EveryFrameHertz) and lets them
// go once it would pose at FormLetGoHertz or slower, so a crowd costs only its nearest bodies' forms. A bounded share of
// the switching bodies is judged each frame (FormGateScan); a pool that knows no view judges none.
//
// A form neither named nor drawn rests (Rests): its slots hold the rest pose on the body's root, which is all its bake
// reads (the demand box rides the slot; a skin bakes at rest), so it costs no drivers, effectors, cues or rates. One that
// stops resting poses in full at once, as a registration new to its pool entry does.
public sealed partial class WorldCreationInstances {
    // The switching bodies the prewarm gate judges each frame, and the tier at or under which a body near no view lets
    // its other forms go.
    private const int FormGateScan = 64;
    private const int FormLetGoHertz = 15;

    // The owner every form of a body names; the pool indices of each body's forms other than the named one; the bodies
    // whose other forms the pool keeps; the bodies wearing a look its selectors switch from, which the gate judges, and
    // the next of them to judge; the bodies listed in m_bodyForms in body order; and the ranges the latest pack published.
    private readonly Dictionary<int, object> m_formOwners = [];
    private readonly Dictionary<int, List<int>> m_bodyForms = [];
    private readonly HashSet<int> m_formsWanted = [];
    private readonly List<int> m_switchingBodies = [];
    private int m_switchingScan;
    private readonly List<int> m_formBodies = [];
    private readonly List<SdfMeshFormRange> m_formRanges = [];
    private bool m_formRangesDirty = true;
    private int m_formRangesSlotBase = -1;

    /// <summary>Gets the slots each form of every body with several forms rides, as the latest
    /// <see cref="PackTransforms"/> placed them (<see cref="ISdfSceneEmitter.MeshForms"/>): the named form first, then
    /// the rest, body by body. The same list for as long as the pool lives, rewritten by a pack after a reconcile or a
    /// moved slot base.</summary>
    public IReadOnlyList<SdfMeshFormRange> MeshForms => m_formRanges;

    /// <summary>Returns whether the pool's bodies' forms must be reconciled again: a body crossed the prewarm gate, or a
    /// body keeps a form that is neither named, drawn nor wanted. Also wakes each form whose drawn flag the bake moved, so
    /// the next pack poses it as what it now is. Called once a frame by the owning emitter before it decides whether to
    /// reconcile; a <see langword="true"/> answer owes a reconcile and a program rebuild.</summary>
    /// <returns><see langword="true"/> when the forms the pool keeps must change.</returns>
    public bool TryTakeFormsMove() {
        var moved = false;

        foreach (var body in m_formBodies) {
            if (TryFindBody(bodyIndex: body, live: out var named, poolIndex: out var namedIndex)) { NoteDrawn(index: namedIndex, live: named); }
            if (!m_bodyForms.TryGetValue(key: body, value: out var forms)) { continue; }
            foreach (var index in forms) {
                if (m_pool[index] is not { Form: { } form } live) { continue; }
                NoteDrawn(index: index, live: live);
                moved |= (!form.Drawn && !live.FormLingers && !m_formsWanted.Contains(item: body));
            }
        }
        if ((m_rateViews.Count == 0) || (m_switchingBodies.Count == 0)) { return moved; }

        var count = Math.Min(val1: FormGateScan, val2: m_switchingBodies.Count);

        for (var judged = 0; (judged < count); judged++) {
            m_switchingScan = ((m_switchingScan + 1) % m_switchingBodies.Count);

            var body = m_switchingBodies[m_switchingScan];

            if (!TryFindBody(bodyIndex: body, live: out var named, poolIndex: out _) || (named.PackedRoot is not { } root)) { continue; }

            var hertz = SdfUpdateRateTiers.BodyHertz(pixelsPerMetre: BodyPixelsPerMetre(center: root.Position, radius: RateReach(live: named, placementScale: root.Scale)), shownElsewhere: false);

            if (m_formsWanted.Contains(item: body) ? (hertz <= FormLetGoHertz) : (hertz >= SdfUpdateRateTiers.EveryFrameHertz)) {
                moved = true;
                if (!m_formsWanted.Remove(item: body)) { _ = m_formsWanted.Add(item: body); }
            }
        }
        return moved;
    }
    // Wakes a form whose drawn flag moved since the pool last acted on it.
    private void NoteDrawn(int index, Registration live) {
        if ((live.Form is not { } form) || (form.Drawn == live.FormDrawnSeen)) { return; }
        live.FormDrawnSeen = form.Drawn;
        WakeEntry(index: index);
    }
    // Whether a registration is a form its body neither wears nor is drawn in, which holds the rest pose on the root.
    private static bool Rests(Registration live) => ((live.Form is { Drawn: false }) && !live.FormNamed && !live.FormLingers);
    // Whether a registration's geometry is drawn: a row's, a body's form the bake draws, or one kept drawn while its leave
    // effect plays. A form drawn by no row carries no volume, mesh draw or door clip, so nothing the frame shows of it
    // precedes its geometry.
    private static bool Shows(Registration live) => ((live.Form?.Drawn ?? true) || live.FormLingers);

    // Reconciles every body's forms against the census: a body that left releases every form; a body's look's creation
    // names the form it wears (an existing one, updated in place, or a new registration); every other form stays while
    // the bake draws it or the body is near a view and its selectors can choose it, and leaves otherwise; and a body near
    // a view registers each such form it lacks.
    private void ReconcileBodies(IReadOnlyList<BodyStamp> bodyStamps, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<DynamicsRow> dynamics, IReadOnlyList<WorldLook> looks, WorldRowAssignment? lookAssignment) {
        var stamps = new Dictionary<int, BodyStamp>(capacity: bodyStamps.Count);

        foreach (var stamp in bodyStamps) { _ = stamps.TryAdd(key: stamp.BodyIndex, value: stamp); }
        for (var index = 0; (index < m_poolCount); index++) {
            if ((m_pool[index] is { BodyIndex: { } body }) && !stamps.ContainsKey(key: body)) {
                Release(index: index);
                if (body < m_bodySlots.Length) { m_bodySlots[body] = 0; }
            }
        }
        foreach (var body in m_formOwners.Keys.ToArray()) {
            if (stamps.ContainsKey(key: body)) { continue; }
            _ = m_formOwners.Remove(key: body);
            _ = m_bodyForms.Remove(key: body);
            _ = m_formsWanted.Remove(item: body);
        }

        var switching = SwitchingLooks(assignment: lookAssignment, creations: creations, looks: looks);
        var kept = new List<int>();

        m_switchingBodies.Clear();
        m_formBodies.Clear();
        foreach (var (body, stamp) in stamps) {
            var owner = (m_formOwners.TryGetValue(key: body, value: out var known) ? known : (m_formOwners[body] = new object()));
            var others = (m_bodyForms.Remove(key: body, value: out var held) ? held : []);
            var worn = -1;

            if (TryFindBody(bodyIndex: body, live: out var named, poolIndex: out var namedIndex)) {
                if (SameCreation(live: named, creation: stamp.Creation)) { worn = namedIndex; } else { others.Add(item: namedIndex); }
            }
            for (var at = 0; ((worn < 0) && (at < others.Count)); at++) {
                if (SameCreation(live: m_pool[others[at]]!, creation: stamp.Creation)) {
                    worn = others[at];
                    others.RemoveAt(index: at);
                }
            }
            if (worn < 0) {
                worn = Admit(dynamics: dynamics, owner: owner, stamp: stamp);
            } else {
                Wear(dynamics: dynamics, live: m_pool[worn]!, stamp: stamp);
            }

            var wearing = m_pool[worn]!;

            wearing.Form ??= new SdfMeshForm(Owner: owner);
            wearing.FormNamed = true;
            ForgetLinger(live: wearing);
            ArrayCapacity.EnsureLength(array: ref m_bodySlots, required: (body + 1));
            m_bodySlots[body] = (worn + 1);

            // The forms the body's selectors can choose besides the one it wears, by creation, kept only near a view.
            var choices = Choices(stamp: stamp, switching: switching);

            if (choices.Count > 0) { m_switchingBodies.Add(item: body); }
            if (!m_formsWanted.Contains(item: body)) { choices.Clear(); }
            kept.Clear();
            foreach (var index in others) {
                var form = m_pool[index]!;

                // A form that loses the name while drawn lingers if its look plays a leave effect, so the bake keeps it
                // drawn past the hand-over (SdfMeshFormRange.Lingers) until the effect has played.
                if (form.FormNamed && (form.Form?.Drawn == true) && (form.Look?.Transition?.Leave is not null)) { form.FormLingers = true; }
                form.FormNamed = false;
                if (choices.Remove(key: form.Creation.Hash, value: out var choice)) {
                    Wear(dynamics: dynamics, live: form, stamp: choice);
                    kept.Add(item: index);
                } else if ((form.Form?.Drawn == true) || form.FormLingers) {
                    kept.Add(item: index);
                } else {
                    Release(index: index);
                }
            }
            foreach (var choice in choices.Values) {
                var index = Admit(dynamics: dynamics, owner: owner, stamp: choice);

                m_pool[index]!.FormNamed = false;
                kept.Add(item: index);
            }

            // A body drawn in no form it keeps has nothing to hold, so the form it wears is drawn.
            var drawn = (wearing.Form.Drawn || kept.Exists(match: index => (m_pool[index]!.Form?.Drawn == true)));

            if (!drawn) { wearing.Form.Drawn = true; }
            if (kept.Count == 0) { continue; }
            m_bodyForms[body] = [.. kept];
            m_formBodies.Add(item: body);
        }
        m_formBodies.Sort();
        m_switchingScan = 0;
        m_formRangesDirty = true;
    }
    // Registers a new form of a body in a free pool entry, drawn by nothing yet, and returns its pool index.
    private int Admit(BodyStamp stamp, object owner, IReadOnlyList<DynamicsRow> dynamics) {
        var fresh = RegisterBody(stamp: stamp);
        var index = FreeSlot();

        ApplyMotion(dynamics: dynamics, live: fresh, motion: stamp.Look.Motion);
        fresh.Form = new SdfMeshForm(Owner: owner);
        m_pool[index] = fresh;
        return index;
    }
    // Updates a kept form in place from the stamp it now stands for: a same-content edit, or a form a switch named.
    private static void Wear(Registration live, BodyStamp stamp, IReadOnlyList<DynamicsRow> dynamics) {
        live.Creation = stamp.Creation;
        live.Scale = stamp.Scale;
        LatchLook(live: live, stamp: stamp);
        ApplyMotion(dynamics: dynamics, live: live, motion: stamp.Look.Motion);
    }
    // Releases a pool entry's registration and its reads.
    private void Release(int index) {
        m_pool[index]!.Reads.Release();
        m_pool[index] = null;
        m_freeSlots.Push(item: index);
    }
    private static bool SameCreation(Registration live, WorldPrototype creation) => SameHash(creation: live.Creation, other: creation);

    // The looks an assignment's selectors switch between, by name (every row a case names and every row its sequence draws
    // from by literal name), each with the creation it wears, or null where the pool would not draw it as a form (a
    // catalog look, a rigid creation, which the rigid catalog copies, or a creation the document lacks). A body wearing
    // one of them can be switched to any other.
    private static Dictionary<string, (WorldLook Look, WorldPrototype? Creation)> SwitchingLooks(IReadOnlyList<WorldLook> looks, IReadOnlyList<WorldPrototype> creations, WorldRowAssignment? assignment) {
        var switching = new Dictionary<string, (WorldLook Look, WorldPrototype? Creation)>(comparer: StringComparer.Ordinal);

        if ((assignment is null) || (assignment.Selectors.Count == 0)) { return switching; }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var selector in assignment.Selectors) {
            foreach (var choice in selector.Cases) { _ = names.Add(item: choice.Row); }
        }
        foreach (var row in assignment.Rows) {
            if (row.Reference is null) { _ = names.Add(item: row.Value); }
        }
        foreach (var look in looks) {
            if ((look.Name.Reference is not null) || !names.Contains(item: look.Name.Value)) { continue; }

            var creation = (((look.Source is WorldLookSource.Creation { PrototypeId.Reference: null } source) &&
                (WorldDefinitionRows.FindCreation(creations: creations, id: source.PrototypeId.Value) is { } found) && !WorldRigidAssets.IsRigid(creation: found, look: look))
                ? found
                : null);

            switching[look.Name.Value] = (look, creation);
        }
        return switching;
    }
    // The forms a body's selectors can choose besides the one it wears, by creation hash, each as the stamp the body would
    // wear it as: the look's scale in place of the worn look's, the body's live scale kept. None for a body wearing a look
    // the selectors do not switch from.
    private static Dictionary<string, BodyStamp> Choices(BodyStamp stamp, Dictionary<string, (WorldLook Look, WorldPrototype? Creation)> switching) {
        var choices = new Dictionary<string, BodyStamp>(comparer: StringComparer.Ordinal);

        if ((switching.Count == 0) || (stamp.Look.Name.Reference is not null) || !switching.ContainsKey(key: stamp.Look.Name.Value)) { return choices; }

        var unscaled = ((stamp.Look.Scale != 0f) ? (stamp.Scale / stamp.Look.Scale) : stamp.Scale);

        foreach (var (look, creation) in switching.Values) {
            if ((creation is null) || SameHash(creation: creation, other: stamp.Creation)) { continue; }
            _ = choices.TryAdd(key: creation.Hash, value: new BodyStamp(BodyIndex: stamp.BodyIndex, Creation: creation, Look: look, Scale: (unscaled * look.Scale)));
        }
        return choices;
    }
    private static bool SameHash(WorldPrototype creation, WorldPrototype other) => string.Equals(a: creation.Hash, b: other.Hash, comparisonType: StringComparison.Ordinal);

    // Publishes each body's forms' slots once a reconcile or the slot base moved them, the named form first.
    private void PublishForms(int slotBase) {
        if (!m_formRangesDirty && (slotBase == m_formRangesSlotBase)) { return; }
        m_formRangesDirty = false;
        m_formRangesSlotBase = slotBase;
        m_formRanges.Clear();
        foreach (var body in m_formBodies) {
            if (!TryFindBody(bodyIndex: body, live: out var named, poolIndex: out var namedIndex) || (named.Form is not { } namedForm)) { continue; }
            m_formRanges.Add(item: new SdfMeshFormRange(Form: namedForm, FirstSlot: (slotBase + RootSlotOffset(index: namedIndex)), Named: true, SlotCount: SlotsAt(index: namedIndex)));
            foreach (var index in m_bodyForms[body]) {
                if (m_pool[index] is { Form: { } form } other) { m_formRanges.Add(item: new SdfMeshFormRange(Form: form, FirstSlot: (slotBase + RootSlotOffset(index: index)), Named: false, SlotCount: SlotsAt(index: index), Lingers: other.FormLingers)); }
            }
        }
    }
    // The first pass for a resting form: it holds the rest pose on its body's root (every slot the root's pose at the
    // registration's scale), rewritten only when the root moves or the slots changed owner.
    private void RestStep(int index, Registration live, ref PackStep step, SdfMovedTransforms moved, Vector3 parkPosition, Span<DynamicTransform> transforms) {
        var rootSlot = step.RootSlot;
        var packedBefore = (live.Rested && ReferenceEquals(objA: m_packedRegistrations[index], objB: live));

        if (!packedBefore && !moved.Everything) {
            WorldTransformOwners.Park(count: (step.Slots - step.PackedSlots), moved: moved, parkPosition: parkPosition, start: (rootSlot + step.PackedSlots), table: transforms);
        }
        live.Rested = true;
        live.RateOwed = false;
        m_packedRegistrations[index] = live;
        if (!m_owners.Wake(deltaSeconds: 0f, discontinuity: !packedBefore, moved: moved, orientation: step.RootRotation, owner: index, position: step.RootPosition) &&
            (transforms[rootSlot].Scale == step.PlacementScale)) {
            step.Stage = PackStage.Retained;
            return;
        }

        var rest = new DynamicTransform(Orientation: step.RootRotation, Position: step.RootPosition) { Scale = step.PlacementScale };
        var changed = false;

        for (var slot = rootSlot; (slot < (rootSlot + step.PackedSlots)); slot++) { changed |= SdfMovedTransforms.Write(slot: slot, table: transforms, value: rest); }
        step.SlotsMoved = changed;
        step.Stage = PackStage.Rested;
    }
    // The last pass for a resting form: settles its slots with the moved set and withdraws what a drawn registration
    // shows beside its geometry (volumes, door clip, mesh draw, a held pose).
    private void EndRest(int index, Registration live, ref PackStep step, SdfMovedTransforms moved, float deltaSeconds, Span<DynamicTransform> transforms) {
        var rootSlot = step.RootSlot;

        if (step.Stage == PackStage.Rested) {
            if (step.SlotsMoved) { m_packPoseMoved = true; }
            m_owners.Settle(deltaSeconds: deltaSeconds, moved: moved.Commit(count: step.PackedSlots, moved: step.SlotsMoved, reseat: m_owners.Reseats(owner: index), start: rootSlot), owner: index);
        }
        live.PackedRoot = transforms[rootSlot];
        if (m_hasAnyVolumes && m_volumeActive[index]) {
            m_volumeActive[index] = false;
            m_packVolumesChanged = true;
        }
        SetClip(clip: null, index: index, live: live);
        SetMeshDraw(draw: null, index: index);
        Unhold(index: index);
        live.VisitPack = m_packCount;
    }
}
