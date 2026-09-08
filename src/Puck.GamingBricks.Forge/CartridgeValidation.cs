using Puck.Assets.Documents;

namespace Puck.GamingBricks.Forge;

internal sealed class CartridgeValidation(CartridgeDocument document) {
    private readonly List<DocumentValidationError> m_errors = [];
    private readonly HashSet<string> m_variables = new(comparer: StringComparer.Ordinal);

    public IReadOnlyList<DocumentValidationError> Run() {
        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: CartridgeDocument.SchemaId) is { } schemaError) {
            Error(path: "schema", message: schemaError);
            return m_errors;
        }
        if (document.Target is not ("cgb" or "agb")) {
            Error(path: "target", message: "Expected cgb or agb.");
        }

        if (!Ascii(value: document.Title, min: 1, max: 12)) {
            Error(path: "title", message: "Use 1 to 12 printable ASCII characters.");
        }

        if (!Ascii(value: document.GameCode, min: 4, max: 4)) {
            Error(path: "gameCode", message: "Use exactly four printable ASCII characters.");
        }

        var colors = document.Target == "agb" ? 16 : 4;
        if (document.Palette is null || document.Palette.Length != colors || document.Palette.Any(predicate: static color => color is < 0 or > 32767)) {
            Error(path: "palette", message: $"Expected {colors} RGB555 integers in 0..32767.");
        }
        if (!Count(items: document.Tiles, path: "tiles", min: 1, max: 256) ||
            !Count(items: document.Map, path: "map", min: 1024, max: 1024) ||
            !Count(items: document.Variables, path: "variables", min: 0, max: 64) ||
            !Count(items: document.Rules, path: "rules", min: 0, max: 64) ||
            !Count(items: document.Sprites, path: "sprites", min: 0, max: 40)) {
            return m_errors;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < document.Tiles.Length; ++index) {
            var tile = document.Tiles[index];
            var path = $"tiles[{index}]";
            if (tile is null) { Error(path: path, message: "A tile cannot be null."); continue; }
            Name(name: tile.Name, names: names, path: path);
            if (tile.Pixels is not { Length: 8 } || tile.Pixels.Any(predicate: row => row is not { Length: 8 } || row.Any(predicate: digit => Digit(digit: digit) is var value && (value < 0 || value >= colors)))) {
                Error(path: path + ".pixels", message: $"Use eight rows of eight hexadecimal palette indices below {colors}.");
            }
        }
        for (var index = 0; index < document.Map.Length; ++index) {
            if (document.Map[index] < 0 || document.Map[index] >= document.Tiles.Length) {
                Error(path: $"map[{index}]", message: "Tile index is outside the authored tile bank.");
            }
        }
        for (var index = 0; index < document.Variables.Length; ++index) {
            var variable = document.Variables[index];
            if (variable is null) { Error(path: $"variables[{index}]", message: "A variable cannot be null."); continue; }
            Name(name: variable.Name, names: m_variables, path: $"variables[{index}]");
            if (variable.Initial is < 0 or > 255) {
                Error(path: $"variables[{index}].initial", message: "Expected a byte in 0..255.");
            }
        }
        Rules();
        // Conservative SM83 machine-cycle ceiling, shared so a target switch preserves frame cadence.
        // Every rule is straight-line; counting all conditions/actions bounds even the all-matching frame.
        var frameCost = 1000 + document.Sprites.Length * 110;
        foreach (var rule in document.Rules) {
            if (rule?.When is { } conditions && rule.Actions is { } actions) {
                frameCost += conditions.Length * 40 + actions.Length * 24;
            }
        }
        if (frameCost > 12000) {
            Error(path: "rules", message: "Rules and sprites exceed the shared per-frame execution budget; reduce conditions, actions or sprites.");
        }
        names.Clear();
        for (var index = 0; index < document.Sprites.Length; ++index) {
            var sprite = document.Sprites[index];
            var path = $"sprites[{index}]";
            if (sprite is null) { Error(path: path, message: "A sprite cannot be null."); continue; }
            Name(name: sprite.Name, names: names, path: path);
            Value(value: sprite.Tile, path: path + ".tile");
            if (sprite.Tile?.Constant >= document.Tiles.Length) {
                Error(path: path + ".tile", message: "Tile is outside the authored bank.");
            }

            Value(value: sprite.X, path: path + ".x"); Value(value: sprite.Y, path: path + ".y"); Value(value: sprite.Visible, path: path + ".visible");
        }
        Value(value: document.ScrollX, path: "scrollX"); Value(value: document.ScrollY, path: "scrollY");
        return m_errors;
    }

    private void Rules() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < document.Rules.Length; ++index) {
            var rule = document.Rules[index];
            var path = $"rules[{index}]";
            if (rule is null) { Error(path: path, message: "A rule cannot be null."); continue; }
            Name(name: rule.Name, names: names, path: path);
            if (!Count(items: rule.When, path: path + ".when", min: 0, max: 8) || !Count(items: rule.Actions, path: path + ".actions", min: 1, max: 16)) {
                continue;
            }

            for (var condition = 0; condition < rule.When.Length; ++condition) {
                Condition(value: rule.When[condition], path: $"{path}.when[{condition}]");
            }

            for (var action = 0; action < rule.Actions.Length; ++action) {
                var item = rule.Actions[action];
                var actionPath = $"{path}.actions[{action}]";
                if (item is null) { Error(path: actionPath, message: "An action cannot be null."); continue; }
                if (!m_variables.Contains(item: item.Variable)) {
                    Error(path: actionPath + ".variable", message: "Unknown state variable.");
                }

                if (item.Operation is not ("set" or "add" or "subtract" or "and" or "or" or "xor")) {
                    Error(path: actionPath + ".operation", message: "Expected set, add, subtract, and, or or xor.");
                }

                Value(value: item.Value, path: actionPath + ".value");
            }
        }
    }

    private void Condition(CartridgeCondition? value, string path) {
        if (value is null) { Error(path: path, message: "A condition cannot be null."); return; }
        switch (value.Kind) {
            case "key":
                if (value.Key is not ("a" or "b" or "start" or "select" or "up" or "down" or "left" or "right")) {
                    Error(path: path + ".key", message: "Unknown joypad key.");
                }

                if (value.Mode is not ("held" or "pressed" or "released")) {
                    Error(path: path + ".mode", message: "Expected held, pressed or released.");
                }

                if (value.Left is not null || value.Right is not null || value.Comparison is not null) {
                    Error(path: path, message: "A key condition cannot carry comparison fields.");
                }

                break;
            case "compare":
                Value(value: value.Left, path: path + ".left"); Value(value: value.Right, path: path + ".right");
                if (value.Comparison is not ("eq" or "ne" or "lt" or "le" or "gt" or "ge")) {
                    Error(path: path + ".comparison", message: "Expected eq, ne, lt, le, gt or ge.");
                }

                if (value.Key is not null || value.Mode is not null) {
                    Error(path: path, message: "A comparison cannot carry key fields.");
                }

                break;
            default: Error(path: path + ".kind", message: "Expected key or compare."); break;
        }
    }

    private void Value(CartridgeValue? value, string path) {
        if (value is null || (value.Constant is null) == (value.Variable is null)) { Error(path: path, message: "Supply exactly one of constant or variable."); return; }
        if (value.Constant is < 0 or > 255) {
            Error(path: path + ".constant", message: "Expected a byte in 0..255.");
        }

        if (value.Variable is { } name && !m_variables.Contains(item: name)) {
            Error(path: path + ".variable", message: $"Unknown state variable '{name}'.");
        }
    }
    private bool Count<T>(T[]? items, string path, int min, int max) {
        if (items is not null && items.Length >= min && items.Length <= max) {
            return true;
        }

        Error(path: path, message: $"Expected an array with {min}..{max} entries.");
        return false;
    }
    private void Name(string? name, HashSet<string> names, string path) {
        if (string.IsNullOrEmpty(value: name) || name.Length > 64 || name.Any(predicate: static ch => !char.IsAsciiLetterOrDigit(c: ch) && ch is not ('_' or '-')) || !names.Add(item: name)) {
            Error(path: path + ".name", message: "Use a unique 1..64 character name containing ASCII letters, digits, underscore or hyphen.");
        }
    }
    private void Error(string path, string message) => m_errors.Add(item: new DocumentValidationError(Path: path, Message: message));
    private static bool Ascii(string? value, int min, int max) => value is not null && value.Length >= min && value.Length <= max && value.All(predicate: static ch => ch is >= ' ' and <= '~');
    internal static int Digit(char digit) => digit switch { >= '0' and <= '9' => digit - '0', >= 'A' and <= 'F' => digit - 'A' + 10, >= 'a' and <= 'f' => digit - 'a' + 10, _ => -1 };
}
