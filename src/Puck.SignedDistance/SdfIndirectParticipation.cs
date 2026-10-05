namespace Puck.SignedDistance;

/// <summary>An independently composable instance's participation in diffuse indirect lighting.</summary>
public enum SdfIndirectParticipation {
    /// <summary>Static instances cast and receive; dynamic instances follow the frame's body policy and tier.</summary>
    Default = 0,
    /// <summary>The instance both casts and receives indirect lighting.</summary>
    Cast = 1,
    /// <summary>The instance receives indirect lighting without contributing geometry to its transport field.</summary>
    Receive = 2,
    /// <summary>The instance neither casts nor receives indirect lighting.</summary>
    Off = 3,
}
/// <summary>Resolves the same whole-instance policy packed for indirect field and receiver queries.</summary>
public static class SdfIndirectPolicy {
    /// <summary>Resolves an instance override, then a dynamic body's frame default, then its tier.</summary>
    /// <param name="participation">The instance override.</param>
    /// <param name="dynamic">Whether the instance follows a dynamic-transform slot.</param>
    /// <param name="tier">The consuming cache tier.</param>
    /// <param name="bodies">The authored dynamic-body override.</param>
    /// <returns>Cast, Receive or Off. Static Default always resolves to Cast.</returns>
    public static SdfIndirectParticipation Resolve(SdfIndirectParticipation participation, bool dynamic,
        SdfIndirectTier tier, SdfIndirectParticipation bodies = SdfIndirectParticipation.Default) =>
        ((participation != SdfIndirectParticipation.Default) ? participation : (!dynamic ? SdfIndirectParticipation.Cast :
        ((bodies != SdfIndirectParticipation.Default) ? bodies : ((tier == SdfIndirectTier.High) ? SdfIndirectParticipation.Cast : SdfIndirectParticipation.Receive))));
}
