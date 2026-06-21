using System.Numerics;

namespace Puck.Demo.MiniAction;

/// <summary>
/// One avatar's movement state and the pure, deterministic per-tick step that advances it. Hollow Knight–tight: near-
/// instant acceleration, an instant facing snap, asymmetric gravity, a variable-height jump, coyote time, and jump
/// buffering. The step is a pure function of <c>(this state, intent, fixed dt, room)</c> — no clock, no allocation, no
/// randomness — so a recorded intent stream replays bit-identically.
/// </summary>
public sealed class PlatformerBody {
    public Vector3 Position;
    public Vector3 Velocity;
    public bool Grounded;
    public float FacingYaw;
    private float m_jumpBuffer;
    private float m_coyote;

    public PlatformerBody(Vector3 position) {
        Position = position;
    }

    /// <summary>The orientation quaternion (yaw about world up) for the dynamic-transform buffer.</summary>
    public Quaternion Orientation => Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: FacingYaw);

    /// <summary>Advances one FIXED simulation tick. <paramref name="dt"/> is the constant tick period in seconds.</summary>
    public void Step(in PlayerIntent intent, PlatformerTuning tuning, float dt, in MiniActionRoom room) {
        var hasInput = (intent.Move.LengthSquared() > 1e-6f);

        // Forgiveness timers (read this tick, decayed for the next): coyote refreshes while grounded; the jump buffer
        // refreshes on a press edge.
        m_coyote = (Grounded ? tuning.CoyoteTime : MathF.Max(0f, (m_coyote - dt)));
        m_jumpBuffer = (intent.JumpPressed ? tuning.JumpBufferTime : MathF.Max(0f, (m_jumpBuffer - dt)));

        // Horizontal: accelerate the XZ velocity toward the camera-relative target. The facing snaps instantly to the
        // input direction (HK), independent of momentum.
        var horizontal = new Vector2(Velocity.X, Velocity.Z);
        var target = (intent.Move * tuning.RunSpeed);
        var acceleration = (Grounded
            ? (hasInput ? tuning.GroundAcceleration : tuning.GroundDeceleration)
            : (hasInput ? tuning.AirAcceleration : tuning.AirDeceleration));

        horizontal = MoveToward(current: horizontal, target: target, maxDelta: (acceleration * dt));
        Velocity.X = horizontal.X;
        Velocity.Z = horizontal.Y;

        if (hasInput) {
            FacingYaw = MathF.Atan2(y: intent.Move.X, x: intent.Move.Y);
        }

        // Jump: fire when a buffered press meets a live coyote window (grounded or just-left a ledge).
        if ((m_jumpBuffer > 0f) && (m_coyote > 0f)) {
            Velocity.Y = tuning.JumpSpeed;
            Grounded = false;
            m_jumpBuffer = 0f;
            m_coyote = 0f;
        }

        // Variable height: cutting upward velocity on the release edge turns a tap into a short hop.
        if (intent.JumpReleased && (Velocity.Y > 0f)) {
            Velocity.Y *= tuning.JumpCutMultiplier;
        }

        // Gravity (heavier while falling) + terminal velocity.
        var gravity = ((Velocity.Y > 0f) ? tuning.RiseGravity : tuning.FallGravity);

        Velocity.Y = MathF.Max((Velocity.Y - (gravity * dt)), -tuning.MaxFallSpeed);

        // Integrate, then resolve against the floor and the four walls (per-axis clamp; this prototype's room is convex
        // and axis-aligned, so a clamp is exact).
        var next = (Position + (Velocity * dt));
        var floorTop = (room.FloorY + room.PlayerHalfExtents.Y);

        if (next.Y <= floorTop) {
            next.Y = floorTop;

            if (Velocity.Y < 0f) {
                Velocity.Y = 0f;
            }

            Grounded = true;
        } else {
            Grounded = false;
        }

        var minX = (room.BoundsMin.X + room.PlayerHalfExtents.X);
        var maxX = (room.BoundsMax.X - room.PlayerHalfExtents.X);
        var minZ = (room.BoundsMin.Y + room.PlayerHalfExtents.Z);
        var maxZ = (room.BoundsMax.Y - room.PlayerHalfExtents.Z);

        if (next.X < minX) { next.X = minX; Velocity.X = MathF.Max(Velocity.X, 0f); }
        if (next.X > maxX) { next.X = maxX; Velocity.X = MathF.Min(Velocity.X, 0f); }
        if (next.Z < minZ) { next.Z = minZ; Velocity.Z = MathF.Max(Velocity.Z, 0f); }
        if (next.Z > maxZ) { next.Z = maxZ; Velocity.Z = MathF.Min(Velocity.Z, 0f); }

        Position = next;
    }

    private static Vector2 MoveToward(Vector2 current, Vector2 target, float maxDelta) {
        var delta = (target - current);
        var distance = delta.Length();

        return (((distance <= maxDelta) || (distance < 1e-6f))
            ? target
            : (current + (delta * (maxDelta / distance))));
    }
}
