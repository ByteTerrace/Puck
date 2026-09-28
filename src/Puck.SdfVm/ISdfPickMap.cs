namespace Puck.SdfVm;

/// <summary>An immutable host identity snapshot matching one frame's instance and mesh draw ordinals. It remains valid
/// after the host rebuilds its scene, so an asynchronous pick cannot reinterpret a reused ordinal.</summary>
public interface ISdfPickMap {
    /// <summary>Resolves a visibility identity to host presentation data.</summary>
    /// <param name="identity">The packed visibility kind and source.</param>
    /// <returns>The host identity, or null for background or geometry the host does not name.</returns>
    object? Resolve(uint identity);
}
