using Puck.Maths;

namespace Puck.Physics;

/// <summary>One swept contact solve a step-up runs: the caller's own sweep over its own contact field, bound to one
/// body's orientation, volumes and up axis, so the step-up measures against exactly what the body's ordinary step
/// resolves against.</summary>
public interface IFixedContactStep {
    /// <summary>Resolves one swept move from <paramref name="previousPosition"/> to <paramref name="position"/>, as
    /// <see cref="IContactField.ResolveSweep"/> does.</summary>
    /// <param name="previousPosition">The foot point the move starts from.</param>
    /// <param name="position">The foot point the move aims for (in/out): the resolved foot point on return.</param>
    /// <param name="velocity">The body's velocity (in/out): the component driving into a resolved surface is
    /// removed.</param>
    /// <returns>The move's resolution.</returns>
    ContactResolution Sweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity);
    /// <summary>Resolves a support-driven landing. Fields composing untrusted support can admit the descent as
    /// part of the response rather than treating it as the body's own integrated motion.</summary>
    /// <param name="previousPosition">The position before the descent.</param>
    /// <param name="responseOrigin">The body's own motion endpoint before any support-selected lift or descent.</param>
    /// <param name="position">The proposed landing, then its admitted position.</param>
    /// <param name="velocity">The incoming velocity, then its admitted response.</param>
    /// <returns>The admitted landing's contact verdict.</returns>
    ContactResolution SweepSupport(in FixedVector3 previousPosition, in FixedVector3 responseOrigin, ref FixedVector3 position, ref FixedVector3 velocity) =>
        Sweep(previousPosition: in previousPosition, position: ref position, velocity: ref velocity);
    /// <summary>Finds how far the body descends along its up axis from <paramref name="start"/> before anything the
    /// sweep resolves against first comes within the contact skin, as <see cref="IContactField.TrySupportDistance"/>
    /// does.</summary>
    /// <param name="start">The foot point the descent starts from.</param>
    /// <param name="reach">The greatest descent searched, in metres, positive.</param>
    /// <param name="descent">The least descent, in metres, at which the body first comes within the skin of a
    /// surface.</param>
    /// <returns><see langword="true"/> when a surface is met within the reach.</returns>
    bool TrySupportDistance(in FixedVector3 start, FixedQ4816 reach, out FixedQ4816 descent);
}
/// <summary>The accepted outcome of <see cref="FixedStepUp.TryStep{TStep}"/>.</summary>
/// <param name="Position">The foot point on the riser's landing surface, including its rounded edge.</param>
/// <param name="Velocity">The velocity after the step-up's moves, projected onto the landing support.</param>
/// <param name="Resolution">The landing move's resolution, grounded on a walkable surface.</param>
public readonly record struct FixedStepUpResult(FixedVector3 Position, FixedVector3 Velocity, ContactResolution Resolution);
/// <summary>What one <see cref="FixedStepUp.Attempt{TStep}"/> decided, in the order its checks run.</summary>
public enum FixedStepUpOutcome : byte {
    /// <summary>The step was accepted: the body stands on the riser's top.</summary>
    Taken = 0,
    /// <summary>Nothing to step: no step height, or no planar displacement to carry over a riser.</summary>
    NotAttempted,
    /// <summary>The blocked sweep reported an edge taller than the step height, so the face is a wall.</summary>
    OverHeight,
    /// <summary>The raise was refused or gained no height: a ceiling lower than any lift.</summary>
    Ceiling,
    /// <summary>A face still stood in the way at the raised height, or the raised move got no further than the blocked
    /// step: a wall, or a riser taller than the step height.</summary>
    Wall,
    /// <summary>The body cleared the riser at the step height but the lowering found no walkable support it proves within
    /// reach: a refused step.</summary>
    NoLanding,
    /// <summary>The body cleared the riser and landed, but on nothing it may stand on as a step: above the step height,
    /// no higher than the blocked step, or not grounded. A refused step.</summary>
    Unstandable,
}
/// <summary>Reads a <see cref="FixedStepUpOutcome"/>.</summary>
public static class FixedStepUpOutcomes {
    /// <summary>Returns whether an outcome is a refused step: the body cleared the riser at the step height and could not
    /// stand on what lay beyond it, as opposed to a wall, a riser taller than the step height or nothing to step.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns><see langword="true"/> for <see cref="FixedStepUpOutcome.NoLanding"/> and
    /// <see cref="FixedStepUpOutcome.Unstandable"/>.</returns>
    public static bool IsRefusal(this FixedStepUpOutcome outcome) => (outcome is FixedStepUpOutcome.NoLanding or FixedStepUpOutcome.Unstandable);
}
/// <summary>Lifts a grounded body over a riser its ordinary step met as a wall: raise by the step height, move the
/// step's planar displacement, and lower onto what lies beneath, each through the caller's own sweep.</summary>
/// <remarks>
/// <para>The step is accepted only when the raised move gets further along the planar displacement than the blocked
/// step did, and the landing grounds on a walkable surface (the contact field's own <c>cos(maxSlope)</c> test, so a
/// steeper face is never stepped onto), rises no more than the step height above the start, and stands higher than
/// the blocked step did. Otherwise the blocked step stands, so a riser taller than the step height, or any wall, is
/// still a wall.</para>
/// <para>The lowering reaches down to the start's height, or to where the original step would have ended along the up
/// axis where that is lower, so a step that rose along its ground finds a surface that rises less steeply beyond the
/// riser (a crest), and a step down a slope finds the slope. The caller attempts a step only for a body on the ground,
/// so a rising step here is one that follows its ground, never a jump. It asks the sweep's own support
/// distance (<see cref="IFixedContactStep.TrySupportDistance"/>) and lands on it, the contact skin off the riser's top: the
/// landing stands within the solve's resting band of the riser's top and resolves it as ground, where one deep move would read the riser's
/// side as the nearer face.</para>
/// <para>Bounded: two sweeps reject a wall, and an accepted step costs four sweeps and one support-distance query.</para>
/// </remarks>
public static class FixedStepUp {
    /// <summary>Projects a velocity onto the walkable support a body stands on, which is what a walking body aims its
    /// step by before it moves: the ground it rests on where it stands, found by one sweep that does not move it (a
    /// resting contact, <see cref="FixedContactPushMath.RestingBand"/>), else the support a ground snap
    /// (<see cref="TrySnap"/>) finds within the distance. A body that stands on its ground, the ordinary case after any
    /// grounded step, pays one sweep and no support-distance query.</summary>
    /// <typeparam name="TStep">The caller's contact sweep.</typeparam>
    /// <param name="step">The contact sweep.</param>
    /// <param name="start">Where the body stands.</param>
    /// <param name="velocity">The velocity to project onto the support.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="distance">The greatest descent a snap searches; zero or less searches nothing.</param>
    /// <param name="result">The support's resolution and the projected velocity; its position is where the body stands
    /// or the snap's landing.</param>
    /// <returns>Whether proven walkable support was found.</returns>
    public static bool TryFollowSupport<TStep>(ref TStep step, in FixedVector3 start, in FixedVector3 velocity,
        in FixedVector3 up, FixedQ4816 distance, out FixedStepUpResult result)
        where TStep : IFixedContactStep, allows ref struct {
        result = default;
        if (distance <= FixedQ4816.Zero) { return false; }
        var stood = start;
        var moved = velocity;
        var resting = step.SweepSupport(position: ref stood, previousPosition: in start, responseOrigin: start, velocity: ref moved);

        return ((stood == start) && TryAccept(landed: start, landing: in resting, moved: moved, uncertain: default, result: out result)) ||
            TrySnap(distance: distance, result: out result, start: in start, step: ref step, up: in up, velocity: in velocity);
    }
    // A support sweep proves walkable support when it was not refused, grounded, and left uncertain only the pairs the
    // pose it started from did; the accepted velocity follows the support.
    private static bool TryAccept(in FixedVector3 landed, in ContactResolution landing, in FixedVector3 moved, ContactUncertainty uncertain,
        out FixedStepUpResult result) {
        if ((landing.Refusal != ContactRefusal.None) || !landing.Uncertainty.IsWithin(earlier: uncertain) || !landing.Grounded) {
            result = default;
            return false;
        }
        result = new FixedStepUpResult(Position: landed, Velocity: moved.RejectFrom(unitAxis: landing.GroundNormal), Resolution: landing);
        return true;
    }

    /// <summary>Finds the first walkable support below a position within a caller-supplied snap distance: one
    /// support-distance query, then one landing sweep down to it, which stands the body the contact skin off the
    /// support, where the solve's resting band (<see cref="FixedContactPushMath.RestingBand"/>) grounds it. Shared by
    /// a step up's lowering; an unsuccessful search changes nothing.</summary>
    /// <typeparam name="TStep">The caller's contact sweep.</typeparam>
    /// <param name="step">The contact sweep.</param>
    /// <param name="start">The position to lower from.</param>
    /// <param name="velocity">The velocity to project onto the support.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="distance">The greatest permitted descent.</param>
    /// <param name="result">The accepted landing.</param>
    /// <param name="responseOrigin">The body's own motion endpoint; omitted for an ordinary snap from start.</param>
    /// <param name="uncertain">The pose lowered from's own uncertainty (<see cref="ContactResolution.Uncertainty"/>). The
    /// landing must ground, and may leave uncertain only pairs this one names: the snap moves the body onto support it
    /// proves and adds no volume and collider it cannot prove clear or in contact, so a field that was clear at the start
    /// and is newly unproved at the landing refuses it.</param>
    /// <returns>Whether the search found proven walkable support.</returns>
    public static bool TrySnap<TStep>(ref TStep step, in FixedVector3 start, in FixedVector3 velocity,
        in FixedVector3 up, FixedQ4816 distance, out FixedStepUpResult result, ContactUncertainty uncertain = default,
        FixedVector3? responseOrigin = null)
        where TStep : IFixedContactStep, allows ref struct {
        result = default;
        if ((distance <= FixedQ4816.Zero) || !step.TrySupportDistance(descent: out var descent, reach: distance, start: in start)) {
            return false;
        }
        var landed = (start - (up * descent));
        var moved = velocity;
        var landing = step.SweepSupport(position: ref landed, previousPosition: in start,
            responseOrigin: responseOrigin ?? start, velocity: ref moved);

        return TryAccept(landed: landed, landing: in landing, moved: moved, uncertain: uncertain, result: out result);
    }

    /// <summary>Gets the radius, in metres, of the probe <see cref="Stands"/> lowers along a body's axis: small enough
    /// that it meets an edge only where the axis passes within this distance of it.</summary>
    public static FixedQ4816 AxisProbeRadius { get; } = FixedQ4816.FromRawBits(value: 512L);

    /// <summary>Returns whether a body resting on support stands there rather than on an edge it rolls off: the ground
    /// under its foot point, where the axis its weight acts along meets the ground, lies no deeper below the foot point
    /// than the plane of the support it rests on reaches across the body's footprint. A body on a face, level or a
    /// walkable slope, has that plane under its axis; a body whose axis has passed an edge rests on the edge alone,
    /// whose normal leans out over a drop, and the next steps roll it off. A probe of radius
    /// <see cref="AxisProbeRadius"/> is lowered from the foot point through the field's own support distance, so every
    /// provider answers it as it answers a ground snap; it moves no body.</summary>
    /// <param name="field">The contact field the body rests in.</param>
    /// <param name="position">The body's foot point, which no volume of the body lies below along
    /// <paramref name="up"/>.</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's volumes, whose extent across <paramref name="up"/> bounds how far the support
    /// may lie from the axis.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="groundNormal">The unit normal of the walkable support the body rests on
    /// (<see cref="ContactResolution.GroundNormal"/>).</param>
    /// <returns><see langword="true"/> when ground lies under the axis within the depth the support's plane allows;
    /// <see langword="false"/> when it does not, or the normal does not face up.</returns>
    public static bool Stands(IContactField field, in FixedVector3 position, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, in FixedVector3 groundNormal) {
        var rise = FixedVector3.Dot(left: groundNormal, right: up);

        if (rise <= FixedQ4816.Zero) {
            return false;
        }

        // The support plane drops by its slope over the distance from the contact to the axis, and the contact lies
        // no further from the axis than the body reaches up the slope, against the way the normal leans.
        var depth = AxisProbeRadius;
        var lean = groundNormal.RejectFrom(unitAxis: up);

        if (lean.TryLength(length: out var leaning) && (leaning > FixedQ4816.Zero)) {
            var downhill = (lean / leaning);
            var footprint = FixedQ4816.Zero;

            foreach (var volume in volumes) {
                footprint = FixedQ4816.Max(x: footprint, y: -FixedColliderBounds.MinimumProjection(normal: downhill,
                    orientation: in orientation, position: FixedVector3.Zero, volume: in volume));
            }

            depth += ((footprint * leaning) / rise);
        }

        ReadOnlySpan<FixedBodyColliderVolume> probe = [FixedBodyColliderVolume.Sphere(center: (up * AxisProbeRadius), radius: AxisProbeRadius)];

        return field.TrySupportDistance(descent: out _, orientation: FixedQuaternion.Identity, position: in position,
            reach: depth, up: in up, volumes: probe);
    }

    /// <summary>Tries the step-up for one blocked step.</summary>
    /// <typeparam name="TStep">The caller's sweep.</typeparam>
    /// <param name="step">The sweep every move and probe runs through.</param>
    /// <param name="start">The foot point the step started from.</param>
    /// <param name="intended">The foot point the step aimed for, before contact resolution.</param>
    /// <param name="velocity">The velocity the step integrated, before contact resolution.</param>
    /// <param name="up">The body's up axis, a unit vector.</param>
    /// <param name="stepHeight">The tallest riser, in world units, the body steps onto; zero or less never steps.</param>
    /// <param name="blocked">The foot point the ordinary, blocked step resolved to.</param>
    /// <param name="result">The accepted step, or <see langword="default"/> when the blocked step stands.</param>
    /// <param name="requiredStepHeight">The edge clearance reported by the blocked sweep.</param>
    /// <returns><see langword="true"/> when the step-up is accepted.</returns>
    public static bool TryStep<TStep>(ref TStep step, in FixedVector3 start, in FixedVector3 intended, in FixedVector3 velocity,
        in FixedVector3 up, FixedQ4816 stepHeight, in FixedVector3 blocked, out FixedStepUpResult result, FixedQ4816 requiredStepHeight = default) where TStep : IFixedContactStep, allows ref struct =>
        (Attempt(blocked: in blocked, intended: in intended, requiredStepHeight: requiredStepHeight, result: out result, start: in start,
            step: ref step, stepHeight: stepHeight, up: in up, velocity: in velocity) == FixedStepUpOutcome.Taken);

    /// <summary>Tries the step-up for one blocked step and says what decided it, as <see cref="TryStep{TStep}"/> does
    /// with its answer; a caller that counts refused steps (<see cref="FixedStepUpOutcomes.IsRefusal"/>) reads the
    /// outcome.</summary>
    /// <typeparam name="TStep">The caller's sweep.</typeparam>
    /// <param name="step">The sweep every move and probe runs through.</param>
    /// <param name="start">The foot point the step started from.</param>
    /// <param name="intended">The foot point the step aimed for, before contact resolution.</param>
    /// <param name="velocity">The velocity the step integrated, before contact resolution.</param>
    /// <param name="up">The body's up axis, a unit vector.</param>
    /// <param name="stepHeight">The tallest riser, in world units, the body steps onto; zero or less never steps.</param>
    /// <param name="blocked">The foot point the ordinary, blocked step resolved to.</param>
    /// <param name="result">The accepted step, or <see langword="default"/> when the blocked step stands.</param>
    /// <param name="requiredStepHeight">The edge clearance reported by the blocked sweep.</param>
    /// <returns><see cref="FixedStepUpOutcome.Taken"/> when the step-up is accepted, else the check that refused it.</returns>
    public static FixedStepUpOutcome Attempt<TStep>(ref TStep step, in FixedVector3 start, in FixedVector3 intended, in FixedVector3 velocity,
        in FixedVector3 up, FixedQ4816 stepHeight, in FixedVector3 blocked, out FixedStepUpResult result, FixedQ4816 requiredStepHeight = default) where TStep : IFixedContactStep, allows ref struct {
        result = default;

        var delta = (intended - start);
        var vertical = FixedVector3.Dot(
            left: delta,
            right: up
        );
        var planar = delta.RejectFrom(unitAxis: up);

        if (
            (stepHeight <= FixedQ4816.Zero) ||
            (planar == FixedVector3.Zero)
        ) {
            return FixedStepUpOutcome.NotAttempted;
        }
        if (stepHeight < requiredStepHeight) {
            return FixedStepUpOutcome.OverHeight;
        }

        var blockedOffset = (blocked - start);
        var blockedProgress = FixedVector3.Dot(
            left: blockedOffset,
            right: planar
        );
        // Raise. A ceiling lower than the step height stops it early, and the lowering gives back only what it gained.
        var moved = velocity;
        var raised = (start + (up * stepHeight));

        if (step.Sweep(
            position: ref raised,
            previousPosition: in start,
            velocity: ref moved
        ).Refusal != ContactRefusal.None) {
            return FixedStepUpOutcome.Ceiling;
        }

        var lift = FixedVector3.Dot(
            left: (raised - start),
            right: up
        );

        if (lift <= FixedQ4816.Zero) {
            return FixedStepUpOutcome.Ceiling;
        }

        // Carry the planar displacement over the riser. A face still in the way at the raised height is a wall.
        var across = (raised + planar);
        var acrossResolution = step.Sweep(position: ref across, previousPosition: in raised, velocity: ref moved);

        if (
            (acrossResolution.Refusal != ContactRefusal.None) || acrossResolution.Unproved ||
            (acrossResolution.ObstructionNormal != FixedVector3.Zero) ||
            (FixedVector3.Dot(
                left: (across - start),
                right: planar
            ) <= blockedProgress)
        ) {
            return FixedStepUpOutcome.Wall;
        }

        // Lower onto the support, down to the start's height, or to where the original step would have ended along up
        // where that is lower (a step down a slope). A step that rose along its ground (a walk up a slope) must still
        // reach a surface that rises less steeply beyond the riser, as a deck does past a crest; the landing still
        // has to stand higher than the blocked step did. Nothing met in that range leaves the body in the air, which is
        // not a step.
        if (!TrySnap(distance: (lift - FixedQ4816.Min(x: vertical, y: FixedQ4816.Zero)), result: out var landingResult, start: in across, step: ref step,
            up: in up, velocity: in moved, responseOrigin: intended)) {
            return FixedStepUpOutcome.NoLanding;
        }
        var landed = landingResult.Position;
        var landing = landingResult.Resolution;

        moved = landingResult.Velocity;
        var rise = FixedVector3.Dot(
            left: (landed - start),
            right: up
        );

        if (
            (landing.Refusal != ContactRefusal.None) ||
            !landing.Grounded ||
            (rise > stepHeight) ||
            (rise <= FixedVector3.Dot(
                left: blockedOffset,
                right: up
            ))
        ) {
            return FixedStepUpOutcome.Unstandable;
        }

        result = new FixedStepUpResult(
            Position: landed,
            Resolution: landing,
            Velocity: moved
        );

        return FixedStepUpOutcome.Taken;
    }
}
