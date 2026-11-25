using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;

namespace Puck.World;

/// <summary>An authored six-class syllable bank. Sources resolve beside this set document when loaded from disk, or by
/// their content pins when delivered over the network.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldVoiceSampleSet(
    string Name,
    string A,
    string E,
    string I,
    string O,
    string U,
    string Consonant,
    int PlaybackRateThousandths = 1500,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConsonantHash = null
);
/// <summary>Loads a voice's sample bank through the world's document-relative asset path resolver or content resolver.</summary>
public static class WorldVoiceSampleLoader {
    /// <summary>The maximum byte size of a pinned sample-set JSON manifest.</summary>
    public const int MaxPinnedManifestBytes = 16_384;
    /// <summary>The maximum byte size of one syllable WAV asset.</summary>
    public const int MaxPinnedSampleBytes = 1_048_576;

    /// <summary>Canonicalizes a sample-set JSON manifest to deterministic UTF-8 bytes, or returns <see langword="null"/>
    /// if the bytes are invalid.</summary>
    public static byte[]? CanonicalBytes(ReadOnlyMemory<byte> bytes) =>
        (TryCanonicalize(bytes: bytes, canonical: out var canonical, error: out _) ? canonical : null);

    /// <summary>Returns whether <paramref name="bytes"/> are already in canonical sample-set manifest form.</summary>
    public static bool IsCanonical(ReadOnlyMemory<byte> bytes) =>
        ((CanonicalBytes(bytes: bytes) is { } canonical) && canonical.AsSpan().SequenceEqual(other: bytes.Span));

    /// <summary>Returns whether <paramref name="bytes"/> decode as a valid mono PCM wave syllable.</summary>
    public static bool IsValidWave(ReadOnlyMemory<byte> bytes) {
        try {
            _ = PcmWave.ReadMono(bytes: bytes.ToArray());
            return true;
        } catch (Exception exception) when (exception is InvalidDataException or ArgumentException) {
            return false;
        }
    }

    /// <summary>Computes the canonical SHA-256 hex64 pin of a sample-set manifest on disk (used by <c>hash: auto</c>).</summary>
    public static bool TryCanonicalHash(string? documentDirectory, string source, out string pin, out string error) {
        pin = string.Empty;
        if (string.IsNullOrWhiteSpace(value: source)) {
            error = "sampleSet is required.";
            return false;
        }
        if (!WorldDocumentPaths.TryResolve(documentDirectory: documentDirectory, path: source, reason: out var reason, resolved: out var path)) {
            error = reason;
            return false;
        }
        try {
            var raw = File.ReadAllBytes(path: path);
            if (!TryCanonicalize(bytes: raw, canonical: out var canonical, error: out error)) {
                return false;
            }
            pin = ContentPin.Compute(content: canonical).Hex;
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Reads a pinned sample-set manifest from <see cref="WorldProjectionContent.Resolver"/> without touching paths.</summary>
    public static bool TryReadPinnedSet(ContentPin pin, ContentAddressedStore? held, out WorldVoiceSampleSet? set) {
        set = null;
        if (!WorldProjectionContent.Resolver.TryRead(content: out var bytes, held: held, maximumBytes: MaxPinnedManifestBytes, pin: pin)) {
            return false;
        }
        try {
            set = JsonSerializer.Deserialize<WorldVoiceSampleSet>(utf8Json: bytes.Span, options: Puck.Assets.Documents.DocumentJsonOptions.Shared);
            return (set is not null);
        } catch (JsonException) {
            return false;
        }
    }

    /// <summary>Validates a delivered sampled voice's manifest and syllable WAVs from <see cref="WorldProjectionContent.Resolver"/>.
    /// Content this process does not hold passes unchecked so a missing sound never refuses a world.</summary>
    public static string? ValidateDelivered(string source, string hash) {
        if (!WorldAssetRowLoader.TryPin(hash: hash, pin: out var manifestPin)) {
            return $"sampleSet '{source}' carries invalid SHA-256 content pin '{hash}'.";
        }
        if (!WorldProjectionContent.Resolver.Contains(pin: manifestPin)) {
            return null;
        }
        if (!WorldProjectionContent.Resolver.TryRead(content: out var manifestBytes, maximumBytes: MaxPinnedManifestBytes, pin: manifestPin)) {
            return $"sampleSet '{source}' pin {manifestPin} could not be read within {MaxPinnedManifestBytes} bytes.";
        }
        if (!IsCanonical(bytes: manifestBytes)) {
            return $"sampleSet '{source}' pin {manifestPin} is not in canonical sample-set manifest form.";
        }
        try {
            var set = (JsonSerializer.Deserialize<WorldVoiceSampleSet>(utf8Json: manifestBytes.Span, options: Puck.Assets.Documents.DocumentJsonOptions.Shared)
                ?? throw new InvalidDataException(message: "Sample set is empty."));
            ValidateSetMetadata(set: set);
            foreach (var (wavSource, wavHash) in SyllableEntries(set: set)) {
                if (!WorldAssetRowLoader.TryPin(hash: wavHash, pin: out var wavPin)) {
                    return $"sampleSet '{source}': syllable '{wavSource}' carries no valid SHA-256 content pin.";
                }
                if (!WorldProjectionContent.Resolver.Contains(pin: wavPin)) {
                    continue;
                }
                if (!WorldProjectionContent.Resolver.TryRead(content: out var wavBytes, maximumBytes: MaxPinnedSampleBytes, pin: wavPin)) {
                    return $"sampleSet '{source}': syllable '{wavSource}' pin {wavPin} could not be read within {MaxPinnedSampleBytes} bytes.";
                }
                if (!IsValidWave(bytes: wavBytes)) {
                    return $"sampleSet '{source}': syllable '{wavSource}' pin {wavPin} is not a valid mono PCM wave.";
                }
            }
            return null;
        } catch (Exception exception) when (exception is InvalidDataException or JsonException) {
            return $"sampleSet '{source}': {exception.Message}";
        }
    }

    /// <summary>Enumerates the pinned syllable WAV content pins declared on <paramref name="set"/>.</summary>
    public static IEnumerable<(string Source, ContentPin Pin)> SyllablePins(WorldVoiceSampleSet set) {
        foreach (var (source, hash) in SyllableEntries(set: set)) {
            if (WorldAssetRowLoader.TryPin(hash: hash, pin: out var pin)) {
                yield return (source, pin);
            }
        }
    }

    /// <summary>Loads and validates the manifest and every PCM asset. The order is A, E, I/Y, O, U, consonant/digit.</summary>
    public static bool TryLoad(string? documentDirectory, string source, out short[][] samples, out int rate, out string? error, string? hash = null) {
        samples = [];
        rate = 0;
        var delivered = ((documentDirectory is null) && !Path.IsPathRooted(path: source));

        try {
            byte[] rawManifest;
            string? setDirectory = null;

            if (delivered) {
                if (!WorldAssetRowLoader.TryPin(hash: hash, pin: out var manifestPin)) {
                    error = $"sampleSet '{source}' is relative, this document has no directory to resolve it beside, and the voice carries no content pin to read it by.";
                    return false;
                }
                if (!WorldProjectionContent.Resolver.TryRead(content: out var pinnedManifest, maximumBytes: MaxPinnedManifestBytes, pin: manifestPin)) {
                    error = $"sampleSet '{source}' is read by its pin {manifestPin}, which this process does not hold.";
                    return false;
                }
                rawManifest = pinnedManifest.ToArray();
            } else {
                if (!WorldDocumentPaths.TryResolve(documentDirectory: documentDirectory, path: source, reason: out var reason, resolved: out var path)) {
                    error = reason;
                    return false;
                }
                rawManifest = File.ReadAllBytes(path: path);
                setDirectory = WorldDocumentPaths.DirectoryOf(documentPath: path);
            }

            var set = (JsonSerializer.Deserialize<WorldVoiceSampleSet>(utf8Json: rawManifest, options: Puck.Assets.Documents.DocumentJsonOptions.Shared)
                ?? throw new InvalidDataException(message: "Sample set is empty."));

            ValidateSetMetadata(set: set);
            if (!delivered && (hash is not null)) {
                if (!WorldAssetRowLoader.TryPin(hash: hash, pin: out var expectedPin)) {
                    throw new InvalidDataException(message: $"Voice hash '{hash}' is not a valid SHA-256 content pin.");
                }
                if (!TryCanonicalize(bytes: rawManifest, canonical: out var canonical, error: out var canonicalError)) {
                    throw new InvalidDataException(message: canonicalError);
                }
                var actualPin = ContentPin.Compute(content: canonical);
                if (actualPin != expectedPin) {
                    throw new InvalidDataException(message: $"Manifest content pin is '{actualPin.Hex}', expected '{expectedPin.Hex}'.");
                }
            }

            var entries = SyllableEntries(set: set);
            var loaded = new short[entries.Length][];
            var requireSyllablePins = (delivered || (hash is not null));

            for (var index = 0; (index < entries.Length); index++) {
                var (wavSource, wavHash) = entries[index];
                byte[] wavBytes;

                if (delivered) {
                    if (!WorldAssetRowLoader.TryPin(hash: wavHash, pin: out var wavPin)) {
                        throw new InvalidDataException(message: $"Syllable '{wavSource}' carries no content pin for remote delivery.");
                    }
                    if (!WorldProjectionContent.Resolver.TryRead(content: out var pinnedWav, maximumBytes: MaxPinnedSampleBytes, pin: wavPin)) {
                        throw new InvalidDataException(message: $"Syllable '{wavSource}' is read by its pin {wavPin}, which this process does not hold.");
                    }
                    wavBytes = pinnedWav.ToArray();
                } else {
                    var asset = WorldDocumentPaths.Resolve(documentDirectory: setDirectory, path: wavSource);
                    using (var stream = File.OpenRead(path: asset)) {
                        wavBytes = (Puck.Confinement.FileContent.ReadAtMost(maximumBytes: MaxPinnedSampleBytes, stream: stream)
                            ?? throw new InvalidDataException(message: $"Syllable '{wavSource}' exceeds the 1 MiB asset limit."));
                    }
                    if (requireSyllablePins || (wavHash is not null)) {
                        if (!WorldAssetRowLoader.TryPin(hash: wavHash, pin: out var expectedWav)) {
                            throw new InvalidDataException(message: $"Syllable '{wavSource}' requires a valid SHA-256 content pin.");
                        }
                        var actualWav = ContentPin.Compute(content: wavBytes);
                        if (actualWav != expectedWav) {
                            throw new InvalidDataException(message: $"Syllable '{wavSource}' content pin is '{actualWav.Hex}', expected '{expectedWav.Hex}'.");
                        }
                    }
                }
                try {
                    loaded[index] = PcmWave.ReadMono(bytes: wavBytes);
                } catch (InvalidDataException exception) {
                    throw new InvalidDataException($"Syllable '{wavSource}': {exception.Message}", exception);
                }
            }

            samples = loaded;
            rate = set.PlaybackRateThousandths;
            error = null;
            return true;
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)) {
            error = $"sampleSet '{source}': {exception.Message}";
            return false;
        }
    }

    private static (string Source, string? Hash)[] SyllableEntries(WorldVoiceSampleSet set) => [
        (set.A, set.AHash),
        (set.E, set.EHash),
        (set.I, set.IHash),
        (set.O, set.OHash),
        (set.U, set.UHash),
        (set.Consonant, set.ConsonantHash),
    ];

    private static void ValidateSetMetadata(WorldVoiceSampleSet set) {
        if (string.IsNullOrWhiteSpace(value: set.Name) || (set.PlaybackRateThousandths is < 250 or > 4000)) {
            throw new InvalidDataException(message: "Sample set needs a name and playbackRateThousandths in [250, 4000].");
        }
        if (string.IsNullOrWhiteSpace(value: set.A) || string.IsNullOrWhiteSpace(value: set.E) || string.IsNullOrWhiteSpace(value: set.I) ||
            string.IsNullOrWhiteSpace(value: set.O) || string.IsNullOrWhiteSpace(value: set.U) || string.IsNullOrWhiteSpace(value: set.Consonant)) {
            throw new InvalidDataException(message: "Sample set requires non-empty paths for a, e, i, o, u, and consonant.");
        }
    }

    private static bool TryCanonicalize(ReadOnlyMemory<byte> bytes, out byte[] canonical, out string error) {
        canonical = [];
        try {
            var set = JsonSerializer.Deserialize<WorldVoiceSampleSet>(utf8Json: bytes.Span, options: Puck.Assets.Documents.DocumentJsonOptions.Shared);
            if (set is null) {
                error = "Sample set is empty.";
                return false;
            }
            ValidateSetMetadata(set: set);
            static string? NormalizeOptionalPin(string? raw, string member) {
                if (raw is null) { return null; }
                return (WorldAssetRowLoader.TryPin(hash: raw, pin: out var pin)
                    ? pin.Hex
                    : throw new InvalidDataException(message: $"Sample set '{member}' is not a valid SHA-256 content pin."));
            }
            var aHash = NormalizeOptionalPin(raw: set.AHash, member: "aHash");
            var consonantHash = NormalizeOptionalPin(raw: set.ConsonantHash, member: "consonantHash");
            var eHash = NormalizeOptionalPin(raw: set.EHash, member: "eHash");
            var iHash = NormalizeOptionalPin(raw: set.IHash, member: "iHash");
            var oHash = NormalizeOptionalPin(raw: set.OHash, member: "oHash");
            var uHash = NormalizeOptionalPin(raw: set.UHash, member: "uHash");

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(utf8Json: buffer)) {
                writer.WriteStartObject();
                writer.WriteString(propertyName: "a", value: set.A);
                if (aHash is not null) { writer.WriteString(propertyName: "aHash", value: aHash); }
                writer.WriteString(propertyName: "consonant", value: set.Consonant);
                if (consonantHash is not null) { writer.WriteString(propertyName: "consonantHash", value: consonantHash); }
                writer.WriteString(propertyName: "e", value: set.E);
                if (eHash is not null) { writer.WriteString(propertyName: "eHash", value: eHash); }
                writer.WriteString(propertyName: "i", value: set.I);
                if (iHash is not null) { writer.WriteString(propertyName: "iHash", value: iHash); }
                writer.WriteString(propertyName: "name", value: set.Name);
                writer.WriteString(propertyName: "o", value: set.O);
                if (oHash is not null) { writer.WriteString(propertyName: "oHash", value: oHash); }
                writer.WriteNumber(propertyName: "playbackRateThousandths", value: set.PlaybackRateThousandths);
                writer.WriteString(propertyName: "u", value: set.U);
                if (uHash is not null) { writer.WriteString(propertyName: "uHash", value: uHash); }
                writer.WriteEndObject();
            }
            canonical = buffer.ToArray();
            error = string.Empty;
            return true;
        } catch (Exception exception) when (exception is JsonException or InvalidDataException) {
            error = exception.Message;
            return false;
        }
    }
}
