using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;

namespace Puck.World;

/// <summary>An authored six-class syllable bank. Sources resolve beside this set document.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldVoiceSampleSet(string Name, string A, string E, string I, string O, string U,
    string Consonant, int PlaybackRateThousandths = 1500);
/// <summary>Loads a voice's sample bank through the world's document-relative asset path resolver.</summary>
public static class WorldVoiceSampleLoader {
    /// <summary>Loads and validates the manifest and every PCM asset. The order is A, E, I/Y, O, U, consonant/digit.</summary>
    public static bool TryLoad(string? documentDirectory, string source, out short[][] samples, out int rate, out string? error) {
        samples = [];
        rate = 0;
        if (!WorldDocumentPaths.TryResolve(documentDirectory: documentDirectory, path: source, reason: out var reason, resolved: out var path)) {
            error = reason;
            return false;
        }
        try {
            var set = (JsonSerializer.Deserialize<WorldVoiceSampleSet>(File.ReadAllBytes(path: path), Puck.Assets.Documents.DocumentJsonOptions.Shared)
                ?? throw new InvalidDataException(message: "Sample set is empty."));

            if (string.IsNullOrWhiteSpace(value: set.Name) || (set.PlaybackRateThousandths is < 250 or > 4000)) {
                throw new InvalidDataException(message: "Sample set needs a name and playbackRateThousandths in [250, 4000].");
            }
            var directory = WorldDocumentPaths.DirectoryOf(documentPath: path);
            string[] sources = [set.A, set.E, set.I, set.O, set.U, set.Consonant];
            var loaded = new short[sources.Length][];

            for (var index = 0; (index < sources.Length); index++) {
                var asset = WorldDocumentPaths.Resolve(documentDirectory: directory, path: sources[index]);

                if (new FileInfo(fileName: asset).Length > 1_048_576) { throw new InvalidDataException(message: $"Syllable '{sources[index]}' exceeds the 1 MiB asset limit."); }
                try {
                    loaded[index] = PcmWave.ReadMono(bytes: File.ReadAllBytes(path: asset));
                } catch (InvalidDataException exception) {
                    throw new InvalidDataException($"Syllable '{sources[index]}': {exception.Message}", exception);
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
}
