using System.Text.Json;

namespace Puck.Cli.Parity;

/// <summary>Strict loader for the two documents <c>puck parity compare</c> reads: a capture pipeline's
/// <c>manifest.json</c> (<see cref="ManifestSchema"/>) and the comparator's own <c>--contract</c> config
/// (<see cref="ContractSchema"/>). Both refuse unknown members, duplicate keys, and any shape drift from the
/// pinned contract — a comparator this strict about its own inputs cannot silently misread a producer's
/// output as agreement.</summary>
internal static class ParityManifestLoader {
    private const string ContractSchema = "puck.parity.contract.v1";
    private const string ManifestSchema = "puck.parity.manifest.v1";
    private const int DefaultTileSize = 16;
    private const int MaxDepth = 16;
    private static readonly Func<string, Exception> Refusal = static message => new ParityDocumentRefusal(message: message);

    public static bool TryLoadManifest(string path, out ParityManifest manifest, out string error) {
        manifest = null!;
        error = string.Empty;

        try {
            using var document = ParseDocument(path: path);
            var root = CliStrictJson.RequireObject(element: document.RootElement, context: "manifest root", refusal: Refusal);

            CliStrictJson.RequireOnlyMembers(element: root, context: "manifest root", unknownMemberDetail: "strict documents refuse fields the comparator does not read.", refusal: Refusal, "schema", "backend", "world", "captures");
            RequireSchema(context: "manifest", element: root, expected: ManifestSchema);

            var backend = CliStrictJson.ReadRequiredString(context: "manifest", element: root, member: "backend", refusal: Refusal);

            if ((backend != "vulkan") && (backend != "directx")) {
                throw new ParityDocumentRefusal(message: $"manifest backend '{backend}' is invalid; use exactly 'vulkan' or 'directx'.");
            }

            var world = CliStrictJson.ReadRequiredString(context: "manifest", element: root, member: "world", refusal: Refusal);
            var capturesElement = CliStrictJson.ReadRequiredArray(context: "manifest", element: root, member: "captures", refusal: Refusal);

            if (capturesElement.GetArrayLength() == 0) {
                throw new ParityDocumentRefusal(message: "manifest captures is empty; a manifest with no scheduled captures proves nothing.");
            }

            var captures = new List<ParityManifestCapture>(capacity: capturesElement.GetArrayLength());
            var seen = new HashSet<(string Station, ulong Tick)>();

            for (var index = 0; (index < capturesElement.GetArrayLength()); index++) {
                var capture = ReadCapture(context: $"manifest captures[{index}]", element: capturesElement[index]);

                if (!seen.Add(item: (capture.Station, capture.Tick))) {
                    throw new ParityDocumentRefusal(message: $"manifest repeats capture station '{capture.Station}' tick {capture.Tick}; identity must be unique.");
                }

                captures.Add(item: capture);
            }

            manifest = new ParityManifest(Backend: backend, World: world, Captures: captures);

            return true;
        } catch (ParityDocumentRefusal refusal) {
            error = $"manifest '{path}' refused: {refusal.Message}";

            return false;
        } catch (JsonException exception) {
            error = $"manifest '{path}' refused: invalid JSON ({exception.Message.ReplaceLineEndings(replacementText: " ")}).";

            return false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            error = $"manifest '{path}' could not be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}.";

            return false;
        }
    }
    public static bool TryLoadContract(string path, out ParityContract contract, out string error) {
        contract = null!;
        error = string.Empty;

        try {
            using var document = ParseDocument(path: path);
            var root = CliStrictJson.RequireObject(element: document.RootElement, context: "contract root", refusal: Refusal);

            CliStrictJson.RequireOnlyMembers(element: root, context: "contract root", unknownMemberDetail: "strict documents refuse fields the comparator does not read.", refusal: Refusal, "schema", "tileSize", "stations");
            RequireSchema(context: "contract", element: root, expected: ContractSchema);

            var tileSize = DefaultTileSize;

            if (root.TryGetProperty(propertyName: "tileSize", value: out var tileSizeElement)) {
                if ((tileSizeElement.ValueKind != JsonValueKind.Number) || !tileSizeElement.TryGetInt32(value: out tileSize) || (tileSize <= 0)) {
                    throw new ParityDocumentRefusal(message: "contract tileSize must be a positive integer.");
                }
            }

            var stationsElement = CliStrictJson.ReadRequiredObject(context: "contract", element: root, member: "stations", refusal: Refusal);
            var stations = new Dictionary<string, ParityStationContract>(comparer: StringComparer.Ordinal);

            foreach (var property in stationsElement.EnumerateObject()) {
                stations[property.Name] = ReadStationContract(context: $"contract stations.{property.Name}", element: property.Value);
            }

            if (stations.Count == 0) {
                throw new ParityDocumentRefusal(message: "contract stations is empty; nothing could be gated or thresholded.");
            }

            contract = new ParityContract(TileSize: tileSize, Stations: stations);

            return true;
        } catch (ParityDocumentRefusal refusal) {
            error = $"contract '{path}' refused: {refusal.Message}";

            return false;
        } catch (JsonException exception) {
            error = $"contract '{path}' refused: invalid JSON ({exception.Message.ReplaceLineEndings(replacementText: " ")}).";

            return false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            error = $"contract '{path}' could not be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}.";

            return false;
        }
    }

    private static ParityManifestCapture ReadCapture(JsonElement element, string context) {
        var row = CliStrictJson.RequireObject(context: context, element: element, refusal: Refusal);

        CliStrictJson.RequireOnlyMembers(element: row, context: context, unknownMemberDetail: "strict documents refuse fields the comparator does not read.", refusal: Refusal, "station", "tick", "frame", "stateHash", "census", "cameraInside");

        var station = CliStrictJson.ReadRequiredString(context: context, element: row, member: "station", refusal: Refusal);
        var tick = ReadRequiredUInt64(context: context, element: row, member: "tick");
        var stateHash = CliStrictJson.ReadRequiredString(context: context, element: row, member: "stateHash", refusal: Refusal);

        if (!IsStateHash(value: stateHash)) {
            throw new ParityDocumentRefusal(message: $"{context} stateHash '{stateHash}' is not 16 lower-case hex digits.");
        }

        var cameraInside = ReadRequiredBool(context: context, element: row, member: "cameraInside");
        var hasFrame = row.TryGetProperty(propertyName: "frame", value: out _);
        var hasCensus = row.TryGetProperty(propertyName: "census", value: out _);

        if (cameraInside) {
            if (hasFrame || hasCensus) {
                throw new ParityDocumentRefusal(message: $"{context} cameraInside is true, so frame and census must be absent.");
            }

            return new ParityManifestCapture(Station: station, Tick: tick, StateHash: stateHash, CameraInside: true, Frame: null, Census: null);
        }

        if (!hasFrame || !hasCensus) {
            throw new ParityDocumentRefusal(message: $"{context} cameraInside is false, so frame and census are required.");
        }

        var frame = CliStrictJson.ReadRequiredString(context: context, element: row, member: "frame", refusal: Refusal);
        var census = ReadCensus(context: context, element: CliStrictJson.ReadRequiredObject(context: context, element: row, member: "census", refusal: Refusal));

        return new ParityManifestCapture(Station: station, Tick: tick, StateHash: stateHash, CameraInside: false, Frame: frame, Census: census);
    }
    private static IReadOnlyDictionary<string, long> ReadCensus(JsonElement element, string context) {
        var census = new Dictionary<string, long>(comparer: StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject()) {
            if ((property.Value.ValueKind != JsonValueKind.Number) || !property.Value.TryGetInt64(value: out var count) || (count < 0)) {
                throw new ParityDocumentRefusal(message: $"{context} census.{property.Name} must be a non-negative integer pixel count.");
            }

            census[property.Name] = count;
        }

        return census;
    }
    private static ParityStationContract ReadStationContract(JsonElement element, string context) {
        var row = CliStrictJson.RequireObject(context: context, element: element, refusal: Refusal);

        CliStrictJson.RequireOnlyMembers(element: row, context: context, unknownMemberDetail: "strict documents refuse fields the comparator does not read.", refusal: Refusal, "tileMeanDelta", "tileMaxDelta", "censusFloor");

        var tileMeanDelta = CliStrictJson.ReadRequiredFiniteNumber(context: context, element: row, member: "tileMeanDelta", descriptor: "finite number", refusal: Refusal);

        if (tileMeanDelta < 0) {
            throw new ParityDocumentRefusal(message: $"{context} tileMeanDelta must be zero or greater.");
        }

        var tileMaxDelta = CliStrictJson.ReadRequiredInt32(context: context, element: row, member: "tileMaxDelta", refusal: Refusal);

        if (tileMaxDelta < 0) {
            throw new ParityDocumentRefusal(message: $"{context} tileMaxDelta must be zero or greater.");
        }

        var censusFloor = new Dictionary<string, long>(comparer: StringComparer.Ordinal);

        if (row.TryGetProperty(propertyName: "censusFloor", value: out var censusFloorElement)) {
            foreach (var property in CliStrictJson.RequireObject(context: $"{context} censusFloor", element: censusFloorElement, refusal: Refusal).EnumerateObject()) {
                if ((property.Value.ValueKind != JsonValueKind.Number) || !property.Value.TryGetInt64(value: out var floor) || (floor < 0)) {
                    throw new ParityDocumentRefusal(message: $"{context} censusFloor.{property.Name} must be a non-negative integer minimum.");
                }

                censusFloor[property.Name] = floor;
            }
        }

        return new ParityStationContract(CensusFloor: censusFloor, TileMaxDelta: tileMaxDelta, TileMeanDelta: tileMeanDelta);
    }
    private static void RequireSchema(JsonElement element, string context, string expected) {
        var schema = CliStrictJson.ReadRequiredString(context: context, element: element, member: "schema", refusal: Refusal);

        if (!string.Equals(a: schema, b: expected, comparisonType: StringComparison.Ordinal)) {
            throw new ParityDocumentRefusal(message: $"{context} schema '{schema}' is not '{expected}'.");
        }
    }
    private static JsonDocument ParseDocument(string path) =>
        CliStrictJson.ParseStrict(path: path, maxDepth: MaxDepth, duplicateDetail: "the comparator must see one value.", refusal: Refusal);
    private static bool IsStateHash(string value) {
        if (value.Length != 16) {
            return false;
        }

        foreach (var character in value) {
            if (!(char.IsAsciiDigit(c: character) || ((character >= 'a') && (character <= 'f')))) {
                return false;
            }
        }

        return true;
    }
    private static bool ReadRequiredBool(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || ((value.ValueKind != JsonValueKind.True) && (value.ValueKind != JsonValueKind.False))) {
            throw new ParityDocumentRefusal(message: $"{context} {member} is required and must be true or false.");
        }

        return (value.ValueKind == JsonValueKind.True);
    }
    private static ulong ReadRequiredUInt64(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.Number) || !value.TryGetUInt64(value: out var result)) {
            throw new ParityDocumentRefusal(message: $"{context} {member} must be a non-negative integer.");
        }

        return result;
    }

    private sealed class ParityDocumentRefusal(string message) : Exception(message: message);
}
