namespace Puck.SignedDistance;

/// <summary>A device's screen-space reflections: how finely a temporal view traces its glossy surfaces' mirror rays through
/// its own picture and how far each may march. A view without them reflects the irradiance volume alone.</summary>
/// <param name="Cell">The edge, in render pixels, of the square one mirror ray serves: 1 traces every glossy pixel, 2 one
/// pixel of each 2 by 2 block a frame, the block's pixels taking turns and each pixel blending the rays of the blocks
/// about it that stand on its surface.</param>
/// <param name="Steps">The most reads of the reflection's depth pyramid one ray's march takes, from 1 through
/// <see cref="MaxSteps"/>: a ray past its budget answers with the irradiance volume.</param>
public sealed record SdfScreenReflections(int Cell = 2, int Steps = 32) {
    /// <summary>The most steps a ray may take.</summary>
    public const int MaxSteps = 256;

    /// <summary>Returns why the setting cannot be authored, or <see langword="null"/> when it can.</summary>
    /// <param name="path">The document path the refusal names.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? Refusal(string path) => Cell is not (1 or 2)
        ? $"{path}.cell must be 1 or 2."
        : ((Steps < 1) || (Steps > MaxSteps)) ? $"{path}.steps must be from 1 through {MaxSteps}." : null;
}
