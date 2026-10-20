using System.Diagnostics.CodeAnalysis;

namespace Puck.Abstractions.Machines;

/// <summary>The machine vocabulary selected by a host for document admission. Offline tools may carry no catalog;
/// they must report the resulting deferred provider checks instead of claiming semantic validation.</summary>
public interface IMachineValidationCatalog {
    /// <summary>Gets the stable metadata fingerprint for cache-partitioned composition, when this catalog can provide one.</summary>
    string CompositionFingerprint => string.Empty;

    /// <summary>Resolves the same provider descriptor used by runtime construction and authoring tools.</summary>
    /// <param name="engineId">The registered engine identifier.</param>
    /// <param name="descriptor">The selected provider description, or null when unavailable.</param>
    /// <returns>Whether the descriptor is available in this host's catalog.</returns>
    bool TryDescriptor(string engineId, [NotNullWhen(true)] out MachineEngineDescriptor? descriptor);
    /// <summary>Determines whether this host can construct the named engine.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    bool IsRegistered(string engineId);
    /// <summary>Determines whether an installed provider recognizes the path as authored source requiring preparation.</summary>
    /// <param name="contentPath">The logical content path.</param>
    bool RequiresPreparation(string contentPath);
    /// <summary>Determines whether the selected engine's provider can prepare the named source format.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    /// <param name="contentPath">The logical content path.</param>
    bool CanPrepare(string engineId, string contentPath);
    /// <summary>Determines whether the selected engine prepares its content through a registered provider, so that a
    /// file the provider does not recognize is not content for it.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    bool HasContentProvider(string engineId);
    /// <summary>Gets how many leading bytes the selected engine reads to recognize a raw image it mounts
    /// (<see cref="IMachineImageRecognizer"/>), or 0 when the engine recognizes no image by its header.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    int ImageHeaderLength(string engineId);
    /// <summary>Returns why the selected engine's machines do not run a raw image, judged by the image's header
    /// (<see cref="IMachineImageRecognizer.RefuseImage"/>).</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    /// <param name="header">The image's leading bytes, <see cref="ImageHeaderLength"/> of them or the whole image.</param>
    /// <param name="imageLength">The whole image's length in bytes.</param>
    /// <returns>The reason, or <see langword="null"/> when the engine's machines run the image; an engine that judges
    /// no image by its header refuses every one.</returns>
    string? RefuseImage(string engineId, ReadOnlySpan<byte> header, long imageLength);
}
