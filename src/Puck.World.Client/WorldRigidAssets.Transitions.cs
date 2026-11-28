using Puck.SdfVm;
using Puck.World.Protocol;

namespace Puck.World.Client;

// A rigid body's form transitions (WorldFormTransition) across its arrival and its leaving: a body a board materializes
// (the holodeck pool), a card a game deals. A body new to its copy (a new occupant of its population slot, which
// RootMotion.Occupant tells from the one before) plays its look's arrive effect, once the copies have packed once, so the
// bodies a world boots with or a client first receives stand at once. A body that leaves (its slot emptied, or taken by
// another occupant) and whose look plays a leave effect leaves a remnant: a copy of its own at its last pose, appended
// after the static copies, which plays the effect and is removed when it has played. A remnant names no body, so nothing
// that resolves a body reaches it.
//
// The invariant: the copies list is the bodies, then the static copies, then the remnants; SeatRemnants is the one door
// that lays the remnants at its tail, and every change to the remnants goes through it and moves the copies' revision, so
// the composition reads every copy again.
public sealed partial class WorldRigidAssets {
    // A copy's play: the body whose copy it plays on, or the remnant it plays on (whose copy goes when it ends), its
    // effect, whether it arrives, its seed, when it began on the copies' clock and its sweep's extent along its direction in
    // creation units.
    private sealed class FormPlay {
        public int Body = -1;
        public SdfMeshInstance? Remnant;
        public required WorldFormEffect Effect;
        public required bool Arriving;
        public required uint Seed;
        public required double Start;
        public float Low;
        public float High;
    }

    private double m_formClock;
    private bool m_formsArmed;
    private long m_formPlaysBegun;
    private readonly List<FormPlay> m_formPlays = [];
    private readonly List<SdfMeshInstance> m_remnants = [];
    private SdfFormBand[] m_formBands = [];
    private long m_formBandsSerial = -1L;
    private long m_formSerial;

    /// <summary>Returns the bands the rigid copies' plays sweep this frame, each over its copy's root slot as the
    /// composition seated it: the same list until the next pack, a new one after a pack that moved any, and the one empty
    /// list while nothing plays.</summary>
    /// <returns>The bands.</returns>
    public IReadOnlyList<SdfFormBand> FormBands() {
        if (m_formPlays.Count == 0) { return []; }
        if (m_formBandsSerial == m_formSerial) { return m_formBands; }

        var bands = new List<SdfFormBand>(capacity: m_formPlays.Count);

        foreach (var play in m_formPlays) {
            if ((play.Remnant ?? CopyOf(body: play.Body)) is not { FrameSlot: > 0 } copy) { continue; }

            var root = copy.Transform;
            var scale = copy.Scale;

            bands.Add(item: WorldFormEffects.Band(effect: play.Effect, arriving: play.Arriving, seed: play.Seed, firstSlot: copy.FrameSlot, slotCount: 1, origin: root.Position,
                rotation: root.Orientation, low: (play.Low * scale), high: (play.High * scale), elapsed: Elapsed(play: play), parted: false));
        }
        m_formBands = [.. bands];
        m_formBandsSerial = m_formSerial;
        return m_formBands;
    }
    // The seconds a play has run, its delay past.
    private float Elapsed(FormPlay play) => (float)(m_formClock - play.Start - Math.Max(val1: play.Effect.Delay, val2: 0f));

    // Begins a play on a body's copy or a remnant: its seed and its sweep's extent along its direction at rest.
    private void BeginPlay(SdfMeshInstance? remnant, WorldPrototype creation, int body, WorldFormEffect effect, bool arriving) {
        var (low, high) = WorldFormEffects.Extent(direction: WorldFormEffects.Direction(effect: effect), document: creation.EngineDocument);

        m_formPlays.Add(item: new FormPlay {
            Body = ((remnant is null) ? body : -1),
            Remnant = remnant,
            Effect = effect,
            Arriving = arriving,
            Seed = WorldFormEffects.Seed(body: body, creation: creation.Hash, ordinal: m_formPlaysBegun++),
            Start = m_formClock,
            Low = low,
            High = high,
        });
        m_formSerial++;
    }
    // Leaves a remnant of a body's copy at its last pose, playing its look's leave effect: a new copy holding what the
    // body's copy held, naming no body.
    private void LeaveRemnant(SdfMeshInstance held, WorldPrototype creation, int body, WorldFormEffect leave) {
        var remnant = new SdfMeshInstance(AssetSlot: held.AssetSlot, Identity: new WorldPickTarget(BodyIndex: null, Placement: null) { Prototype = creation.Id }, Transform: held.Transform, Scale: held.Scale, IsDynamic: true);

        _ = remnant.Update(assetSlot: held.AssetSlot, identity: remnant.Identity, motionIdentity: held.MotionIdentity, transform: held.Transform, scale: held.Scale, isDynamic: true, clip: null,
            mesh: held.Mesh, material: held.Material, otherAssetSlots: held.OtherAssetSlots, usesRootMaterialFrame: held.UsesRootMaterialFrame, templateRange: held.TemplateRange, audience: held.Audience);
        m_remnants.Add(item: remnant);
        BeginPlay(arriving: false, body: body, creation: creation, effect: leave, remnant: remnant);
        SeatRemnants();
    }
    // Lays the remnants at the copies' tail, after the bodies and the static copies, and moves the copies' revision.
    private void SeatRemnants() {
        var fixedCopies = (m_bodies.Count + m_staticCopies.Count);

        if (m_copies.Count > fixedCopies) { m_copies.RemoveRange(index: fixedCopies, count: (m_copies.Count - fixedCopies)); }
        m_copies.AddRange(collection: m_remnants);
        CopiesChanged();
    }
    // Advances the copies' clock and ends the plays that have played, removing their remnants.
    private void StepFormPlays(float deltaSeconds) {
        m_formClock += Math.Max(val1: deltaSeconds, val2: 0f);
        if (m_formPlays.Count == 0) { return; }
        m_formSerial++;

        var removed = false;

        for (var index = (m_formPlays.Count - 1); (index >= 0); index--) {
            var play = m_formPlays[index];

            if (Elapsed(play: play) <= (play.Effect.TotalSeconds - Math.Max(val1: play.Effect.Delay, val2: 0f))) { continue; }
            m_formPlays.RemoveAt(index: index);
            if (play.Remnant is { } remnant) { removed |= m_remnants.Remove(item: remnant); }
        }
        if (removed) { SeatRemnants(); }
    }
    // Notes the occupant a body's copy is packed for: a new occupant arriving (once the copies have packed once) plays its
    // look's arrive effect, and the occupant it replaced at the same population slot leaves a remnant if its look plays a
    // leave effect. Called by PackBody before it rewrites the copy, which still holds the replaced occupant's row.
    private void NoteOccupant(RootMotion motion, WorldCreationInstances.BodyStamp body, WorldEntityAddress address, SdfMeshInstance copy) {
        if (motion.Occupant is { } occupant && (occupant == address)) { return; }

        if ((motion.Occupant is not null) && (motion.Occupied is { } before) && (before.Look.Transition?.Leave is { } leave)) {
            LeaveRemnant(body: before.BodyIndex, creation: before.Creation, held: copy, leave: leave);
        }
        // A play the slot's previous occupant began is not the new one's.
        _ = m_formPlays.RemoveAll(match: play => (play.Body == body.BodyIndex));
        motion.Occupant = address;
        motion.Occupied = body;
        if (m_formsArmed && (body.Look.Transition?.Arrive is { } arrive)) {
            BeginPlay(arriving: true, body: body.BodyIndex, creation: body.Creation, effect: arrive, remnant: null);
        }
    }
    // Leaves a remnant of each body the census no longer holds whose look plays a leave effect, from its copy as the last
    // pack wrote it, and forgets the plays begun on those bodies. Called by Reconcile before it lays the copies out again.
    private void LeaveRetired(Func<int, bool> stays) {
        foreach (var (body, at) in m_bodyAt) {
            if (stays(body) || (at >= m_bodies.Count) || (at >= m_copies.Count)) { continue; }

            var stamp = m_bodies[at];

            _ = m_formPlays.RemoveAll(match: play => (play.Body == body));
            if (stamp.Look.Transition?.Leave is { } leave) {
                LeaveRemnant(body: body, creation: stamp.Creation, held: m_copies[at], leave: leave);
            }
        }
    }
}
