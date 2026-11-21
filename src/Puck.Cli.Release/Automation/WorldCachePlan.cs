using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Cli.Mesh;
using Puck.World.Client;

namespace Puck.Cli.Automation;

/// <summary>The bake groups a player build's world plans, which a cache set records when it is produced and a release
/// checks its own world against, with no device: a set is whole for a release only when the release plans exactly the
/// groups the set was produced for.</summary>
/// <remarks>The plan is the world's rest scene at its finest tier (<see cref="RestScene.PlanKeys"/>), which both sides
/// compute from the world documents with the same bake code, since a set is taken only under an equal mesh bake identity.
/// It names every group the world plans, so a document change to one group's surface makes the set stale for a release.
/// The plan is compared rather than looked up in the store because a live frame keys some groups (a posed character's
/// parts) by poses a rest scene does not stand, so those keys cannot be enumerated offline. What the plan cannot show
/// is whether the producer's store held every group and tier a view demands; the player folder's own check boots
/// (<see cref="WorldPlayerCommand"/>), which draw the finished store alone on each target's profile at every authored
/// place, show that, and a folder whose check boots did not is not complete, so it produces no set.</remarks>
public static class WorldCachePlan {
    /// <summary>Plans the groups of the world a player folder runs.</summary>
    /// <param name="folder">The player folder, whose <c>worlds/</c> holds the compiled package.</param>
    /// <param name="world">The world's path relative to the folder, as <c>player.json</c> records it.</param>
    /// <param name="keys">The groups, when the world composed.</param>
    /// <param name="why">Why the world did not compose, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the world's groups were planned.</returns>
    public static bool TryRead(string folder, string world, out IReadOnlyList<RestGroupKey> keys, out string? why) {
        keys = [];

        var path = Path.GetFullPath(path: Path.Combine(path1: folder, path2: world));

        if (!CliWorldDocuments.TryLoad(definition: out var definition, fullPath: out var document, path: path, reason: out var reason)) {
            why = $"its world {world} does not load: {reason}";
            return false;
        }
        try {
            keys = RestScene.Compose(definition: definition, sourcePath: document).PlanKeys(farDistance: WorldRenderFarDistance.Resolve(defaults: definition.Render));
            why = null;
            return true;
        } catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) {
            why = $"its world {world} does not compose offline: {exception.Message}";
            return false;
        }
    }
    /// <summary>Writes a plan as a cache set's record holds it: one string a group, its hash and its label.</summary>
    /// <param name="keys">The groups (<see cref="TryRead"/>).</param>
    /// <returns>The array.</returns>
    public static JsonArray ToJson(IReadOnlyList<RestGroupKey> keys) =>
        new([.. keys.Select(selector: static key => JsonValue.Create(value: $"{key.Digest.ToString(format: "x32", provider: CultureInfo.InvariantCulture)} {key.Label}"))]);
    /// <summary>Returns the groups of a release's plan that a cache set's recorded plan does not hold.</summary>
    /// <param name="release">The release's own plan (<see cref="TryRead"/>).</param>
    /// <param name="recorded">The plan the set's record holds (<see cref="ToJson"/>), or <see langword="null"/> for a record
    /// without one.</param>
    /// <returns>The labels of the groups the set was not produced for, in plan order; <see langword="null"/> when the record
    /// holds no plan, which a set cannot be known whole without.</returns>
    public static IReadOnlyList<string>? Lacking(IReadOnlyList<RestGroupKey> release, JsonNode? recorded) {
        if (recorded is not JsonArray entries) { return null; }

        var held = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var entry in entries) {
            var text = (string?)entry;

            if ((text is not null) && (text.IndexOf(value: ' ') is var space and > 0)) { _ = held.Add(item: text[..space]); }
        }

        return [.. release.Where(predicate: key => !held.Contains(item: key.Digest.ToString(format: "x32", provider: CultureInfo.InvariantCulture))).Select(selector: static key => key.Label)];
    }
    /// <summary>Names the groups a set was not produced for: how many, and the first few by label.</summary>
    /// <param name="lacking">The labels (<see cref="Lacking"/>).</param>
    /// <param name="total">The groups the release's world plans.</param>
    /// <returns>The sentence fragment.</returns>
    public static string Describe(IReadOnlyList<string> lacking, int total) {
        const int Named = 5;

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{lacking.Count} of the world's {total} bake groups ({string.Join(separator: ", ", values: lacking.Take(count: Named))}{((lacking.Count > Named) ? ", ..." : string.Empty)})");
    }
}
