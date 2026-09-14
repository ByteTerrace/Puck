using Azure.Storage.Blobs.Models;

namespace Puck.Storage;

/// <summary>
/// The blob endpoint published content is served from, and the upload options it is written with.
/// </summary>
public sealed class PublicStorageOptions {
    /// <summary>
    /// Gets or sets the upload options applied to a published blob when a caller supplies none.
    /// </summary>
    public BlobUploadOptions? DefaultUploadOptions { get; set; }
    /// <summary>
    /// Gets or sets the absolute blob endpoint published content is served from.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Deconstructs the options into their upload options and endpoint.
    /// </summary>
    public void Deconstruct(
        out BlobUploadOptions? defaultUploadOptions,
        out string? endpoint
    ) {
        defaultUploadOptions = DefaultUploadOptions;
        endpoint = Endpoint;
    }
}
