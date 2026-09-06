using Azure.Storage.Blobs.Models;

namespace Puck.Actors;

public sealed class PublicStorageOptions
{
    public BlobUploadOptions? DefaultUploadOptions { get; set; }
    public string? Endpoint { get; set; }

    public void Deconstruct(
        out BlobUploadOptions? defaultUploadOptions,
        out string? endpoint
    ) {
        defaultUploadOptions = DefaultUploadOptions;
        endpoint = Endpoint;
    }
}

