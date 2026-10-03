using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>One active handoff's 16-byte presentation control, uploaded through the counted shadow region.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct SdfShadowHandoff {
    /// <summary>The outgoing frame light index, or -1 when its name has departed.</summary>
    public int Outgoing;
    /// <summary>The incoming frame light index, or -1 when its name has departed.</summary>
    public int Incoming;
    /// <summary>The outgoing visibility's stable slot.</summary>
    public int Slot;
    /// <summary>The tick-derived incoming coverage, from zero through one.</summary>
    public float Weight;

    /// <summary>Creates the control for one active handoff.</summary>
    /// <param name="Outgoing">The outgoing frame light index.</param>
    /// <param name="Incoming">The incoming frame light index.</param>
    /// <param name="Slot">The stable slot.</param>
    /// <param name="Weight">The incoming coverage.</param>
    public SdfShadowHandoff(int Outgoing, int Incoming, int Slot, float Weight) {
        this.Outgoing = Outgoing;
        this.Incoming = Incoming;
        this.Slot = Slot;
        this.Weight = Weight;
    }

    /// <summary>Fades the outgoing light's own occlusion deficit toward full visibility.</summary>
    /// <param name="marched">That light's marched visibility.</param>
    /// <returns>Visibility applied only to the outgoing light.</returns>
    public readonly float OutgoingVisibility(float marched) => (1f - ((1f - marched) * (1f - Weight)));
    /// <summary>Fades the incoming light's own occlusion deficit from full visibility.</summary>
    /// <param name="marched">That light's marched visibility.</param>
    /// <returns>Visibility applied only to the incoming light.</returns>
    public readonly float IncomingVisibility(float marched) => (1f - ((1f - marched) * Weight));
}
