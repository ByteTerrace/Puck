namespace Puck.Assets.Textures;

/// <summary>How a texture's values map to light.</summary>
public enum TextureColorSpace : byte {
    /// <summary>The values are linear: data, or linear light.</summary>
    Linear = 0,
    /// <summary>The color channels are sRGB-encoded; alpha is linear.</summary>
    Srgb = 1,
}
