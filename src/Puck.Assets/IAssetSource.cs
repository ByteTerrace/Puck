namespace Puck.Assets;

/// <summary>A source of raw asset bytes addressed by a path. Decouples asset loaders from the underlying
/// store — the local file system, an archive, an embedded resource, and so on.</summary>
public interface IAssetSource {
    /// <summary>Determines whether an asset exists at <paramref name="path"/>.</summary>
    /// <param name="path">The asset path.</param>
    /// <returns><see langword="true"/> if an asset exists; otherwise <see langword="false"/>.</returns>
    bool Exists(string path);
    /// <summary>Reads the full contents of the asset at <paramref name="path"/>.</summary>
    /// <param name="path">The asset path.</param>
    /// <returns>The asset's bytes.</returns>
    ReadOnlyMemory<byte> Read(string path);
}
