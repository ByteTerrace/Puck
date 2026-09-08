using Azure;
using Azure.Core;
using Azure.Messaging;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Puck.Azure.Functions.EventGridTriggers;

internal sealed class BatchMapEntry
{
    private static readonly IEqualityComparer<CloudEvent> CloudEventEqualityComparer = EqualityComparer<CloudEvent>.Create(
        equals: static (x, y) => StringComparer.Ordinal.Equals(x: x!.Id, y: y!.Id),
        getHashCode: static @event => StringComparer.Ordinal.GetHashCode(obj: @event.Id)
    );

    private readonly HashSet<CloudEvent> m_events = new(comparer: CloudEventEqualityComparer);

    public IEnumerable<CloudEvent> Events => m_events;
    public DateTimeOffset MaxTime { get; private set; } = DateTimeOffset.MinValue;

    public void Add(CloudEvent @event) {
        if (m_events.Add(item: @event) && (MaxTime < @event.Time!.Value)) {
            MaxTime = @event.Time.Value;
        }
    }
}

public sealed class BlobCloudEvent(
    ILogger<BlobCloudEvent> logger,
    [FromKeyedServices(key: "Default")] TokenCredential tokenCredential
)
{
    private const int BatchTimeoutInSeconds = 23;
    private const string DestinationSegment = "system";
    private const int MaxDegreeOfParallelism = 7;
    private const string UnhandledExceptionMessage = "Unhandled exception during Event Grid trigger.";

    private static readonly BlobClientOptions BlobClientOptions = new() {
        EnableTenantDiscovery = false,
        Retry = {
            Delay = TimeSpan.FromMilliseconds(milliseconds: 500),
            MaxRetries = 3,
            Mode = RetryMode.Exponential,
            NetworkTimeout = TimeSpan.FromSeconds(seconds: 3)
        },
    };
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <remarks>
    /// Assumes input matches the expected Azure Storage Blob event schema.
    /// </remarks>
    public static string ExtractUrlFromPayload(BinaryData value) {
        var reader = new Utf8JsonReader(jsonData: value.ToMemory().Span);

        while (reader.Read()) {
            if (
                (JsonTokenType.PropertyName == reader.TokenType) &&
                reader.ValueTextEquals(text: "url")
            ) {
                _ = reader.Read();

                return reader.GetString()!;
            }
        }

        throw new UnreachableException();
    }
    /// <remarks>
    /// Assumes input matches the expected Azure Storage Blob event schema.
    /// <br />
    /// <br />Input: https://{storage-account-name}.blob.core.windows.net/{container-name}/{blob-path}
    /// <br />Output: https://{storage-account-name}.blob.core.windows.net/{container-name}
    /// </remarks>
    public static string GetContainerUri(ReadOnlySpan<char> value) {
        var offset = 8; // NOTE: starts parsing after "https://"
        var span = value[offset..];

        // NOTE: first iteration skips the domain name, second iteration skips the container name
        for (var i = 0; (2 > i); ++i) {
            var jump = span.IndexOf('/') + 1;

            offset += jump;
            span = span[jump..];
        }

        return value[..(offset - 1)].ToString(); // NOTE: drops remaining characters and returns the result
    }
    /// <remarks>
    /// Assumes input matches the expected Azure Storage Blob event schema.
    /// <br />
    /// <br />Input: /blobServices/default/containers/{container-name}/blobs/{first-blob-path-segment}[/{remaining-blob-path}]
    /// <br />Output: {container-name}, {first-blob-path-segment}
    /// </remarks>
    public static void ParseSubject(
        ReadOnlySpan<char> value,
        out ReadOnlySpan<char> containerName,
        out ReadOnlySpan<char> firstBlobPathSegment
    ) {
        var span = value[33..]; // NOTE: starts parsing after "/blobServices/default/containers/"
        var nextSlashIndex = span.IndexOf(value: '/');

        containerName = span[..nextSlashIndex];
        span = span[(nextSlashIndex + 7)..]; // NOTE: continues parsing after "/blobs/"
        nextSlashIndex = span.IndexOf('/');
        firstBlobPathSegment = ((nextSlashIndex == -1) ? span : span[..nextSlashIndex]); // NOTE: drops trailing slash if necessary
    }

    [Function(nameof(BlobCloudEvent))]
    public async Task Run([EventGridTrigger(IsBatched = true)] CloudEvent[] cloudEvents) {
        try {
            using var cancellationTokenSource = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: BatchTimeoutInSeconds));

            var containerBatchMap = new Dictionary<string, BatchMapEntry>();

            foreach (var @event in cloudEvents) {
                switch (@event.Type) {
                    case "Microsoft.Storage.BlobCreated":
                    case "Microsoft.Storage.BlobDeleted":
                    case "Microsoft.Storage.BlobRenamed":
                        ParseSubject(
                            containerName: out var containerName,
                            firstBlobPathSegment: out var firstBlobPathSegment,
                            value: @event.Subject!
                        );

                        // NOTE: protects against infinite loops in the case where the event subscription lacks the expected filter
                        if (firstBlobPathSegment.SequenceEqual(other: DestinationSegment)) {
                            throw new InvalidOperationException(message: "BYTRC_BLOBEVENT_001: infinite loop detected");
                        }

                        // NOTE: protects against invalid containers in the case where a blob event fails to meet the expected subject format
                        if (!Guid.TryParseExact(
                            format: "D",
                            input: containerName,
                            result: out var _
                        )) {
                            throw new InvalidOperationException(message: "BYTRC_BLOBEVENT_002: invalid container name detected");
                        }

                        var containerUri = GetContainerUri(value: ExtractUrlFromPayload(value: @event.Data!));

                        if (!containerBatchMap.TryGetValue(
                            key: containerUri,
                            value: out var entry
                        )) {
                            entry = new();
                            containerBatchMap[key: containerUri] = entry;
                        }

                        entry.Add(@event: @event);
                        break;
                    default:
                        // NOTE: protects against unsupported events in the case where the event subscription lacks the expected filter
                        throw new InvalidOperationException(message: "BYTRC_BLOBEVENT_000: unsupported event type");
                }
            }

            await Parallel.ForEachAsync(
                body: async (keyValuePair, cancellationToken) => {
                    var (key, value) = keyValuePair;
                    var blobName = $"{DestinationSegment}/events/blob/{value.MaxTime.UtcDateTime:yyyyMMddTHHmmssZ}_{Guid.NewGuid():D}";
                    var blobUriBuilder = new BlobUriBuilder(uri: new(uriString: key)) { BlobName = blobName };

                    try {
                        await using var blobStream = await new BlobClient(
                                blobUri: blobUriBuilder.ToUri(),
                                credential: tokenCredential,
                                options: BlobClientOptions
                            )
                            .OpenWriteAsync(
                                cancellationToken: cancellationToken,
                                overwrite: true
                            );

                        await JsonSerializer.SerializeAsync(
                            cancellationToken: cancellationToken,
                            options: JsonSerializerOptions,
                            utf8Json: blobStream,
                            value: JsonPayload.Create(value: value.Events.ToArray())
                        );
                    }
                    catch (RequestFailedException e)
                    when (
                        (HttpStatusCode.NotFound == ((HttpStatusCode)e.Status)) &&
                        ("ContainerNotFound" == e.ErrorCode)
                    ) {
                        throw new InvalidOperationException(
                            innerException: e,
                            message: "BYTRC_BLOBEVENT_003: container not found"
                        );
                    }
                },
                parallelOptions: new() {
                    CancellationToken = cancellationTokenSource.Token,
                    MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                },
                source: containerBatchMap
            );
        }
        catch (Exception e) {
            logger.LogCritical(
                exception: e,
                message: UnhandledExceptionMessage
            );

            throw;
        }
    }
}

