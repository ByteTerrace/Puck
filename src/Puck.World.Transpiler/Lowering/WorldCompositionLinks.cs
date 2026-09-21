using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

/// <summary>One source-level relationship between two worlds emitted by the same compilation.</summary>
public sealed record WorldCompositionLink(
    string Kind,
    string LeftWorld,
    string LeftEndpoint,
    string RightWorld,
    string RightEndpoint,
    JsonObject Options,
    SourceOrigin Origin
) {
    /// <summary>Creates a link for an in-memory source without file or module provenance.</summary>
    /// <param name="kind">The border or door construct.</param>
    /// <param name="leftWorld">The source world's declared name.</param>
    /// <param name="leftEndpoint">The source endpoint.</param>
    /// <param name="rightWorld">The destination world's declared name.</param>
    /// <param name="rightEndpoint">The destination endpoint.</param>
    /// <param name="options">The authored link options.</param>
    /// <param name="span">The link's source span.</param>
    public WorldCompositionLink(string kind, string leftWorld, string leftEndpoint, string rightWorld, string rightEndpoint, JsonObject options, SourceSpan span)
        : this(kind, leftWorld, leftEndpoint, rightWorld, rightEndpoint, options, new SourceOrigin(span, null, null)) { }

    /// <summary>Gets the source span used for link refusals and compilation-budget accounting.</summary>
    public SourceSpan Span => Origin.Span;
}
/// <summary>Lowers source-level borders and doors after every participating world has been emitted.</summary>
public static class WorldCompositionLinks {
    private const string DiagnosticCode = PuckDiagnosticCodes.CompositionRefused;

    /// <summary>Applies every reciprocal link to its two emitted documents.</summary>
    /// <param name="worlds">The emitted documents, keyed by their composition names.</param>
    /// <param name="links">The links to lower.</param>
    /// <param name="diagnostics">The destination for link refusals.</param>
    /// <param name="sourceMaps">The optional per-world generated-source maps.</param>
    /// <param name="budget">The compilation budget charged for copied document subtrees, or
    /// <see langword="null"/> for direct low-level callers outside compilation.</param>
    public static void Apply(IReadOnlyDictionary<string, JsonObject> worlds, IReadOnlyList<WorldCompositionLink> links, DiagnosticBag diagnostics, IReadOnlyDictionary<string, SourceMap>? sourceMaps = null, DocumentEvaluationBudget? budget = null) {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(diagnostics);

        foreach (var link in links) {
            if (!worlds.TryGetValue(key: link.LeftWorld, value: out var left) || !worlds.TryGetValue(key: link.RightWorld, value: out var right)) {
                Refuse(link, diagnostics, $"{link.Kind} names a world that this source does not emit ('{link.LeftWorld}', '{link.RightWorld}').");
                continue;
            }
            if (string.Equals(a: link.LeftWorld, b: link.RightWorld, comparisonType: StringComparison.Ordinal)) {
                Refuse(link, diagnostics, $"{link.Kind} must join two different worlds; both endpoints name '{link.LeftWorld}'.");
                continue;
            }
            var leftShapeValid = HasValidSectionShapes(reason: out var leftShapeReason, world: left);
            var rightShapeValid = HasValidSectionShapes(reason: out var rightShapeReason, world: right);

            if (!leftShapeValid || !rightShapeValid) {
                Refuse(diagnostics: diagnostics, link: link, message: $"world document sections are malformed: {leftShapeReason}{rightShapeReason}");
                continue;
            }

            var diagnosticCount = diagnostics.Count;
            var leftCounts = Counts(world: left);
            var rightCounts = Counts(world: right);

            switch (link.Kind) {
                case "border":
                    ApplyBorder(budget: budget, diagnostics: diagnostics, left: left, link: link, right: right);
                    break;
                case "door":
                    ApplyDoor(budget: budget, diagnostics: diagnostics, left: left, link: link, right: right);
                    break;
                default:
                    Refuse(link, diagnostics, $"unknown composition link kind '{link.Kind}'.");
                    break;
            }
            if (diagnostics.Count == diagnosticCount) {
                RegisterLinkOrigins(left, link.LeftWorld, link.Origin, sourceMaps, leftCounts, link.LeftEndpoint.Replace(newChar: '-', oldChar: '.'));
                RegisterLinkOrigins(right, link.RightWorld, link.Origin, sourceMaps, rightCounts, ((link.Kind == "door") ? link.LeftWorld : link.RightEndpoint.Replace(newChar: '-', oldChar: '.')));
            }
        }
        foreach (var world in worlds.Values) {
            world.Remove(propertyName: "$composition");
        }
    }

    private static void ApplyBorder(JsonObject left, JsonObject right, WorldCompositionLink link, DiagnosticBag diagnostics, DocumentEvaluationBudget? budget) {
        var admitted = new HashSet<string>(collection: ["center", "yaw", "pitch", "width", "height", "hysteresis"], comparer: StringComparer.Ordinal);

        if (link.Options.Any(predicate: property => !admitted.Contains(item: property.Key))) {
            Refuse(diagnostics: diagnostics, link: link, message: "border admits only center, yaw, pitch, width, height, and hysteresis.");
            return;
        }
        if (((link.Options["hysteresis"] is { } hysteresis) && !TryFiniteNumber(hysteresis, minimumExclusive: false, out _)) ||
            ((link.Options["height"] is { } height) && !TryFiniteNumber(height, minimumExclusive: true, out _)) ||
            ((link.Options["width"] is { } width) && !TryFiniteNumber(width, minimumExclusive: true, out _)) ||
            ((link.Options["yaw"] is { } yaw) && !TryFiniteNumber(yaw, minimumExclusive: false, out _, allowNegative: true)) ||
            ((link.Options["pitch"] is { } pitch) && !TryFiniteNumber(pitch, minimumExclusive: false, out _, allowNegative: true)) ||
            ((link.Options["center"] is { } center) && !FiniteVector3(node: center))) {
            Refuse(diagnostics: diagnostics, link: link, message: "border center/yaw/pitch must be finite, width and height positive finite distances, and hysteresis a non-negative finite distance.");
            return;
        }
        var hasExplicitFrameMember = ((link.Options["center"] is not null) || (link.Options["yaw"] is not null) || (link.Options["pitch"] is not null));

        if (hasExplicitFrameMember && ((link.Options["center"] is null) || (link.Options["width"] is null) || (link.Options["height"] is null))) {
            Refuse(diagnostics: diagnostics, link: link, message: "an explicit border frame requires center, width, and height; yaw and pitch cannot modify an automatic ground side.");
            return;
        }
        var leftValid = TryBoundary(link.LeftEndpoint, link.Options, left, false, link, budget, out var leftBoundary, out var leftReason);
        var rightValid = TryBoundary(link.RightEndpoint, link.Options, right, true, link, budget, out var rightBoundary, out var rightReason);

        if (!leftValid || !rightValid) {
            Refuse(diagnostics: diagnostics, link: link, message: $"border geometry cannot be derived: {leftReason}{rightReason}");
            return;
        }
        if ((Number(leftBoundary["width"], -1d) != Number(rightBoundary["width"], -2d)) ||
            (Number(leftBoundary["height"], -1d) != Number(rightBoundary["height"], -2d))) {
            Refuse(diagnostics: diagnostics, link: link, message: "border endpoints derive different rectangle dimensions; the two ground sides do not meet.");
            return;
        }
        if ((link.Options["center"] is null) && !SameVector(left: (leftBoundary["center"] as JsonArray), right: (rightBoundary["center"] as JsonArray))) {
            Refuse(diagnostics: diagnostics, link: link, message: "automatic border ground sides do not occupy the same edge in composition coordinates.");
            return;
        }
        if ((link.Options["center"] is null) && !AreOppositeCardinalSides(leftEndpoint: link.LeftEndpoint, rightEndpoint: link.RightEndpoint)) {
            Refuse(diagnostics: diagnostics, link: link, message: "automatic border ground sides must face one another (north/south or east/west).");
            return;
        }
        var leftReserved = TryReserveTopology(left, link.LeftEndpoint, link.RightWorld, out var leftDestination, out var leftTopologyReason);
        var rightReserved = TryReserveTopology(right, link.RightEndpoint, link.LeftWorld, out var rightDestination, out var rightTopologyReason);

        if (!leftReserved || !rightReserved) {
            Refuse(diagnostics: diagnostics, link: link, message: $"border topology collides with authored rows: {leftTopologyReason}{rightTopologyReason}");
            return;
        }

        Rows(name: "adjacencies", world: left).AppendNode(item: new JsonObject {
            ["name"] = link.LeftEndpoint,
            ["destination"] = leftDestination,
            ["counterpart"] = link.RightEndpoint,
            ["boundary"] = leftBoundary,
            ["unavailable"] = "Closed",
            ["hysteresis"] = (Copy(link.Options["hysteresis"], link, budget) ?? JsonValue.Create(0)),
        });
        Rows(name: "adjacencies", world: right).AppendNode(item: new JsonObject {
            ["name"] = link.RightEndpoint,
            ["destination"] = rightDestination,
            ["counterpart"] = link.LeftEndpoint,
            ["boundary"] = rightBoundary,
            ["unavailable"] = "Closed",
            ["hysteresis"] = (Copy(link.Options["hysteresis"], link, budget) ?? JsonValue.Create(0)),
        });
    }
    private static void ApplyDoor(JsonObject left, JsonObject right, WorldCompositionLink link, DiagnosticBag diagnostics, DocumentEvaluationBudget? budget) {
        if (link.Options.Count != 0) {
            Refuse(diagnostics: diagnostics, link: link, message: "door takes no property block; travel is body-scoped and arrival is the named spawn.");
            return;
        }
        var faceValid = TryFindFace(left, link.LeftEndpoint, out var leftFace, out var leftPlacement, out var faceReason);
        var spawnValid = TryFindSpawn(right, link.RightEndpoint, out var arrival, out var spawnReason);

        if (!faceValid || !spawnValid) {
            Refuse(diagnostics: diagnostics, link: link, message: $"door endpoint cannot be resolved: {faceReason}{spawnReason}");
            return;
        }
        if ((arrival["position"] is not JsonArray arrivalPosition) || !FiniteVector3(node: arrivalPosition) ||
            (arrival["yawDegrees"] is not { } arrivalYaw) || !TryFiniteNumber(arrivalYaw, minimumExclusive: false, out _, allowNegative: true)) {
            Refuse(link, diagnostics, $"spawn '{link.RightEndpoint}' must declare a finite position and yawDegrees.");
            return;
        }
        var returnId = $"return-{link.LeftWorld}-{link.LeftEndpoint}";

        if (FindNamed((right["placements"]?["rows"] as JsonArray), "id", returnId) is not null) {
            Refuse(link, diagnostics, $"generated return arch placement '{returnId}' already exists in world '{link.RightWorld}'.");
            return;
        }
        if (leftPlacement["parent"] is not null) {
            Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' has a parent; a generated return arch must have a self-contained world transform.");
            return;
        }
        if (leftFace["portal"] is not null) {
            Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' already declares a portal on the selected face.");
            return;
        }
        if ((leftPlacement["faceSources"] is not JsonArray authoredFaces) || authoredFaces.Any(predicate: static face => (face is not JsonObject))) {
            Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' has malformed face source metadata.");
            return;
        }
        foreach (var authoredFace in authoredFaces.OfType<JsonObject>()) {
            if (authoredFace["portal"] is not null) {
                Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' already declares a portal on another copied face; its destination is not part of the return arch.");
                return;
            }
            if ((authoredFace["source"] is not JsonObject source) || (StringValue(node: source["$type"]) is not { } sourceType)) {
                Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' has malformed face source metadata.");
                return;
            }
            if (sourceType is not ("none" or "testPattern" or "console" or "qr")) {
                Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' uses dependent face source '{sourceType}'; a generated return arch accepts only self-contained none, testPattern, console, or qr sources on every copied face.");
                return;
            }
        }
        var sourcePlacement = ((JsonObject)Copy(budget: budget, link: link, node: leftPlacement)!);
        var prototypeId = StringValue(node: sourcePlacement["prototypeId"]);
        var sourcePrototype = ((prototypeId is null) ? null : FindNamed((left["prototypes"] as JsonArray), "id", prototypeId));

        if (sourcePrototype is null) {
            Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' names no prototype to copy into the return world.");
            return;
        }
        var existingPrototype = FindNamed((right["prototypes"] as JsonArray), "id", prototypeId!);

        if ((existingPrototype is not null) && !JsonNode.DeepEquals(node1: existingPrototype, node2: sourcePrototype)) {
            Refuse(link, diagnostics, $"return world '{link.RightWorld}' already declares a different prototype '{prototypeId}'.");
            return;
        }
        sourcePlacement["id"] = returnId;
        sourcePlacement["position"] = (Copy(arrival["position"], link, budget) ?? new JsonArray(0, 0, 0));
        sourcePlacement["yawDegrees"] = (Copy(arrival["yawDegrees"], link, budget) ?? 0);
        var sourceFaces = ((sourcePlacement["faceSources"] as JsonArray) ?? []);

        sourcePlacement["faceSources"] = sourceFaces;
        var faceName = StringValue(node: leftFace["face"]);
        var sourcePlacementId = StringValue(node: leftPlacement["id"]);
        var returnFace = sourceFaces.OfType<JsonObject>().FirstOrDefault(predicate: candidate => string.Equals(a: StringValue(node: candidate["face"]), b: faceName, comparisonType: StringComparison.Ordinal));

        if ((faceName is null) || (sourcePlacementId is null) || (returnFace is null)) {
            Refuse(link, diagnostics, $"arch placement '{link.LeftEndpoint}' has malformed id or face metadata.");
            return;
        }
        var outwardReserved = TryReserveTopology(left, link.LeftEndpoint, link.RightWorld, out var outward, out var outwardReason);
        var returnReserved = TryReserveTopology(right, link.LeftWorld, link.LeftWorld, out var returning, out var returnReason);

        if (!outwardReserved || !returnReserved) {
            Refuse(diagnostics: diagnostics, link: link, message: $"door topology collides with authored rows: {outwardReason}{returnReason}");
            return;
        }
        if (existingPrototype is null) {
            Rows(name: "prototypes", world: right).AppendNode(item: Copy(budget: budget, link: link, node: sourcePrototype));
        }
        leftFace["portal"] = new JsonObject {
            ["destination"] = outward,
            ["travel"] = "body",
            ["arrival"] = "mapped",
            ["counterpart"] = $"{returnId}/{faceName}",
        };
        returnFace["portal"] = new JsonObject {
            ["destination"] = returning,
            ["travel"] = "body",
            ["arrival"] = "mapped",
            ["counterpart"] = $"{sourcePlacementId}/{faceName}",
        };
        Rows(name: "placements", world: right).AppendNode(item: sourcePlacement);
    }
    private static bool TryBoundary(string endpoint, JsonObject options, JsonObject world, bool reciprocal, WorldCompositionLink link, DocumentEvaluationBudget? budget, out JsonObject boundary, out string reason) {
        boundary = null!;
        reason = string.Empty;
        if ((options["center"] is JsonArray center) && (options["width"] is JsonValue width) && (options["height"] is JsonValue height)) {
            var explicitYaw = Number(options["yaw"], 0d);
            var pitch = Number(options["pitch"], 0d);

            boundary = Boundary(Copy(budget: budget, link: link, node: center)!, (reciprocal ? (explicitYaw + 180d) : explicitYaw), (reciprocal ? -pitch : pitch), Copy(budget: budget, link: link, node: width)!, Copy(budget: budget, link: link, node: height)!);
            return true;
        }

        var split = endpoint.LastIndexOf(value: '.');
        var groundName = ((split > 0) ? endpoint[..split] : string.Empty);
        var side = ((split > 0) ? endpoint[(split + 1)..] : endpoint);
        var grounds = (world["$composition"]?["grounds"] as JsonArray);
        var ground = ((groundName.Length == 0)
            ? ((grounds?.Count == 1) ? (grounds[0] as JsonObject) : null)
            : grounds?.OfType<JsonObject>().FirstOrDefault(predicate: candidate => string.Equals(a: StringValue(node: candidate["name"]), b: groundName, comparisonType: StringComparison.Ordinal))
        );

        if ((ground is null) || (ground["center"] is not JsonArray groundCenter) || !FiniteVector3(node: groundCenter) ||
            (ground["size"] is not JsonArray { Count: 2 } size) ||
            !TryFiniteNumber(size[0]!, minimumExclusive: true, out _) || !TryFiniteNumber(size[1]!, minimumExclusive: true, out _)) {
            reason = $" endpoint '{endpoint}' is not an explicit boundary and names no unique ground descriptor;";
            return false;
        }
        var x = Number(groundCenter[0], 0d);
        var y = Number(groundCenter[1], 0d);
        var z = Number(groundCenter[2], 0d);
        var sx = Number(size[0], 0d);
        var sz = Number(size[1], 0d);
        var heightValue = (Copy(options["height"], link, budget) ?? JsonValue.Create(16d)!);

        var (dx, dz, yaw, widthValue) = side switch {
            "north" => (0d, (-sz / 2d), 180d, sx),
            "east" => ((sx / 2d), 0d, 90d, sz),
            "south" => (0d, (sz / 2d), 0d, sx),
            "west" => ((-sx / 2d), 0d, -90d, sz),
            _ => (double.NaN, 0d, 0d, 0d),
        };
        if (double.IsNaN(d: dx)) {
            reason = $" endpoint '{endpoint}' must end in north, east, south, or west;";
            return false;
        }
        if (options["width"] is { } authoredWidth) {
            var requested = Number(fallback: double.NaN, node: authoredWidth);

            if (requested > widthValue) {
                reason = $" endpoint '{endpoint}' has only {widthValue} units of ground edge, less than the authored width {requested};";
                return false;
            }
            widthValue = requested;
        }
        boundary = Boundary(new JsonArray((x + dx), y, (z + dz)), yaw, 0, JsonValue.Create(widthValue)!, heightValue);
        return true;
    }
    private static JsonObject Boundary(JsonNode center, double yaw, double pitch, JsonNode width, JsonNode height) => new() {
        ["center"] = center,
        ["outwardYawDegrees"] = yaw,
        ["outwardPitchDegrees"] = pitch,
        ["width"] = width,
        ["height"] = height,
    };
    private static bool TryReserveTopology(JsonObject world, string preferredName, string otherWorld, out string destinationName, out string reason) {
        destinationName = preferredName.Replace(newChar: '-', oldChar: '.');
        reason = string.Empty;
        if ((FindNamed(Rows(name: "references", world: world), "name", destinationName) is not null) || (FindNamed(Rows(name: "destinations", world: world), "name", destinationName) is not null)) {
            reason = $" generated name '{destinationName}' is already declared;";
            return false;
        }
        Rows(name: "references", world: world).AppendNode(item: new JsonObject { ["name"] = destinationName, ["document"] = $"{otherWorld}.world.json" });
        Rows(name: "destinations", world: world).AppendNode(item: new JsonObject { ["name"] = destinationName, ["reference"] = destinationName, ["durability"] = "persisted", ["scope"] = "global" });
        return true;
    }
    private static bool TryFindFace(JsonObject world, string endpoint, out JsonObject face, out JsonObject placement, out string reason) {
        var slash = endpoint.IndexOf(value: '/');
        var placementName = ((slash > 0) ? endpoint[..slash] : endpoint);
        var faceName = ((slash > 0) ? endpoint[(slash + 1)..] : "portal");

        placement = FindNamed((world["placements"]?["rows"] as JsonArray), "id", placementName)!;
        face = ((placement?["faceSources"] is JsonArray faces) ? FindNamed(member: "face", name: faceName, rows: faces)! : null!);
        reason = ((face is null) ? $" '{endpoint}' names no placement face;" : string.Empty);
        return (face is not null);
    }
    private static bool TryFindSpawn(JsonObject world, string endpoint, out JsonObject spawn, out string reason) {
        spawn = FindNamed((world["spawnPoints"] as JsonArray), "id", endpoint)!;
        reason = ((spawn is null) ? $" '{endpoint}' names no spawn point;" : string.Empty);
        return (spawn is not null);
    }
    private static JsonArray Rows(JsonObject world, string name) {
        if (name == "placements") {
            var section = (world[name] as JsonObject);

            if (section?["rows"] is JsonArray placementRows) {
                return placementRows;
            }
            section ??= [];
            placementRows = [];
            section["rows"] = placementRows;
            world[name] = section;
            return placementRows;
        }
        if (world[name] is JsonArray rows) {
            return rows;
        }
        rows = [];
        world[name] = rows;
        return rows;
    }
    private static JsonObject? FindNamed(JsonArray? rows, string member, string name) => rows?.OfType<JsonObject>().FirstOrDefault(predicate: row => string.Equals(a: StringValue(node: row[member]), b: name, comparisonType: StringComparison.Ordinal));
    private static JsonNode? Copy(JsonNode? node, WorldCompositionLink link, DocumentEvaluationBudget? budget) => ((budget is null)
        ? node?.DeepClone()
        : budget.Copy(value: node, span: link.Span)
    );
    private static string? StringValue(JsonNode? node) => (((node is JsonValue value) && value.TryGetValue<string>(value: out var text)) ? text : null);
    private static bool HasValidSectionShapes(JsonObject world, out string reason) {
        foreach (var sectionName in new[] { "references", "destinations", "adjacencies", "prototypes", "spawnPoints" }) {
            if (world[sectionName] is not null and not JsonArray) {
                reason = $" section '{sectionName}' must be an array;";
                return false;
            }
        }
        if (world["placements"] is not null and not JsonObject) {
            reason = " section 'placements' must be an object;";
            return false;
        }
        if ((world["placements"] is JsonObject placements) && (placements["rows"] is not null and not JsonArray)) {
            reason = " section 'placements.rows' must be an array;";
            return false;
        }
        if (world["$composition"] is not null and not JsonObject) {
            reason = " section '$composition' must be an object;";
            return false;
        }
        if ((world["$composition"] is JsonObject composition) && (composition["grounds"] is not null and not JsonArray)) {
            reason = " section '$composition.grounds' must be an array;";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private readonly record struct LinkCounts(int References, int Destinations, int Adjacencies, int Prototypes, int Placements);

    private static LinkCounts Counts(JsonObject world) => new(
        References: ((world["references"] as JsonArray)?.Count ?? 0),
        Destinations: ((world["destinations"] as JsonArray)?.Count ?? 0),
        Adjacencies: ((world["adjacencies"] as JsonArray)?.Count ?? 0),
        Prototypes: ((world["prototypes"] as JsonArray)?.Count ?? 0),
        Placements: ((world["placements"]?["rows"] as JsonArray)?.Count ?? 0)
    );
    private static void RegisterLinkOrigins(JsonObject world, string worldName, SourceOrigin origin, IReadOnlyDictionary<string, SourceMap>? sourceMaps, LinkCounts before, string portalDestination) {
        if ((sourceMaps is null) || !sourceMaps.TryGetValue(key: worldName, value: out var map)) {
            return;
        }
        foreach (var (sectionName, start) in new[] {
            ("references", before.References), ("destinations", before.Destinations),
            ("adjacencies", before.Adjacencies), ("prototypes", before.Prototypes),
        }) {
            if (world[sectionName] is not JsonArray rows) {
                continue;
            }
            for (var index = start; (index < rows.Count); index++) {
                map.Register(jsonPointer: $"/{sectionName}/{index}", origin: origin);
            }
        }
        if (world["placements"]?["rows"] is JsonArray placements) {
            for (var placementIndex = 0; (placementIndex < placements.Count); placementIndex++) {
                if (placementIndex >= before.Placements) {
                    map.Register(jsonPointer: $"/placements/rows/{placementIndex}", origin: origin);
                }
                if (placements[placementIndex]?["faceSources"] is not JsonArray faces) {
                    continue;
                }
                for (var faceIndex = 0; (faceIndex < faces.Count); faceIndex++) {
                    if ((faces[faceIndex] is JsonObject face) && (face["portal"] is JsonObject portal) && string.Equals(a: StringValue(node: portal["destination"]), b: portalDestination, comparisonType: StringComparison.Ordinal)) {
                        map.Register(jsonPointer: $"/placements/rows/{placementIndex}/faceSources/{faceIndex}/portal", origin: origin);
                    }
                }
            }
        }
    }
    private static double Number(JsonNode? node, double fallback) {
        if (node is not JsonValue value) {
            return fallback;
        }
        if (value.TryGetValue<double>(value: out var number)) {
            return number;
        }
        if (value.TryGetValue<decimal>(value: out var exact)) {
            return ((double)exact);
        }
        if (value.TryGetValue<int>(value: out var integer)) {
            return integer;
        }
        if (value.TryGetValue<long>(value: out var wide)) {
            return wide;
        }
        return fallback;
    }
    private static bool TryFiniteNumber(JsonNode node, bool minimumExclusive, out double number, bool allowNegative = false) {
        number = Number(fallback: double.NaN, node: node);
        return (double.IsFinite(d: number) && (allowNegative || (minimumExclusive ? (number > 0d) : (number >= 0d))));
    }
    private static bool FiniteVector3(JsonNode node) => ((node is JsonArray { Count: 3 } vector) && vector.All(predicate: item => ((item is not null) && TryFiniteNumber(item, minimumExclusive: false, out _, allowNegative: true))));
    private static bool SameVector(JsonArray? left, JsonArray? right) {
        if ((left is not { Count: 3 }) || (right is not { Count: 3 })) {
            return false;
        }
        for (var index = 0; (index < 3); index++) {
            if (Number(left[index], double.NaN) != Number(right[index], double.NaN)) {
                return false;
            }
        }
        return true;
    }
    private static bool AreOppositeCardinalSides(string leftEndpoint, string rightEndpoint) {
        var left = leftEndpoint[(leftEndpoint.LastIndexOf(value: '.') + 1)..];
        var right = rightEndpoint[(rightEndpoint.LastIndexOf(value: '.') + 1)..];

        return ((left, right) is ("north", "south") or ("south", "north") or ("east", "west") or ("west", "east"));
    }
    private static void Refuse(WorldCompositionLink link, DiagnosticBag diagnostics, string message) => diagnostics.ReportError(code: DiagnosticCode, message: $"{link.Kind} {link.LeftWorld}.{link.LeftEndpoint}, {link.RightWorld}.{link.RightEndpoint}: {message}", span: link.Span);
}
