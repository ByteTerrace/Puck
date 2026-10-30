using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>The built-in <c>observation.fixture</c> provider: a read-only collection written in the extension
/// configuration itself, so a test or a headless run drives an observation with no service behind it.</summary>
/// <remarks>The provider settings are empty. An observation's settings hold <c>items</c> (each a <c>key</c> and its
/// <c>fields</c>) and an optional <c>failure</c> string; with a failure, every read throws, which is how a fixture shows
/// the unavailable and unauthorised feed. It performs no I/O and holds no credential.</remarks>
public sealed class WorldFixtureObservationProvider : IWorldConfiguredProvider, IWorldConfiguredObservationProvider {
    /// <summary>The provider's catalog key.</summary>
    public const string Type = "observation.fixture";

    /// <summary>Gets the catalog registration of the fixture provider.</summary>
    public static WorldExtensionProviderType Registration { get; } = new(
        Type,
        static settings => {
            if (settings.ValueKind is JsonValueKind.Object && settings.EnumerateObject().Any()) {
                throw new ArgumentException(message: "observation.fixture takes no provider settings.");
            }
            return new WorldFixtureObservationProvider();
        }
    );

    /// <inheritdoc/>
    public WorldExtensionOperation Bind(string name, string description, JsonElement settings) =>
        throw new ArgumentException(message: "observation.fixture provides observations, not operations.");
    /// <inheritdoc/>
    public IWorldExtensionObservationSource BindObservation(JsonElement settings, int maximumItems) {
        var fixture = (settings.Deserialize(WorldFixtureObservationJson.Default.WorldFixtureObservationSettings)
            ?? throw new JsonException(message: "Fixture observation settings are required."));

        if (fixture.Items.Count > maximumItems) {
            throw new ArgumentException(message: "Fixture observation holds more items than the observation allows.");
        }
        return new Source(
            failure: fixture.Failure,
            items: [.. fixture.Items.Select(selector: static item => new WorldExtensionObservationItem(
                item.Key,
                new Dictionary<string, string>(
                    collection: item.Fields,
                    comparer: StringComparer.Ordinal
                )
            ))]
        );
    }
    /// <inheritdoc/>
    public void Dispose() { }

    private sealed class Source(string? failure, IReadOnlyList<WorldExtensionObservationItem> items) : IWorldExtensionObservationSource {
        public string Kind => "fixture";

        public void Dispose() { }
        public ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken) =>
            ((failure is null)
                ? ValueTask.FromResult(result: items)
                : throw new InvalidOperationException(message: failure)
            );
    }
}
internal sealed record WorldFixtureObservationSettings(IReadOnlyList<WorldFixtureObservationItem> Items, string? Failure = null);
internal sealed record WorldFixtureObservationItem(string Key, IReadOnlyDictionary<string, string> Fields);
[JsonSerializable(typeof(WorldFixtureObservationSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
internal sealed partial class WorldFixtureObservationJson : JsonSerializerContext;
