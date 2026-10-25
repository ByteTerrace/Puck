using Puck.SdfVm;

namespace Puck.World;

/// <summary>The seam <see cref="WorldPatrolCommandModule"/> picks a rendered view through: one request in flight, answered
/// over later frames.</summary>
public interface IWorldPatrolView {
    /// <summary>Gets the name of the rendered view instance.</summary>
    string Instance { get; }
    /// <summary>Gets the latest published answer, or <see langword="null"/> while the latest request has none.</summary>
    SdfPickResult? Result { get; }

    /// <summary>Requests the pixel at normalized coordinates, superseding every earlier request.</summary>
    /// <param name="x">The horizontal coordinate in [0, 1).</param>
    /// <param name="y">The vertical coordinate in [0, 1).</param>
    /// <returns>The request identity the completed answer echoes.</returns>
    long Request(float x, float y);
}
