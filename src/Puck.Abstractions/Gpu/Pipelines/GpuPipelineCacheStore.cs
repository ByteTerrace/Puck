using System.Buffers.Binary;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Where backends keep their persistent pipeline caches on disk: the directory, and the host's content key (the kernel
/// set it ships). Each device's file within it is a <see cref="GpuPipelineCacheFile"/>, which owns the file name, the
/// read, the write and the replace. Several processes may share the directory.
/// </summary>
public sealed class GpuPipelineCacheStore {
    /// <summary>Initializes a new instance of the <see cref="GpuPipelineCacheStore"/> class.</summary>
    /// <param name="directory">The directory the caches live under; created on the first write.</param>
    /// <param name="contentKey">The host's name for the pipelines it creates, a hash of its kernel set; one path
    /// segment of lowercase letters, digits and hyphens.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty, or <paramref name="contentKey"/> is not
    /// one such segment.</exception>
    public GpuPipelineCacheStore(string directory, string contentKey) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        RequireSegment(
            paramName: nameof(contentKey),
            segment: contentKey
        );

        ContentKey = contentKey;
        Directory = Path.GetFullPath(path: directory).Replace(
            newChar: '/',
            oldChar: '\\'
        );
    }

    /// <summary>Gets the host's content key.</summary>
    public string ContentKey { get; }
    /// <summary>Gets the absolute directory the caches live under, with forward slashes.</summary>
    public string Directory { get; }

    /// <summary>Hashes content into a key: the first 16 hexadecimal digits of the SHA-256 of every part, each prefixed
    /// with its length so no two different part lists hash alike.</summary>
    /// <param name="parts">The content, in a fixed order.</param>
    /// <returns>A lowercase 16-digit key.</returns>
    public static string ContentKeyOf(params ReadOnlySpan<ReadOnlyMemory<byte>> parts) {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(hashAlgorithm: System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];

        foreach (var part in parts) {
            BinaryPrimitives.WriteInt64LittleEndian(
                destination: length,
                value: part.Length
            );
            hash.AppendData(data: length);
            hash.AppendData(data: part.Span);
        }

        return Convert.ToHexStringLower(inArray: hash.GetHashAndReset()).Substring(
            length: 16,
            startIndex: 0
        );
    }

    // Requires a path segment of lowercase ASCII letters, digits and hyphens.
    internal static void RequireSegment(string segment, string paramName) {
        ArgumentException.ThrowIfNullOrEmpty(
            argument: segment,
            paramName: paramName
        );

        foreach (var character in segment) {
            if (!(char.IsAsciiLetterLower(c: character) || char.IsAsciiDigit(c: character) || (character == '-'))) {
                throw new ArgumentException(
                    message: $"'{segment}' must be one path segment of lowercase letters, digits and hyphens.",
                    paramName: paramName
                );
            }
        }
    }
}
