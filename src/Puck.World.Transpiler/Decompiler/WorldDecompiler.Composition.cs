using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

// A composition's worlds print back as one source: each world a module and a `world name = name()` declaration, and
// each `border` and `door` between them read back out of the rows it generated (WorldCompositionLinks). A link's rows
// are generated names in both worlds it joins, so neither world prints alone.
public static partial class WorldDecompiler {
    // One link read back out of the rows it generated, with the options that regenerate them.
    private sealed record ReadLink(string Kind, string LeftWorld, string LeftEndpoint, string RightWorld, string RightEndpoint, JsonObject Options) {
        public WorldCompositionLink ToLink() => new(
            kind: Kind,
            leftEndpoint: LeftEndpoint,
            leftWorld: LeftWorld,
            options: ((JsonObject)Options.DeepClone()),
            rightEndpoint: RightEndpoint,
            rightWorld: RightWorld,
            span: SourceSpan.None
        );
    }

    private const double DefaultBorderHeight = 16d;
    private const string AnyLink = "link$*";

    /// <summary>Decompiles the worlds one composition emits into the one source that emits them: each world a
    /// <c>module</c> expanded by a <c>world name = name()</c> declaration, and each <c>border</c> and <c>door</c>
    /// between them read back from the adjacency, reference, destination, portal and return-arch rows it generated
    /// in the two worlds it joins.</summary>
    /// <param name="worlds">The worlds, each under the name its declaration gives it (its <c>&lt;name&gt;.world.json</c>
    /// file stem), in declaration order.</param>
    /// <param name="embeddings">Optional companion embedding lock file for resolving vector literals.</param>
    /// <returns>The composition source.</returns>
    /// <exception cref="ArgumentException"><paramref name="worlds"/> is empty or names a world twice.</exception>
    /// <exception cref="WorldDecompileRefusedException">A link's rows do not regenerate from any <c>border</c> or
    /// <c>door</c>, or a world declares a generated-form name no construct prints back.</exception>
    public static string DecompileComposition(IReadOnlyList<KeyValuePair<string, JsonObject>> worlds, EmbeddingLock? embeddings = null) {
        ArgumentNullException.ThrowIfNull(worlds);

        if (worlds.Count == 0) {
            throw new ArgumentException(message: "a composition emits at least one world", paramName: nameof(worlds));
        }

        var originals = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);
        var order = new List<string>(capacity: worlds.Count);

        foreach (var (name, document) in worlds) {
            ArgumentNullException.ThrowIfNull(document);

            if (!originals.TryAdd(key: name, value: document)) {
                throw new ArgumentException(message: $"the world '{name}' is named twice", paramName: nameof(worlds));
            }

            order.Add(item: name);
        }

        var links = ReadLinks(
            order: order,
            worlds: originals
        );
        var stripped = order.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: name => StripLinks(
                document: originals[name],
                links: links,
                world: name,
                worlds: originals
            ),
            keySelector: static name => name
        );

        VerifyLinks(
            links: links,
            order: order,
            originals: originals,
            stripped: stripped
        );

        var source = new StringBuilder(value: Header);

        foreach (var name in order) {
            source.Append(value: "module ").Append(value: ModuleName(world: name, worlds: order)).AppendLine(value: "() {");
            source.Append(value: DecompileDocument(
                embeddings: embeddings,
                moduleBody: true,
                root: stripped[name]
            ));
            source.AppendLine(value: "}").AppendLine();
        }
        foreach (var name in order) {
            source.Append(value: "world ").Append(value: PuckPrinter.PrintName(name: name)).Append(value: " = ").Append(value: ModuleName(world: name, worlds: order)).AppendLine(value: "()");
        }
        foreach (var link in links) {
            source.AppendLine();
            AppendLink(
                link: link,
                source: source
            );
        }

        var text = source.ToString();
        var formatted = PuckPrinter.Format(
            source: text,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        return (formatted.Value ?? text);
    }

    // A world's module takes the world's own name where that is an identifier a call can spell, and a positional one
    // otherwise.
    private static string ModuleName(string world, List<string> worlds) => (IdentifierSpelling.IsName(text: world)
        ? world
        : $"world{worlds.IndexOf(item: world).ToString(provider: CultureInfo.InvariantCulture)}"
    );
    private static void AppendLink(ReadLink link, StringBuilder source) {
        source.Append(value: link.Kind)
            .Append(value: ' ')
            .Append(value: Endpoint(endpoint: link.LeftEndpoint, world: link.LeftWorld))
            .Append(value: ", ")
            .Append(value: Endpoint(endpoint: link.RightEndpoint, world: link.RightWorld));

        if (link.Options.Count == 0) {
            source.AppendLine();

            return;
        }

        source.AppendLine(value: " {");
        foreach (var (key, value) in link.Options) {
            source.Append(value: "    ").Append(value: key).Append(value: FieldSeparator(value: value)).AppendLine(value: FormatValue(indentLevel: 1, node: value));
        }
        source.AppendLine(value: "}");
    }
    // `world.endpoint`, dotted as written for a ground side (`hub.floor.east`); a door face written after a slash
    // rides the call form, which appends its argument to the endpoint (`hub.arch("/side")`).
    private static string Endpoint(string world, string endpoint) {
        var slash = endpoint.IndexOf(value: '/');

        return ((slash < 0)
            ? QualifiedName.Parse(text: world).Append(member: endpoint).ToString()
            : $"{QualifiedName.Parse(text: world).Append(member: endpoint[..slash])}({PuckStrings.Write(value: endpoint[slash..])})"
        );
    }
    // ---- reading links back ---------------------------------------------------------------------------------------

    // Every link the worlds' rows were generated by, in the order the links were applied: each world's link-generated
    // reference rows stand in its links' order, and the links are ordered to agree with every world at once.
    private static List<ReadLink> ReadLinks(Dictionary<string, JsonObject> worlds, List<string> order) {
        var found = new List<ReadLink>();

        foreach (var world in order) {
            var document = worlds[world];

            foreach (var adjacency in Rows(node: document["adjacencies"])) {
                if (
                    (adjacency["destination"]?.GetValue<string>() is not { } destination) ||
                    !GeneratedName.TryStripHead(head: WorldCompositionLinks.LinkHead, name: destination, rest: out _)
                ) {
                    continue;
                }

                var other = LinkedWorld(document: document, reference: destination, world: world, worlds: worlds);
                var name = adjacency["name"]?.GetValue<string>();
                var counterpart = adjacency["counterpart"]?.GetValue<string>();

                if ((name is null) || (counterpart is null)) {
                    throw new WorldDecompileRefusedException(name: destination, pointer: $"/adjacencies", reason: "a border writes each adjacency it generates with a name and a counterpart");
                }
                if (found.Any(predicate: link => ((link.Kind == "border") && (link.RightWorld == world) && (link.RightEndpoint == name) && (link.LeftWorld == other) && (link.LeftEndpoint == counterpart)))) {
                    continue;
                }

                found.Add(item: ReadBorder(
                    adjacency: adjacency,
                    left: world,
                    leftEndpoint: name,
                    right: other,
                    rightEndpoint: counterpart,
                    worlds: worlds
                ));
            }
            foreach (var placement in Rows(node: document["placements"]?["rows"])) {
                if (
                    (placement["id"]?.GetValue<string>() is not { } id) ||
                    !GeneratedName.TryStripHead(head: WorldCompositionLinks.ReturnHead, name: id, rest: out var rest)
                ) {
                    continue;
                }

                found.Add(item: ReadDoor(
                    placement: placement,
                    rest: rest,
                    returnId: id,
                    right: world,
                    worlds: worlds
                ));
            }
        }

        return OrderLinks(
            links: found,
            order: order,
            worlds: worlds
        );
    }
    private static string LinkedWorld(JsonObject document, string reference, Dictionary<string, JsonObject> worlds, string world) {
        var row = Rows(node: document["references"]).FirstOrDefault(predicate: candidate => (candidate["name"]?.GetValue<string>() == reference));
        var named = row?["document"]?.GetValue<string>();

        if (
            (named is null) ||
            !worlds.ContainsKey(key: named) ||
            (named == world)
        ) {
            throw new WorldDecompileRefusedException(
                name: reference,
                pointer: "/references",
                reason: $"a border or door generates it as a reference to another world of the composition, and it names '{named}', which is not one of them"
            );
        }

        return named;
    }
    private static ReadLink ReadBorder(JsonObject adjacency, string left, string leftEndpoint, string right, string rightEndpoint, Dictionary<string, JsonObject> worlds) {
        var options = new JsonObject();
        var boundary = (adjacency["boundary"] as JsonObject);
        var opposite = Rows(node: worlds[right]["adjacencies"]).FirstOrDefault(predicate: candidate => (candidate["name"]?.GetValue<string>() == rightEndpoint));

        if ((boundary is null) || (opposite?["boundary"] is not JsonObject oppositeBoundary)) {
            throw new WorldDecompileRefusedException(
                name: (adjacency["destination"]?.GetValue<string>() ?? ""),
                pointer: "/adjacencies",
                reason: $"a border generates one adjacency with a boundary in each world it joins, and '{right}' carries none named '{rightEndpoint}'"
            );
        }

        // A written frame is the left side's; the right side's faces back through it (yaw + 180, pitch negated).
        // An automatic one is each side's own ground edge, which a ground-side endpoint rederives.
        var written = !IsGroundSide(endpoint: leftEndpoint);

        if (written) {
            var yaw = Number(node: boundary["outwardYawDegrees"]);
            var oppositeYaw = Number(node: oppositeBoundary["outwardYawDegrees"]);

            if (((yaw + 180d) != oppositeYaw) && ((oppositeYaw + 180d) == yaw)) {
                (left, right, leftEndpoint, rightEndpoint, boundary) = (right, left, rightEndpoint, leftEndpoint, oppositeBoundary);
            }

            options["center"] = boundary["center"]?.DeepClone();

            if (Number(node: boundary["outwardYawDegrees"]) != 0d) {
                options["yaw"] = boundary["outwardYawDegrees"]?.DeepClone();
            }
            if (Number(node: boundary["outwardPitchDegrees"]) != 0d) {
                options["pitch"] = boundary["outwardPitchDegrees"]?.DeepClone();
            }

            options["width"] = boundary["width"]?.DeepClone();
            options["height"] = boundary["height"]?.DeepClone();
        } else {
            options["width"] = boundary["width"]?.DeepClone();

            if (Number(node: boundary["height"]) != DefaultBorderHeight) {
                options["height"] = boundary["height"]?.DeepClone();
            }
        }

        if (Number(node: adjacency["hysteresis"]) != 0d) {
            options["hysteresis"] = adjacency["hysteresis"]?.DeepClone();
        }

        return new ReadLink(
            Kind: "border",
            LeftEndpoint: leftEndpoint,
            LeftWorld: left,
            Options: options,
            RightEndpoint: rightEndpoint,
            RightWorld: right
        );
    }
    private static bool IsGroundSide(string endpoint) => (QualifiedName.Parse(text: endpoint).Last is "north" or "east" or "south" or "west");
    // A door's return arch is `return$<left world>$<endpoint parts>` in the world it arrives in, standing on the
    // arrival spawn: the endpoint is its parts rejoined (`arch` or `arch/face`), the spawn the one it stands on.
    private static ReadLink ReadDoor(JsonObject placement, string rest, string returnId, string right, Dictionary<string, JsonObject> worlds) {
        var parts = rest.Split(separator: GeneratedName.Joiner);

        if ((parts.Length is < 2 or > 3) || !worlds.ContainsKey(key: parts[0]) || (parts[0] == right)) {
            throw new WorldDecompileRefusedException(
                name: returnId,
                pointer: "/placements/rows",
                reason: "a door generates its return arch as return$<world>$<arch>[$<face>], naming another world of the composition"
            );
        }

        var spawn = Rows(node: worlds[right]["spawnPoints"]).FirstOrDefault(predicate: candidate => (
            DocumentValueEqualityComparer.Instance.Equals(x: candidate["position"], y: placement["position"]) &&
            DocumentValueEqualityComparer.Instance.Equals(x: candidate["yawDegrees"], y: placement["yawDegrees"])
        ));

        if (spawn?["id"]?.GetValue<string>() is not { } arrival) {
            throw new WorldDecompileRefusedException(
                name: returnId,
                pointer: "/placements/rows",
                reason: $"a door stands its return arch on the spawn it arrives at, and no spawn point of '{right}' stands where this arch does"
            );
        }

        return new ReadLink(
            Kind: "door",
            LeftEndpoint: string.Join(separator: '/', values: parts[1..]),
            LeftWorld: parts[0],
            Options: [],
            RightEndpoint: arrival,
            RightWorld: right
        );
    }
    // The link-generated reference rows of every world, each owned by the link that generated it, give a partial
    // order; the links are laid out in it, breaking ties by where each was first read.
    private static List<ReadLink> OrderLinks(List<ReadLink> links, Dictionary<string, JsonObject> worlds, List<string> order) {
        var owner = new Dictionary<(string World, string Reference), int>();

        for (var index = 0; (index < links.Count); index++) {
            var link = links[index];

            owner[(link.LeftWorld, WorldCompositionLinks.LinkName(from: link.LeftEndpoint))] = index;
            owner[(link.RightWorld, ((link.Kind == "door")
                ? WorldCompositionLinks.LinkName(from: link.LeftEndpoint, world: link.LeftWorld)
                : WorldCompositionLinks.LinkName(from: link.RightEndpoint)))] = index;
        }

        var after = links.Select(selector: static _ => new HashSet<int>()).ToArray();
        var before = new int[links.Count];

        foreach (var world in order) {
            var previous = -1;

            foreach (var reference in Rows(node: worlds[world]["references"])) {
                if (
                    (reference["name"]?.GetValue<string>() is not { } name) ||
                    !owner.TryGetValue(key: (world, name), value: out var index)
                ) {
                    continue;
                }
                if ((previous >= 0) && (previous != index) && after[previous].Add(item: index)) {
                    before[index]++;
                }

                previous = index;
            }
        }

        var ordered = new List<ReadLink>(capacity: links.Count);
        var placed = new bool[links.Count];

        while (ordered.Count < links.Count) {
            var next = Enumerable.Range(start: 0, count: links.Count).FirstOrDefault(predicate: index => (!placed[index] && (before[index] == 0)), defaultValue: -1);

            if (next < 0) {
                throw new WorldDecompileRefusedException(
                    name: AnyLink,
                    pointer: "/references",
                    reason: "the worlds' link references stand in orders no one sequence of links writes"
                );
            }

            placed[next] = true;
            ordered.Add(item: links[next]);

            foreach (var successor in after[next]) {
                before[successor]--;
            }
        }

        return ordered;
    }
    // ---- stripping and verifying ----------------------------------------------------------------------------------

    // The world as its module writes it: every row a link generated removed, and the grounds its automatic borders
    // derive from restated as the compile-time descriptors the links read.
    private static JsonObject StripLinks(JsonObject document, string world, List<ReadLink> links, Dictionary<string, JsonObject> worlds) {
        var stripped = ((JsonObject)document.DeepClone());

        foreach (var section in new[] { "references", "destinations" }) {
            RemoveRows(
                document: stripped,
                keep: static row => !IsLinkName(name: row["name"]?.GetValue<string>()),
                section: section
            );
        }
        RemoveRows(
            document: stripped,
            keep: static row => !IsLinkName(name: row["destination"]?.GetValue<string>()),
            section: "adjacencies"
        );

        if (stripped["placements"] is JsonObject placements) {
            RemoveRows(
                document: placements,
                keep: static row => !GeneratedName.TryStripHead(head: WorldCompositionLinks.ReturnHead, name: (row["id"]?.GetValue<string>() ?? ""), rest: out _),
                section: "rows"
            );

            foreach (var placement in Rows(node: placements["rows"])) {
                foreach (var face in Rows(node: placement["faceSources"])) {
                    if (IsLinkName(name: face["portal"]?["destination"]?.GetValue<string>())) {
                        _ = face.Remove(propertyName: "portal");
                    }
                }
            }

            if (placements.Count == 0) {
                _ = stripped.Remove(propertyName: "placements");
            }
        }

        // A door copies its arch's prototype into the world it returns to unless that world already declares it, so
        // a copy nothing else in the world names is the door's.
        WorldDefinition? typed = null;

        foreach (var link in links.Where(predicate: link => ((link.Kind == "door") && (link.RightWorld == world)))) {
            var arch = link.LeftEndpoint.Split(separator: '/')[0];
            var prototypeId = Rows(node: worlds[link.LeftWorld]["placements"]?["rows"])
                .FirstOrDefault(predicate: row => (row["id"]?.GetValue<string>() == arch))?["prototypeId"]?.GetValue<string>();

            if (prototypeId is null) {
                continue;
            }
            if (
                (typed is null) &&
                !WorldJsonPayload.TryParse(
                deferDrawSites: true,
                error: out var error,
                info: WorldJsonContext.Default.WorldDefinition,
                json: stripped.ToJsonString(),
                value: out typed
            )
            ) {
                throw new WorldDecompileRefusedException(
                    name: world,
                    pointer: "",
                    reason: $"the world a door returns to does not read as a world document: {error}"
                );
            }
            if (WorldDefinitionRows.FindCreationReference(
                creationId: prototypeId,
                definition: typed!
            ) is null) {
                RemoveRows(
                    document: stripped,
                    keep: row => (row["id"]?.GetValue<string>() != prototypeId),
                    section: "prototypes"
                );
            }
        }

        return stripped;
    }
    private static bool IsLinkName(string? name) => ((name is not null) && GeneratedName.TryStripHead(head: WorldCompositionLinks.LinkHead, name: name, rest: out _));
    private static void RemoveRows(JsonObject document, string section, Func<JsonObject, bool> keep) {
        if (document[section] is not JsonArray rows) {
            return;
        }

        for (var index = (rows.Count - 1); (index >= 0); index--) {
            if ((rows[index] is JsonObject row) && !keep(arg: row)) {
                rows.RemoveAt(index: index);
            }
        }

        if (rows.Count == 0) {
            _ = document.Remove(propertyName: section);
        }
    }
    // Reapplies every link read back, in order, to the stripped worlds (with each world's grounds restated as the
    // descriptors a border reads) and requires every world to come back as it was; otherwise some row was not the
    // link's, or no link writes it the way the document does, and the composition does not print back.
    private static void VerifyLinks(List<ReadLink> links, Dictionary<string, JsonObject> originals, Dictionary<string, JsonObject> stripped, List<string> order) {
        var rebuilt = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var name in order) {
            var copy = ((JsonObject)stripped[name].DeepClone());
            var sugar = new GeneratedSugar();

            ReverseGrounds(
                root: ((JsonObject)copy.DeepClone()),
                sugar: sugar
            );
            if (sugar.Grounds.Count > 0) {
                copy["$composition"] = new JsonObject {
                    ["grounds"] = new JsonArray([.. sugar.Grounds.Select(selector: static ground => ((JsonNode)new JsonObject {
                        ["name"] = ground.Name,
                        ["center"] = ground.Center.DeepClone(),
                        ["size"] = ground.Size.DeepClone(),
                    }))]),
                };
            }

            rebuilt[name] = copy;
        }

        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(
            diagnostics: diagnostics,
            links: [.. links.Select(selector: static link => link.ToLink())],
            worlds: rebuilt
        );

        if (diagnostics.HasErrors) {
            throw new WorldDecompileRefusedException(
                name: AnyLink,
                pointer: "/",
                reason: $"the links read back do not apply: {diagnostics.First(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)).Message}"
            );
        }

        foreach (var name in order) {
            if (!DocumentValueEqualityComparer.Instance.Equals(x: originals[name], y: rebuilt[name])) {
                throw new WorldDecompileRefusedException(
                    name: name,
                    pointer: "/",
                    reason: $"the borders and doors read back out of world '{name}' do not regenerate it as it stands"
                );
            }
        }
    }
    private static IEnumerable<JsonObject> Rows(JsonNode? node) => ((node as JsonArray) ?? []).OfType<JsonObject>();
    private static double Number(JsonNode? node) => (DocumentLowering.TryReadNumber(node: node, number: out var number)
        ? number
        : double.NaN
    );
}
