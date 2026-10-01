namespace Puck.SdfVm;

/// <summary>An immutable host identity snapshot matching one frame's instance and mesh draw ordinals. It remains valid
/// after the host rebuilds its scene, so an asynchronous pick cannot reinterpret a reused ordinal.</summary>
public interface ISdfPickMap {
    /// <summary>Resolves a visibility identity to host presentation data.</summary>
    /// <param name="identity">The packed visibility kind and source.</param>
    /// <returns>The host identity, or null for background or geometry the host does not name.</returns>
    object? Resolve(uint identity);
    /// <summary>Resolves a rendered material to the host's captured name, or null when it has none.</summary>
    /// <param name="identity">The captured winning instance identity.</param>
    /// <param name="material">The program-relative material index.</param>
    /// <returns>The immutable presentation name.</returns>
    string? MaterialName(uint identity, int material) => null;
}
