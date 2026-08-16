using Puck.Storage;

namespace Puck.World.Tests;

/// <summary>An in-memory <see cref="IObjectBlobStore"/> honoring <see cref="ObjectBlobWriteMode.CreateOnly"/>/
/// <see cref="ObjectBlobWriteMode.Overwrite"/> and if-match semantics closely enough to exercise the real clobber
/// guards in <c>Puck.World.Server</c>'s storage engine, with read/write counters (overall and per-key) so a test can
/// prove a refusal happened BEFORE any network call, and that a shared blob was written exactly once.</summary>
public sealed class FakeObjectBlobStore : IObjectBlobStore {
    private readonly Dictionary<(Guid ObjectId, string Key), (byte[] Bytes, string Token)> m_blobs = [];
    private readonly Dictionary<(Guid ObjectId, string Key), TimeSpan> m_readDelays = [];
    private readonly Dictionary<(Guid ObjectId, string Key), int> m_writeCounts = [];

    private int m_nextToken;

    /// <summary>Gets the total number of <see cref="ReadAsync"/> calls made, across every key.</summary>
    public int ReadCount { get; private set; }
    /// <summary>Gets the total number of <see cref="WriteAsync"/> calls made, across every key.</summary>
    public int WriteCount { get; private set; }

    private string NextToken() => $"t{++m_nextToken}";

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) {
        var normalized = ((keyPrefix.Length == 0)
            ? keyPrefix
            : (keyPrefix.EndsWith(value: '/')
                ? keyPrefix
                : $"{keyPrefix}/"
        ));
        var keys = m_blobs.Keys
            .Where(predicate: candidate => ((candidate.ObjectId == objectId) && candidate.Key.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: normalized
        )))
            .Select(selector: static candidate => candidate.Key)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<string>>(result: keys);
    }
    /// <inheritdoc/>
    public async ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) {
        ReadCount++;

        if (m_readDelays.TryGetValue(
            key: (address.ObjectId, address.Key),
            value: out var delay
        )) {
            await Task.Delay(
                cancellationToken: cancellationToken,
                delay: delay
            );
        }

        return (m_blobs.TryGetValue(
            key: (address.ObjectId, address.Key),
            value: out var entry
        )
            ? new ObjectBlobContent(
                Content: entry.Bytes,
                VersionToken: entry.Token
            )
            : null
        );
    }
    /// <summary>Seeds one blob directly, bypassing <see cref="WriteAsync"/>'s clobber guard.</summary>
    public void Seed(Guid objectId, string key, byte[] bytes, string? token = null) {
        m_blobs[key: (objectId, key)] = (bytes, (token ?? NextToken()));
    }
    /// <summary>Makes every future <see cref="ReadAsync"/> of one key wait <paramref name="delay"/> (observing the
    /// caller's <see cref="CancellationToken"/>) before answering — the fixture a shared-deadline test drives.</summary>
    public void SeedReadDelay(Guid objectId, string key, TimeSpan delay) {
        m_readDelays[key: (objectId, key)] = delay;
    }
    /// <summary>Gets the raw bytes currently stored at a key, or <see langword="null"/> when absent.</summary>
    public byte[]? TryGetBytes(Guid objectId, string key) => (m_blobs.TryGetValue(
        key: (objectId, key),
        value: out var entry
    )
        ? entry.Bytes
        : null
    );
    /// <inheritdoc/>
    public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
        WriteCount++;

        var key = (address.ObjectId, address.Key);
        var exists = m_blobs.TryGetValue(
            key: key,
            value: out var current
        );

        if (
            (mode == ObjectBlobWriteMode.CreateOnly) &&
            exists
        ) {
            return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                PreconditionFailed: false,
                Succeeded: false,
                VersionToken: current.Token
            ));
        }

        if (
            (ifMatchVersion is not null) &&
            (!exists ||
            !string.Equals(
            a: current.Token,
            b: ifMatchVersion,
            comparisonType: StringComparison.Ordinal
        ))
        ) {
            return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                PreconditionFailed: true,
                Succeeded: false,
                VersionToken: (exists
                ? current.Token
                : null)
            ));
        }

        var token = NextToken();

        m_blobs[key: key] = (content.ToArray(), token);
        m_writeCounts[key: key] = (m_writeCounts.GetValueOrDefault(key: key) + 1);

        return ValueTask.FromResult(result: new ObjectBlobWriteResult(
            PreconditionFailed: false,
            Succeeded: true,
            VersionToken: token
        ));
    }
    /// <summary>Gets how many times <see cref="WriteAsync"/> landed at one key.</summary>
    public int WriteCountFor(Guid objectId, string key) => m_writeCounts.GetValueOrDefault(key: (objectId, key));
}
