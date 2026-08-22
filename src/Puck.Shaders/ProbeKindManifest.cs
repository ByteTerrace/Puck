using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Puck.Shaders;

/// <summary>A <see cref="ProbeKindManifest"/> socket's shape: what a bound <c>WorldFrameSource</c> must supply.</summary>
[JsonConverter(typeof(ProbeSocketClassJsonConverter))]
public enum ProbeSocketClass {
    /// <summary>Any one frame.</summary>
    Frame = 0,
    /// <summary>A strobing infrared sensor's lit frame and the unlit frame kept before it — two consecutive
    /// kernel registers, lit then unlit.</summary>
    StrobePair = 1,
}
/// <summary>Converts <see cref="ProbeSocketClass"/> to/from its lower camelCase JSON string. Hand-written (not a
/// reflection-based <c>JsonStringEnumConverter</c> naming policy) to stay AOT/trim-safe.</summary>
public sealed class ProbeSocketClassJsonConverter : JsonConverter<ProbeSocketClass> {
    /// <inheritdoc/>
    public override ProbeSocketClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var text = reader.GetString();

        return text switch {
            "frame" => ProbeSocketClass.Frame,
            "strobePair" => ProbeSocketClass.StrobePair,
            _ => throw new JsonException(message: $"Unrecognized probe socket class '{text}'; expected frame or strobePair."),
        };
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ProbeSocketClass value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: value switch {
            ProbeSocketClass.Frame => "frame",
            ProbeSocketClass.StrobePair => "strobePair",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The probe socket class is not defined."),
        });
    }
}
/// <summary>Where a <see cref="ProbeKindManifest"/> probe kind runs, in the document's own vocabulary; the
/// document never states a runtime, only this class.</summary>
[JsonConverter(typeof(ProbeKindClassJsonConverter))]
public enum ProbeKindClass {
    /// <summary>Handwritten GPU compute against the camera's own shared frame, on the kind's own device/thread.</summary>
    Kernel = 0,
    /// <summary>An out-of-process model host reading the shared frame read-only on its own device.</summary>
    Model = 1,
}
/// <summary>Converts <see cref="ProbeKindClass"/> to/from its lower camelCase JSON string. Hand-written (not a
/// reflection-based <c>JsonStringEnumConverter</c> naming policy) to stay AOT/trim-safe.</summary>
public sealed class ProbeKindClassJsonConverter : JsonConverter<ProbeKindClass> {
    /// <inheritdoc/>
    public override ProbeKindClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var text = reader.GetString();

        return text switch {
            "kernel" => ProbeKindClass.Kernel,
            "model" => ProbeKindClass.Model,
            _ => throw new JsonException(message: $"Unrecognized probe kind class '{text}'; expected kernel or model."),
        };
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ProbeKindClass value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: value switch {
            ProbeKindClass.Kernel => "kernel",
            ProbeKindClass.Model => "model",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The probe kind class is not defined."),
        });
    }
}
/// <summary>A <see cref="ProbeKindManifest"/>'s declared socket: a document row plugs one <c>WorldFrameSource</c>
/// into it.</summary>
/// <param name="Name">The socket's identifier: letters, digits, or <c>-</c>, starting with a letter, unique within
/// the manifest.</param>
/// <param name="Class">What a bound source must supply.</param>
/// <param name="Optional">Whether a document row may leave the socket unbound.</param>
public sealed record ProbeKindInput(string Name, ProbeSocketClass Class, bool Optional = false);
/// <summary>A kind's texture output: one <c>rgba8</c> ring at the bound source's extent for the named socket,
/// published like a camera frame; a screen names the probe as its source to show it.</summary>
/// <param name="Of">The socket whose bound source's extent the output takes.</param>
/// <param name="Format">The pixel format; only <c>rgba8</c> is admitted.</param>
public sealed record ProbeKindOutput(string Of, string Format = ProbeKindOutput.Rgba8) {
    public const string Rgba8 = "rgba8";
}
/// <summary>A KERNEL-class kind's compiled-shader entry points.</summary>
/// <param name="Source">The HLSL source file's name, beside the manifest.</param>
/// <param name="Accumulate">The per-pixel accumulation entry point.</param>
/// <param name="Finalize">The single-dispatch entry point that writes the reading's channels.</param>
public sealed record ProbeKindKernel(string Source, string Accumulate, string Finalize);

/// <summary>
/// A <c>puck.probe.v1</c> probe kind manifest: one <c>&lt;id&gt;.puck.probe.json</c> declaring an probe's
/// input, its channels, and — for a <see cref="ProbeKindClass.Kernel"/> kind — the HLSL source and entry points a
/// kernel host compiles and runs. Registered exactly the way a <see cref="ShaderSetManifest"/> registers a shader
/// set: a document names a kind by id, and shipping the manifest beside its kernel source (when it has one) IS
/// registering it. Config binds through the same <see cref="ShaderConfigBinding"/> a shader set's config binds
/// through.
/// </summary>
/// <param name="Schema">The schema tag; must equal <see cref="SchemaTag"/>.</param>
/// <param name="Name">The kind's id; must equal the manifest's file stem (the text before <see cref="FileSuffix"/>).</param>
/// <param name="Class">Where the kind runs.</param>
/// <param name="Inputs">The kind's declared sockets, bound at <c>t0, t1, …</c> in this order; a
/// <see cref="ProbeSocketClass.StrobePair"/> socket takes two consecutive registers.</param>
/// <param name="Channels">The kind's channels, in declaration order; <c>1..</c><see cref="MaxChannels"/>.</param>
/// <param name="Kernel">The kernel's source and entry points; required when <paramref name="Class"/> is
/// <see cref="ProbeKindClass.Kernel"/>.</param>
/// <param name="Description">What the kind measures; carried into the emitted config JSON Schema.</param>
/// <param name="Config">The config schema, name → field, in the order a schema emits them; <see langword="null"/>
/// when the kind takes no configuration.</param>
/// <param name="Trigger">The socket whose new frame starts a cycle, by name; <see langword="null"/> takes the
/// first socket.</param>
/// <param name="Output">The texture the kind writes, or <see langword="null"/> when it only reports channels.</param>
public sealed partial record ProbeKindManifest(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    ProbeKindClass Class,
    IReadOnlyList<ProbeKindInput> Inputs,
    IReadOnlyList<ProbeChannelSpec> Channels,
    ProbeKindKernel? Kernel = null,
    string? Description = null,
    IReadOnlyDictionary<string, ShaderConfigField>? Config = null,
    string? Trigger = null,
    ProbeKindOutput? Output = null
) {
    /// <summary>Gets the index into <see cref="Inputs"/> of the socket whose new frame starts a cycle —
    /// <see cref="Trigger"/>'s socket when authored, else socket 0; -1 when <see cref="Trigger"/> names no
    /// declared socket.</summary>
    [JsonIgnore]
    public int TriggerSocket {
        get {
            if (Trigger is null) {
                return 0;
            }

            for (var index = 0; (index < Inputs.Count); index++) {
                if (string.Equals(a: Inputs[index].Name, b: Trigger, comparisonType: StringComparison.Ordinal)) {
                    return index;
                }
            }

            return -1;
        }
    }

    /// <summary>The file suffix every manifest carries; the text before it is the kind's id.</summary>
    public const string FileSuffix = ".puck.probe.json";
    /// <summary>The required <c>$schema</c> value of every <see cref="ProbeKindManifest"/> document.</summary>
    public const string SchemaTag = "puck.probe.v1";
    /// <summary>The channel-count ceiling — matches <c>Puck.Platform.Probes.ProbeReadingLimits.MaxChannels</c>,
    /// the fixed slot count a <c>ProbeReading</c> carries; keep the two in sync.</summary>
    public const int MaxChannels = 8;
    /// <summary>The socket-count ceiling: a kind declares at least one socket and at most this many.</summary>
    public const int MaxSockets = 8;

    /// <summary>Gets the directory the manifest was loaded from — where its kernel source resolves.</summary>
    [JsonIgnore]
    public string Directory { get; private init; } = "";

    /// <summary>Reads, parses, and validates a manifest file: the <c>$schema</c> tag, the name against the file
    /// stem, the channel list (non-empty, at most <see cref="MaxChannels"/>, unique names, each neutral inside its
    /// own range), a kernel-class kind's kernel block and source file, and the config schema.</summary>
    /// <param name="manifestPath">The manifest file's path.</param>
    /// <returns>The validated manifest.</returns>
    /// <exception cref="FileNotFoundException">The manifest, or a kernel's source file, does not exist.</exception>
    /// <exception cref="InvalidDataException">The manifest is malformed under any rule above.</exception>
    public static ProbeKindManifest Load(string manifestPath) {
        if (!File.Exists(path: manifestPath)) {
            throw new FileNotFoundException(fileName: manifestPath, message: $"Probe kind manifest not found: {manifestPath}");
        }

        ProbeKindManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize(json: File.ReadAllText(path: manifestPath), jsonTypeInfo: ProbeKindManifestJsonContext.Default.ProbeKindManifest)
                ?? throw new InvalidDataException(message: $"Probe kind manifest is empty or 'null': {manifestPath}"));
        } catch (JsonException exception) {
            throw new InvalidDataException(message: $"Probe kind manifest '{manifestPath}' is malformed: {exception.Message}", innerException: exception);
        }

        if (!string.Equals(a: manifest.Schema, b: SchemaTag, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"Probe kind manifest '{manifestPath}' declares '$schema' = '{manifest.Schema}'; expected '{SchemaTag}'.");
        }

        var fileName = Path.GetFileName(path: manifestPath);
        var stem = (fileName.EndsWith(comparisonType: StringComparison.Ordinal, value: FileSuffix) ? fileName[..^FileSuffix.Length] : null);

        if ((stem is null) || !string.Equals(a: stem, b: manifest.Name, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"Probe kind manifest '{manifestPath}' is named '{manifest.Name}'; the file must be '{manifest.Name}{FileSuffix}'.");
        }

        var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: manifestPath)) ?? "");

        if ((manifest.Inputs is null) || (manifest.Inputs.Count == 0)) {
            throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares no inputs.");
        }
        if (manifest.Inputs.Count > MaxSockets) {
            throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares {manifest.Inputs.Count} sockets; a probe kind admits at most {MaxSockets}.");
        }

        var socketNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var input in manifest.Inputs) {
            if (!IsValidSocketName(name: input.Name)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares a socket named '{input.Name}'; a socket name is letters, digits, or '-', starting with a letter.");
            }
            if (!socketNames.Add(item: input.Name)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares socket '{input.Name}' twice.");
            }
        }

        if (manifest.TriggerSocket < 0) {
            throw new InvalidDataException(message: $"'{manifest.Name}' manifest's trigger '{manifest.Trigger}' does not name a declared socket.");
        }
        if (manifest.Output is { } output) {
            if (!socketNames.Contains(item: output.Of)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest's output.of '{output.Of}' does not name a declared socket.");
            }
            if (!string.Equals(a: output.Format, b: ProbeKindOutput.Rgba8, comparisonType: StringComparison.Ordinal)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest's output format '{output.Format}' is not {ProbeKindOutput.Rgba8}.");
            }
        }
        if (manifest.Channels.Count == 0) {
            throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares no channels.");
        }
        if (manifest.Channels.Count > MaxChannels) {
            throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares {manifest.Channels.Count} channels; a sense reading carries at most {MaxChannels}.");
        }

        var channelNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var channel in manifest.Channels) {
            if (string.IsNullOrEmpty(value: channel.Name)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares a channel with an empty name.");
            }
            if (!channelNames.Add(item: channel.Name)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares channel '{channel.Name}' twice.");
            }
            if (channel.Min > channel.Max) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest's channel '{channel.Name}' has min {Format(value: channel.Min)} above max {Format(value: channel.Max)}.");
            }
            if ((channel.Neutral < channel.Min) || (channel.Neutral > channel.Max)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest's channel '{channel.Name}' has neutral {Format(value: channel.Neutral)} outside [{Format(value: channel.Min)}, {Format(value: channel.Max)}].");
            }
        }

        if (manifest.Class == ProbeKindClass.Kernel) {
            if (manifest.Kernel is not { } kernel) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest is a kernel-class kind and must declare a 'kernel' block.");
            }
            if (string.IsNullOrEmpty(value: kernel.Source)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest's kernel.source is empty.");
            }
            if (string.IsNullOrEmpty(value: kernel.Accumulate) || string.IsNullOrEmpty(value: kernel.Finalize)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest's kernel accumulate/finalize entry points must be non-empty.");
            }

            var sourcePath = Path.Combine(path1: directory, path2: kernel.Source);

            if (!File.Exists(path: sourcePath)) {
                throw new FileNotFoundException(fileName: sourcePath, message: $"'{manifest.Name}' manifest's kernel source '{kernel.Source}' does not exist beside the manifest.");
            }
        }

        ShaderConfigBinding.ValidateSchema(schema: manifest.Config, ownerName: manifest.Name);

        return (manifest with { Directory = directory });
    }
    /// <summary>Binds a document's <c>config</c> for this kind, or throws with the id and the refusing field.</summary>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <returns>The bound values, every absent field at its default.</returns>
    /// <exception cref="InvalidOperationException">The configuration is invalid.</exception>
    public ShaderConfigValues BindConfig(JsonElement? config) {
        return (TryBindConfig(config: config, values: out var values, reason: out var reason)
            ? values
            : throw new InvalidOperationException(message: $"'{Name}' config is invalid: {reason}")
        );
    }
    /// <summary>Emits this kind's config schema as a JSON Schema object.</summary>
    /// <returns>The schema node.</returns>
    public JsonObject ConfigJsonSchema() =>
        ShaderConfigBinding.JsonSchema(schema: Config, description: Description);
    /// <summary>Validates a document's <c>config</c> against this kind's config schema and resolves every absent
    /// field to its default.</summary>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <param name="values">The bound values, set only when this returns <see langword="true"/>.</param>
    /// <param name="reason">The refusal reason naming the field, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="config"/> is valid.</returns>
    public bool TryBindConfig(JsonElement? config, out ShaderConfigValues values, out string reason) =>
        ShaderConfigBinding.TryBind(schema: Config, config: config, ownerName: Name, values: out values, reason: out reason);
    /// <summary>The constant-buffer size granule: a Direct3D 11 constant buffer's byte width must be a multiple of
    /// 16, so <see cref="ConstantsBlock"/> pads the packed fields up to it.</summary>
    public const int ConstantsBlockAlignment = 16;

    /// <summary>Finds where one config field sits inside <see cref="ConstantsBlock"/>'s packed bytes.</summary>
    /// <param name="field">The config field name.</param>
    /// <param name="offset">The field's byte offset, set only when this returns <see langword="true"/>.</param>
    /// <param name="type">The field's value type, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the kind declares the field.</returns>
    public bool TryGetConstantOffset(string field, out int offset, out ShaderValueType type) {
        if (Config is null) {
            offset = 0;
            type = default;

            return false;
        }

        var types = new ShaderValueType[Config.Count];
        var index = 0;

        foreach (var candidate in Config.Values) {
            types[index] = candidate.Type;
            index++;
        }

        var offsets = ShaderPushConstantLayout.ComputeOffsets(types: types, sizeBytes: out _);

        index = 0;

        foreach (var (name, candidate) in Config) {
            if (string.Equals(a: name, b: field, comparisonType: StringComparison.Ordinal)) {
                offset = ((int)offsets[index]);
                type = candidate.Type;

                return true;
            }

            index++;
        }

        offset = 0;
        type = default;

        return false;
    }
    /// <summary>Packs bound config values into the kernel's constant-buffer bytes, in <see cref="Config"/>'s
    /// declaration order under the same packing rule as a shader set's push-constant block
    /// (<see cref="ShaderPushConstantLayout.ComputeOffsets"/>), then pads the block to a multiple of
    /// <see cref="ConstantsBlockAlignment"/>.</summary>
    /// <param name="values">This kind's bound config (<see cref="BindConfig"/>).</param>
    /// <returns>The packed bytes; empty when the kind takes no configuration.</returns>
    public ReadOnlyMemory<byte> ConstantsBlock(ShaderConfigValues values) {
        ArgumentNullException.ThrowIfNull(argument: values);

        if (Config is null) {
            return ReadOnlyMemory<byte>.Empty;
        }

        var types = new ShaderValueType[Config.Count];
        var index = 0;

        foreach (var field in Config.Values) {
            types[index] = field.Type;
            index++;
        }

        var offsets = ShaderPushConstantLayout.ComputeOffsets(types: types, sizeBytes: out var sizeBytes);
        var paddedSize = ((((int)sizeBytes) + (ConstantsBlockAlignment - 1)) / ConstantsBlockAlignment) * ConstantsBlockAlignment;
        var block = new byte[paddedSize];

        index = 0;

        foreach (var name in Config.Keys) {
            values[name].Bytes.Span.CopyTo(destination: block.AsSpan(start: ((int)offsets[index])));
            index++;
        }

        return block;
    }

    private static string Format(double value) => value.ToString(provider: CultureInfo.InvariantCulture);
    private static bool IsValidSocketName(string name) {
        if (string.IsNullOrEmpty(value: name) || !char.IsAsciiLetter(c: name[0])) {
            return false;
        }

        foreach (var character in name) {
            if (!char.IsAsciiLetterOrDigit(c: character) && (character != '-')) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The source-generated (AOT/trim-safe) serialization context for <see cref="ProbeKindManifest"/>.</summary>
[JsonSerializable(typeof(ProbeKindManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ProbeKindManifestJsonContext : JsonSerializerContext {
}
