using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// Resolves a placement's ATTACH facet (<see cref="WorldPlacementAttach"/>) against the live population — the
/// AUTHORITATIVE answer, and the <c>world.attachments</c> read-back's door. Fixed point throughout: the body's
/// authoritative <see cref="WorldBody.FixedPosition"/>/<see cref="WorldBody.FixedYaw"/> compose with the facet's
/// authored local offset (quantized to fixed point here, the same boundary <see cref="WorldPlacementRegion"/>'s
/// sensing already crosses through) — never a float on the sim side.
/// </summary>
/// <remarks>The RENDERED pose is a separate derivation, deliberately: <c>Client.WorldStampPool</c> composes the same
/// offset over the client's INTERPOLATED body pose in presentation float, every frame, so an attached row is as smooth
/// as the body it rides — reading this authoritative resolve there would judder at the tick rate and would reach across
/// the client/server seam. The two agree on the RULE (rotate the offset into the body's frame, then add) and on the
/// inactive-body verdict (the row contributes nothing); they differ only where every avatar pose already differs, in
/// the interpolation between ticks.</remarks>
public static class WorldPlacementAttachment {
    /// <summary>Attempts to resolve an attached placement's world transform for the CURRENT tick.</summary>
    /// <param name="attach">The placement's attach facet.</param>
    /// <param name="population">The live entity table.</param>
    /// <param name="position">The resolved world position — the body's position plus <see cref="WorldPlacementAttach.LocalOffset"/>
    /// rotated by the body's orientation (the <c>OrientedFollowRig</c> local-frame convention). Set only on success.</param>
    /// <param name="yawRadians">The resolved world yaw — the body's yaw plus <see cref="WorldPlacementAttach.LocalYawDegrees"/>,
    /// fixed-point radians. Set only on success.</param>
    /// <param name="reason">A human-readable reason the row contributes nothing this tick. Set only on failure.</param>
    /// <returns><see langword="true"/> when <see cref="WorldPlacementAttach.BodyIndex"/> names an ACTIVE population
    /// entry this tick; <see langword="false"/> for an out-of-range index (author-time refuses this — reachable only
    /// if the caller bypassed validation) or an inactive/despawned body (an ordinary RUNTIME condition, not a
    /// refusal) — either way the placement contributes nothing rather than rendering a stale pose.</returns>
    public static bool TryResolve(WorldPlacementAttach attach, WorldPopulation population, out FixedVector3 position, out FixedQ4816 yawRadians, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: attach);
        ArgumentNullException.ThrowIfNull(argument: population);

        position = default;
        yawRadians = default;

        if (((uint)attach.BodyIndex) >= ((uint)population.Capacity)) {
            reason = $"body:{attach.BodyIndex} is outside the {population.Capacity}-slot entity table";

            return false;
        }

        if (
            !population.IsActive(index: attach.BodyIndex) ||
            (population.EntryBody(index: attach.BodyIndex) is not { } body)
        ) {
            reason = $"body:{attach.BodyIndex} is not an active population entry";

            return false;
        }

        var localOffset = new FixedVector3(
            X: FixedQ4816.FromDouble(value: attach.LocalOffset.X),
            Y: FixedQ4816.FromDouble(value: attach.LocalOffset.Y),
            Z: FixedQ4816.FromDouble(value: attach.LocalOffset.Z)
        );
        var localYaw = FixedQ4816.FromDouble(value: (attach.LocalYawDegrees * (Math.PI / 180.0)));

        position = (body.FixedPosition + body.FixedOrientation.Rotate(vector: localOffset));
        yawRadians = (body.FixedYaw + localYaw);
        reason = string.Empty;

        return true;
    }
}
