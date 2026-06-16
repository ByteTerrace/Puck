namespace Puck.Storage;

public sealed record AzureBlobObjectStorageTarget : ObjectStorageTarget {
    public static AzureBlobObjectStorageTarget FromConnectionStringOrServiceUri(string value) {
        if (string.IsNullOrWhiteSpace(value: value)) {
            throw new ArgumentException(
                message: "The Azure Blob target value must not be empty.",
                paramName: nameof(value)
            );
        }

        return (Uri.TryCreate(
            result: out var serviceUri,
            uriKind: UriKind.Absolute,
            uriString: value
        )
            ? new AzureBlobObjectStorageTarget(serviceUri: serviceUri)
            : new AzureBlobObjectStorageTarget(connectionString: value));
    }

    public string? ConnectionString { get; }
    public Uri? ServiceUri { get; }

    public AzureBlobObjectStorageTarget(Uri serviceUri) {
        ArgumentNullException.ThrowIfNull(serviceUri);

        if (!serviceUri.IsAbsoluteUri) {
            throw new ArgumentException(
                message: "The Azure Blob service URI must be absolute.",
                paramName: nameof(serviceUri)
            );
        }

        ServiceUri = serviceUri;
    }
    public AzureBlobObjectStorageTarget(string connectionString) {
        if (string.IsNullOrWhiteSpace(value: connectionString)) {
            throw new ArgumentException(
                message: "The Azure Blob connection string must not be empty.",
                paramName: nameof(connectionString)
            );
        }

        ConnectionString = connectionString;
    }
}
