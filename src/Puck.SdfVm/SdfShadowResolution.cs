using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The texels along each side of a view's shadow cascades, a power-of-two fraction of the presenting device's
/// full resolution (<see cref="SdfFrame.ShadowTexels"/>): a coarser cascade casts softer, blunter shadows over the same
/// reach for a quarter of the memory and fill a step. The default is full resolution.</summary>
public enum SdfShadowResolution {
    /// <summary>Every cascade at the device's full texels a side.</summary>
    Full,
    /// <summary>Every cascade at half the device's full texels a side.</summary>
    Half,
    /// <summary>Every cascade at a quarter of the device's full texels a side.</summary>
    Quarter,
}
/// <summary>The texel counts of <see cref="SdfShadowResolution"/>.</summary>
public static class SdfShadowResolutions {
    /// <summary>The texels a side of a full-resolution cascade on a device whose table authors none
    /// (<see cref="SdfWorldPackage.MeshShadowResolution"/>).</summary>
    public const int DefaultTexels = SdfWorldPackage.MeshShadowResolution;

    /// <summary>Returns the texels along each side of a cascade at a resolution of a full resolution.</summary>
    /// <param name="resolution">The resolution.</param>
    /// <param name="full">The texels a side at <see cref="SdfShadowResolution.Full"/>; at least one.</param>
    /// <returns>The texels a side, at least one.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resolution"/> is not defined, or
    /// <paramref name="full"/> is below one.</exception>
    public static int Texels(SdfShadowResolution resolution, int full = DefaultTexels) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: full, other: 1);

        return (Enum.IsDefined(value: resolution)
            ? Math.Max(val1: 1, val2: (full >> ((int)resolution)))
            : throw new ArgumentOutOfRangeException(actualValue: resolution, message: "The shadow resolution is not defined.", paramName: nameof(resolution)));
    }
}
