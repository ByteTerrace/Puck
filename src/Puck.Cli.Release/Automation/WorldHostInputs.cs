using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>A hosting machine's typed deployment inputs: everything its bootstrap (<c>puck world host install</c>) writes
/// and starts, rendered by the deploying host and retained as an immutable object in the owner's release content store.
/// They hold no signing key: keys travel only in the protected bootstrap (<see cref="WorldHostBootstrap"/>).</summary>
/// <param name="Release">The world release the machine serves.</param>
/// <param name="Engine">The engine the machine runs: the payload identity of the host release it installed.</param>
/// <param name="Silo">The silo document. Each key file it names (<c>federation.pk8</c>, <c>issuer.pk8</c>) is a bare
/// credential name the install resolves into the silo unit's credentials directory.</param>
/// <param name="Guard">The boot guard: the release a bootstrap starts, checked against the group record.</param>
/// <param name="HealthPort">The silo's lifecycle listener port.</param>
/// <param name="QuicPort">The world's public QUIC port.</param>
/// <param name="ShutdownSeconds">The silo's drain allowance, in seconds.</param>
/// <param name="ReadySeconds">How long, in seconds, the bootstrap waits for private readiness.</param>
/// <param name="Cpu">The processors the silo may use.</param>
/// <param name="MemoryInGB">The memory, in gigabytes, the silo may use.</param>
/// <param name="Mcp">The hosted MCP front, or <see langword="null"/> when the deployment exposes none.</param>
public sealed record WorldHostInputs(
    [property: JsonPropertyName("release")] string Release,
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("silo")] JsonObject Silo,
    [property: JsonPropertyName("guard")] WorldReleaseGuestGuardRequest Guard,
    [property: JsonPropertyName("healthPort")] int HealthPort,
    [property: JsonPropertyName("quicPort")] int QuicPort,
    [property: JsonPropertyName("shutdownSeconds")] int ShutdownSeconds,
    [property: JsonPropertyName("readySeconds")] int ReadySeconds,
    [property: JsonPropertyName("cpu")] int Cpu,
    [property: JsonPropertyName("memoryInGB")] int MemoryInGB,
    [property: JsonPropertyName("mcp")] WorldHostMcp? Mcp
) {
    /// <summary>The schema a retained input document names.</summary>
    public const string CurrentSchema = "puck.world.host-inputs.v1";

    /// <summary>Gets the schema this document names.</summary>
    [JsonPropertyName("schema")] public string Schema { get; init; } = CurrentSchema;
}
/// <summary>The hosted MCP front: the silo's remote MCP configuration, the TLS front's configuration, the public host
/// name its certificate names, and the store key prefix its TLS state is kept under.</summary>
/// <param name="Options">The silo's remote MCP configuration (<c>--mcp</c>).</param>
/// <param name="Tls">The TLS front's configuration.</param>
/// <param name="Host">The public DNS name the front terminates.</param>
/// <param name="TlsState">The key, inside the owner's private object, under which the TLS state and its issuance lease
/// sit; it ends with <c>/</c>.</param>
public sealed record WorldHostMcp(
    [property: JsonPropertyName("options")] JsonObject Options,
    [property: JsonPropertyName("tls")] JsonObject Tls,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("tlsState")] string TlsState
);
/// <summary>The protected bootstrap a hosting machine's first command receives: where its inputs are, the pin they must
/// match, the signing keys, and the environment the provider's ambient identity reads.</summary>
/// <param name="Store">The owner's store as a silo document names one (<c>type</c> and <c>settings</c>), opened through
/// the installed world extensions.</param>
/// <param name="Owner">The world owner whose private object holds the inputs and the group record.</param>
/// <param name="Inputs">The full content pin of the deployment's retained input document, whose <c>host</c> member is
/// the machine's <see cref="WorldHostInputs"/>.</param>
/// <param name="Keys">The signing keys by credential file name, base64 PKCS#8.</param>
/// <param name="Environment">The environment every service on the machine starts with (the provider's identity
/// selection, such as a managed identity's client id).</param>
public sealed record WorldHostBootstrap(
    [property: JsonPropertyName("store")] JsonObject Store,
    [property: JsonPropertyName("owner")] Guid Owner,
    [property: JsonPropertyName("inputs")] string Inputs,
    [property: JsonPropertyName("keys")] IReadOnlyDictionary<string, string> Keys,
    [property: JsonPropertyName("environment")] IReadOnlyDictionary<string, string> Environment
) {
    /// <summary>Encodes the bootstrap for the install's standard input, which keeps its keys out of every process's
    /// arguments and environment.</summary>
    /// <returns>The base64 JSON.</returns>
    public string Encode() => Convert.ToBase64String(inArray: JsonSerializer.SerializeToUtf8Bytes(value: this));
    /// <summary>Decodes a bootstrap the install read from its standard input.</summary>
    /// <param name="value">The base64 JSON.</param>
    /// <returns>The bootstrap.</returns>
    /// <exception cref="InvalidDataException">The value is not a bootstrap.</exception>
    public static WorldHostBootstrap Decode(string value) {
        try {
            return (JsonSerializer.Deserialize<WorldHostBootstrap>(utf8Json: Convert.FromBase64String(s: value)) ?? throw new InvalidDataException(message: "the host bootstrap is empty"));
        } catch (Exception exception) when (exception is FormatException or JsonException) {
            throw new InvalidDataException(message: $"the host bootstrap is not readable: {exception.Message}");
        }
    }
}
/// <summary>What an installed hosting machine keeps about itself in <c>/etc/puck/host.json</c>: its store, owner and
/// environment, and the inputs it installed. The guest verbs (<c>guard</c>, <c>probe</c>, <c>request</c>) and the TLS
/// keeper read it, so a controller's guest command names only what changes per effect.</summary>
/// <param name="Store">The owner's store, as <see cref="WorldHostBootstrap.Store"/>.</param>
/// <param name="Owner">The world owner.</param>
/// <param name="Environment">The services' environment.</param>
/// <param name="Inputs">The inputs the machine installed.</param>
public sealed record WorldHostMachine(
    [property: JsonPropertyName("store")] JsonObject Store,
    [property: JsonPropertyName("owner")] Guid Owner,
    [property: JsonPropertyName("environment")] IReadOnlyDictionary<string, string> Environment,
    [property: JsonPropertyName("inputs")] WorldHostInputs Inputs
) {
    /// <summary>The machine record's path.</summary>
    public static string Path => $"{WorldHostLayout.ConfigurationDirectory}/host.json";

    /// <summary>Reads a machine record.</summary>
    /// <param name="path">The record's path.</param>
    /// <returns>The record.</returns>
    /// <exception cref="InvalidDataException">The file is missing or not a record.</exception>
    public static WorldHostMachine Read(string path) {
        try {
            return (JsonSerializer.Deserialize<WorldHostMachine>(utf8Json: File.ReadAllBytes(path: path)) ?? throw new InvalidDataException(message: $"{path} is empty"));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            throw new InvalidDataException(message: $"{path} is not a host machine record: {exception.Message}");
        }
    }
}
