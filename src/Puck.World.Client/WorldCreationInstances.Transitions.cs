using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

// A body's form transitions (WorldFormTransition), played across a form switch. When the bake hands a body's draws to its
// newly named form (SdfMeshPathBake's forms), the pool sees the drawn form change (m_formDrawn) and begins the new form's
// look's arrive effect and the old form's leave effect at once. The old form was marked lingering when it lost the name
// (Forms.cs's reconcile: FormLingers, published as SdfMeshFormRange.Lingers), so the bake keeps drawing it beside the new
// one; it rests on nothing, keeps its pose, and is let go (ForgetLinger) once its effect has played, when the next
// reconcile releases it as any undrawn form. A whole form sweeps under one band in its root's frame; a form played in
// pieces moves each slot with WorldFormPieces, which writes its slots after the pool's own pack each frame.
//
// The invariant: a form that lingers is kept by the reconcile and drawn by the bake until ForgetLinger runs, and
// ForgetLinger is the one door that clears FormLingers; it also ends the form's leave play and wakes the registration so
// its next pack writes over whatever its pieces left in its slots. A form named again stops lingering through the same
// door (the reconcile calls it for the form a body wears), so a switch back mid-effect draws the form whole at once.
public sealed partial class WorldCreationInstances {
    // A form's play: its effect, whether it arrives, its seed, when it began on the pool's form clock, the sweep's
    // extent along its direction in creation units, and its pieces when it plays in pieces.
    private sealed class FormPlay {
        public required WorldFormEffect Effect;
        public required bool Arriving;
        public required uint Seed;
        public required double Start;
        public float Low;
        public float High;
        public WorldFormPieces? Pieces;
        public int Index;
    }

    // The pool's form clock (presentation seconds the packs advanced), the form each body was last seen drawn in, each
    // playing form's play, the plays begun so far (each play's seed counts it), and the bands the latest pack published.
    private double m_formClock;
    private readonly Dictionary<int, Registration> m_formDrawn = [];
    private readonly Dictionary<Registration, FormPlay> m_formPlays = new(comparer: ReferenceEqualityComparer.Instance);
    private long m_formPlaysBegun;
    private IReadOnlyList<SdfFormBand> m_poolBands = [];
    private readonly List<SdfFormBand> m_bandScratch = [];
    private readonly List<Registration> m_formEnded = [];
    private IReadOnlyList<SdfFormBand> m_publishedBands = [];
    private long m_publishedBandsPack = -1L;

    /// <summary>Gets the form bands the latest pack found sweeping the pool's forms and its rigid copies
    /// (<see cref="ISdfSceneEmitter.FormBands"/>), in absolute slots: a new list whenever a band moved, the one empty list
    /// while no form changes. A rigid copy's band takes the root slot the composition seated it at this frame.</summary>
    public IReadOnlyList<SdfFormBand> FormBands {
        get {
            var rigid = Rigid.FormBands();

            if (rigid.Count == 0) { return m_poolBands; }
            if (m_poolBands.Count == 0) { return rigid; }
            if (m_publishedBandsPack != m_packCount) {
                m_publishedBands = [.. m_poolBands, .. rigid];
                m_publishedBandsPack = m_packCount;
            }
            return m_publishedBands;
        }
    }

    // Stops a form lingering: ends its leave play, publishes its range without the mark so the bake lets it go, and wakes
    // it so its next pack writes its own pose over its pieces'.
    private void ForgetLinger(Registration live) {
        if (!live.FormLingers) { return; }
        live.FormLingers = false;
        m_formRangesDirty = true;
        if (m_formPlays.Remove(key: live, value: out var play) && (play.Index < m_poolCount) && ReferenceEquals(objA: m_pool[play.Index], objB: live)) { WakeEntry(index: play.Index); }
    }

    // Finds each body's drawn form, begins the plays a hand-over owes, steps every play, writing its pieces' slots and
    // publishing its bands, and ends the plays that finished. Runs once a pack, after the pool's slots are written.
    private void StepFormTransitions(DynamicTransform[] transforms, IWorldStampSource client, SdfMovedTransforms moved, int slotBase, float deltaSeconds) {
        m_formClock += Math.Max(val1: deltaSeconds, val2: 0f);
        foreach (var body in m_formBodies) {
            if (!TryFindBody(bodyIndex: body, live: out var named, poolIndex: out var namedIndex)) { continue; }

            var (drawn, drawnIndex) = ((named.Form?.Drawn == true) ? (named, namedIndex) : (null, -1));

            if (drawn is null) {
                foreach (var index in m_bodyForms[body]) {
                    if (m_pool[index] is { Form.Drawn: true } other) { (drawn, drawnIndex) = (other, index); break; }
                }
            }
            if (drawn is null) { continue; }
            if (m_formDrawn.TryGetValue(key: body, value: out var before) && !ReferenceEquals(objA: before, objB: drawn)) {
                if (drawn.Look?.Transition?.ArriveFrom(look: before.Look?.Name.Value) is { } arrive) { BeginPlay(live: drawn, index: drawnIndex, effect: arrive, arriving: true, transforms: transforms, rootSlot: (slotBase + RootSlotOffset(index: drawnIndex))); }
                if (before.FormLingers && (before.Look?.Transition?.LeaveTo(look: drawn.Look?.Name.Value) is { } leave) && (IndexOf(live: before) is var beforeIndex and >= 0)) {
                    BeginPlay(live: before, index: beforeIndex, effect: leave, arriving: false, transforms: transforms, rootSlot: (slotBase + RootSlotOffset(index: beforeIndex)));
                }
            }
            m_formDrawn[body] = drawn;
        }
        foreach (var body in m_formDrawn.Keys.Where(predicate: body => !m_formBodies.Contains(item: body) && !TryFindBody(bodyIndex: body, live: out _, poolIndex: out _)).ToArray()) {
            _ = m_formDrawn.Remove(key: body);
        }

        var hadBands = (m_poolBands.Count > 0);

        m_bandScratch.Clear();
        m_formEnded.Clear();
        foreach (var (live, play) in m_formPlays) {
            if ((play.Index >= m_poolCount) || !ReferenceEquals(objA: m_pool[play.Index], objB: live)) {
                m_formEnded.Add(item: live);
                continue;
            }

            var elapsed = (float)(m_formClock - play.Start - Math.Max(val1: play.Effect.Delay, val2: 0f));
            var rootSlot = (slotBase + RootSlotOffset(index: play.Index));
            var root = transforms[rootSlot];

            if (play.Pieces is { } pieces) {
                pieces.Step(bands: m_bandScratch, deltaSeconds: deltaSeconds, elapsed: elapsed, field: client.StaticField, firstSlot: rootSlot, moved: moved, root: in root, table: transforms);
                if (elapsed > pieces.Length) { m_formEnded.Add(item: live); }
            } else {
                m_bandScratch.Add(item: WorldFormEffects.Band(effect: play.Effect, arriving: play.Arriving, seed: play.Seed, firstSlot: rootSlot, slotCount: SlotsAt(index: play.Index),
                    origin: root.Position, rotation: root.Orientation, low: (play.Low * root.Scale), high: (play.High * root.Scale), elapsed: elapsed, parted: false));
                if (elapsed > (play.Effect.TotalSeconds - Math.Max(val1: play.Effect.Delay, val2: 0f))) { m_formEnded.Add(item: live); }
            }
        }
        foreach (var live in m_formEnded) {
            if (live.FormLingers) {
                ForgetLinger(live: live);
            } else if (m_formPlays.Remove(key: live, value: out var play) && (play.Index < m_poolCount) && ReferenceEquals(objA: m_pool[play.Index], objB: live)) {
                WakeEntry(index: play.Index);
            }
        }
        if ((m_bandScratch.Count > 0) || hadBands) { m_poolBands = ((m_bandScratch.Count == 0) ? [] : [.. m_bandScratch]); }
    }
    // Begins a form's play: its seed, its sweep's extent along its direction at rest, and its pieces, each slot's reach and
    // centre measured from the shapes riding it as the form stands now (their composed poses, and the slots' poses in the
    // table the pool just wrote).
    private void BeginPlay(Registration live, int index, WorldFormEffect effect, bool arriving, DynamicTransform[] transforms, int rootSlot) {
        var direction = WorldFormEffects.Direction(effect: effect);
        var (low, high) = WorldFormEffects.Extent(direction: direction, document: live.Creation.EngineDocument);
        var play = new FormPlay {
            Effect = effect,
            Arriving = arriving,
            Seed = WorldFormEffects.Seed(body: (live.BodyIndex ?? -1), creation: live.Creation.Hash, ordinal: m_formPlaysBegun++),
            Start = m_formClock,
            Low = low,
            High = high,
            Index = index,
        };

        if (effect.Pieces) {
            var slots = live.Slots;
            var radii = new float[slots];
            var centres = new Vector3[slots];
            var outward = new Vector3[slots];
            var sums = new Vector3[slots];
            var counts = new int[slots];
            var reaches = ShapeReachesOf(live: live);
            var shapeSlots = live.Motion.ShapeSlots;
            var scale = live.Scale;
            var rootPose = transforms[rootSlot];

            for (var shape = 0; (shape < Math.Min(val1: shapeSlots.Length, val2: live.ShapeTransforms.Length)); shape++) {
                var slot = shapeSlots[shape];

                if ((slot < 0) || (slot >= slots) || (reaches[shape] < 0f)) { continue; }
                sums[slot] += live.ShapeTransforms[shape].Position;
                counts[slot]++;
            }
            for (var slot = 0; (slot < slots); slot++) {
                if (counts[slot] > 0) { sums[slot] /= counts[slot]; }
            }
            for (var shape = 0; (shape < Math.Min(val1: shapeSlots.Length, val2: live.ShapeTransforms.Length)); shape++) {
                var slot = shapeSlots[shape];

                if ((slot < 0) || (slot >= slots) || (reaches[shape] < 0f)) { continue; }
                radii[slot] = MathF.Max(x: radii[slot], y: (Vector3.Distance(value1: live.ShapeTransforms[shape].Position, value2: sums[slot]) + (reaches[shape] * scale)));
            }
            // Each piece's centre in its slot's frame, and its way out from the root in the root's frame.
            var inverseRoot = Quaternion.Inverse(value: rootPose.Orientation);

            for (var slot = 0; (slot < slots); slot++) {
                if ((counts[slot] == 0) || ((rootSlot + slot) >= transforms.Length)) { continue; }

                var slotPose = transforms[(rootSlot + slot)];

                centres[slot] = Vector3.Transform(value: (sums[slot] - slotPose.Position), rotation: Quaternion.Inverse(value: slotPose.Orientation));
                outward[slot] = Vector3.Transform(value: (sums[slot] - rootPose.Position), rotation: inverseRoot);
            }
            play.Pieces = new WorldFormPieces(effect: effect, arriving: arriving, seed: play.Seed, radii: radii, centres: centres, outward: outward);
        }
        m_formPlays[live] = play;
        WakeEntry(index: index);
    }
    // The pool index holding a registration, or -1.
    private int IndexOf(Registration live) {
        for (var index = 0; (index < m_poolCount); index++) {
            if (ReferenceEquals(objA: m_pool[index], objB: live)) { return index; }
        }
        return -1;
    }
}
