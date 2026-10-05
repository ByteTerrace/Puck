using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The name and light-motion validity of one view's successfully submitted K history. Names never use
/// light-table indices as identity. Storage is bounded by four stable slots.</summary>
public sealed class SdfShadowHistory {
    private readonly string?[] m_owners = new string?[SdfShadowSlots.MaxSlots];
    private readonly Vector3[] m_directions = new Vector3[SdfShadowSlots.MaxSlots];
    private readonly float[] m_penumbrae = new float[SdfShadowSlots.MaxSlots];
    private readonly bool[] m_fading = new bool[SdfShadowSlots.MaxSlots];

    private bool m_valid;

    /// <summary>Returns the slots rejected by ownership (including a handoff and its first completed rebuild).</summary>
    /// <param name="lights">The current lights.</param>
    /// <returns>A bit per stable slot.</returns>
    public uint Ownership(SdfLights lights) {
        var rejected = 0U;

        for (var slot = 0; (slot < lights.ShadowSlots.SlotCount); slot++) {
            var owner = lights.ShadowSlots.Owner(slot: slot);

            if (!m_valid || (owner is null) || !StringComparer.Ordinal.Equals(x: owner, y: m_owners[slot]) ||
                m_fading[slot] || Fading(lights: lights, slot: slot)) {
                rejected |= (1u << slot);
            }
        }
        return rejected;
    }
    /// <summary>Returns slots whose directions leave their retained penumbra anchor. Every accepted sample is at most
    /// one eighth of the penumbra angle from the anchor, so any pair differs by at most one quarter. Keeping the anchor
    /// until a full rebuild also rejects cumulative slow motion.</summary>
    /// <param name="lights">The current lights.</param>
    /// <returns>A bit per stable slot.</returns>
    public uint LightMotion(SdfLights lights) {
        var rejected = 0U;

        for (var slot = 0; (slot < lights.ShadowSlots.SlotCount); slot++) {
            var index = lights.ShadowSlots[slot];

            if ((index < 0) || (index >= lights.Count)) { continue; }
            var light = lights[index];

            if (SdfLightMotion.Changed(direction: light.Direction, penumbra: light.Param,
                anchorDirection: m_directions[slot], anchorPenumbra: m_penumbrae[slot])) {
                rejected |= (1u << slot);
            }
        }
        return rejected;
    }
    /// <summary>Commits metadata only after the shadow writer submits successfully. Only fully rebuilt slots move
    /// their light anchors; partial reprojection retains the anchor covering all samples still in the K history.</summary>
    /// <param name="lights">The submitted frame's light snapshot.</param>
    /// <param name="rebuilt">The slots that marched fully, including an unamortized frame.</param>
    public void Submitted(SdfLights lights, uint rebuilt) {
        for (var slot = 0; (slot < SdfShadowSlots.MaxSlots); slot++) {
            if (slot >= lights.ShadowSlots.SlotCount) {
                m_owners[slot] = null;
                m_directions[slot] = Vector3.Zero;
                m_penumbrae[slot] = 0f;
                m_fading[slot] = false;
                continue;
            }
            if (((rebuilt & (1u << slot)) != 0) || !m_valid) {
                m_owners[slot] = lights.ShadowSlots.Owner(slot: slot);
                var index = lights.ShadowSlots[slot];

                m_directions[slot] = (((index >= 0) && (index < lights.Count)) ? SdfLights.UnitDirection(direction: lights[index].Direction) : Vector3.Zero);
                m_penumbrae[slot] = (((index >= 0) && (index < lights.Count)) ? lights[index].Param : 0f);
            }
            m_fading[slot] = Fading(lights: lights, slot: slot);
        }
        m_valid = true;
    }

    private static bool Fading(SdfLights lights, int slot) {
        foreach (var handoff in lights.ShadowSlots.Handoffs) {
            if (handoff.Slot == slot) { return true; }
        }
        return false;
    }
}
