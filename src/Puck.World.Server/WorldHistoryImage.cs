using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One active body's authoritative pose and rigid residue — the lanes the pose hash folds, held as values.</summary>
/// <param name="Position">The fixed-point position.</param>
/// <param name="Orientation">The fixed-point attitude.</param>
/// <param name="Yaw">The heading scalar, in radians.</param>
/// <param name="Velocity">The rigid linear velocity.</param>
/// <param name="AngularVelocity">The rigid angular velocity.</param>
/// <param name="Resting">Whether the rigid body rests.</param>
/// <param name="Carrying">The carried body's index, or <see langword="null"/>.</param>
/// <param name="CarriedBy">The carrier's index, or <see langword="null"/>.</param>
/// <param name="Scale">The body's scale.</param>
public readonly record struct WorldHistoryBodyImage(FixedVector3 Position, FixedQuaternion Orientation, FixedQ4816 Yaw, FixedVector3 Velocity, FixedVector3 AngularVelocity, bool Resting, int? Carrying, int? CarriedBy, FixedQ4816 Scale);
/// <summary>One stored cell's resolved value.</summary>
/// <param name="Kind">The row's cell kind, which decides how <paramref name="Raw"/> reads.</param>
/// <param name="Raw">The raw resolved value: Q48.16 bits for a fixed row, the integer for an int row, 0 or 1 for a
/// bool row; <see langword="null"/> when the cell resolves to none.</param>
/// <param name="Text">The resolved text, or <see langword="null"/>.</param>
public readonly record struct WorldHistoryCellImage(CellKind Kind, long? Raw, string? Text) {
    /// <summary>Returns the value as a builder reads it.</summary>
    /// <returns>The formatted value.</returns>
    public override string ToString() {
        if (Text is { } text) {
            return $"\"{text}\"";
        }

        if (Raw is not { } raw) {
            return "none";
        }

        return (Kind switch {
            CellKind.Fixed => FixedQ4816.FromRawBits(value: raw).ToString(),
            CellKind.Bool => ((raw != 0L)
                ? "true"
                : "false"),
            _ => raw.ToString(provider: CultureInfo.InvariantCulture),
        });
    }
}
/// <summary>A structural image of a server's authoritative state at one tick: the digest of every authoritative
/// hash component, every active body's pose lanes, every stored cell's resolved value, and every field cell. Two
/// images diff into the entities, rows, and components that changed.</summary>
/// <param name="Tick">The tick the image describes.</param>
/// <param name="Components">The digest of each authoritative component, folded alone, by component.</param>
/// <param name="Bodies">Every active body's lanes, by body index.</param>
/// <param name="Cells">Every stored cell's resolved value, by row then key.</param>
/// <param name="Fields">Every field lattice's raw cells, by field name.</param>
public sealed record WorldHistoryImage(
    ulong Tick,
    IReadOnlyDictionary<WorldStateHashComponent, ulong> Components,
    IReadOnlyDictionary<int, WorldHistoryBodyImage> Bodies,
    IReadOnlyDictionary<(string Row, string Key), WorldHistoryCellImage> Cells,
    IReadOnlyDictionary<string, long[]> Fields
) {
    /// <summary>Captures the image of a server's state at its last completed tick.</summary>
    /// <param name="server">The server to read.</param>
    /// <returns>The image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static WorldHistoryImage Capture(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var tick = (server.NextInputTick - 1UL);
        var components = new Dictionary<WorldStateHashComponent, ulong>();

        foreach (var component in WorldStateHashComposition.Authoritative) {
            if (component is WorldStateHashComponent.Seed or WorldStateHashComponent.Tick) {
                continue;
            }

            components[component] = WorldStateHashComposition.Compose(
                order: [component],
                seed: 0UL,
                server: server,
                tick: tick
            );
        }

        var bodies = new SortedDictionary<int, WorldHistoryBodyImage>();
        var population = server.Population;

        for (var index = 0; (index < population.Capacity); index++) {
            if (
                !population.IsActive(index: index) ||
                (population.EntryBody(index: index) is not { } body)
            ) {
                continue;
            }

            bodies[index] = new WorldHistoryBodyImage(
                AngularVelocity: body.RigidAngularVelocity,
                CarriedBy: body.CarriedBy,
                Carrying: body.Carrying,
                Orientation: body.FixedOrientation,
                Position: body.FixedPosition,
                Resting: body.Resting,
                Scale: body.Scale,
                Velocity: body.RigidVelocity,
                Yaw: body.FixedYaw
            );
        }

        var cells = new SortedDictionary<(string Row, string Key), WorldHistoryCellImage>();

        WorldStateExport.VisitResolvedCells(
            definition: server.Definition,
            engineTick: server.CompletedEngineTicks,
            tick: tick,
            visit: (row, key, raw, text) => cells[(row.Name.Value, key)] = new WorldHistoryCellImage(
                Kind: row.Kind,
                Raw: raw,
                Text: text
            ),
            visitRow: static _ => { }
        );

        var fields = new SortedDictionary<string, long[]>(comparer: StringComparer.Ordinal);

        if (population.Fields is { } lattice) {
            var captured = lattice.Capture();

            for (var field = 0; (field < lattice.FieldCount); field++) {
                fields[lattice.Input.Fields[field].Name] = [.. captured.Raw[field]];
            }
        }

        return new WorldHistoryImage(
            Bodies: bodies,
            Cells: cells,
            Components: components,
            Fields: fields,
            Tick: tick
        );
    }
}
/// <summary>One body's change between two images.</summary>
/// <param name="Index">The body's index.</param>
/// <param name="From">The body's lanes before, or <see langword="null"/> when it was not active.</param>
/// <param name="To">The body's lanes after, or <see langword="null"/> when it is no longer active.</param>
/// <param name="Lanes">The lanes that changed, by name, in declaration order.</param>
public sealed record WorldHistoryBodyChange(int Index, WorldHistoryBodyImage? From, WorldHistoryBodyImage? To, IReadOnlyList<string> Lanes);
/// <summary>One stored cell's change between two images.</summary>
/// <param name="Row">The row's name.</param>
/// <param name="Key">The cell's key.</param>
/// <param name="From">The value before, or <see langword="null"/> when the cell was not stored.</param>
/// <param name="To">The value after, or <see langword="null"/> when the cell is no longer stored.</param>
public sealed record WorldHistoryCellChange(string Row, string Key, WorldHistoryCellImage? From, WorldHistoryCellImage? To);
/// <summary>One field lattice's changed cells between two images.</summary>
/// <param name="Field">The field's name.</param>
/// <param name="Cells">Each changed cell's index with its raw Q48.16 value before and after, in cell order.</param>
public sealed record WorldHistoryFieldChange(string Field, IReadOnlyList<(int Cell, long From, long To)> Cells);
/// <summary>What changed between two images: the authoritative components whose digests moved, and inside the
/// value lanes, exactly the bodies, cells, and field cells that differ, with their values. A component with no value
/// walk (rule latches, decisions, navigation, flock perception, search) reports only its digest moving.</summary>
/// <param name="From">The earlier image's tick.</param>
/// <param name="To">The later image's tick.</param>
/// <param name="Components">The components whose digests differ, in fold order.</param>
/// <param name="Bodies">The bodies that changed, by index.</param>
/// <param name="Cells">The cells that changed, by row then key.</param>
/// <param name="Fields">The fields with changed cells, by name.</param>
public sealed record WorldHistoryDiff(ulong From, ulong To, IReadOnlyList<WorldStateHashComponent> Components, IReadOnlyList<WorldHistoryBodyChange> Bodies, IReadOnlyList<WorldHistoryCellChange> Cells, IReadOnlyList<WorldHistoryFieldChange> Fields) {
    /// <summary>Gets whether nothing differs.</summary>
    public bool Empty => ((((Components.Count == 0) && (Bodies.Count == 0)) && (Cells.Count == 0)) && (Fields.Count == 0));

    private static string Describe(FixedVector3 value) => $"({value.X}, {value.Y}, {value.Z})";
    private static string Describe(FixedQuaternion value) => $"({value.X}, {value.Y}, {value.Z}, {value.W})";
    private static string Describe(int? value) => ((value is { } index)
        ? $"body:{index}"
        : "none");
    private static string DescribeLane(string lane, WorldHistoryBodyImage? image) {
        if (image is not { } body) {
            return "inactive";
        }

        return (lane switch {
            "position" => Describe(value: body.Position),
            "orientation" => Describe(value: body.Orientation),
            "yaw" => body.Yaw.ToString(),
            "velocity" => Describe(value: body.Velocity),
            "angularVelocity" => Describe(value: body.AngularVelocity),
            "resting" => (body.Resting
                ? "true"
                : "false"),
            "carrying" => Describe(value: body.Carrying),
            "carriedBy" => Describe(value: body.CarriedBy),
            _ => body.Scale.ToString(),
        });
    }
    private static IReadOnlyList<string> LanesBetween(WorldHistoryBodyImage? from, WorldHistoryBodyImage? to) {
        if (
            (from is not { } a) ||
            (to is not { } b)
        ) {
            return ["active"];
        }

        var lanes = new List<string>();

        if (a.Position != b.Position) { lanes.Add(item: "position"); }
        if (a.Orientation != b.Orientation) { lanes.Add(item: "orientation"); }
        if (a.Yaw != b.Yaw) { lanes.Add(item: "yaw"); }
        if (a.Velocity != b.Velocity) { lanes.Add(item: "velocity"); }
        if (a.AngularVelocity != b.AngularVelocity) { lanes.Add(item: "angularVelocity"); }
        if (a.Resting != b.Resting) { lanes.Add(item: "resting"); }
        if (a.Carrying != b.Carrying) { lanes.Add(item: "carrying"); }
        if (a.CarriedBy != b.CarriedBy) { lanes.Add(item: "carriedBy"); }
        if (a.Scale != b.Scale) { lanes.Add(item: "scale"); }

        return lanes;
    }

    /// <summary>Diffs two images.</summary>
    /// <param name="from">The earlier image.</param>
    /// <param name="to">The later image.</param>
    /// <returns>The diff.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldHistoryDiff Between(WorldHistoryImage from, WorldHistoryImage to) {
        ArgumentNullException.ThrowIfNull(argument: from);
        ArgumentNullException.ThrowIfNull(argument: to);

        var components = new List<WorldStateHashComponent>();

        foreach (var component in WorldStateHashComposition.Authoritative) {
            if (
                from.Components.TryGetValue(key: component, value: out var before) &&
                to.Components.TryGetValue(key: component, value: out var after) &&
                (before != after)
            ) {
                components.Add(item: component);
            }
        }

        var bodies = new List<WorldHistoryBodyChange>();

        foreach (var index in from.Bodies.Keys.Union(second: to.Bodies.Keys).Order()) {
            WorldHistoryBodyImage? before = (from.Bodies.TryGetValue(key: index, value: out var a) ? a : null);
            WorldHistoryBodyImage? after = (to.Bodies.TryGetValue(key: index, value: out var b) ? b : null);

            if (before == after) {
                continue;
            }

            bodies.Add(item: new WorldHistoryBodyChange(
                From: before,
                Index: index,
                Lanes: LanesBetween(from: before, to: after),
                To: after
            ));
        }

        var cells = new List<WorldHistoryCellChange>();

        foreach (var key in from.Cells.Keys.Union(second: to.Cells.Keys).Order()) {
            WorldHistoryCellImage? before = (from.Cells.TryGetValue(key: key, value: out var a) ? a : null);
            WorldHistoryCellImage? after = (to.Cells.TryGetValue(key: key, value: out var b) ? b : null);

            if (before == after) {
                continue;
            }

            cells.Add(item: new WorldHistoryCellChange(
                From: before,
                Key: key.Key,
                Row: key.Row,
                To: after
            ));
        }

        var fields = new List<WorldHistoryFieldChange>();

        foreach (var name in from.Fields.Keys.Union(second: to.Fields.Keys).Order(comparer: StringComparer.Ordinal)) {
            var before = (from.Fields.TryGetValue(key: name, value: out var a) ? a : []);
            var after = (to.Fields.TryGetValue(key: name, value: out var b) ? b : []);
            var changed = new List<(int Cell, long From, long To)>();

            for (var cell = 0; (cell < Math.Max(val1: before.Length, val2: after.Length)); cell++) {
                var x = ((cell < before.Length) ? before[cell] : 0L);
                var y = ((cell < after.Length) ? after[cell] : 0L);

                if (x != y) {
                    changed.Add(item: (cell, x, y));
                }
            }

            if (changed.Count > 0) {
                fields.Add(item: new WorldHistoryFieldChange(
                    Cells: changed,
                    Field: name
                ));
            }
        }

        return new WorldHistoryDiff(
            Bodies: bodies,
            Cells: cells,
            Components: components,
            Fields: fields,
            From: from.Tick,
            To: to.Tick
        );
    }
    /// <summary>Returns the diff as compact console lines: one summary line, then one line per changed body, cell,
    /// and field, up to <paramref name="maxLines"/> detail lines with a closing count of the rest.</summary>
    /// <param name="maxLines">The most detail lines to print.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<string> DescribeLines(int maxLines) {
        var detail = new List<string>();

        foreach (var body in Bodies) {
            var lanes = string.Join(
                separator: "; ",
                values: body.Lanes.Select(selector: lane => ((lane == "active")
                    ? $"{((body.From is null) ? "inactive -> active" : "active -> inactive")}"
                    : $"{lane} {DescribeLane(image: body.From, lane: lane)} -> {DescribeLane(image: body.To, lane: lane)}"))
            );

            detail.Add(item: $"body:{body.Index} {lanes}");
        }

        foreach (var cell in Cells) {
            detail.Add(item: $"cell {cell.Row}/{cell.Key} {((cell.From is { } a) ? a.ToString() : "absent")} -> {((cell.To is { } b) ? b.ToString() : "absent")}");
        }

        foreach (var field in Fields) {
            var (first, before, after) = field.Cells[0];

            detail.Add(item: $"field {field.Field}: {field.Cells.Count} cell(s) changed (first #{first} {FixedQ4816.FromRawBits(value: before)} -> {FixedQ4816.FromRawBits(value: after)})");
        }

        var lines = new List<string>(capacity: (Math.Min(val1: detail.Count, val2: maxLines) + 2)) {
            $"{From} -> {To}: {Bodies.Count} bod{((Bodies.Count == 1) ? "y" : "ies")}, {Cells.Count} cell(s), {Fields.Count} field(s) changed | components: {((Components.Count == 0) ? "none" : string.Join(separator: ", ", values: Components))}",
        };

        lines.AddRange(collection: detail.Take(count: maxLines));

        if (detail.Count > maxLines) {
            lines.Add(item: $"… {(detail.Count - maxLines)} more (--json prints every change)");
        }

        return lines;
    }
    /// <summary>Returns the diff as canonical UTF-8 JSON with exact raw values (no BOM, LF newlines, two-space
    /// indentation, one trailing newline) — the machine form for tools.</summary>
    /// <returns>The canonical bytes.</returns>
    public byte[] ToCanonicalJson() {
        static JsonNode? Vector(FixedVector3 value) => new JsonArray(value.X.Value, value.Y.Value, value.Z.Value);
        static JsonNode? Body(WorldHistoryBodyImage? image) => ((image is { } body)
            ? new JsonObject {
                ["position"] = Vector(value: body.Position),
                ["orientation"] = new JsonArray(body.Orientation.X.Value, body.Orientation.Y.Value, body.Orientation.Z.Value, body.Orientation.W.Value),
                ["yaw"] = body.Yaw.Value,
                ["velocity"] = Vector(value: body.Velocity),
                ["angularVelocity"] = Vector(value: body.AngularVelocity),
                ["resting"] = body.Resting,
                ["carrying"] = body.Carrying,
                ["carriedBy"] = body.CarriedBy,
                ["scale"] = body.Scale.Value,
            }
            : null);
        static JsonNode? Cell(WorldHistoryCellImage? image) => ((image is { } cell)
            ? new JsonObject {
                ["kind"] = cell.Kind.ToString(),
                ["raw"] = cell.Raw,
                ["text"] = cell.Text,
            }
            : null);

        var document = new JsonObject {
            ["schema"] = "puck.world.history-diff.v1",
            ["from"] = From,
            ["to"] = To,
            ["components"] = new JsonArray([.. Components.Select(selector: static component => JsonValue.Create(value: component.ToString()))]),
            ["bodies"] = new JsonArray([.. Bodies.Select(selector: static body => new JsonObject {
                ["index"] = body.Index,
                ["lanes"] = new JsonArray([.. body.Lanes.Select(selector: static lane => JsonValue.Create(value: lane))]),
                ["from"] = Body(image: body.From),
                ["to"] = Body(image: body.To),
            })]),
            ["cells"] = new JsonArray([.. Cells.Select(selector: static cell => new JsonObject {
                ["row"] = cell.Row,
                ["key"] = cell.Key,
                ["from"] = Cell(image: cell.From),
                ["to"] = Cell(image: cell.To),
            })]),
            ["fields"] = new JsonArray([.. Fields.Select(selector: static field => new JsonObject {
                ["field"] = field.Field,
                ["cells"] = new JsonArray([.. field.Cells.Select(selector: static cell => new JsonArray(cell.Cell, cell.From, cell.To))]),
            })]),
        };

        return CanonicalJsonDocument.Serialize(node: document);
    }
}
