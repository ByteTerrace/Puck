namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Attempts to begin <paramref name="carrierIndex"/> carrying <paramref name="targetIndex"/> — see
    /// <see cref="WorldBody.TryBeginCarry"/> for the full refusal set (a carrier kit with no carry facet, a target
    /// with no rigid facet, either body already a party to another carry relationship, out of reach, over the
    /// carrier's mass ceiling).</summary>
    /// <param name="carrierIndex">The would-be carrier's population index.</param>
    /// <param name="targetIndex">The would-be target's population index.</param>
    /// <param name="reason">The refusal, by name, on failure; empty on success.</param>
    public bool TryBeginCarry(int carrierIndex, int targetIndex, out string reason) {
        if (
            ((uint)carrierIndex >= (uint)Capacity) ||
            (m_entries[carrierIndex] is not { Active: true, Body: { } carrier })
        ) {
            reason = $"body:{carrierIndex} is not active";
            return false;
        }
        if (
            ((uint)targetIndex >= (uint)Capacity) ||
            (m_entries[targetIndex] is not { Active: true, Body: { } target })
        ) {
            reason = $"body:{targetIndex} is not active";
            return false;
        }

        return carrier.TryBeginCarry(
            target: target,
            targetIndex: targetIndex,
            selfIndex: carrierIndex,
            reason: out reason
        );
    }

    /// <summary>Ends <paramref name="carrierIndex"/>'s active carry, handing the target back to the rigid solver
    /// with the carrier's own current velocity — see <see cref="WorldBody.EndCarry"/>.</summary>
    /// <param name="carrierIndex">The carrier's population index.</param>
    /// <param name="reason">The refusal, by name, on failure (not active, or not currently carrying anything);
    /// empty on success.</param>
    public bool TryEndCarry(int carrierIndex, out string reason) {
        if (
            ((uint)carrierIndex >= (uint)Capacity) ||
            (m_entries[carrierIndex] is not { Active: true, Body: { } carrier })
        ) {
            reason = $"body:{carrierIndex} is not active";
            return false;
        }
        if (carrier.Carrying is not { } targetIndex) {
            reason = $"body:{carrierIndex} is not carrying anything";
            return false;
        }
        if (m_entries[targetIndex] is not { Active: true, Body: { } target }) {
            // The carried body went inactive out from under the carrier (despawn, capacity reclaim) without going
            // through this path — drop the carrier's own half; there is nothing left to hand a release velocity to.
            carrier.ForceDropCarrying();
            reason = $"body:{targetIndex} is no longer active — carry dropped";
            return false;
        }

        carrier.EndCarry(target: target);
        reason = "";
        return true;
    }

    /// <summary>Derives every carried body's pose from its carrier, once per tick, after both advance passes have
    /// run and before <see cref="ResolveDynamicContacts"/> — so a carried body reads the carrier's post-movement
    /// pose for this tick, one tick behind any depenetration that later pass applies to the carrier (see
    /// <see cref="WorldBody.FollowCarrier"/>). Also self-heals a relationship whose mirror broke without going
    /// through <see cref="TryEndCarry"/> — a body going inactive or a live kit retune away from the facet the
    /// relationship depends on are the only paths that can leave one half dangling. A no-op world (no kit ever
    /// authors a carry facet — the overwhelming common case) never reaches the loop body.</summary>
    public void UpdateCarriedBodies() {
        if (!m_anyCarryCapableKit) {
            return;
        }

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { CarriedBy: { } carrierIndex } target }) {
                continue;
            }

            if (
                (m_entries[carrierIndex] is not { Active: true, Body: { } carrier }) ||
                (carrier.Carrying != index)
            ) {
                target.ForceRelease();
                continue;
            }

            target.FollowCarrier(carrier: carrier);
        }

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { Carrying: { } targetIndex } carrier }) {
                continue;
            }

            if (
                (m_entries[targetIndex] is not { Active: true, Body: { CarriedBy: { } backIndex } }) ||
                (backIndex != index)
            ) {
                carrier.ForceDropCarrying();
            }
        }
    }
}
