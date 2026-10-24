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
}
/// <summary>The accepted outcome of <see cref="FixedStepUp.TryStep{TStep}"/>.</summary>
/// <param name="Position">The foot point on top of the riser.</param>
/// <param name="Velocity">The velocity after the step-up's moves: the riser never clipped it, so the body keeps its
/// pace.</param>
/// <param name="Resolution">The landing move's resolution, grounded on a walkable surface.</param>
public readonly record struct FixedStepUpResult(FixedVector3 Position, FixedVector3 Velocity, ContactResolution Resolution);
/// <summary>Lifts a grounded body over a riser its ordinary step met as a wall: raise by the step height, move the
/// step's planar displacement, and lower onto what lies beneath, each through the caller's own sweep.</summary>
/// <remarks>
/// <para>The step is accepted only when the raised move gets further along the planar displacement than the blocked
/// step did, and the landing grounds on a walkable surface (the contact field's own <c>cos(maxSlope)</c> test, so a
/// steeper face is never stepped onto), rises no more than the step height above the start, and stands higher than
/// the blocked step did. Otherwise the blocked step stands, so a riser taller than the step height, or any wall, is
/// still a wall.</para>
/// <para>The lowering reaches no further than where the original step would have ended along the up axis, so a step
/// that was rising (a jump) lands on nothing it would not otherwise have reached. It finds the support by bisection,
/// <see cref="LandingProbes"/> sweeps from the raised point, and lands at the shallowest descent found to meet it:
/// a provider whose sweep is only an endpoint solve then meets the riser's top by less than the probe resolution
/// and resolves it as ground, where one deep move would read the riser's side as the nearer face.</para>
/// <para>Bounded: two sweeps reject a wall, and an accepted step costs <see cref="LandingProbes"/> plus four.</para>
/// </remarks>
public static class FixedStepUp {
    /// <summary>The bisection sweeps the landing search spends: the support height is found to within the descent
    /// divided by two to this power.</summary>
    public const int LandingProbes = 8;

    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);

    /// <summary>Finds the first walkable support below a position within a caller-supplied snap distance.
    /// Uses the same sweep and bounded landing search as a step up; an unsuccessful search changes nothing.</summary>
    /// <typeparam name="TStep">The caller's contact sweep.</typeparam>
    /// <param name="step">The contact sweep.</param>
    /// <param name="start">The position to lower from.</param>
    /// <param name="velocity">The velocity to project onto the support.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="distance">The greatest permitted descent.</param>
    /// <param name="result">The accepted landing.</param>
    /// <returns>Whether the search found proven walkable support.</returns>
    public static bool TrySnap<TStep>(ref TStep step, in FixedVector3 start, in FixedVector3 velocity,
        in FixedVector3 up, FixedQ4816 distance, out FixedStepUpResult result)
        where TStep : IFixedContactStep, allows ref struct {
        result = default;
        if ((distance <= FixedQ4816.Zero) || !Meets(above: in start, descent: distance, step: ref step,
            up: in up, velocity: in velocity)) {
            return false;
        }
        var clear = FixedQ4816.Zero;
        var meet = distance;

        for (var probe = 0; (probe < LandingProbes); probe++) {
            var middle = ((clear + meet) / Two);

            if (Meets(above: in start, descent: middle, step: ref step, up: in up, velocity: in velocity)) {
                meet = middle;
            } else {
                clear = middle;
            }
        }
        var landed = (start - (up * meet));
        var moved = velocity;
        var landing = step.Sweep(position: ref landed, previousPosition: in start, velocity: ref moved);

        if ((landing.Refusal != ContactRefusal.None) || landing.Unproved || !landing.Grounded) {
            return false;
        }
        result = new FixedStepUpResult(Position: landed,
            Velocity: moved.RejectFrom(unitAxis: landing.GroundNormal), Resolution: landing);
        return true;
    }

    // Whether lowering the body by descent from above meets anything: the sweep stops, or the endpoint solve pushes.
    private static bool Meets<TStep>(ref TStep step, in FixedVector3 above, in FixedVector3 up, FixedQ4816 descent, in FixedVector3 velocity)
        where TStep : IFixedContactStep, allows ref struct {
        var target = (above - (up * descent));
        var probe = target;
        var scratch = velocity;

        _ = step.Sweep(
            position: ref probe,
            previousPosition: in above,
            velocity: ref scratch
        );

        return (probe != target);
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
            (stepHeight < requiredStepHeight) ||
            (planar == FixedVector3.Zero)
        ) {
            return false;
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
            return false;
        }

        var lift = FixedVector3.Dot(
            left: (raised - start),
            right: up
        );

        if (lift <= FixedQ4816.Zero) {
            return false;
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
            return false;
        }

        // Lower onto the support: bisect for the shallowest descent that meets it, down to where the original step
        // would have ended along up. Nothing met in that range leaves the body in the air, which is not a step.
        if (!TrySnap(distance: (lift - vertical), result: out var landingResult, start: in across, step: ref step,
            up: in up, velocity: in moved)) {
            return false;
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
            return false;
        }

        result = new FixedStepUpResult(
            Position: landed,
            Resolution: landing,
            Velocity: moved
        );

        return true;
    }
}
