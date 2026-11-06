using System.Runtime.InteropServices;

using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

// ONE RIGID SOLVER. Every rigid contact, against the static field or between bodies, is solved by FixedRigidWorld's
// substep sequence, from manifolds the bodies own and carry in their continuation. A step runs the exact geometry once
// per candidate pair: one certified probe sweep per awake rigid body against the static field (ProbeRigidSurface) and
// one overlap query per broadphase pair (ResolveDynamicContacts), whose normals and depths FixedRigidContacts spreads
// over the shapes' support features. A guard sweep re-certifies only a body whose solved motion left the motion its
// probe certified. Kit bodies keep FixedStaticContactSolver's certified sweep; a kit body meets a rigid one as a static
// side that pushes with its velocity and is never pushed.
public sealed partial class WorldPopulation {
    /// <summary>The most occupied manifold slots one body's checkpoint entry can carry: one surface contact and up to
    /// <see cref="WorldBodyContactPolicy.MaximumPairsPerBody"/> body contacts, each of
    /// <see cref="FixedPairManifoldSlotTable.Capacity"/> slots.</summary>
    public const int MaximumRigidContactSlotsPerBody = ((WorldBodyContactPolicy.MaximumPairsPerBody + 1) * FixedPairManifoldSlotTable.Capacity);
    // How far a box face corner may lie outside the other box and still be clamped onto it as a contact point.
    private static readonly FixedQ4816 RigidPairClipMargin = FixedQ4816.FromDouble(value: 0.001d);

    private readonly FixedRigidBody m_rigidSurface = new();
    private readonly List<FixedRigidBody> m_rigidStepBodies = [];
    private readonly List<FixedRigidManifold> m_rigidStepManifolds = [];
    private readonly List<int> m_rigidRoster = [];
    private readonly List<RigidSurfaceProbe> m_rigidProbes = [];
    private readonly RigidSurface[] m_rigidSurfaces = new RigidSurface[WorldBody.MaximumRigidSurfaces];
    private readonly List<FixedTwoBodyContact> m_rigidCandidates = [];
    private readonly List<RigidPairHit> m_rigidPairHits = [];
    private readonly List<int> m_rigidNeighbourScratch = [];
    private List<int>?[] m_rigidContactReferrers = [];
    private List<WorldRigidContactSlotImage>? m_viewRigidContacts;
    private FixedRigidWorld? m_rigidWorld;
    private ulong m_rigidTickStepTicks;

    /// <summary>Gets the number of contact manifolds the most recent rigid step solved.</summary>
    public int RigidManifoldsThisTick { get; private set; }
    /// <summary>Gets the number of contact points (constraint slots) the most recent rigid step solved.</summary>
    public int RigidContactPointsThisTick { get; private set; }
    /// <summary>Gets the number of rigid bodies the most recent rigid step integrated.</summary>
    public int RigidBodiesThisTick { get; private set; }
    /// <summary>Gets the number of guard sweeps the most recent rigid step ran: bodies whose solved motion left the
    /// motion their probe certified.</summary>
    public int RigidGuardSweepsThisTick { get; private set; }
    /// <summary>Gets the document-derived speed a <c>body.impulse</c> command's resulting velocity may not exceed —
    /// <see cref="WorldFacePortalPolicy.SpeedCeiling"/>, the same fastest travel the document already declares for
    /// crossing a face. <see cref="WorldBody.TryApplyRigidImpulse"/> refuses by name rather than applying a delta
    /// that would carry a rigid body past it, so a degenerate impulse magnitude never reaches the solver as an
    /// unrepresentable velocity.</summary>
    public FixedQ4816 RigidVelocityCeiling => m_rigidVelocityCeiling;

    private readonly record struct RigidPairHit(int Left, int Right, FixedVector3 Correction);

    /// <summary>Describes the rigid solver's current census and last-tick work — the <c>world.budget</c> cost-sheet
    /// segment: rigid body count, resting and sleeping counts, the bodies, manifolds and contact points the last step
    /// solved, its guard sweeps and solver refusals, and the compiled policy.</summary>
    /// <returns>The segment.</returns>
    public string DescribeRigidWork() {
        var rigidCount = 0;
        var restingCount = 0;

        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index] is not { Active: true, Body: { IsRigid: true } body }) {
                continue;
            }

            rigidCount++;

            if (body.Resting) {
                restingCount++;
            }
        }

        return $"rigid {rigidCount} body/bodies ({restingCount} resting, {SleepingRigidBodies} sleeping), solved {RigidBodiesThisTick} body/bodies over {RigidManifoldsThisTick} manifold(s) and {RigidContactPointsThisTick} contact point(s), substeps={m_bodyContactPolicy.RigidSubstepCeiling}, guardSweeps={RigidGuardSweepsThisTick}, refusals={(m_rigidWorld?.RefusalCount ?? 0)}, contactBudget={(m_bodyContactPolicy.MaxPairsPerBody + 1)}/body, restSpeed<={m_bodyContactPolicy.RigidRestSpeed:0.###} restHold={m_bodyContactPolicy.RigidRestHoldSeconds:0.###}s guardFraction={m_bodyContactPolicy.RigidSubstepTravelFraction:0.###} guardMinTravel={m_bodyContactPolicy.RigidSubstepMinimumTravel:0.####} restitutionSpeed={m_bodyContactPolicy.RigidPairRestitutionSpeed:0.###} impulseVelocityCeiling={((double)m_rigidVelocityCeiling):0.###}";
    }

    // The solver for the step's rate, formed once per rate and policy.
    private FixedRigidWorld RigidWorld(ulong stepTicks) {
        var ticksPerSecond = FixedTickConversion.TicksPerSecond;

        if ((stepTicks == 0UL) || ((ticksPerSecond % stepTicks) != 0UL)) {
            throw new InvalidOperationException(message: $"a rigid step of {stepTicks} engine ticks does not divide one second of {ticksPerSecond}.");
        }

        var rateHz = checked((int)(ticksPerSecond / stepTicks));

        if (
            (m_rigidWorld is not { } world) ||
            (world.Options.RateHz != rateHz) ||
            (world.Options.SubstepCount != m_rigidContactPolicy.SubstepCeiling) ||
            (world.Options.RestitutionThreshold != m_rigidContactPolicy.PairRestitutionThreshold)
        ) {
            m_rigidWorld = world = new FixedRigidWorld(options: new FixedRigidSolverOptions {
                RateHz = rateHz,
                RestitutionThreshold = m_rigidContactPolicy.PairRestitutionThreshold,
                Scales = FixedWorldRigid.Scales,
                SubstepCount = m_rigidContactPolicy.SubstepCeiling,
            });
        }

        return world;
    }
    // Solves one step of every awake rigid body: probes, candidates, the solve, the commit and the rest latch. The pair
    // hits are the broadphase's overlapping pairs with a rigid side, every sleeper among them already woken.
    private void SolveRigidContacts() {
        RigidBodiesThisTick = 0;
        RigidManifoldsThisTick = 0;
        RigidContactPointsThisTick = 0;
        RigidGuardSweepsThisTick = 0;
        m_rigidRoster.Clear();
        m_rigidProbes.Clear();

        if (m_rigidTickStepTicks == 0UL) {
            m_rigidPairHits.Clear();

            return;
        }

        foreach (var index in m_awakeBodies) {
            if (
                !m_entries[index].Active ||
                (m_entries[index].Body is not { IsRigid: true, CarriedBy: null, Collider: not null } body)
            ) {
                continue;
            }

            // A body woken during this tick's contacts joins the step it was not prepared for.
            if (!body.RigidStepPrepared) {
                body.PrepareRigidStep(entityIndex: index, stepTicks: m_rigidTickStepTicks);
            }

            if (body.RigidStepPrepared) {
                m_rigidRoster.Add(item: index);
            }
        }

        if (m_rigidRoster.Count == 0) {
            m_rigidPairHits.Clear();

            return;
        }

        var world = RigidWorld(stepTicks: m_rigidTickStepTicks);

        // One exact query per surface within each body's reach: its surface contacts.
        foreach (var index in m_rigidRoster) {
            var body = m_entries[index].Body!;
            var probe = body.ProbeRigidSurface(reach: m_contactReach, surfaces: m_rigidSurfaces);

            m_rigidProbes.Add(item: probe);

            if (probe.Refused) {
                continue;
            }

            m_rigidCandidates.Clear();

            var shape = body.RigidContactShape(id: index);

            for (var surface = 0; (surface < probe.Count); surface++) {
                _ = FixedRigidContacts.AppendSurface(body: in shape, candidates: m_rigidCandidates, normal: m_rigidSurfaces[surface].Normal,
                    separation: m_rigidSurfaces[surface].Separation, sourceId: m_rigidSurfaces[surface].SourceId, surfaceId: WorldRigidContacts.Surface);
            }

            if (m_rigidCandidates.Count > 0) {
                Associate(contact: body.RigidContact(partner: WorldRigidContacts.Surface, created: out _));
            }
        }

        // One overlap query per pair, already run by the broadphase: its body contacts.
        foreach (var hit in m_rigidPairHits) {
            var left = m_entries[hit.Left].Body!;
            var right = m_entries[hit.Right].Body!;
            var normal = hit.Correction.Normalize();
            var depth = hit.Correction.Length;

            m_rigidCandidates.Clear();

            int owner;
            int partner;

            if (left.IsRigid && right.IsRigid) {
                // The lower index owns the pair and is its A side; the normal runs from right to left.
                owner = Math.Min(val1: hit.Left, val2: hit.Right);
                partner = Math.Max(val1: hit.Left, val2: hit.Right);
                _ = FixedRigidContacts.AppendPair(
                    a: m_entries[owner].Body!.RigidContactShape(id: owner),
                    b: m_entries[partner].Body!.RigidContactShape(id: partner),
                    candidates: m_rigidCandidates,
                    clipMargin: RigidPairClipMargin,
                    depth: depth,
                    normal: ((owner == hit.Right) ? normal : -normal),
                    sourceId: 0
                );
            } else {
                // The rigid side owns the pair and is its B side; the kit body is a static A side.
                owner = (left.IsRigid ? hit.Left : hit.Right);
                partner = (left.IsRigid ? hit.Right : hit.Left);
                _ = FixedRigidContacts.AppendWitness(
                    aCenter: m_entries[partner].Body!.FixedPosition,
                    aId: partner,
                    b: m_entries[owner].Body!.RigidContactShape(id: owner),
                    candidates: m_rigidCandidates,
                    depth: depth,
                    normal: ((owner == hit.Left) ? normal : -normal),
                    sourceId: 0
                );
            }

            var ownerBody = m_entries[owner].Body!;

            if (!ownerBody.RigidStepPrepared && !m_entries[partner].Body!.RigidStepPrepared) {
                continue;
            }

            var contact = ownerBody.RigidContact(partner: partner, created: out var created);

            if (created) {
                AddRigidReferrer(owner: owner, partner: partner);
            }

            Associate(contact: contact);
        }

        m_rigidPairHits.Clear();

        // The solver views, in roster order, then the touched manifolds in owner then partner order.
        m_rigidStepBodies.Clear();
        m_rigidStepManifolds.Clear();

        foreach (var index in m_rigidRoster) {
            m_rigidStepBodies.Add(item: m_entries[index].Body!.RigidSolverHandle());
        }

        foreach (var index in m_rigidRoster) {
            var body = m_entries[index].Body!;

            foreach (var contact in body.RigidContacts) {
                if (!contact.Touched) {
                    continue;
                }

                var manifold = contact.Manifold;

                if (contact.Partner == WorldRigidContacts.Surface) {
                    manifold.BodyA = m_rigidSurface;
                    manifold.BodyB = body.RigidSolverHandle();
                    manifold.Friction = body.RigidFriction;
                    manifold.Restitution = body.RigidRestitution;
                } else if (m_entries[contact.Partner].Body is { IsRigid: true } other) {
                    if (!other.RigidStepPrepared) {
                        continue;
                    }

                    manifold.BodyA = body.RigidSolverHandle();
                    manifold.BodyB = other.RigidSolverHandle();
                    manifold.Friction = AverageNonnegative(left: body.RigidFriction, right: other.RigidFriction);
                    manifold.Restitution = AverageNonnegative(left: body.RigidRestitution, right: other.RigidRestitution);
                } else {
                    manifold.BodyA = m_entries[contact.Partner].Body!.RigidSolverHandle();
                    manifold.BodyB = body.RigidSolverHandle();
                    manifold.Friction = body.RigidFriction;
                    manifold.Restitution = body.RigidRestitution;
                }

                m_rigidStepManifolds.Add(item: manifold);
            }
        }

        world.Step(
            bodies: CollectionsMarshal.AsSpan(list: m_rigidStepBodies),
            manifolds: CollectionsMarshal.AsSpan(list: m_rigidStepManifolds)
        );
        RigidBodiesThisTick = m_rigidRoster.Count;
        RigidManifoldsThisTick = m_rigidStepManifolds.Count;
        RigidContactPointsThisTick = world.LastStepConstraintCount;

        for (var ordinal = 0; (ordinal < m_rigidRoster.Count); ordinal++) {
            var body = m_entries[m_rigidRoster[ordinal]].Body!;

            if (
                !m_rigidProbes[ordinal].Refused &&
                body.CommitRigidStep(handle: m_rigidStepBodies[ordinal], policy: m_rigidContactPolicy, probedRoot: m_rigidProbes[ordinal].ResolvedRoot)
            ) {
                RigidGuardSweepsThisTick++;
            }
        }

        foreach (var index in m_rigidRoster) {
            var body = m_entries[index].Body!;

            body.FinishRigidStep(
                loadedPoint: LoadedSurfacePoint(body: body),
                policy: m_rigidContactPolicy,
                supported: RigidSupported(index: index)
            );
        }

        // Released only once every body has read its support: a contact another body owns supports this one too.
        foreach (var index in m_rigidRoster) {
            ReleaseUntouchedRigidContacts(index: index);
        }
    }
    // Whether a pair with a sleeper is the resting contact the sleeper was parked in: an occupied contact between them
    // whose overlap reaches no deeper than the contact reach plus the rigid support travel (FieldWakeMargin).
    private bool RestingContactHolds(int left, int right, FixedVector3 correction) {
        var leftBody = m_entries[left].Body!;
        var rightBody = m_entries[right].Body!;
        int owner;
        int partner;

        if (leftBody.IsRigid && rightBody.IsRigid) {
            owner = Math.Min(val1: left, val2: right);
            partner = Math.Max(val1: left, val2: right);
        } else {
            owner = (leftBody.IsRigid ? left : right);
            partner = (leftBody.IsRigid ? right : left);
        }

        return (
            (FindRigidContact(body: m_entries[owner].Body!, partner: partner) is { } contact) &&
            contact.Manifold.Slots.AnyOccupied &&
            (correction.Length <= FieldWakeMargin())
        );
    }
    private void Associate(WorldRigidContact contact) {
        if (m_rigidCandidates.Count > FixedPairManifoldSlotTable.Capacity) {
            throw new InvalidOperationException(message: $"a rigid contact generated {m_rigidCandidates.Count} candidates, past the {FixedPairManifoldSlotTable.Capacity} slots of its manifold.");
        }

        contact.Manifold.Associate(candidates: m_rigidCandidates);
        contact.Touched = true;
    }
    // Whether a contact under the walkable slope carried load this step, whichever body owns it.
    private bool RigidSupported(int index) {
        var body = m_entries[index].Body!;

        foreach (var contact in body.RigidContacts) {
            // The owner is the A side of a pair with a rigid partner, and the B side of every other contact.
            var ownerIsA = ((contact.Partner >= 0) && (m_entries[contact.Partner].Body is { IsRigid: true }));

            if (contact.Touched && Supports(manifold: contact.Manifold, towardB: !ownerIsA)) {
                return true;
            }
        }

        if (index < m_rigidContactReferrers.Length && (m_rigidContactReferrers[index] is { } referrers)) {
            foreach (var owner in referrers) {
                if (
                    (m_entries[owner].Body is { } ownerBody) &&
                    (FindRigidContact(body: ownerBody, partner: index) is { Touched: true } contact) &&
                    Supports(manifold: contact.Manifold, towardB: true)
                ) {
                    return true;
                }
            }
        }

        return false;
    }
    private bool Supports(FixedRigidManifold manifold, bool towardB) {
        for (var slot = 0; (slot < FixedPairManifoldSlotTable.Capacity); slot++) {
            ref readonly var state = ref manifold.Slots[slot];

            if (
                (state.Disposition == FixedPairManifoldSlotDisposition.Constraint) &&
                (state.NormalImpulseRaw > 0L) &&
                ((towardB ? state.Normal.Y : -state.Normal.Y) >= m_walkableThreshold)
            ) {
                return true;
            }
        }

        return false;
    }
    // The world point of the most loaded surface contact point, for the contact diagnostics.
    private static FixedVector3? LoadedSurfacePoint(WorldBody body) {
        if (
            (body.RigidContacts.Count == 0) ||
            (body.RigidContacts[0] is not { Partner: WorldRigidContacts.Surface, Touched: true } surface)
        ) {
            return null;
        }

        var loaded = -1;

        for (var slot = 0; (slot < FixedPairManifoldSlotTable.Capacity); slot++) {
            if (
                (surface.Manifold.Slots[slot].NormalImpulseRaw > 0L) &&
                ((loaded < 0) || (surface.Manifold.Slots[slot].NormalImpulseRaw > surface.Manifold.Slots[loaded].NormalImpulseRaw))
            ) {
                loaded = slot;
            }
        }

        return ((loaded < 0) ? null : (body.RigidCenterOfMass + surface.Manifold.Slots[loaded].AnchorB));
    }
    private static WorldRigidContact? FindRigidContact(WorldBody body, int partner) {
        foreach (var contact in body.RigidContacts) {
            if (contact.Partner == partner) {
                return contact;
            }
        }

        return null;
    }
    // A contact the step produced no candidates for is released; the rest stay, their touch flags cleared.
    private void ReleaseUntouchedRigidContacts(int index) {
        var body = m_entries[index].Body!;

        for (var position = (body.RigidContacts.Count - 1); (position >= 0); position--) {
            var contact = body.RigidContacts[position];

            if (contact.Touched) {
                contact.Touched = false;

                continue;
            }

            if (contact.Partner >= 0) {
                RemoveRigidReferrer(owner: index, partner: contact.Partner);
            }

            body.ReleaseRigidContactAt(position: position);
        }
    }
    private void AddRigidReferrer(int owner, int partner) {
        if (m_rigidContactReferrers.Length != Capacity) {
            Array.Resize(array: ref m_rigidContactReferrers, newSize: Capacity);
        }

        var referrers = (m_rigidContactReferrers[partner] ??= []);
        var position = referrers.BinarySearch(item: owner);

        if (position < 0) {
            referrers.Insert(index: ~position, item: owner);
        }
    }
    private void RemoveRigidReferrer(int owner, int partner) {
        if (
            (partner < m_rigidContactReferrers.Length) &&
            (m_rigidContactReferrers[partner] is { } referrers)
        ) {
            var position = referrers.BinarySearch(item: owner);

            if (position >= 0) {
                referrers.RemoveAt(index: position);
            }
        }
    }
    /// <summary>Releases every contact a slot's body owns and every contact another body owns with it: the slot's body
    /// left, or changed into one whose contacts no longer mean anything.</summary>
    /// <param name="index">The slot's population index.</param>
    /// <param name="keepReferred">Whether contacts other bodies own with this slot stay: the slot still holds a body
    /// they may touch.</param>
    private void ForgetRigidContacts(int index, bool keepReferred) {
        if (m_entries[index].Body is { } body) {
            for (var position = (body.RigidContacts.Count - 1); (position >= 0); position--) {
                if (body.RigidContacts[position].Partner >= 0) {
                    RemoveRigidReferrer(owner: index, partner: body.RigidContacts[position].Partner);
                }

                body.ReleaseRigidContactAt(position: position);
            }
        }

        if (
            keepReferred ||
            (index >= m_rigidContactReferrers.Length) ||
            (m_rigidContactReferrers[index] is not { } referrers)
        ) {
            return;
        }

        foreach (var owner in referrers) {
            if (m_entries[owner].Body is not { } ownerBody) {
                continue;
            }

            for (var position = (ownerBody.RigidContacts.Count - 1); (position >= 0); position--) {
                if (ownerBody.RigidContacts[position].Partner == index) {
                    ownerBody.ReleaseRigidContactAt(position: position);
                }
            }
        }

        referrers.Clear();
    }
    // The partner index derives from the owned contacts alone; a restore rebuilds it whole.
    private void RebuildRigidContactReferrers() {
        if (m_rigidContactReferrers.Length != Capacity) {
            m_rigidContactReferrers = new List<int>?[Capacity];
        }

        foreach (var referrers in m_rigidContactReferrers) {
            referrers?.Clear();
        }

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { } body }) {
                continue;
            }

            foreach (var contact in body.RigidContacts) {
                if (contact.Partner >= 0) {
                    AddRigidReferrer(owner: index, partner: contact.Partner);
                }
            }
        }
    }
    // Writes a checkpoint entry's contact slots into its body; the partner index is rebuilt after every entry.
    private void ApplyRigidContactImages(int index, IReadOnlyList<WorldRigidContactSlotImage> images) {
        var body = m_entries[index].Body!;

        for (var position = (body.RigidContacts.Count - 1); (position >= 0); position--) {
            body.ReleaseRigidContactAt(position: position);
        }

        foreach (var image in images) {
            ref var slot = ref body.RigidContact(partner: image.Partner, created: out _).Manifold.Slots[image.Slot];

            slot = default;
            slot.Occupied = true;
            slot.SourceId = image.SourceId;
            slot.FeatureId = image.FeatureId;
            slot.AnchorA = image.AnchorA;
            slot.AnchorB = image.AnchorB;
            slot.Normal = image.Normal;
            slot.NormalImpulseRaw = image.NormalImpulseRaw;
            slot.FrictionImpulse = image.FrictionImpulse;
            slot.IdleSteps = image.IdleSteps;
        }
    }
    /// <summary>Restores one live body's owned contacts from its captured image: the scoped replay's restore of a body
    /// continuation.</summary>
    /// <param name="index">The body's population index.</param>
    /// <param name="images">The captured contact slots.</param>
    internal void RestoreRigidContacts(int index, IReadOnlyList<WorldRigidContactSlotImage> images) {
        ForgetRigidContacts(index: index, keepReferred: true);
        ApplyRigidContactImages(index: index, images: images);

        foreach (var contact in m_entries[index].Body!.RigidContacts) {
            if (contact.Partner >= 0) {
                AddRigidReferrer(owner: index, partner: contact.Partner);
            }
        }
    }
    // A captured contact image names valid slots in ascending (partner, slot) order, on a rigid body, with partners the
    // owner rule allows: the static field, a kit body, or a rigid body of higher index.
    private void ValidateRigidContactImages(WorldPopulationEntryCheckpoint captured, WorldPopulationEntryCheckpoint?[] restored, byte seatKit) {
        if (captured.RigidContacts is null) {
            throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} has no rigid contact image.");
        }

        if (captured.RigidContacts.Count == 0) {
            return;
        }

        if (m_kits[CapturedKitIndex(captured: captured, seatKit: seatKit)].Rigid is null) {
            throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} carries rigid contacts without a rigid facet.");
        }

        var previous = (Partner: int.MinValue, Slot: int.MinValue);

        foreach (var image in captured.RigidContacts) {
            var key = (image.Partner, image.Slot);

            if (
                (key.CompareTo(other: previous) <= 0) ||
                (((uint)image.Slot) >= FixedPairManifoldSlotTable.Capacity) ||
                (image.IdleSteps < 0) ||
                (image.NormalImpulseRaw < 0L) ||
                ((image.Partner != WorldRigidContacts.Surface) && (
                    (((uint)image.Partner) >= ((uint)Capacity)) ||
                    (image.Partner == captured.Index) ||
                    (restored[image.Partner] is not { } partner) ||
                    ((m_kits[CapturedKitIndex(captured: partner, seatKit: seatKit)].Rigid is not null) && (image.Partner < captured.Index))
                ))
            ) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} has an invalid rigid contact slot.");
            }

            previous = key;
        }
    }
    // (left + right) / 2 rounded to nearest, ties to even — the same value (left + right) * 0.5 yields through the
    // fixed-point multiply — computed on the halves so the intermediate sum can never leave the raw.
    private static FixedQ4816 AverageNonnegative(FixedQ4816 left, FixedQ4816 right) {
        var leftRaw = Math.Max(
            val1: 0L,
            val2: left.Value
        );
        var rightRaw = Math.Max(
            val1: 0L,
            val2: right.Value
        );
        var halvesRaw = ((leftRaw >> 1) + (rightRaw >> 1));
        var lowBits = ((leftRaw & 1L) + (rightRaw & 1L));
        var averageRaw = (halvesRaw + ((lowBits == 2L)
            ? 1L
            : ((lowBits == 1L)
                ? halvesRaw & 1L
                : 0L)));

        return FixedQ4816.FromRawBits(value: averageRaw);
    }
}
