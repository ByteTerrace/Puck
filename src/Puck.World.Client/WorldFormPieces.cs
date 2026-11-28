using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// A form shed or assembled piece by piece (<see cref="WorldFormEffect.Pieces"/>): each moving part of a body's form (each
/// of its slots that carries geometry) flies on its own and carries a band of its own over itself.
/// <para>
/// Shedding launches each piece, after its staggered start, from the pose the form last drew it at with a seeded
/// impulse (out from the body's root, leaning up by the effect's lift, spread by the stream) and a seeded tumble, then
/// steps it under gravity, sweeping a sphere about its centre through the world's real contact field
/// (<see cref="WorldQueryField.SphereCast"/>) so it strikes and bounces off floors and walls, keeping the effect's share of
/// its speed; its band dissolves it once it has flown <see cref="WorldFormEffect.PieceSeconds"/>. Assembling starts each
/// piece at a seeded point the effect's reach out from its place, turned by a seeded twist, and flies it in along an arc to
/// the place the form's own pose puts it this frame, overshooting and settling as it locks (<see cref="WorldFormEffects.LockIn"/>,
/// the turn along <see cref="VectorFunctions.Arc"/>); its band builds it while it flies.
/// </para>
/// <para>
/// The pieces write their slots after the stamp pool has packed them, so they are the last writer of the frame. An
/// assembling piece composes on the form's own pose: it takes as its base whatever the pool last wrote to its slot,
/// telling the pool's write from its own by the exact transform it wrote, so a pose the pool held still is never offset
/// twice. Presentation only: every draw is a <see cref="Pcg32XshRr"/> stream of the effect's seed, one stream a piece.
/// </para>
/// </summary>
public sealed class WorldFormPieces {
    // The longest step a piece takes in one frame, seconds: a frame that hitches past it slows the flight rather than
    // tunnelling through a wall.
    private const float LongestStep = (1f / 30f);
    // The speed under which a piece that struck a surface comes to rest, metres a second.
    private const float RestSpeed = 0.25f;

    private readonly WorldFormEffect m_effect;
    private readonly bool m_arriving;
    private readonly uint m_seed;
    private readonly Piece[] m_pieces;
    private readonly float m_lastStart;

    private struct Piece {
        public bool Solid;
        public float Radius;
        public Vector3 Centre;
        public float Stagger;
        public Vector3 Heading;
        public float Speed;
        public Vector3 SpinAxis;
        public float SpinRate;
        public Quaternion Twist;
        public bool Launched;
        public bool Clear;
        public bool Resting;
        public Vector3 Position;
        public Vector3 Velocity;
        public Quaternion Turn;
        public Quaternion Rest;
        public DynamicTransform Base;
        public DynamicTransform Written;
        public bool Wrote;
        public bool Settled;
    }

    /// <summary>Initializes a new instance of the <see cref="WorldFormPieces"/> class: one piece a slot of the run, each
    /// drawing its stagger, heading, speed, tumble and twist from its own stream of the seed.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="arriving">Whether the pieces assemble (fly in) or are shed (fly off).</param>
    /// <param name="seed">The effect's seed.</param>
    /// <param name="radii">Each slot's reach about its piece's centre, metres; zero for a slot that carries no geometry,
    /// which no piece moves.</param>
    /// <param name="centres">Each piece's centre in its slot's own frame, metres.</param>
    /// <param name="outward">Each piece's way out from the body's root, in the root's frame (unit, or zero to draw one).</param>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The spans differ in length.</exception>
    public WorldFormPieces(WorldFormEffect effect, bool arriving, uint seed, ReadOnlySpan<float> radii, ReadOnlySpan<Vector3> centres, ReadOnlySpan<Vector3> outward) {
        ArgumentNullException.ThrowIfNull(argument: effect);
        if ((radii.Length != centres.Length) || (radii.Length != outward.Length)) { throw new ArgumentException(message: "One radius, centre and way out a slot.", paramName: nameof(centres)); }

        m_effect = effect;
        m_arriving = arriving;
        m_seed = seed;
        m_pieces = new Piece[radii.Length];
        for (var index = 0; (index < m_pieces.Length); index++) {
            var stream = Pcg32XshRr.Create(state: seed, stream: (ulong)index);
            var random = LowDiscrepancy.SphereAt(u: Unit(stream: ref stream), v: Unit(stream: ref stream));
            var away = ((outward[index].LengthSquared() > 1e-8f) ? Vector3.Normalize(value: outward[index]) : random);
            var level = new Vector3(x: away.X, y: 0f, z: away.Z);

            level = ((level.LengthSquared() > 1e-6f) ? Vector3.Normalize(value: level) : random);

            var lift = Math.Clamp(value: effect.PieceLift, min: 0f, max: 1f);
            var heading = Vector3.Normalize(value: ((level * (1f - lift)) + (Vector3.UnitY * lift) + (random * 0.35f)));
            var spinAxis = LowDiscrepancy.SphereAt(u: Unit(stream: ref stream), v: Unit(stream: ref stream));
            var twistAngle = ((0.6f + (0.6f * Unit(stream: ref stream))) * MathF.PI * 0.5f);

            m_pieces[index] = new Piece {
                Solid = (radii[index] > 0f),
                Radius = radii[index],
                Centre = centres[index],
                Stagger = (MathF.Max(x: effect.PieceStagger, y: 0f) * Unit(stream: ref stream)),
                Heading = (arriving ? Vector3.Normalize(value: (away + (random * 0.5f))) : heading),
                Speed = (effect.PieceSpeed * (0.7f + (0.6f * Unit(stream: ref stream)))),
                SpinAxis = spinAxis,
                SpinRate = (effect.PieceSpin * (0.5f + Unit(stream: ref stream))),
                Twist = Quaternion.CreateFromAxisAngle(axis: spinAxis, angle: twistAngle),
                Turn = Quaternion.Identity,
            };
            m_lastStart = MathF.Max(x: m_lastStart, y: m_pieces[index].Stagger);
        }
    }

    /// <summary>Gets the seconds from the effect's start (its delay past) after which every piece has finished: flown,
    /// swept and its last motes faded.</summary>
    public float Length => (m_lastStart + (m_arriving
        ? MathF.Max(x: m_effect.PieceSeconds, y: m_effect.Seconds)
        : (m_effect.PieceSeconds + m_effect.Seconds)) + ((m_effect.Motes > 0) ? m_effect.MoteSeconds : 0f));

    /// <summary>Steps every piece to a moment and writes its slot: a shed piece's flight from where it was launched, an
    /// assembling piece's place on the form's own pose this frame; and appends each piece's band.</summary>
    /// <param name="elapsed">The seconds since the effect started, its delay past; negative before it starts.</param>
    /// <param name="deltaSeconds">The seconds since the previous step.</param>
    /// <param name="table">The whole dynamic-transform table, which holds the pool's pose of the run this frame.</param>
    /// <param name="firstSlot">The run's first absolute slot; piece <c>k</c> is slot <c>firstSlot + k</c>.</param>
    /// <param name="root">The run's root pose this frame, which assembling pieces scatter about.</param>
    /// <param name="field">The world's contact field, or <see langword="null"/> where none is resolved, which lets shed
    /// pieces fall through.</param>
    /// <param name="moved">The frame's moved set, which every slot a piece moves is owed to.</param>
    /// <param name="bands">The frame's bands, which each piece's band is appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="moved"/> or <paramref name="bands"/> is
    /// <see langword="null"/>.</exception>
    public void Step(float elapsed, float deltaSeconds, Span<DynamicTransform> table, int firstSlot, in DynamicTransform root, WorldQueryField? field, SdfMovedTransforms moved, List<SdfFormBand> bands) {
        ArgumentNullException.ThrowIfNull(argument: moved);
        ArgumentNullException.ThrowIfNull(argument: bands);

        var step = Math.Clamp(value: deltaSeconds, min: 0f, max: LongestStep);

        for (var index = 0; (index < m_pieces.Length); index++) {
            ref var piece = ref m_pieces[index];
            var slot = (firstSlot + index);

            if (!piece.Solid || (slot < 0) || (slot >= table.Length)) { continue; }

            var since = (elapsed - piece.Stagger);
            var pose = (m_arriving ? Assemble(piece: ref piece, since: since, current: table[slot], root: in root) : Shed(piece: ref piece, since: since, step: step, current: table[slot], field: field));

            if (pose is { } written) {
                if (SdfMovedTransforms.Write(slot: slot, table: table, value: written)) { moved.Owe(count: 1, start: slot); }
                piece.Written = written;
            }

            var drawn = table[slot];
            var centre = (drawn.Position + Vector3.Transform(value: piece.Centre, rotation: drawn.Orientation));

            bands.Add(item: WorldFormEffects.Band(effect: m_effect, arriving: m_arriving, seed: unchecked(m_seed + (uint)(index * 0x9E3779B9u)), firstSlot: slot, slotCount: 1,
                origin: centre, rotation: drawn.Orientation, low: -piece.Radius, high: piece.Radius, elapsed: (m_arriving ? since : (since - m_effect.PieceSeconds)), parted: true));
        }
    }
    // A shed piece's pose at a moment: none before its launch, then its flight from the pose it was launched at.
    private DynamicTransform? Shed(ref Piece piece, float since, float step, DynamicTransform current, WorldQueryField? field) {
        if (since < 0f) { return null; }
        if (!piece.Launched) {
            piece.Launched = true;
            piece.Rest = current.Orientation;
            piece.Position = (current.Position + Vector3.Transform(value: piece.Centre, rotation: current.Orientation));
            piece.Velocity = (piece.Heading * piece.Speed);
            piece.Base = current;
        }
        if (!piece.Resting && (step > 0f)) {
            piece.Velocity -= (Vector3.UnitY * (m_effect.PieceGravity * step));

            var travel = (piece.Velocity * step);
            var length = travel.Length();
            var radius = MathF.Max(x: (piece.Radius * 0.4f), y: 0.02f);

            // A piece launched from where it overlaps the world (a boot on the floor) flies free until it is clear of it,
            // so its first sweep does not strike the surface it started in.
            if (!piece.Clear) {
                piece.Clear = ((field is null) || !field.Overlap(center: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: piece.Position)), radius: FixedQ4816.FromDouble(value: radius)));
            }
            if (length > 1e-6f) {
                var direction = (travel / length);

                if (piece.Clear && (field is not null) && field.SphereCast(origin: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: piece.Position)),
                    dir: FixedVector3.FromVector3(value: direction), radius: FixedQ4816.FromDouble(value: radius), maxDist: FixedQ4816.FromDouble(value: length), hit: out var hit)) {
                    var normal = hit.Normal.ToVector3();

                    normal = ((normal.LengthSquared() > 1e-8f) ? Vector3.Normalize(value: normal) : -direction);
                    piece.Position += (direction * (float)(double)hit.Distance);

                    // The strike keeps the effect's share of the speed into the surface and a little more along it.
                    var into = (Vector3.Dot(vector1: piece.Velocity, vector2: normal) * normal);
                    var along = (piece.Velocity - into);
                    var bounce = Math.Clamp(value: m_effect.PieceBounce, min: 0f, max: 1f);

                    piece.Velocity = ((along * (0.5f + (0.5f * bounce))) - (into * bounce));
                    piece.SpinRate *= (0.5f + (0.5f * bounce));
                    if (piece.Velocity.Length() < RestSpeed) {
                        piece.Velocity = Vector3.Zero;
                        piece.Resting = true;
                    }
                } else {
                    piece.Position += travel;
                }
            }
            piece.Turn = Quaternion.Normalize(value: (Quaternion.CreateFromAxisAngle(axis: piece.SpinAxis, angle: (piece.SpinRate * step)) * piece.Turn));
        }

        var orientation = Quaternion.Normalize(value: (piece.Turn * piece.Rest));

        return new DynamicTransform(Position: (piece.Position - Vector3.Transform(value: piece.Centre, rotation: orientation)), Orientation: orientation, Lanes: piece.Base.Lanes) {
            MaterialScale = piece.Base.MaterialScale,
            Scale = piece.Base.Scale,
        };
    }
    // An assembling piece's pose at a moment: held scattered before its start, flown along an arc onto the form's own pose
    // of its slot as it stands this frame, and that pose itself once it has locked, after which it writes no more.
    private DynamicTransform? Assemble(ref Piece piece, float since, DynamicTransform current, in DynamicTransform root) {
        if (piece.Settled) { return null; }
        if (!piece.Wrote || !current.Equals(other: piece.Written)) { piece.Base = current; }
        piece.Wrote = true;

        var share = ((m_effect.PieceSeconds > 0f) ? Math.Clamp(value: (since / m_effect.PieceSeconds), min: 0f, max: 1f) : 1f);

        if (share >= 1f) {
            piece.Settled = true;
            piece.Wrote = false;
            return piece.Base;
        }

        var eased = WorldFormEffects.LockIn(share: share);
        var scatter = Vector3.Transform(value: (piece.Heading * m_effect.PieceReach), rotation: root.Orientation);
        var arc = (Vector3.Transform(value: Vector3.UnitY, rotation: root.Orientation) * (MathF.Sin(x: (MathF.PI * share)) * (0.25f * m_effect.PieceReach)));
        var target = piece.Base.Orientation;
        var from = VectorFunctions.WithPositiveScalar(turn: (piece.Twist * target));
        var to = VectorFunctions.WithPositiveScalar(turn: target);

        if (Quaternion.Dot(quaternion1: from, quaternion2: to) < 0f) { from = Quaternion.Negate(value: from); }

        var orientation = VectorFunctions.Arc(from: from, to: to, amount: Math.Clamp(value: eased, min: 0f, max: 1f));
        var centreAt = (piece.Base.Position + Vector3.Transform(value: piece.Centre, rotation: target) + (scatter * (1f - eased)) + arc);

        return new DynamicTransform(Position: (centreAt - Vector3.Transform(value: piece.Centre, rotation: orientation)), Orientation: orientation, Lanes: piece.Base.Lanes) {
            MaterialScale = piece.Base.MaterialScale,
            Scale = piece.Base.Scale,
        };
    }
    // A uniform number in [0, 1) from the stream.
    private static float Unit(ref Pcg32XshRr stream) => (float)(double)stream.NextUnitFraction32();
}
