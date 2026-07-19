using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>The player's momentary action buttons — abstract digital channels riding an intent alongside the analog
/// movement channels, one bit per lane. Independent of the movement tape and sticks (a tape-driven runner mid-segment
/// can still fire one): a lane is merged into the intent every sub-step from a separate action track (live edge presses
/// plus timed auto-release presses), never from the tape. <see cref="None"/> is the default, so every movement-only
/// producer fills no lanes. What a channel DOES is the entity's kit binding (a world-definition row), never an engine
/// fact; a future channel is a new member here plus a kit binding.</summary>
/// <remarks>Lane capacity is a data-bounded genre-path axis: it stays 2 today, but RTS/MMO worlds widen it via document
/// data (more action channels + kit bindings), never by an enum ritual — the widening is a capacity change, not a
/// protocol reshape.</remarks>
[Flags]
internal enum ActionLanes {
    /// <summary>No action lane held — the default every movement-only producer fills.</summary>
    None = 0,

    /// <summary>The primary action channel. Its meaning is the kit's binding — the default world's grounded kits bind
    /// the jump composition (rising edge launches with coyote/buffer forgiveness, release cuts for variable height);
    /// an unbound kit leaves it inert.</summary>
    Primary = 1,

    /// <summary>The secondary action channel — the default world's grounded kits bind the dash composition, its free
    /// kits the surge; an unbound kit leaves it inert.</summary>
    Secondary = 2,
}

/// <summary>One simulation tick's player intent: the merged movement command the avatar advances from, each axis in
/// <c>[-1, 1]</c>. The three planar channels — <see cref="MoveForward"/> along the avatar's facing (+1 forward, -1
/// back), <see cref="MoveStrafe"/> along its right (+1 right, -1 left), and <see cref="Turn"/> about its up (+1 left /
/// counter-clockwise, -1 right) — are all any grounded producer fills; the three 6DOF channels (<see cref="MoveUp"/>
/// along the body's up, <see cref="Pitch"/> about the body's right, <see cref="Roll"/> about the body's forward) default
/// to zero, so every planar producer fills only the first three. Every producer resolves to one of these, so
/// <see cref="Puck.World.Server.WorldBody.Advance"/> integrates a single shape; the <see cref="MotionModel"/> decides which channels
/// bite (grounded ignores <see cref="MoveUp"/>/<see cref="Pitch"/>/<see cref="Roll"/>, free integrates all six in the
/// body frame).</summary>
/// <param name="MoveForward">Motion along facing, +1 forward / -1 back.</param>
/// <param name="MoveStrafe">Motion along the avatar's right, +1 right / -1 left.</param>
/// <param name="Turn">Yaw rate about the body's up, +1 left (counter-clockwise) / -1 right.</param>
/// <param name="MoveUp">Motion along the body's up, +1 up / -1 down (free model only).</param>
/// <param name="Pitch">Pitch rate about the body's right, +1 nose-up / -1 nose-down (free model only).</param>
/// <param name="Roll">Roll rate about the body's forward, +1 / -1 (free model only).</param>
/// <param name="Actions">The momentary action button lanes held this sub-step (a bitmask), defaulting to
/// <see cref="ActionLanes.None"/>. The digital buttons ride their own <see cref="Puck.World.Server.WorldBody"/> action track, merged in
/// every sub-step independent of the movement tape.</param>
internal readonly record struct PlayerIntent(
    FixedQ4816 MoveForward,
    FixedQ4816 MoveStrafe,
    FixedQ4816 Turn,
    FixedQ4816 MoveUp = default,
    FixedQ4816 Pitch = default,
    FixedQ4816 Roll = default,
    ActionLanes Actions = ActionLanes.None
) {
    public PlayerIntent(float MoveForward, float MoveStrafe, float Turn, float MoveUp = 0f, float Pitch = 0f, float Roll = 0f, ActionLanes Actions = ActionLanes.None)
        : this(
            MoveForward: FixedQ4816.FromDouble(value: MoveForward),
            MoveStrafe: FixedQ4816.FromDouble(value: MoveStrafe),
            Turn: FixedQ4816.FromDouble(value: Turn),
            MoveUp: FixedQ4816.FromDouble(value: MoveUp),
            Pitch: FixedQ4816.FromDouble(value: Pitch),
            Roll: FixedQ4816.FromDouble(value: Roll),
            Actions: Actions
        ) { }
}

/// <summary>How a <see cref="Puck.World.Server.WorldBody"/> integrates its single merged <see cref="PlayerIntent"/> into a pose. The
/// pose is always 6DOF (a free <see cref="System.Numerics.Vector3"/> position and a
/// <see cref="System.Numerics.Quaternion"/> orientation); the model only constrains how the integration writes it, so
/// the same six-channel intent drives a ground avatar or a free-flying craft with no other pipeline change.</summary>
internal enum MotionModel {
    /// <summary>The default ground avatar. Yaw integrates from the <see cref="PlayerIntent.Turn"/> rate, the planar step
    /// rides forward/strafe along the heading, Y is pinned to the ground plane, and the orientation is a pure yaw
    /// rotation about world up (pitch and roll always zero). Ignores the
    /// <see cref="PlayerIntent.MoveUp"/>/<see cref="PlayerIntent.Pitch"/>/<see cref="PlayerIntent.Roll"/> channels.</summary>
    Grounded,

    /// <summary>The free-flight model — all six channels integrate in the body frame: linear
    /// velocity is <c>(forward·facing + strafe·bodyRight + up·bodyUp) · MoveSpeed</c> with no ground pin and no gravity,
    /// and the angular yaw/pitch/roll rates compose into the orientation quaternion per sub-step about the body's own
    /// axes.</summary>
    Free,
}

/// <summary>What fills an entity's intent gaps between tape segments — the one per-entity intent-source axis. The
/// per-tick merge rule is: live tape segment &gt; submitted intent (admitted unless <see cref="Idle"/>) &gt; producer
/// output (iff the source names a producer member, today only <see cref="Wander"/>) &gt; zero. A wire lane press
/// (<c>player.press</c>) always overlays regardless; the device-held lane image is admitted only under
/// <see cref="Live"/>. A network peer is just <see cref="Live"/> — its remote client submits intents.</summary>
/// <remarks>ADMISSION RULE: producer identity is folded into this axis — a future producer (flock, replay, …) is a
/// NEW ENUM MEMBER plus its producer implementation, never a parallel flag.</remarks>
internal enum IntentSource {
    /// <summary>The live submitted stream fills gaps (a seat's device image or a remote client's submissions), and the
    /// seat's device edges fire. The seat/boot default.</summary>
    Live,

    /// <summary>Nothing fills gaps: submissions are masked, a tape gap holds still, and device edges no-op — only the
    /// tape and the wire-sourced <c>player.press</c> lane reach the entity.</summary>
    Idle,

    /// <summary>The deterministic index-seeded wander producer fills gaps. Submissions are still admitted above it
    /// (submitted &gt; producer); the device-held lane image is not (wander is not possession by the human).</summary>
    Wander,

    /// <summary>The deterministic attend producer fills gaps: the body steers toward and orbits its kit's
    /// <see cref="AttendTarget"/> while one is inside the notice band, and falls back to the kit's
    /// <see cref="WanderFlavor"/> when none is. Submissions are still admitted above it (submitted &gt; producer),
    /// exactly like <see cref="Wander"/>. A kit with no attend flavor rejects this source at validation.</summary>
    Attend,
}
