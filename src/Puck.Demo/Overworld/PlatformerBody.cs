using System.Numerics;
using Puck.Maths;

namespace Puck.Demo.Overworld;

/// <summary>
/// The fixed-point collision surfaces the simulation resolves against, derived ONCE from the authored (float)
/// <see cref="OverworldRoom"/> via deterministic rounding. Both the wall half-thickness AND the avatar's box
/// half-extents are folded in, so these are the exact planes a body's center clamps to — a body's face then rests
/// flush against the wall's inner face, not buried to the wall centerline.
/// </summary>
/// <param name="FloorTop">The world Y the body's center rests at on the floor.</param>
/// <param name="MinX">The minimum X the body's center may reach.</param>
/// <param name="MaxX">The maximum X the body's center may reach.</param>
/// <param name="MinZ">The minimum Z the body's center may reach.</param>
/// <param name="MaxZ">The maximum Z the body's center may reach.</param>
/// <param name="Consoles">The console stands as fixed-point obstacles + boot targets, in console-index order.</param>
/// <param name="InteractRange">The per-axis XZ distance from a stand's center within which interact boots it.</param>
/// <param name="Shelf">The shelf slots as fixed-point obstacles + pick-up targets, in library-index order.</param>
/// <param name="ShelfInteractRange">The per-axis XZ distance from a shelf slot's center within which interact picks up
/// its cartridge.</param>
/// <param name="WalkGrid">The optional baked walk grid (<see langword="null"/> = no world loaded, or a world with no
/// baked grid): when present, <see cref="PlatformerBody.Step"/> additionally clamps the body's center out of any
/// blocked cell, per axis, after the wall/obstacle clamps. A <see langword="null"/> grid is EXACTLY today's code
/// path — the walk-grid clamp is skipped entirely, not merely a no-op query.</param>
public readonly record struct FixedRoom(FixedQ4816 FloorTop, FixedQ4816 MinX, FixedQ4816 MaxX, FixedQ4816 MinZ, FixedQ4816 MaxZ, FixedConsole[] Consoles, FixedQ4816 InteractRange, FixedConsole[] Shelf, FixedQ4816 ShelfInteractRange, FixedWalkGrid? WalkGrid = null) {
    /// <summary>Resolves the fixed-point collision planes from an authored room (half-extents folded in).</summary>
    /// <param name="room">The authored room to resolve.</param>
    /// <param name="walkGrid">The optional baked walk grid to attach (default <see langword="null"/> — the walls are
    /// the sole authority, today's exact behavior).</param>
    /// <returns>The fixed-point collision surfaces.</returns>
    public static FixedRoom From(OverworldRoom room, FixedWalkGrid? walkGrid = null) {
        ArgumentNullException.ThrowIfNull(argument: room);

        var halfX = FixedQ4816.FromDouble(value: room.PlayerHalfExtents.X);
        var halfY = FixedQ4816.FromDouble(value: room.PlayerHalfExtents.Y);
        var halfZ = FixedQ4816.FromDouble(value: room.PlayerHalfExtents.Z);
        // The perimeter wall boxes are CENTERED on the bounds, so a wall's inner face sits one wall-half-thickness inside
        // the bound. Fold BOTH that thickness AND the player half-extent into the center clamp, so the body's face rests
        // flush against the inner face (bound ∓ wall) instead of sinking to the wall centerline.
        var wall = FixedQ4816.FromDouble(value: room.WallThickness);

        // Each console stand (and, identically, each shelf slot) becomes a full-height keep-out box: its faces expanded
        // by the player half-extents, so the body's center clamps to a surface its own face rests flush against — the
        // same fold-in as the walls.
        var consoles = ToFixedObstacles(centers: room.Consoles.Select(static stand => (stand.Center, stand.HalfExtents)), halfX: halfX, halfZ: halfZ);
        var shelf = ToFixedObstacles(centers: room.Shelf.Select(static slot => (slot.Center, slot.HalfExtents)), halfX: halfX, halfZ: halfZ);

        return new FixedRoom(
            Consoles: consoles,
            FloorTop: (FixedQ4816.FromDouble(value: room.FloorY) + halfY),
            InteractRange: FixedQ4816.FromDouble(value: room.ConsoleInteractRange),
            MaxX: (FixedQ4816.FromDouble(value: room.BoundsMax.X) - wall - halfX),
            MaxZ: (FixedQ4816.FromDouble(value: room.BoundsMax.Y) - wall - halfZ),
            MinX: (FixedQ4816.FromDouble(value: room.BoundsMin.X) + wall + halfX),
            MinZ: (FixedQ4816.FromDouble(value: room.BoundsMin.Y) + wall + halfZ),
            Shelf: shelf,
            ShelfInteractRange: FixedQ4816.FromDouble(value: room.ShelfInteractRange),
            WalkGrid: walkGrid
        );
    }

    private static FixedConsole[] ToFixedObstacles(IEnumerable<(Vector2 Center, Vector3 HalfExtents)> centers, FixedQ4816 halfX, FixedQ4816 halfZ) {
        var list = centers.ToArray();
        var result = new FixedConsole[list.Length];

        for (var index = 0; (index < result.Length); index++) {
            var (center, halfExtents) = list[index];
            var centerX = FixedQ4816.FromDouble(value: center.X);
            var centerZ = FixedQ4816.FromDouble(value: center.Y);
            var expandX = (FixedQ4816.FromDouble(value: halfExtents.X) + halfX);
            var expandZ = (FixedQ4816.FromDouble(value: halfExtents.Z) + halfZ);

            result[index] = new FixedConsole(
                CenterX: centerX,
                CenterZ: centerZ,
                MaxX: (centerX + expandX),
                MaxZ: (centerZ + expandZ),
                MinX: (centerX - expandX),
                MinZ: (centerZ - expandZ)
            );
        }

        return result;
    }
}

/// <summary>One console stand's (or shelf slot's) fixed-point footprint: the EXPANDED keep-out bounds the body's
/// center resolves against (player half-extents folded in) plus the unexpanded center the interact range measures
/// from. Shared shape for both obstacle families — a stand and a slot resolve identically.</summary>
/// <param name="CenterX">The stand's center X (unexpanded).</param>
/// <param name="CenterZ">The stand's center Z (unexpanded).</param>
/// <param name="MinX">The minimum X of the keep-out (a body center inside is pushed out).</param>
/// <param name="MaxX">The maximum X of the keep-out.</param>
/// <param name="MinZ">The minimum Z of the keep-out.</param>
/// <param name="MaxZ">The maximum Z of the keep-out.</param>
public readonly record struct FixedConsole(FixedQ4816 CenterX, FixedQ4816 CenterZ, FixedQ4816 MinX, FixedQ4816 MaxX, FixedQ4816 MinZ, FixedQ4816 MaxZ);

/// <summary>
/// One avatar's movement state and the pure, deterministic per-tick step that advances it. Hollow Knight–tight: near-
/// instant acceleration, an instant facing snap, asymmetric gravity, a variable-height jump, coyote time, and jump
/// buffering. The state and the step are FIXED-POINT (<see cref="FixedQ4816"/>): the step is a pure function of
/// <c>(this state, intent, fixed dt, room)</c> with no float, no clock, no allocation, and no randomness — so a
/// recorded intent stream replays bit-identically, on every machine. Float appears only at the seams: the input
/// intent is converted in, and <see cref="RenderRelativePositionAt"/> / <see cref="OrientationAt"/> are converted out
/// for the renderer (presentation never feeds back into the sim).
/// </summary>
public sealed class PlatformerBody {
    public WorldCoord3 Position;
    public FixedVector3 Velocity;
    public bool Grounded;
    public FixedQ4816 FacingYaw;

    private FixedQ4816 m_jumpBuffer;
    private FixedQ4816 m_coyote;
    // The previous tick's presentation state, snapshotted at the top of each Step so the renderer can interpolate
    // toward the current tick by the frame's InterpolationAlpha. PRESENTATION ONLY — mirrors Position/FacingYaw, is
    // never folded into StateHash, and is never read back by the fixed-point step.
    private WorldCoord3 m_previousPosition;
    private FixedQ4816 m_previousFacingYaw;

    public PlatformerBody(WorldCoord3 position) {
        Position = position;
        m_previousPosition = position; // pre-step state == spawn until the first Step, so alpha-lerp is a no-op
    }

    /// <summary>The position relative to <paramref name="renderOrigin"/>, interpolated between the previous and current
    /// fixed tick by <paramref name="alpha"/>, as a single-precision vector for the renderer's dynamic-transform buffer.
    /// The rebase subtraction happens in FIXED POINT before the float cast, so the renderer only ever sees small
    /// camera-relative coordinates (precise no matter how far the body sits from the world origin); the interpolation is
    /// pure presentation (never hashed, never fed back into the sim).</summary>
    /// <param name="renderOrigin">The per-frame world anchor the position is expressed relative to (the cell-aware delta is taken in fixed point before the float cast).</param>
    /// <param name="alpha">The fraction in <c>[0, 1)</c> between the previous and current fixed tick (the frame's <c>InterpolationAlpha</c>).</param>
    /// <returns>The render-relative, interpolated position.</returns>
    public Vector3 RenderRelativePositionAt(WorldCoord3 renderOrigin, float alpha) =>
        Vector3.Lerp(
            amount: alpha,
            value1: m_previousPosition.ToRenderRelative(origin: renderOrigin),
            value2: Position.ToRenderRelative(origin: renderOrigin)
        );
    /// <summary>The orientation quaternion (yaw about world up), interpolated between the previous and current fixed tick
    /// by <paramref name="alpha"/> along the SHORTEST arc — the yaw is an <c>Atan2</c> angle, so the delta is wrapped into
    /// <c>[-π, π]</c> to avoid spinning the long way across the wrap. PRESENTATION ONLY.</summary>
    /// <param name="alpha">The fraction in <c>[0, 1)</c> between the previous and current fixed tick.</param>
    /// <returns>The interpolated yaw orientation.</returns>
    public Quaternion OrientationAt(float alpha) {
        var previousYaw = ((float)m_previousFacingYaw);
        var delta = (((float)FacingYaw) - previousYaw);

        delta -= (MathF.Tau * MathF.Round(x: (delta / MathF.Tau))); // wrap the delta into [-π, π] for the shortest arc

        return Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (previousYaw + (delta * alpha)));
    }

    /// <summary>Advances one FIXED simulation tick. <paramref name="dt"/> is the constant tick period in seconds.</summary>
    public void Step(in PlayerIntent intent, PlatformerTuning tuning, FixedQ4816 dt, in FixedRoom room) {
        ArgumentNullException.ThrowIfNull(argument: tuning);

        // Snapshot the pre-step presentation state so the renderer can interpolate from the previous tick to this one by
        // the frame's alpha. Captured at the TOP of every Step, so across a multi-tick frame the previous state is the
        // one tick behind current (the two most-recent states) — PRESENTATION ONLY, never enters the state hash.
        m_previousPosition = Position;
        m_previousFacingYaw = FacingYaw;

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
        var target = (move * (intent.RunHeld ? (tuning.RunSpeed * tuning.SprintMultiplier) : tuning.RunSpeed));
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

        // Integrate and resolve collisions in the body's CELL-LOCAL frame (the room planes are expressed there, and the
        // body shares the room's cell). Per-axis clamp — this prototype's room is convex and axis-aligned, so it's exact.
        var nextLocal = (Position.Local + (Velocity * dt));

        if (nextLocal.Y <= room.FloorTop) {
            nextLocal = (nextLocal with { Y = room.FloorTop });

            if (Velocity.Y < FixedQ4816.Zero) {
                Velocity = (Velocity with { Y = FixedQ4816.Zero });
            }

            Grounded = true;
        } else {
            Grounded = false;
        }

        if (nextLocal.X < room.MinX) { nextLocal = (nextLocal with { X = room.MinX }); Velocity = (Velocity with { X = FixedQ4816.Max(x: Velocity.X, y: FixedQ4816.Zero) }); }
        if (nextLocal.X > room.MaxX) { nextLocal = (nextLocal with { X = room.MaxX }); Velocity = (Velocity with { X = FixedQ4816.Min(x: Velocity.X, y: FixedQ4816.Zero) }); }
        if (nextLocal.Z < room.MinZ) { nextLocal = (nextLocal with { Z = room.MinZ }); Velocity = (Velocity with { Z = FixedQ4816.Max(x: Velocity.Z, y: FixedQ4816.Zero) }); }
        if (nextLocal.Z > room.MaxZ) { nextLocal = (nextLocal with { Z = room.MaxZ }); Velocity = (Velocity with { Z = FixedQ4816.Min(x: Velocity.Z, y: FixedQ4816.Zero) }); }

        // Console stands and shelf slots are both solid full-height keep-outs: push a center that landed inside one out
        // along the axis of least penetration (per-axis and exact like the walls; the obstacles are spaced apart, so one
        // pass resolves), and stop the velocity component that was driving inward. Index order — deterministic; consoles
        // resolve before the shelf (an arbitrary but fixed tie-break; the two families never overlap in practice).
        foreach (var console in room.Consoles) {
            ResolveAgainstObstacle(obstacle: console, nextLocal: ref nextLocal, velocity: ref Velocity);
        }

        foreach (var slot in room.Shelf) {
            ResolveAgainstObstacle(obstacle: slot, nextLocal: ref nextLocal, velocity: ref Velocity);
        }

        // The baked walk grid (optional — a NULL grid skips this block entirely, so a room with no world loaded takes
        // EXACTLY today's code path). Per-axis clamp, same shape and same order as the wall clamp above: attempt the X
        // move first — if the destination body-center cell is blocked, hold X at its pre-move coordinate (provably
        // safe by induction from this same clamp last tick; re-deriving a boundary from the current cell is
        // directionally ambiguous ON the boundary and would creep through blockers) and zero the inward velocity
        // component; then the same for Z (Z's blocked check uses the already-resolved X, matching
        // the wall clamp's own per-axis sequencing so a body sliding along a blocked edge behaves identically to
        // sliding along a wall or console).
        if (room.WalkGrid is { } grid) {
            var clampedX = grid.ClampAxisX(current: Position.Local.X, candidate: nextLocal.X, otherAxis: Position.Local.Z);

            if (clampedX != nextLocal.X) {
                Velocity = (Velocity with { X = FixedQ4816.Zero });
            }

            nextLocal = (nextLocal with { X = clampedX });

            var clampedZ = grid.ClampAxisZ(current: Position.Local.Z, candidate: nextLocal.Z, otherAxis: nextLocal.X);

            if (clampedZ != nextLocal.Z) {
                Velocity = (Velocity with { Z = FixedQ4816.Zero });
            }

            nextLocal = (nextLocal with { Z = clampedZ });
        }

        // Re-anchor (carry the offset into the cell index if it ever leaves the centred range). The room planes are
        // expressed in the SPAWN cell's local frame, so this single-room prototype assumes the body stays in that cell —
        // which the clamp guarantees, making Normalize a no-op here. A room SPANNING cells would first have to rebase the
        // planes into the body's current cell (via Delta) before the clamp above.
        Position = (Position with { Local = nextLocal }).Normalize();
    }

    private static void ResolveAgainstObstacle(FixedConsole obstacle, ref FixedVector3 nextLocal, ref FixedVector3 velocity) {
        if (
            (nextLocal.X <= obstacle.MinX) || (nextLocal.X >= obstacle.MaxX) ||
            (nextLocal.Z <= obstacle.MinZ) || (nextLocal.Z >= obstacle.MaxZ)
        ) {
            return;
        }

        var toMinX = (nextLocal.X - obstacle.MinX);
        var toMaxX = (obstacle.MaxX - nextLocal.X);
        var toMinZ = (nextLocal.Z - obstacle.MinZ);
        var toMaxZ = (obstacle.MaxZ - nextLocal.Z);
        var depthX = FixedQ4816.Min(x: toMinX, y: toMaxX);
        var depthZ = FixedQ4816.Min(x: toMinZ, y: toMaxZ);

        if (depthX <= depthZ) {
            if (toMinX <= toMaxX) { nextLocal = (nextLocal with { X = obstacle.MinX }); velocity = (velocity with { X = FixedQ4816.Min(x: velocity.X, y: FixedQ4816.Zero) }); }
            else { nextLocal = (nextLocal with { X = obstacle.MaxX }); velocity = (velocity with { X = FixedQ4816.Max(x: velocity.X, y: FixedQ4816.Zero) }); }
        } else {
            if (toMinZ <= toMaxZ) { nextLocal = (nextLocal with { Z = obstacle.MinZ }); velocity = (velocity with { Z = FixedQ4816.Min(x: velocity.Z, y: FixedQ4816.Zero) }); }
            else { nextLocal = (nextLocal with { Z = obstacle.MaxZ }); velocity = (velocity with { Z = FixedQ4816.Max(x: velocity.Z, y: FixedQ4816.Zero) }); }
        }
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
