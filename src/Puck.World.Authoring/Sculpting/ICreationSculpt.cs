using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;

namespace Puck.World.Authoring.Sculpting;

/// <summary>The world document a sculpt reads to carry forward hand-authored data by name (see
/// <see cref="CreationBuilder.CarryRig"/>) — the document as it stands right now, either the live in-engine
/// definition or an offline file, both reduced to the same raw <see cref="JsonObject"/> shape.</summary>
/// <param name="Document">The whole world document.</param>
public sealed record SculptContext(JsonObject Document) {
    /// <summary>Reads and deserializes one <c>prototypes[].document</c> entry's <see cref="CreationDocument"/> by
    /// its prototype id, for carrying rig data (swings/slides) forward by shape name. Null when the document
    /// declares no such prototype, or the prototype names no <c>document</c>.</summary>
    /// <param name="prototypeId">The prototype's <c>id</c>.</param>
    public CreationDocument? TryGetCreationDocument(string prototypeId) {
        var prototypes = (Document["prototypes"] as JsonArray);
        var match = prototypes?.FirstOrDefault(predicate: node => string.Equals(
            a: (node?["id"]?.GetValue<string>()),
            b: prototypeId,
            comparisonType: StringComparison.Ordinal
        ));

        if ((match?["document"]) is not { } documentNode) {
            return null;
        }

        return JsonSerializer.Deserialize<CreationDocument>(
            json: documentNode.ToJsonString(),
            options: DocumentJsonOptions.Shared
        );
    }
}

/// <summary>One named, registered sculpt: a generator that turns the current world document into a
/// <see cref="SculptPatch"/> — the same power a hand-authored world document has, expressed as code instead of JSON.
/// A sculpt reads <see cref="SculptContext.Document"/> to carry forward hand-authored data by name; it never writes
/// to it directly, so applying its patch (offline, or live through the console) is the only door a sculpt's changes
/// cross.</summary>
public interface ICreationSculpt {
    /// <summary>The sculpt's registered name — the token <c>creation.sculpt &lt;name&gt;</c>/<c>puck creation
    /// sculpt &lt;name&gt;</c> address it by.</summary>
    string Name { get; }
    /// <summary>One line describing what this sculpt authors.</summary>
    string Description { get; }
    /// <summary>Builds the patch this sculpt applies against <paramref name="context"/>'s document.</summary>
    SculptPatch Sculpt(SculptContext context);
}

/// <summary>The sculpt registry — empty as shipped; a sculpt is registered by a composition root or a test through
/// <see cref="Register"/>.</summary>
public static class CreationSculptRegistry {
    private static readonly Dictionary<string, ICreationSculpt> s_byName = new(comparer: StringComparer.Ordinal);
    private static readonly List<ICreationSculpt> s_shipped = [];

    /// <summary>Every registered sculpt, in registration order.</summary>
    public static IReadOnlyList<ICreationSculpt> All => s_shipped;

    /// <summary>Registers (or replaces) a sculpt by its own <see cref="ICreationSculpt.Name"/> — the seam a
    /// composition root or a test registers a sculpt through.</summary>
    public static void Register(ICreationSculpt sculpt) {
        ArgumentNullException.ThrowIfNull(argument: sculpt);

        if (s_byName.TryAdd(
            key: sculpt.Name,
            value: sculpt
        )) {
            s_shipped.Add(item: sculpt);
        } else {
            s_byName[sculpt.Name] = sculpt;

            var index = s_shipped.FindIndex(match: existing => string.Equals(
                a: existing.Name,
                b: sculpt.Name,
                comparisonType: StringComparison.Ordinal
            ));

            s_shipped[index] = sculpt;
        }
    }
    /// <summary>Resolves a sculpt by name.</summary>
    public static bool TryGet(string name, out ICreationSculpt sculpt) => s_byName.TryGetValue(
        key: name,
        value: out sculpt!
    );
}
