using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// Resolves a body's compiled volumes against a scalar field — the field provider's half of
/// <see cref="IContactField"/>, over <see cref="IFieldEvaluator"/> and the sweeping half of
/// <see cref="IWorldQuery"/>.
/// </summary>
/// <remarks>
/// <para>Contact is measured, never assumed: every volume is solved by the certified surface solve field colliders
/// read (<see cref="FixedFieldColliderContact.Solve"/>), over the whole field as one unbounded field collider in world
/// space, so a body stands its skin outside the surface the field describes at the distance the solve measures, never
/// at the value the field reads.</para>
/// <para><paramref name="field"/> and <paramref name="query"/> are ordinarily the same object under two seams. They
/// are separate parameters because the two capabilities are separate: a provider that can answer a distance need not
/// be able to march a sight line.</para>
/// </remarks>
/// <param name="field">The scalar field the solve measures against; its capabilities propose its rays' steps.</param>
/// <param name="query">The surface line-of-sight queries march.</param>
/// <param name="sweep">The certified sweep that proves how far a moving body may travel before the endpoint solve
/// runs, or <see langword="null"/> when the field holds no geometry for a body to cross.</param>
/// <param name="bounds">Certified bounds on every answer of <paramref name="field"/> over a box, the one proof that a
/// body is clear, or <see langword="null"/>, which proves none clear.</param>
/// <param name="sweepBoundsQueryBudget">The most bounds queries one core sphere's certified sweep may spend, at least
/// one.</param>
/// <param name="sweepWork">The ledger the certified sweeps count their work into.</param>
/// <param name="contactSkin">The signed skin kept between a body and every surface.</param>
/// <param name="groundedThreshold">The <c>cos(maxSlope)</c> a contact normal's up-alignment must clear to ground.</param>
/// <param name="gradientProbe">The finite-difference step, in metres, the field's direction is measured with, by the
/// solve and the up axis alike; zero or less takes <see cref="FixedColliderField.DefaultGradientStep"/> for the solve and
/// the field's own default for the up axis.</param>
/// <param name="maxIterations">The relaxation iteration count; values below one resolve one pass.</param>
/// <param name="gradientUp">Whether the up axis comes from the field gradient rather than world <c>+Y</c>.</param>
public sealed class FixedFieldContactSolver(
    IFieldEvaluator field,
    IWorldQuery query,
    FixedQ4816 contactSkin,
    FixedQ4816 groundedThreshold,
    FixedQ4816 gradientProbe,
    int maxIterations,
    bool gradientUp,
    ICertifiedSweepQuery? sweep,
    IFieldBounds? bounds,
    int sweepBoundsQueryBudget,
    FixedContactSweepWork sweepWork
) : IContactField {
    /// <summary>The bounds queries a host's solver lets one core sphere's certified sweep spend: enough for an
    /// ordinary tick's motion along a floor in steps of the core's clearance, so a sweep that runs out has met
    /// geometry its budget cannot resolve, and the body stops at the ground it proved.</summary>
    public const int DefaultSweepBoundsQueryBudget = 64;
    /// <summary>The most core spheres one capsule's certified sweep runs: one per bounds query a single core's sweep may
    /// spend at <see cref="DefaultSweepBoundsQueryBudget"/>, so a capsule's sweep costs at most that budget squared.
    /// A capsule whose core needs more is refused, as authored by collider validation
    /// (<see cref="CapsuleCoreFitsSweep"/>) and at a step by <see cref="ContactRefusal.OversizedCapsuleCore"/>.</summary>
    public const int MaximumCapsuleSweepPieces = DefaultSweepBoundsQueryBudget;

    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);
    private readonly IFieldEvaluator m_field = field;
    private readonly FixedQ4816 m_gradientProbe = gradientProbe;
    private readonly bool m_gradientUp = gradientUp;
    private readonly FixedQ4816 m_groundedThreshold = groundedThreshold;
    private readonly int m_iterations = Math.Max(
        val1: 1,
        val2: maxIterations
    );
    private readonly IWorldQuery m_query = query;
    private readonly FixedQ4816 m_skin = contactSkin;
    // The whole field as one unbounded field collider at the world origin: the frame every solve reads it in.
    private readonly FixedStaticCollider m_surface = FixedStaticCollider.FieldSurface(
        center: FixedVector3.Zero,
        field: new FixedColliderField(bounded: false, field: field,
            gradientStep: ((gradientProbe > FixedQ4816.Zero) ? gradientProbe : FixedColliderField.DefaultGradientStep)) { Bounds = bounds },
        halfExtent: FixedVector3.Zero,
        rotation: FixedQuaternion.Identity
    );
    private readonly ICertifiedSweepQuery? m_sweep = sweep;
    private readonly int m_sweepBudget = ((sweepBoundsQueryBudget >= 1)
        ? sweepBoundsQueryBudget
        : throw new ArgumentOutOfRangeException(paramName: nameof(sweepBoundsQueryBudget), message: "A certified sweep needs a budget of at least one bounds query."));
    private readonly FixedContactSweepWork m_sweepWork = (sweepWork ?? throw new ArgumentNullException(paramName: nameof(sweepWork)));

    /// <summary>Gets a value indicating whether the up axis is derived from the field gradient.</summary>
    public bool GradientUp => m_gradientUp;

    // Sweeps one core sphere — its centre the body-local offset from the body's foot point — along the step, and
    // lowers the least proved displacement when it proves less of the step than every core before it. The fractions
    // compare on the sweep's own 2⁻³² grid: a long step's cores can differ by less than 2⁻¹⁶ of it, and the lesser one
    // must win. A sweep the field refuses (its origin outside the field's frame) proves nothing, so the body keeps its
    // start, the only ground proved.
    private void SweepCore(ICertifiedSweepQuery certified, in FixedVector3 previousPosition, in FixedVector3 delta, FixedVector3 offset, FixedQ4816 radius, ref UnitInterval32 least, ref FixedVector3 reached, ref bool unproved) {
        var start = (previousPosition + offset);

        // The contact skin is the tolerance: within it the endpoint solve already holds a body off the surface, so a
        // sweep need not prove the last sliver of approach.
        if (!certified.TryCertifiedSweep(
            boundsQueryBudget: m_sweepBudget,
            contactTolerance: m_skin,
            displacement: delta,
            origin: FixedPosition.FromLocal(local: start),
            radius: radius,
            sweep: out var sweep
        )) {
            m_sweepWork.Count(boundsQueries: 0, contact: false, exhausted: true);
            least = UnitInterval32.Zero;
            reached = FixedVector3.Zero;
            unproved = true;

            return;
        }

        m_sweepWork.Count(
            boundsQueries: sweep.BoundsQueries,
            contact: (sweep.Outcome == CertifiedSweepOutcome.Contact),
            exhausted: (sweep.Outcome == CertifiedSweepOutcome.Exhausted)
        );

        if (
            (sweep.Outcome == CertifiedSweepOutcome.Clear) ||
            (sweep.Fraction >= least) ||
            !sweep.Reached.TryDelta(
                delta: out var centre,
                origin: FixedPosition.Zero
            )
        ) {
            return;
        }

        least = sweep.Fraction;
        reached = (centre - start);
        // Only a sweep that stopped at a possible surface proved its stop; one that ran out of budget or reached a box
        // its field could not bound held the body for want of proof, which is no physical block.
        unproved = (sweep.Outcome != CertifiedSweepOutcome.Contact);
    }
    // The pieces a capsule core sweeps in: one more than its length in radii, which keeps every piece's half-length
    // below the core sphere's radius (half the capsule's) by a margin no rounding of the piece centres reaches, so the
    // pieces cover the whole core however long it is. Taken in Int128, where the length plus the radius cannot wrap.
    // False past MaximumCapsuleSweepPieces. A capsule with no radius sweeps its lower end alone.
    private static bool TryCapsuleSweepPieces(FixedQ4816 coreLength, FixedQ4816 radius, out int pieces) {
        pieces = 1;

        if (radius.Value <= 0L) {
            return true;
        }

        var count = ((((((Int128)coreLength.Value) + radius.Value) - 1L) / radius.Value) + 1L);

        if (count > MaximumCapsuleSweepPieces) {
            return false;
        }

        pieces = ((int)count);

        return true;
    }

    /// <summary>Returns whether a capsule's core, as authored, sweeps in at most <see cref="MaximumCapsuleSweepPieces"/>
    /// pieces at every orientation a body turns it to.</summary>
    /// <param name="core">The core segment from the lower centre to the upper, body-local.</param>
    /// <param name="radius">The capsule's radius.</param>
    /// <param name="pieces">The pieces the core needs at its longest rotated length; one past the ceiling when it
    /// needs more.</param>
    /// <returns><see langword="true"/> when <paramref name="pieces"/> is within the ceiling.</returns>
    /// <remarks>A body's orientation is a normalized fixed-point quaternion, whose squared norm is within 2⁻¹⁵ of one,
    /// and each rotated endpoint lies within three raws a component of its exact image, so a rotated core is at most
    /// <c>(1 + 2⁻¹⁵)·length + 12</c> raws long. The core is measured at <c>length + length/2¹⁴ + 16</c> raws, past
    /// that, so a capsule this accepts is never refused at a step for its orientation; only a body's runtime scale,
    /// which can shrink the radius to a few raws, still reaches <see cref="ContactRefusal.OversizedCapsuleCore"/>.</remarks>
    public static bool CapsuleCoreFitsSweep(FixedVector3 core, FixedQ4816 radius, out long pieces) {
        var length = ((Int128)core.Length.Value);
        var rotated = Int128.Min(x: ((length + (length >> 14)) + 16L), y: long.MaxValue);

        if (TryCapsuleSweepPieces(coreLength: FixedQ4816.FromRawBits(value: ((long)rotated)), pieces: out var counted, radius: radius)) {
            pieces = counted;

            return true;
        }

        pieces = (MaximumCapsuleSweepPieces + 1L);

        return false;
    }

    /// <summary>The least radius, in raws, a collider volume's sphere radius or least half extent may quantize to: half
    /// of it, the radius of the core spheres a moving body's certified sweep moves, must be at least one raw. Below it
    /// the core is a point (half a raw rounds to zero, to even), and a capsule's core then sweeps its lower end alone.
    /// Collider validation refuses a volume past it by name (<see cref="VolumeCoreSweeps"/>).</summary>
    public const long MinimumColliderRadiusRaws = 2L;

    /// <summary>Returns whether a collider volume's core spheres have a radius of at least one raw: its sphere or
    /// capsule radius, or a box's least half extent, at least <see cref="MinimumColliderRadiusRaws"/>.</summary>
    /// <param name="volume">The compiled collider volume.</param>
    /// <returns><see langword="true"/> when the certified sweep moves a core sphere, not a point.</returns>
    public static bool VolumeCoreSweeps(in FixedBodyColliderVolume volume) =>
        (ColliderRadius(volume: in volume).Value >= MinimumColliderRadiusRaws);

    // The radius a volume's core spheres take half of: a sphere's or capsule's own, or a box's least half extent.
    private static FixedQ4816 ColliderRadius(in FixedBodyColliderVolume volume) => FixedColliderBounds.MinimumSupportRadius(volume: in volume);
    // A core sphere's radius: half the volume's least radius, so a body resting against a surface keeps half its radius
    // of clearance and its sweep never stalls there.
    private static FixedQ4816 CoreRadius(in FixedBodyColliderVolume volume) =>
        (ColliderRadius(volume: in volume) / Two);
    // Walks every core sphere of every volume: a sphere or box volume's centre, and a capsule's core segment covered by
    // spheres spaced closer than their radius. Each core sphere's radius is half the volume's least radius, so a body
    // resting against a surface keeps half its radius of clearance and its sweep never stalls there. Without a sweep
    // it only proves the step sweepable: every core's start representable and every capsule within its piece ceiling,
    // the refusal that stops the step otherwise. With one it sweeps every core, each start the representable one that
    // walk proved, and lowers the least proved displacement.
    private ContactRefusal WalkCores(ICertifiedSweepQuery? certified, in FixedVector3 previousPosition, in FixedVector3 delta, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, ref UnitInterval32 least, ref FixedVector3 reached, ref bool unproved) {
        foreach (ref readonly var volume in volumes) {
            var lowerOffset = orientation.Rotate(vector: volume.Center);
            ContactRefusal refusal;

            switch (volume.Kind) {
                case FixedBodyColliderKind.Sphere:
                    refusal = Core(certified: certified, delta: in delta, least: ref least, offset: lowerOffset, previousPosition: in previousPosition, radius: CoreRadius(volume: in volume), reached: ref reached, unproved: ref unproved);
                    break;
                case FixedBodyColliderKind.Box:
                case FixedBodyColliderKind.Ellipsoid:
                    refusal = Core(
                        certified: certified,
                        delta: in delta,
                        least: ref least,
                        offset: lowerOffset,
                        previousPosition: in previousPosition,
                        radius: CoreRadius(volume: in volume),
                        reached: ref reached, unproved: ref unproved
                    );
                    break;
                case FixedBodyColliderKind.Capsule: {
                        var core = (orientation.Rotate(vector: volume.Endpoint) - lowerOffset);
                        var radius = CoreRadius(volume: in volume);

                        if (!TryCapsuleSweepPieces(coreLength: core.Length, pieces: out var pieces, radius: volume.Radius)) {
                            return ContactRefusal.OversizedCapsuleCore;
                        }

                        var denominator = FixedQ4816.FromInteger(value: (2L * pieces));

                        refusal = ContactRefusal.None;

                        for (var piece = 0; ((piece < pieces) && (refusal == ContactRefusal.None)); piece++) {
                            refusal = Core(
                                certified: certified,
                                delta: in delta,
                                least: ref least,
                                offset: (lowerOffset + (core * (FixedQ4816.FromInteger(value: ((2L * piece) + 1L)) / denominator))),
                                previousPosition: in previousPosition,
                                radius: radius,
                                reached: ref reached, unproved: ref unproved
                            );
                        }

                        break;
                    }
                default:
                    throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}.");
            }

            if (refusal != ContactRefusal.None) {
                return refusal;
            }
        }

        return ContactRefusal.None;
    }
    // One core sphere of the walk: proves its start representable without a sweep, or sweeps it with one.
    private ContactRefusal Core(ICertifiedSweepQuery? certified, in FixedVector3 previousPosition, in FixedVector3 delta, FixedVector3 offset, FixedQ4816 radius, ref UnitInterval32 least, ref FixedVector3 reached, ref bool unproved) {
        if (certified is null) {
            return (FixedVector3.TryAdd(left: previousPosition, right: offset, sum: out _)
                ? ContactRefusal.None
                : ContactRefusal.UnrepresentableSweep);
        }

        SweepCore(certified: certified, delta: in delta, least: ref least, offset: offset, previousPosition: in previousPosition, radius: radius, reached: ref reached, unproved: ref unproved);

        return ContactRefusal.None;
    }

    /// <summary>Queries the wrapped deterministic SDF evaluator for an unobstructed segment.</summary>
    public bool LineOfSight(in FixedVector3 from, in FixedVector3 to) =>
        m_query.LineOfSight(
            from: FixedPosition.FromLocal(local: from),
            to: FixedPosition.FromLocal(local: to)
        );
    /// <summary>Measures the field at a point the way the solver does — the <c>world.collision.probe</c> diagnostic: the
    /// surface solve's signed distance and outward normal there, and the material the field reads. The normal is the
    /// body up axis only under a gradient-derived up axis; a flat-up world's bodies integrate against constant
    /// <c>+Y</c> regardless of what this prints.</summary>
    /// <param name="position">The world-space point to measure.</param>
    /// <param name="distance">The signed distance to the surface the solve reaches (negative inside geometry), when the
    /// method returns <see langword="true"/>.</param>
    /// <param name="material">The nearest surface's material id, when the method returns <see langword="true"/>.</param>
    /// <param name="gradient">The unit outward normal there, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the field has no geometry to answer against, or the solve reaches no
    /// surface from the point (<see cref="FixedFieldColliderContact.TryMeasure"/>).</returns>
    public bool Probe(in FixedVector3 position, out FixedQ4816 distance, out int material, out FixedVector3 gradient) {
        gradient = FixedVector3.Zero;
        distance = FixedQ4816.Zero;

        return (
            m_field.TryDistance(
                distance: out _,
                material: out material,
                position: FixedPosition.FromLocal(local: position)
            ) &&
            FixedFieldColliderContact.TryMeasure(
                collider: in m_surface,
                distance: out distance,
                normal: out gradient,
                point: position
            )
        );
    }
    /// <summary>Reads the field at a point without a surface solve (<see cref="FixedFieldColliderContact.TryRead"/>): a
    /// signed reading whose sign is exact and whose magnitude is a lower bound on the distance to the surface, the
    /// material read there and the field's outward direction. What a diagnostic claiming a point lies off the surface
    /// rests on.</summary>
    /// <param name="position">The world-space point to read.</param>
    /// <param name="distance">The signed reading, in metres, negative inside geometry, when the method returns <see langword="true"/>.</param>
    /// <param name="material">The material id the field reads there, when the method returns <see langword="true"/>.</param>
    /// <param name="gradient">The unit outward direction there; zero where the field measures none.</param>
    /// <returns><see langword="false"/> when the field does not answer at the point.</returns>
    public bool Read(in FixedVector3 position, out FixedQ4816 distance, out int material, out FixedVector3 gradient) =>
        FixedFieldColliderContact.TryRead(collider: in m_surface, distance: out distance, material: out material, normal: out gradient, point: position);
    /// <inheritdoc/>
    /// <remarks>A contact pushes and may ground the body; an embedded volume is extracted along the direction the solve
    /// measured out of the solid, by its depth plus its reach, and never grounds it. One volume claims the extraction a
    /// relaxation pass allows, so the volumes of one body never pull it apart. A volume the solve leaves uncertain in the
    /// last pass (<see cref="FixedFieldContact.Uncertain"/>: unproved, or embedded with no measured way out) marks the
    /// resolution unproved.</remarks>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
        var grounded = false;
        var groundNormal = FixedVector3.Zero;
        var lastNormal = FixedVector3.Zero;
        var uncertain = default(ContactUncertainty);

        for (var iteration = 0; (iteration < m_iterations); iteration++) {
            // A field authoring its own up samples it HERE, per iteration and at the body's current position: that is
            // what makes a gradient-derived walker follow the surface it is being extracted from. Only a caller with
            // no such field of its own imposes the axis.
            if (!(m_gradientUp && TryUp(
                position: in position,
                up: out var iterationUp
            ))) {
                iterationUp = up;
            }

            var pushed = false;

            // Only the last pass, at the pose the body keeps, says whether that pose is proved.
            uncertain = default;
            var extracted = false;

            for (var volumeIndex = 0; (volumeIndex < volumes.Length); volumeIndex++) {
                ref readonly var volume = ref volumes[volumeIndex];
                var contact = FixedFieldColliderContact.Solve(
                    collider: in m_surface,
                    orientation: in orientation,
                    position: position,
                    skin: m_skin,
                    volume: in volume
                );

                if (contact.Uncertain) {
                    uncertain = uncertain.With(pair: new ContactUncertainPair(Collider: 0, Span: ContactUncertaintySpan.Field, Volume: volumeIndex));
                }

                if ((contact.Outcome == FixedFieldContactOutcome.Contact) && contact.Pushes) {
                    FixedContactPushMath.Commit(
                        grounded: ref grounded,
                        groundNormal: ref groundNormal,
                        lastNormal: ref lastNormal,
                        position: ref position,
                        trial: FixedContactPushMath.ComputeOrdinary(
                            groundedThreshold: m_groundedThreshold,
                            normal: contact.Normal,
                            penetration: contact.Penetration,
                            up: iterationUp,
                            velocity: in velocity
                        ),
                        velocity: ref velocity
                    );
                    pushed = true;
                } else if ((contact.Outcome == FixedFieldContactOutcome.Embedded) && contact.Pushes && !extracted) {
                    extracted = true;
                    position += (contact.Normal * contact.Penetration);

                    var into = FixedVector3.Dot(left: velocity, right: contact.Normal);

                    if (into < FixedQ4816.Zero) {
                        velocity -= (contact.Normal * into);
                    }

                    pushed = true;
                }
            }

            if (!pushed) {
                break;
            }
        }

        return new ContactResolution(
            GroundNormal: groundNormal,
            Grounded: grounded,
            ObstructionNormal: lastNormal,
            Uncertainty: uncertain
        );
    }
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
        // Prove how far the body may move before solving at its endpoint: every core sphere of every volume is swept
        // by certified conservative advancement, so no core crosses a surface between two samples however fast the
        // step, and a body that would have tunneled stops at the face it approached, still on its exterior, where the
        // ordinary solve's measured normal resolves the face the body actually reached. A sweep that runs out of its
        // budget keeps only the ground it proved, so the body never moves through space it did not prove clear.
        //
        // One rule holds for every field the sweep runs over, the program's and the lattice's alike: a step whose
        // displacement or any core's start leaves the carrier is refused before anything moves, never wrapped to a
        // point no sweep proved and never clamped to one. The body keeps its start, its velocity comes back as it
        // came, no sweep runs and no endpoint push is applied. A capsule core past its piece ceiling is refused the
        // same way.
        if (m_sweep is { } certified) {
            if (!FixedVector3.TrySubtract(difference: out var delta, left: position, right: previousPosition)) {
                return Refuse(position: ref position, previousPosition: in previousPosition, refusal: ContactRefusal.UnrepresentableSweep);
            }

            if (delta != FixedVector3.Zero) {
                var least = UnitInterval32.One;
                var reached = delta;
                var unproved = false;
                var refusal = WalkCores(certified: null, delta: in delta, least: ref least, orientation: in orientation, previousPosition: in previousPosition, reached: ref reached, unproved: ref unproved, volumes: volumes);

                if (refusal != ContactRefusal.None) {
                    return Refuse(position: ref position, previousPosition: in previousPosition, refusal: refusal);
                }

                _ = WalkCores(certified: certified, delta: in delta, least: ref least, orientation: in orientation, previousPosition: in previousPosition, reached: ref reached, unproved: ref unproved, volumes: volumes);

                // The whole step when every sweep cleared it, else the least proved one. Every core moves by the same
                // displacement and the sweeps form each step's box the same way from a point start, so the least one's
                // reached displacement lies inside every other core's proved boxes.
                if (least < UnitInterval32.One) {
                    position = (previousPosition + reached);

                    if (unproved) {
                        return (Resolve(
                            orientation: in orientation,
                            position: ref position,
                            up: in up,
                            velocity: ref velocity,
                            volumes: volumes
                        ) with { Uncertainty = ContactUncertainty.Unknown });
                    }
                }
            }
        }

        return Resolve(
            orientation: in orientation,
            position: ref position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    }

    // A refused step: the body keeps its start and reports no contact, naming why.
    private static ContactResolution Refuse(ref FixedVector3 position, in FixedVector3 previousPosition, ContactRefusal refusal) {
        position = previousPosition;

        return new ContactResolution(
            Grounded: false,
            ObstructionNormal: FixedVector3.Zero,
            Refusal: refusal
        );
    }

    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        // Gradient-derived ambient up is authored (WorldContactRequirement.GradientDerivedUp), never assumed. When
        // disabled this provider answers +Y at zero field-query cost; a caller may still pass another resolved up to
        // the contact solve (for example, one opposed to authored gravity).
        if (!m_gradientUp) {
            up = FixedVector3.UnitY;

            return true;
        }

        if (m_field.TryFieldGradient(
            position: FixedPosition.FromLocal(local: position),
            epsilon: m_gradientProbe,
            gradient: out var gradient
        )) {
            // The gradient is the direction of steepest distance INCREASE — the direction pointing directly away from the
            // nearest surface, i.e. UP. A grounded body's gravity opposes it and the standing test aligns against it.
            up = gradient;

            return true;
        }

        up = FixedVector3.UnitY;

        return false;
    }
}
