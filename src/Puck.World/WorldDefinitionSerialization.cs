using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The System.Text.Json SOURCE-GENERATION context for the world document (<c>puck.world.def.v1</c>) — the only
/// sanctioned entry point for (de)serializing a <see cref="WorldDefinition"/>. Source-gen (not runtime reflection) keeps
/// the load/save boundary trimming/AOT-clean; enums serialize BY NAME (<see cref="MotionModel"/>,
/// <see cref="ShadowTier"/>, <see cref="Puck.Scene.WorldRenderScaleTier"/>, <see cref="ActionFact"/>), the camelCase
/// policy matches the authored JSON, and <see cref="Vector3"/> rides <see cref="Vector3JsonConverter"/> as a
/// three-element array (never <c>IncludeFields</c> — STJ silently zeroes struct fields without it; a converter sidesteps
/// the trap entirely).
/// </summary>
[JsonSerializable(typeof(WorldDefinition))]
// The row shapes the runtime mutation verbs parse as ONE inline-JSON argument — the same wire shape as the document
// section, so an editor/agent speaks one grammar. Every one is reachable from WorldDefinition already; these entries
// only expose the typed WorldJsonContext.Default.<Type> accessors the verbs deserialize through.
[JsonSerializable(typeof(WorldKit))]
[JsonSerializable(typeof(WorldScreen))]
[JsonSerializable(typeof(WorldCamera))]
[JsonSerializable(typeof(WorldScene))]
[JsonSerializable(typeof(WorldSceneRow))]
[JsonSerializable(typeof(WorldSpawnPoint[]))]
[JsonSerializable(typeof(MotionTuning))]
[JsonSerializable(typeof(WanderTuning))]
[JsonSerializable(typeof(WorldRenderDefaults))]
[JsonSerializable(typeof(WorldAddonRow))]
// The per-world binding overlay row the world.bindings.set verb parses as ONE inline-JSON argument — the same wire
// shape as the document section. Its BindingProfileDocument (from Puck.Commands) is registered explicitly so source-gen
// emits its metadata for both the canonical writer and the verb accessor.
[JsonSerializable(typeof(WorldBindingOverlay))]
[JsonSerializable(typeof(BindingProfileDocument))]
// The §D6 creation/placement rows (world.creation.set / world.placement.set verb accessors). The embedded
// puck.creation.v1 document rides CreationDocumentJsonConverter — its OWN canonical serializer — never this context's
// member policies (see the converter's remarks).
[JsonSerializable(typeof(WorldCreation))]
[JsonSerializable(typeof(WorldPlacement))]
// The P5.5 editor/authoring policy row (world.authoring.set verb accessor).
[JsonSerializable(typeof(WorldAuthoringDefaults))]
[JsonSourceGenerationOptions(
    Converters = new[] { typeof(Vector3JsonConverter), typeof(CreationDocumentJsonConverter) },
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true
)]
internal sealed partial class WorldJsonContext : JsonSerializerContext {
}

/// <summary>
/// Reads and writes a <see cref="Vector3"/> as a three-element JSON array <c>[x, y, z]</c>. Numbers ride STJ's default
/// shortest-round-trip invariant formatting — the canonical number form. Registered on <see cref="WorldJsonContext"/> so
/// every authored coordinate crosses this seam rather than exposing the struct's fields.
/// </summary>
internal sealed class Vector3JsonConverter : JsonConverter<Vector3> {
    /// <inheritdoc/>
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "a Vector3 must be a three-element [x, y, z] array.");
        }

        var x = ReadComponent(reader: ref reader);
        var y = ReadComponent(reader: ref reader);
        var z = ReadComponent(reader: ref reader);

        if (!reader.Read() || (reader.TokenType != JsonTokenType.EndArray)) {
            throw new JsonException(message: "a Vector3 array must contain exactly three elements.");
        }

        return new Vector3(x: x, y: y, z: z);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options) {
        writer.WriteStartArray();
        writer.WriteNumberValue(value: value.X);
        writer.WriteNumberValue(value: value.Y);
        writer.WriteNumberValue(value: value.Z);
        writer.WriteEndArray();
    }

    private static float ReadComponent(ref Utf8JsonReader reader) {
        if (!reader.Read() || (reader.TokenType != JsonTokenType.Number)) {
            throw new JsonException(message: "a Vector3 element must be a finite number.");
        }

        return reader.GetSingle();
    }
}

/// <summary>
/// Bridges an embedded <see cref="Puck.Authoring.CreationDocument"/> (a <see cref="WorldCreation.Document"/>) through
/// the creation contract's OWN serializer shape (<see cref="Puck.Authoring.DocumentJsonOptions.Shared"/> — member
/// order, string enums, and the LOAD-BEARING <c>IncludeFields</c> for Vector3/Quaternion) instead of this context's
/// policies, so the inline-canonical embed carries exactly the member vocabulary
/// <see cref="Puck.Authoring.CreationCanonicalizer"/> hashes. Formatting (indent/newlines) rides the outer canonical
/// writer, which is deterministic — the ouroboros gate covers the composition.
/// </summary>
internal sealed class CreationDocumentJsonConverter : JsonConverter<Puck.Authoring.CreationDocument> {
    /// <inheritdoc/>
    public override Puck.Authoring.CreationDocument? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<Puck.Authoring.CreationDocument>(reader: ref reader, options: Puck.Authoring.DocumentJsonOptions.Shared);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Puck.Authoring.CreationDocument value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer: writer, value: value, options: Puck.Authoring.DocumentJsonOptions.Shared);
}

/// <summary>
/// The canonical serializer for the world document — the ouroboros gate. <see cref="Save"/> emits a stable canonical
/// form (member order = record declaration order, invariant number formatting, no incidental whitespace drift): UTF-8
/// with no BOM, LF newlines, two-space indentation, and exactly one trailing newline at EOF, so a load→save reproduces
/// the file byte-for-byte and world files stay diffable and git-friendly.
/// </summary>
internal static class WorldDefinitionSerialization {
    // LF newlines + indentation, independent of the platform default, so a save is byte-stable across machines.
    private static readonly JsonWriterOptions s_writerOptions = new() {
        Indented = true,
        NewLine = "\n",
    };

    /// <summary>Serializes a definition to its canonical UTF-8 bytes (no BOM, LF newlines, one trailing newline).</summary>
    /// <param name="definition">The definition to serialize.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: stream, options: s_writerOptions)) {
            JsonSerializer.Serialize(writer: writer, value: definition, jsonTypeInfo: WorldJsonContext.Default.WorldDefinition);
        }

        // One trailing newline at EOF — the canonical, git-friendly terminator.
        stream.WriteByte(value: (byte)'\n');

        return stream.ToArray();
    }

    /// <summary>Writes a definition to <paramref name="path"/> in canonical form (the <c>world.save</c> path).</summary>
    /// <param name="definition">The definition to write.</param>
    /// <param name="path">The destination file path.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public static long Save(WorldDefinition definition, string path) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        var bytes = Serialize(definition: definition);

        File.WriteAllBytes(path: path, bytes: bytes);

        return bytes.LongLength;
    }
}
