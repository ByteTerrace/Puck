namespace Puck.DirectX;

/// <summary>
/// Specifies a Direct3D feature level — the set of GPU capabilities a device is guaranteed to support, as a
/// <c>D3D_FEATURE_LEVEL</c> value. The numeric values match the native enumeration so a cast is lossless.
/// </summary>
public enum DirectXFeatureLevel {
    /// <summary>Direct3D feature level 11.0 (<c>D3D_FEATURE_LEVEL_11_0</c>).</summary>
    Level110 = 45056,
    /// <summary>Direct3D feature level 11.1 (<c>D3D_FEATURE_LEVEL_11_1</c>).</summary>
    Level111 = 45312,
    /// <summary>Direct3D feature level 12.0 (<c>D3D_FEATURE_LEVEL_12_0</c>).</summary>
    Level120 = 49152,
    /// <summary>Direct3D feature level 12.1 (<c>D3D_FEATURE_LEVEL_12_1</c>).</summary>
    Level121 = 49408,
    /// <summary>Direct3D feature level 12.2 (<c>D3D_FEATURE_LEVEL_12_2</c>).</summary>
    Level122 = 49664
}
