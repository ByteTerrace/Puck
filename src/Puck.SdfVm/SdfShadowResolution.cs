using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The texels along each side of a view's shadow cascades, a power-of-two fraction of
/// <see cref="SdfWorldPackage.MeshShadowResolution"/>: a coarser cascade casts softer, blunter shadows over the same
/// reach for a quarter of the memory and fill a step. The default is full resolution.</summary>
public enum SdfShadowResolution {
    /// <summary>Every cascade at <see cref="SdfWorldPackage.MeshShadowResolution"/> texels a side.</summary>
    Full,
    /// <summary>Every cascade at half <see cref="SdfWorldPackage.MeshShadowResolution"/> texels a side.</summary>
    Half,
    /// <summary>Every cascade at a quarter of <see cref="SdfWorldPackage.MeshShadowResolution"/> texels a side.</summary>
    Quarter,
}
/// <summary>The texel counts of <see cref="SdfShadowResolution"/>.</summary>
public static class SdfShadowResolutions {
    /// <summary>Returns the texels along each side of a cascade at a resolution.</summary>
    /// <param name="resolution">The resolution.</param>
    /// <returns>The texels a side.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resolution"/> is not defined.</exception>
    public static int Texels(SdfShadowResolution resolution) => (Enum.IsDefined(value: resolution)
        ? (SdfWorldPackage.MeshShadowResolution >> ((int)resolution))
        : throw new ArgumentOutOfRangeException(actualValue: resolution, message: "The shadow resolution is not defined.", paramName: nameof(resolution)));
}
