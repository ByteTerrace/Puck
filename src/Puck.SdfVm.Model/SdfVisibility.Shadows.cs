namespace Puck.SdfVm;

public static partial class SdfVisibility {
    /// <summary>The bits of one stable slot in the visibility record's single K word.</summary>
    public const int ShadowBits = 8;
    /// <summary>The mask of one slot's code within the K word. The top code marks a slot the surface pass did not query,
    /// and queried visibilities take the codes below it (<c>SDF_SHADOW_UNQUERIED</c> and <c>SDF_SHADOW_SCALE</c> in
    /// <c>frame/sdf-shadow-visibility.hlsli</c>, which alone packs and reads the word).</summary>
    public const uint ShadowMask = ((1u << ShadowBits) - 1u);
}
