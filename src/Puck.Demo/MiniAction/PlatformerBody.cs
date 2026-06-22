using System.Numerics;
using Puck.Maths;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The fixed-point collision surfaces the simulation resolves against, derived ONCE from the authored (float)
/// <see cref="MiniActionRoom"/> via deterministic rounding. The avatar's box half-extents are already folded in,
/// so these are the exact planes a body's center clamps to.
/// </summary>
/// <param name="FloorTop">The world Y the body's center rests at on the floor.</param>
/// <param name="MinX">The minimum X the body's center may reach.</param>
/// <param name="MaxX">The maximum X the body's center may reach.</param>
/// <param name="MinZ">The minimum Z the body's center may reach.</param>
/// <param name="MaxZ">The maximum Z the body's center may reach.</param>
public readonly record struct FixedRoom(FixedQ4816 FloorTop, FixedQ4816 MinX, FixedQ4816 MaxX, FixedQ4816 MinZ, FixedQ4816 MaxZ) {
    /// <summary>Resolves the fixed-point collision planes from an authored room (half-extents folded in).</summary>
    /// <param name="room">The authored room to resolve.</param>
    /// <returns>The fixed-point collision surfaces.</returns>
    public static FixedRoom From(MiniActionRoom room) {
        ArgumentNullException.ThrowIfNull(argument: room);

        var halfX = FixedQ4816.FromDouble(value: room.PlayerHalfExtents.X);
        var halfY = FixedQ4816.FromDouble(value: room.PlayerHalfExtents.Y);
        var halfZ = FixedQ4816.FromDouble(value: room.PlayerHalfExtents.Z);

        return new FixedRoom(
            FloorTop: (FixedQ4816.FromDouble(value: room.FloorY) + halfY),
            MaxX: (FixedQ4816.FromDouble(value: room.BoundsMax.X) - halfX),
            MaxZ: (FixedQ4816.FromDouble(value: room.BoundsMax.Y) - halfZ),
            MinX: (FixedQ4816.FromDouble(value: room.BoundsMin.X) + halfX),
            MinZ: (FixedQ4816.FromDouble(value: room.BoundsMin.Y) + halfZ)
        );
    }
}

/// <summary>
/// One avatar's movement state and the pure, deterministic per-tick step that advances it. Hollow Knight–tight: near-
/// instant acceleration, an instant facing snap, asymmetric gravity, a variable-height jump, coyote time, and jump
/// buffering. The state and the step are FIXED-POINT (<see cref="FixedQ4816"/>): the step is a pure function of
/// <c>(this state, intent, fixed dt, room)</c> with no float, no clock, no allocation, and no randomness — so a
/// recorded intent stream replays bit-identically, on every machine. Float appears only at the seams: the input
/// intent is converted in, and <see cref="PresentationPosition"/> / <see cref="Orientation"/> are converted out for
/// the renderer (presentation never feeds back into the sim).
/// </summary>
public sealed class PlatformerBody {
    public FixedVector3 Position;
    public FixedVector3 Velocity;
    public bool Grounded;
    public FixedQ4816 FacingYaw;

    private FixedQ4816 m_jumpBuffer;
    private FixedQ4816 m_coyote;

    public PlatformerBody(FixedVector3 position) {
        Position = position;
    }

    /// <summary>The position as a single-precision vector for the renderer's dynamic-transform buffer (presentation only).</summary>
    public Vector3 PresentationPosition => Position.ToVector3();
    /// <summary>The orientation quaternion (yaw about world up) for the dynamic-transform buffer (presentation only).</summary>
    public Quaternion Orientation => Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: ((float)FacingYaw));

    /// <summary>Advances one FIXED simulation tick. <paramref name="dt"/> is the constant tick period in seconds.</summary>
    public void Step(in PlayerIntent intent, PlatformerTuning tuning, FixedQ4816 dt, in FixedRoom room) {
        ArgumentNullException.ThrowIfNull(argument: tuning);

        // The intent is the float input seam; convert it into the fixed sim once, here.
        var move = new FixedVector2(
            X: FixedQ4816.FromDouble(value: intent.Move.X),
            Y: FixedQ4816.FromDouble(value: intent.Move.Y)
        );
        var hasInput = ((move.X != FixedQ4816.Zero) || (move.Y != FixedQ4816.Zero));

        // Forgiveness timers (read this tick, decayed for the next): coyote refreshes while grounded; the jump buffer
        // refreshes on a press edge.
        m_coyote = (Grounded ? tuning.CoyoteTime : FixedQ4816.Max(x: FixedQ4816.Zero, y: (m_coyote - dt)));
        m_jumpBuffer = (intent.JumpPressed ? tuning.JumpBufferTime : FixedQ4816.Max(x: FixedQ4816.Zero, y: (m_jumpBuffer - dt)));

        // Horizontal: accelerate the XZ velocity toward the camera-relative target. The facing snaps instantly to the
        // input direction (HK), independent of momentum.
        var horizontal = new FixedVector2(X: Velocity.X, Y: Velocity.Z);
        var target = (move * tuning.RunSpeed);
        var acceleration = (Grounded
            ? (hasInput ? tuning.GroundAcceleration : tuning.GroundDeceleration)
            : (hasInput ? tuning.AirAcceleration : tuning.AirDeceleration));

        horizontal = MoveToward(current: horizontal, target: target, maxDelta: (acceleration * dt));
        Velocity = (Velocity with { X = horizontal.X, Z = horizontal.Y });

        if (hasInput) {
            FacingYaw = FixedQ4816.Atan2(y: move.X, x: move.Y);
        }

        // Jump: fire when a buffered press meets a live coyote window (grounded or just-left a ledge).
        if ((m_jumpBuffer > FixedQ4816.Zero) && (m_coyote > FixedQ4816.Zero)) {
            Velocity = (Velocity with { Y = tuning.JumpSpeed });
            Grounded = false;
            m_jumpBuffer = FixedQ4816.Zero;
            m_coyote = FixedQ4816.Zero;
        }

        // Variable height: cutting upward velocity on the release edge turns a tap into a short hop.
        if (intent.JumpReleased && (Velocity.Y > FixedQ4816.Zero)) {
            Velocity = (Velocity with { Y = (Velocity.Y * tuning.JumpCutMultiplier) });
        }

        // Gravity (heavier while falling) + terminal velocity.
        var gravity = ((Velocity.Y > FixedQ4816.Zero) ? tuning.RiseGravity : tuning.FallGravity);

        Velocity = (Velocity with { Y = FixedQ4816.Max(x: (Velocity.Y - (gravity * dt)), y: -tuning.MaxFallSpeed) });

        // Integrate, then resolve against the floor and the four walls (per-axis clamp; this prototype's room is convex
        // and axis-aligned, so a clamp is exact).
        var next = (Position + (Velocity * dt));

        if (next.Y <= room.FloorTop) {
            next = (next with { Y = room.FloorTop });

            if (Velocity.Y < FixedQ4816.Zero) {
                Velocity = (Velocity with { Y = FixedQ4816.Zero });
            }

            Grounded = true;
        } else {
            Grounded = false;
        }

        if (next.X < room.MinX) { next = (next with { X = room.MinX }); Velocity = (Velocity with { X = FixedQ4816.Max(x: Velocity.X, y: FixedQ4816.Zero) }); }
        if (next.X > room.MaxX) { next = (next with { X = room.MaxX }); Velocity = (Velocity with { X = FixedQ4816.Min(x: Velocity.X, y: FixedQ4816.Zero) }); }
        if (next.Z < room.MinZ) { next = (next with { Z = room.MinZ }); Velocity = (Velocity with { Z = FixedQ4816.Max(x: Velocity.Z, y: FixedQ4816.Zero) }); }
        if (next.Z > room.MaxZ) { next = (next with { Z = room.MaxZ }); Velocity = (Velocity with { Z = FixedQ4816.Min(x: Velocity.Z, y: FixedQ4816.Zero) }); }

        Position = next;
    }

    private static FixedVector2 MoveToward(FixedVector2 current, FixedVector2 target, FixedQ4816 maxDelta) {
        var delta = (target - current);
        var distance = delta.Length;

        // distance == 0 (already at target) folds into the first test, so the divisor below is always positive.
        return (((distance <= maxDelta) || (distance <= FixedQ4816.Zero))
            ? target
            : (current + (delta * (maxDelta / distance))));
    }
}
