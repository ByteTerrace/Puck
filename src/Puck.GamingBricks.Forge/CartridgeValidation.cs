using System.Diagnostics.CodeAnalysis;
using Puck.Assets.Documents;

namespace Puck.GamingBricks.Forge;

internal sealed class CartridgeValidation(CartridgeDocument document) {
    private readonly List<DocumentValidationError> m_errors = [];
    private readonly Dictionary<string, int> m_arrays = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, (int Width, int Height)> m_screens = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_variables = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_recordings = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_sounds = new(comparer: StringComparer.Ordinal);
    private bool m_clocks;
    private bool m_saves;

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
        var paletteCount = document.Target == "agb" ? CartridgeLimits.AdvancedPaletteCount : CartridgeLimits.HumblePaletteCount;
        Palettes(colors: colors, limit: paletteCount);
        if (!Count(items: document.Tiles, path: "tiles", min: 1, max: CartridgeLimits.TileCount) ||
            !Count(items: document.Map, path: "map", min: 1024, max: 1024) ||
            !Count(items: document.Variables, path: "variables", min: 0, max: CartridgeLimits.VariableCount) ||
            !Count(items: document.Arrays, path: "arrays", min: 0, max: CartridgeLimits.ArrayCount) ||
            !Count(items: document.Screens, path: "screens", min: 0, max: CartridgeLimits.ScreenCount) ||
            !Count(items: document.Sounds, path: "sounds", min: 0, max: CartridgeLimits.SoundCount) ||
            !Count(items: document.Raster, path: "raster", min: 0, max: CartridgeLimits.RasterRowCount) ||
            !Count(items: document.Layers, path: "layers", min: 0, max: CartridgeLimits.LayerCount) ||
            !Count(items: document.Rules, path: "rules", min: 0, max: CartridgeLimits.RuleCount) ||
            !Count(items: document.Sprites, path: "sprites", min: 0, max: CartridgeLimits.SpriteCount)) {
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
        if (document.MapPalettes is { } cells) {
            var background = document.Palettes?.Background?.Length ?? 0;
            if (cells.Length != 1024) {
                Error(path: "mapPalettes", message: "Expected 1024 entries, one per map cell.");
            } else if (cells.Any(predicate: entry => entry < 0 || entry >= background)) {
                Error(path: "mapPalettes", message: "A cell names a background palette that is not declared.");
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

        Arrays();
        Raster();
        Bitmap();
        Layers();
        Affine();
        Clock();
        Window();
        Screens();
        Sounds();
        Save();
        Rules();
        if (m_errors.Count == 0) {
            var writes = 0;
            foreach (var rule in document.Rules) {
                writes += MapWrites(statements: rule.Body);
            }

            if (writes > CartridgeLimits.MapWriteCount) {
                Error(path: "rules", message: $"A frame can execute {writes} map writes against a queue of {CartridgeLimits.MapWriteCount}; gate the redraws behind branches or spread them across frames.");
            }
        }

        // Only cost a document whose shape already checked out; a malformed step has no meaningful weight.
        if (m_errors.Count == 0) {
            var frame = CartridgeCost.Frame(document: document);
            if (frame.IsUnmodeled) {
                Error(path: "rules", message: $"{frame.Reason} A document cannot be admitted against an unmeasured primitive.");
            } else if (!CartridgeCost.Admits(bound: frame)) {
                Error(path: "rules", message: $"Rules and sprites need {frame.Cycles} work units against a per-frame reservation of {CartridgeCost.FrameBudget}; reduce iterations, conditions, steps, indexed accesses or sprites.");
            }
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
            foreach (var (flag, name) in new[] { (sprite.FlipX, "flipX"), (sprite.FlipY, "flipY"), (sprite.BehindBackground, "behindBackground") }) {
                if (flag is not null) {
                    Value(value: flag, path: $"{path}.{name}");
                }
            }

            if (sprite.Turn is { } turn) {
                if (document.Target != "agb") {
                    Error(path: path + ".turn", message: "Turning an object needs the advanced machine; the cgb target can only mirror one.");
                } else {
                    Value(value: turn, path: path + ".turn");
                }
            }

            if (sprite.Palette is { } slot) {
                Value(value: slot, path: path + ".palette");
                if (slot.Constant >= (document.Palettes?.Object?.Length ?? 0)) {
                    Error(path: path + ".palette", message: "Sprite names an object palette that is not declared.");
                }
            }
        }
        Value(value: document.ScrollX, path: "scrollX"); Value(value: document.ScrollY, path: "scrollY");
        return m_errors;
    }

    // A blit repaints its whole rectangle outside the queue, so only map steps are counted. Loops multiply and a
    // branch takes its heavier arm, matching the cost walk, so gated redraws do not all count at once.
    private int MapWrites(CartridgeStatement[]? statements) {
        var total = 0;
        foreach (var statement in statements ?? []) {
            total += statement?.Kind switch {
                "map" => 1,
                "if" => Math.Max(val1: MapWrites(statements: statement.Then), val2: MapWrites(statements: statement.Else)),
                "repeat" => Math.Clamp(value: statement.Count ?? 0, min: 0, max: CartridgeLimits.RepeatCount) * MapWrites(statements: statement.Body),
                _ => 0,
            };
        }

        return total;
    }

    private void Palettes(int colors, int limit) {
        if (document.Palettes is not { } palettes) {
            Error(path: "palettes", message: "A cartridge declares its palettes.");
            return;
        }

        Bank(bank: palettes.Background, colors: colors, limit: limit, path: "palettes.background");
        Bank(bank: palettes.Object, colors: colors, limit: limit, path: "palettes.object");
    }

    private void Bank(int[][]? bank, int colors, int limit, string path) {
        if (!Count(items: bank, path: path, min: 1, max: limit)) {
            return;
        }

        for (var index = 0; index < bank.Length; ++index) {
            var entry = bank[index];
            if (entry is null || entry.Length != colors) {
                Error(path: $"{path}[{index}]", message: $"Expected {colors} RGB555 integers.");
                continue;
            }

            if (entry.Any(predicate: static color => color is < 0 or > 32767)) {
                Error(path: $"{path}[{index}]", message: "Expected RGB555 integers in 0..32767.");
            }
        }
    }

    private void Raster() {
        var previous = -1;
        for (var index = 0; index < document.Raster.Length; ++index) {
            var row = document.Raster[index];
            var path = $"raster[{index}]";
            if (row is null) { Error(path: path, message: "A raster row cannot be null."); continue; }
            if (row.Line is < 1 or > 143) {
                // Line zero is the document's own scroll, so a row there would name a band that has nothing above it.
                Error(path: path + ".line", message: "Expected a scanline in 1..143.");
            } else if (row.Line <= previous) {
                // The handler walks the rows in order, so an unsorted list would silently skip some of them.
                Error(path: path + ".line", message: "Rows run in ascending scanline order, each past the one before.");
            }

            previous = row.Line;
            Value(value: row.ScrollX, path: path + ".scrollX");
            Value(value: row.ScrollY, path: path + ".scrollY");
        }
    }

    private void Bitmap() {
        if (document.Bitmap is not { } bitmap) {
            return;
        }

        if (document.Target != "agb") {
            Error(path: "bitmap", message: "A per-pixel surface needs the advanced machine; the cgb target draws only from tiles.");
            return;
        }

        // The surface takes the whole picture, so a tile background declared beside it would never be seen.
        if (document.Window is not null || document.Affine is not null || document.Layers.Length != 0) {
            Error(path: "bitmap", message: "A per-pixel surface replaces the tile background, so it cannot share a document with a panel, a turning background or extra layers.");
        }

        if (bitmap.Clear is not null) {
            Value(value: bitmap.Clear, path: "bitmap.clear");
        }
    }

    private void Layers() {
        if (document.Layers.Length == 0) {
            return;
        }

        if (document.Target != "agb") {
            Error(path: "layers", message: "Extra scrolling backgrounds need the advanced machine; the cgb target has one background and a panel.");
            return;
        }

        // The nearer of the two hardware surfaces is where a turning background lives, so the two cannot both claim it.
        var room = document.Affine is null ? CartridgeLimits.LayerCount : CartridgeLimits.LayerCount - 1;
        if (document.Layers.Length > room) {
            Error(path: "layers", message: $"A turning background occupies one of the two surfaces, leaving room for {room}.");
        }

        for (var index = 0; index < document.Layers.Length; ++index) {
            var layer = document.Layers[index];
            var path = $"layers[{index}]";
            if (layer is null) { Error(path: path, message: "A layer cannot be null."); continue; }
            if (!Count(items: layer.Map, path: path + ".map", min: 1024, max: 1024)) { continue; }
            if (layer.Map.Any(predicate: tile => tile < 0 || tile >= document.Tiles.Length)) {
                Error(path: path + ".map", message: "Tile index is outside the authored tile bank.");
            }

            if (layer.MapPalettes is { } cells) {
                var background = document.Palettes?.Background?.Length ?? 0;
                if (cells.Length != 1024) {
                    Error(path: path + ".mapPalettes", message: "Expected 1024 entries, one per cell.");
                } else if (cells.Any(predicate: entry => entry < 0 || entry >= background)) {
                    Error(path: path + ".mapPalettes", message: "A cell names a background palette that is not declared.");
                }
            }

            if (layer.Priority is < 0 or > 3) {
                Error(path: path + ".priority", message: "Expected a priority in 0..3, nearest to furthest.");
            }

            Value(value: layer.ScrollX, path: path + ".scrollX");
            Value(value: layer.ScrollY, path: path + ".scrollY");
            Value(value: layer.Visible, path: path + ".visible");
        }
    }

    private void Affine() {
        if (document.Affine is not { } affine) {
            return;
        }

        if (document.Target != "agb") {
            Error(path: "affine", message: "A rotating background needs the advanced machine; the cgb target's backgrounds only scroll.");
            return;
        }

        // A square map of one of the four sizes the hardware knows.
        if (affine.Map is null || affine.Map.Length is not (256 or 1024 or 4096 or 16384)) {
            Error(path: "affine.map", message: "Expected 256, 1024, 4096 or 16384 tile indices, forming a square map.");
        } else if (affine.Map.Any(predicate: tile => tile < 0 || tile >= document.Tiles.Length)) {
            Error(path: "affine.map", message: "Tile index is outside the authored tile bank.");
        }

        Value(value: affine.Angle, path: "affine.angle");
        Value(value: affine.Scale, path: "affine.scale");
        Value(value: affine.CentreX, path: "affine.centreX");
        Value(value: affine.CentreY, path: "affine.centreY");
        Value(value: affine.Visible, path: "affine.visible");
        if (affine.Scale?.Constant == 0) {
            Error(path: "affine.scale", message: "A zero scale has no inverse; use one or more.");
        }
    }

    private void Clock() {
        if (document.Clock is not { } clock) {
            return;
        }

        m_clocks = true;

        // The two machines carry different clocks: one counts days since the cartridge was started, the other keeps a
        // calendar date. Naming a field the target's device does not have would silently report a made-up value.
        if (document.Target == "agb" && clock.Days is not null) {
            Error(path: "clock.days", message: "The agb clock keeps a calendar date rather than a day count; name day, month and year instead.");
        }

        foreach (var (value, field) in new[] { (clock.Day, "day"), (clock.Month, "month"), (clock.Year, "year") }) {
            if (value is not null && document.Target != "agb") {
                Error(path: "clock." + field, message: $"A calendar {field} needs the advanced machine; the cgb clock counts days rather than dates.");
            }
        }

        var named = 0;
        foreach (var (name, field) in new[] {
            (clock.Seconds, "seconds"), (clock.Minutes, "minutes"), (clock.Hours, "hours"), (clock.Days, "days"),
            (clock.Day, "day"), (clock.Month, "month"), (clock.Year, "year"),
        }) {
            if (name is null) {
                continue;
            }

            ++named;
            if (!m_variables.Contains(item: name)) {
                Error(path: $"clock.{field}", message: $"Unknown state variable '{name}'.");
            }
        }

        if (named == 0) {
            Error(path: "clock", message: "A clock names at least one state slot to fill.");
        }
    }

    private void Window() {
        if (document.Window is not { } window) {
            return;
        }

        if (!Count(items: window.Map, path: "window.map", min: 1024, max: 1024)) {
            return;
        }

        if (window.Map.Any(predicate: tile => tile < 0 || tile >= document.Tiles.Length)) {
            Error(path: "window.map", message: "Tile index is outside the authored tile bank.");
        }

        if (window.MapPalettes is { } cells) {
            var background = document.Palettes?.Background?.Length ?? 0;
            if (cells.Length != 1024) {
                Error(path: "window.mapPalettes", message: "Expected 1024 entries, one per cell.");
            } else if (cells.Any(predicate: entry => entry < 0 || entry >= background)) {
                Error(path: "window.mapPalettes", message: "A cell names a background palette that is not declared.");
            }
        }

        Value(value: window.X, path: "window.x");
        Value(value: window.Y, path: "window.y");
        Value(value: window.Visible, path: "window.visible");
    }

    private void Sounds() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < document.Sounds.Length; ++index) {
            var sound = document.Sounds[index];
            var path = $"sounds[{index}]";
            if (sound is null) { Error(path: path, message: "A sound cannot be null."); continue; }
            Name(name: sound.Name, names: names, path: path);
            var supplied = (sound.Music is not null ? 1 : 0) + (sound.Effect is not null ? 1 : 0) + (sound.Sample is not null ? 1 : 0);
            if (supplied != 1) {
                Error(path: path, message: "Supply exactly one of music, effect or sample.");
                continue;
            }

            if (sound.Waveform is not null && sound.Effect is null) {
                Error(path: path + ".waveform", message: "Only an effect on the wave voice carries a waveform.");
                continue;
            }

            if (sound.Sample is { } pcm) {
                if (document.Target != "agb") {
                    Error(path: path + ".sample", message: "Recorded sound needs the advanced machine's digital sound path; the cgb target has none.");
                    continue;
                }

                if (!Count(items: pcm, path: path + ".sample", min: 1, max: CartridgeLimits.SampleLength)) {
                    continue;
                }

                if (pcm.Any(predicate: static value => value is < -128 or > 127)) {
                    Error(path: path + ".sample", message: "Expected signed eight-bit samples in -128..127.");
                }

                if (sound.Frames is not null) {
                    Error(path: path + ".frames", message: "A recorded one-shot plays at the mixer's own rate.");
                }

                m_sounds.Add(item: sound.Name);
                m_recordings.Add(item: sound.Name);
                continue;
            }

            if (sound.Music is not null && sound.Frames is not null) {
                Error(path: path + ".frames", message: "A music track takes its pacing from the document's tempo.");
            }

            if (sound.Effect is { } effect) {
                var wave = effect.Voice == Puck.Assets.Documents.AudioEffectDocument.VoiceWave;
                if (effect.Voice is not (Puck.Assets.Documents.AudioEffectDocument.VoicePulse1 or Puck.Assets.Documents.AudioEffectDocument.VoiceNoise or Puck.Assets.Documents.AudioEffectDocument.VoiceWave)) {
                    Error(path: path + ".effect.voice", message: "Expected pulse1, noise or wave.");
                } else if (wave != (sound.Waveform is not null)) {
                    Error(path: path + ".waveform", message: "A wave effect carries a waveform, and no other voice may.");
                } else if (sound.Waveform is { } pattern && (pattern.Length != 32 || pattern.Any(predicate: static level => level is < 0 or > 15))) {
                    Error(path: path + ".waveform", message: "Expected 32 four-bit levels in 0..15.");
                }

                if (effect.Rows is not { Count: > 0 }) {
                    Error(path: path + ".effect.rows", message: "An effect carries at least one row.");
                }

                if (sound.Frames is not { } frames || frames is < 1 or > 255) {
                    Error(path: path + ".frames", message: "Expected a per-row frame count in 1..255.");
                }
            }

            m_sounds.Add(item: sound.Name);
        }
    }

    private void Save() {
        if (document.Save is not { } save) {
            return;
        }

        m_saves = true;
        if (save.Version is < 0 or > 255) {
            Error(path: "save.version", message: "Expected a version byte in 0..255.");
        }

        var payload = 0;
        var named = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var name in save.Variables ?? []) {
            if (!m_variables.Contains(item: name)) {
                Error(path: "save.variables", message: $"Unknown state variable '{name}'.");
            } else if (!named.Add(item: name)) {
                Error(path: "save.variables", message: $"'{name}' is persisted more than once.");
            } else {
                payload += 1;
            }
        }

        foreach (var name in save.Arrays ?? []) {
            if (!m_arrays.TryGetValue(key: name, value: out var length)) {
                Error(path: "save.arrays", message: $"Unknown array '{name}'.");
            } else if (!named.Add(item: name)) {
                Error(path: "save.arrays", message: $"'{name}' is persisted more than once.");
            } else {
                payload += length;
            }
        }

        if (payload == 0) {
            Error(path: "save", message: "A save declares at least one variable or array.");
        }

        if (payload > CartridgeLimits.SaveByteCount) {
            Error(path: "save", message: $"The save payload is {payload} bytes; the battery-backed mirror holds {CartridgeLimits.SaveByteCount}.");
        }
    }

    private void Screens() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < document.Screens.Length; ++index) {
            var screen = document.Screens[index];
            var path = $"screens[{index}]";
            if (screen is null) { Error(path: path, message: "A screen cannot be null."); continue; }
            Name(name: screen.Name, names: names, path: path);
            if (screen.Width is < 1 or > 32) {
                Error(path: path + ".width", message: "Expected a width in 1..32.");
                continue;
            }

            if (screen.Tiles is null || screen.Tiles.Length == 0 || screen.Tiles.Length % screen.Width != 0) {
                Error(path: path + ".tiles", message: "Expected a whole number of rows of width entries.");
                continue;
            }

            if (screen.Tiles.Length / screen.Width > 32) {
                Error(path: path + ".tiles", message: "A screen is at most 32 rows tall.");
                continue;
            }

            if (screen.Tiles.Any(predicate: tile => tile < 0 || tile >= document.Tiles.Length)) {
                Error(path: path + ".tiles", message: "Tile index is outside the authored tile bank.");
            }

            m_screens[key: screen.Name] = (screen.Width, screen.Tiles.Length / screen.Width);
        }
    }

    private void Arrays() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var total = 0;
        for (var index = 0; index < document.Arrays.Length; ++index) {
            var array = document.Arrays[index];
            var path = $"arrays[{index}]";
            if (array is null) { Error(path: path, message: "An array cannot be null."); continue; }
            Name(name: array.Name, names: names, path: path);
            if (!Count(items: array.Initial, path: path + ".initial", min: 1, max: CartridgeLimits.ArrayLength)) {
                continue;
            }

            if (array.Initial.Any(predicate: static value => value is < 0 or > 255)) {
                Error(path: path + ".initial", message: "Expected bytes in 0..255.");
            }

            total += array.Initial.Length;
            if (m_variables.Contains(item: array.Name)) {
                Error(path: path + ".name", message: "An array cannot reuse a variable name.");
            } else {
                m_arrays[key: array.Name] = array.Initial.Length;
            }
        }
        if (total > CartridgeLimits.ArrayByteCount) {
            Error(path: "arrays", message: $"Arrays total {total} bytes; the shared state budget is {CartridgeLimits.ArrayByteCount}.");
        }
    }

    private void Rules() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < document.Rules.Length; ++index) {
            var rule = document.Rules[index];
            var path = $"rules[{index}]";
            if (rule is null) { Error(path: path, message: "A rule cannot be null."); continue; }
            Name(name: rule.Name, names: names, path: path);
            if (!Count(items: rule.When, path: path + ".when", min: 0, max: CartridgeLimits.ConditionCount) || !Count(items: rule.Body, path: path + ".body", min: 1, max: CartridgeLimits.StatementCount)) {
                continue;
            }

            for (var condition = 0; condition < rule.When.Length; ++condition) {
                Condition(value: rule.When[condition], path: $"{path}.when[{condition}]");
            }

            if (Steps(statements: rule.Body) > CartridgeLimits.StatementCount) {
                Error(path: path + ".body", message: $"A rule body holds at most {CartridgeLimits.StatementCount} steps in total.");
            }

            Statements(statements: rule.Body, path: path + ".body", depth: 0, inLoop: false);
        }
    }

    private static int Steps(CartridgeStatement[]? statements) {
        if (statements is null) {
            return 0;
        }

        var total = 0;
        foreach (var statement in statements) {
            total += 1 + Steps(statements: statement?.Then) + Steps(statements: statement?.Else) + Steps(statements: statement?.Body);
        }

        return total;
    }

    private void Statements(CartridgeStatement[]? statements, string path, int depth, bool inLoop) {
        if (statements is null) {
            return;
        }

        if (depth > CartridgeLimits.StatementDepth) {
            Error(path: path, message: $"Steps nest at most {CartridgeLimits.StatementDepth} deep.");
            return;
        }

        for (var index = 0; index < statements.Length; ++index) {
            Statement(value: statements[index], path: $"{path}[{index}]", depth: depth, inLoop: inLoop);
        }
    }

    private void Statement(CartridgeStatement? value, string path, int depth, bool inLoop) {
        if (value is null) { Error(path: path, message: "A step cannot be null."); return; }
        switch (value.Kind) {
            case "set":
                Reject(value: value, path: path, allowed: "target operation value");
                Action(value: value, path: path);
                break;
            case "if":
                Reject(value: value, path: path, allowed: "when then else");
                if (!Count(items: value.When, path: path + ".when", min: 0, max: CartridgeLimits.ConditionCount) || !Count(items: value.Then, path: path + ".then", min: 1, max: CartridgeLimits.StatementCount)) {
                    break;
                }

                for (var condition = 0; condition < value.When.Length; ++condition) {
                    Condition(value: value.When[condition], path: $"{path}.when[{condition}]");
                }

                Statements(statements: value.Then, path: path + ".then", depth: depth + 1, inLoop: inLoop);
                if (value.Else is not null && Count(items: value.Else, path: path + ".else", min: 1, max: CartridgeLimits.StatementCount)) {
                    Statements(statements: value.Else, path: path + ".else", depth: depth + 1, inLoop: inLoop);
                }

                break;
            case "repeat":
                Reject(value: value, path: path, allowed: "count index body");
                if (value.Count is not { } count || count < 1 || count > CartridgeLimits.RepeatCount) {
                    Error(path: path + ".count", message: $"Expected a literal iteration count in 1..{CartridgeLimits.RepeatCount}.");
                }

                if (value.Index is not { } loopIndex) {
                    Error(path: path + ".index", message: "A repeat requires an index variable.");
                } else if (!m_variables.Contains(item: loopIndex)) {
                    Error(path: path + ".index", message: $"Unknown state variable '{loopIndex}'.");
                }

                if (Count(items: value.Body, path: path + ".body", min: 1, max: CartridgeLimits.StatementCount)) {
                    Statements(statements: value.Body, path: path + ".body", depth: depth + 1, inLoop: true);
                }

                break;
            case "map":
                Reject(value: value, path: path, allowed: "row column tile");
                Value(value: value.Row, path: path + ".row");
                Value(value: value.Column, path: path + ".column");
                Value(value: value.Tile, path: path + ".tile");
                break;
            case "blit":
                Reject(value: value, path: path, allowed: "screen row column");
                if (value.Screen is not { } screen || !m_screens.TryGetValue(key: screen, value: out var size)) {
                    Error(path: path + ".screen", message: $"Unknown screen '{value.Screen}'.");
                    break;
                }

                // A blit paints with the display off, so its destination is fixed at build time rather than sampled.
                if (value.Row?.Constant is not { } row || value.Column?.Constant is not { } column) {
                    Error(path: path, message: "A blit takes a literal row and column.");
                    break;
                }

                if (row + size.Height > 32 || column + size.Width > 32) {
                    Error(path: path, message: $"Screen '{screen}' at row {row}, column {column} runs past the 32 by 32 map.");
                }

                if (size.Width * size.Height > CartridgeLimits.BlitCellCount) {
                    Error(path: path + ".screen", message: $"Screen '{screen}' covers {size.Width * size.Height} cells; a blit paints at most {CartridgeLimits.BlitCellCount} before the display-off window costs frames.");
                }

                break;
            case "play":
                Reject(value: value, path: path, allowed: "sound rate");
                if (value.Sound is not { } track || !m_sounds.Contains(item: track)) {
                    Error(path: path + ".sound", message: $"Unknown sound '{value.Sound}'.");
                } else if (value.Rate is not null && !m_recordings.Contains(item: track)) {
                    // Only a recording is resampled; the melodic voices take their pitch from the track's own rows.
                    Error(path: path + ".rate", message: $"Sound '{track}' is not a recording, so it has no playback rate.");
                }

                if (value.Rate is not null) {
                    Value(value: value.Rate, path: path + ".rate");
                }

                break;
            case "stop":
                Reject(value: value, path: path, allowed: "");
                if (m_sounds.Count == 0) {
                    Error(path: path, message: "A stop step requires a declared sound.");
                }

                break;
            case "plot":
                Reject(value: value, path: path, allowed: "row column colour");
                if (document.Bitmap is null) {
                    Error(path: path, message: "A plot step requires a declared bitmap.");
                }

                Value(value: value.Row, path: path + ".row");
                Value(value: value.Column, path: path + ".column");
                Value(value: value.Colour, path: path + ".colour");
                break;
            case "blend":
                Reject(value: value, path: path, allowed: "surface weight");
                if (document.Target != "agb") {
                    // The Color machine has no blend unit; its fade works by rebaking palettes, which cannot mix two
                    // surfaces because a palette entry knows nothing about what is drawn beneath it.
                    Error(path: path, message: "Blending two surfaces is an agb capability; the cgb target has no blend unit.");
                }

                if (value.Surface is null || Array.IndexOf(array: CartridgeLimits.BlendSurfaces, value: value.Surface) < 0) {
                    Error(path: path + ".surface", message: "Expected background, panel, middle, far, sprites or backdrop.");
                } else if (value.Surface == "panel" && document.Window is null) {
                    Error(path: path + ".surface", message: "Blending the panel needs a declared window.");
                } else if (value.Surface == "middle" && document.Affine is null && document.Layers.Length < 1) {
                    Error(path: path + ".surface", message: "Blending the middle surface needs a turning background or a declared layer.");
                } else if (value.Surface == "far" && document.Layers.Length < (document.Affine is null ? 2 : 1)) {
                    Error(path: path + ".surface", message: "Blending the far surface needs a layer behind the middle one.");
                }

                Value(value: value.Weight, path: path + ".weight");
                if (value.Weight?.Constant > CartridgeLimits.BlendWeights) {
                    Error(path: path + ".weight", message: $"A blend weight runs 0 through {CartridgeLimits.BlendWeights}.");
                }

                break;
            case "fade":
                Reject(value: value, path: path, allowed: "amount toward");
                Value(value: value.Amount, path: path + ".amount");
                if (value.Amount?.Constant > CartridgeLimits.FadeSteps) {
                    Error(path: path + ".amount", message: $"A fade runs 0 through {CartridgeLimits.FadeSteps}.");
                }

                if (value.Toward is not ("black" or "white")) {
                    Error(path: path + ".toward", message: "Expected black or white.");
                }

                break;
            case "clock":
                Reject(value: value, path: path, allowed: "");
                if (!m_clocks) {
                    Error(path: path, message: "A clock step requires a declared clock.");
                }

                break;
            case "save":
            case "load":
                Reject(value: value, path: path, allowed: "");
                if (!m_saves) {
                    Error(path: path, message: $"A {value.Kind} step requires a declared save.");
                }

                break;
            case "break":
                Reject(value: value, path: path, allowed: "");
                if (!inLoop) {
                    Error(path: path, message: "A break must sit inside a repeat.");
                }

                break;
            default: Error(path: path + ".kind", message: "Expected set, if, repeat, break, map, blit, plot, save, load, play, stop, clock, fade or blend."); break;
        }
    }

    // Every field outside the step's own kind must be absent, so a mistyped kind cannot silently drop authored data.
    private void Reject(CartridgeStatement value, string path, string allowed) {
        foreach (var (name, present) in new[] {
            ("target", value.Target is not null), ("operation", value.Operation is not null), ("value", value.Value is not null),
            ("when", value.When is not null), ("then", value.Then is not null), ("else", value.Else is not null),
            ("count", value.Count is not null), ("index", value.Index is not null), ("body", value.Body is not null),
            ("row", value.Row is not null), ("column", value.Column is not null), ("tile", value.Tile is not null),
            ("screen", value.Screen is not null), ("sound", value.Sound is not null), ("rate", value.Rate is not null),
            ("amount", value.Amount is not null), ("toward", value.Toward is not null), ("colour", value.Colour is not null),
            ("surface", value.Surface is not null), ("weight", value.Weight is not null),
        }) {
            if (present && !allowed.Contains(value: name, comparisonType: StringComparison.Ordinal)) {
                Error(path: $"{path}.{name}", message: $"A {value.Kind} step cannot carry '{name}'.");
            }
        }
    }

    private void Action(CartridgeStatement value, string path) {
        Target(target: value.Target, path: path + ".target");
        if (value.Operation is not ("set" or "add" or "subtract" or "and" or "or" or "xor" or "mul" or "div" or "mod" or "shl" or "shr")) {
            Error(path: path + ".operation", message: "Expected set, add, subtract, and, or, xor, mul, div, mod, shl or shr.");
        }

        Value(value: value.Value, path: path + ".value");
        // A literal zero divisor or an out-of-range literal shift is always a defect; the runtime forms are total.
        if (value.Operation is "div" or "mod" && value.Value?.Constant == 0) {
            Error(path: path + ".value", message: "A literal zero divisor is refused; a runtime zero divisor yields zero.");
        }

        if (value.Operation is "shl" or "shr" && value.Value?.Constant >= 8) {
            Error(path: path + ".value", message: "A literal shift of eight or more is refused; it can only produce zero.");
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

    private void Target(CartridgeTarget? target, string path) {
        if (target is null) { Error(path: path, message: "Supply a target variable or array element."); return; }
        if ((target.Variable is null) == (target.Array is null)) {
            Error(path: path, message: "Supply exactly one of variable or array.");
            return;
        }

        if (target.Variable is { } variable) {
            if (target.Index is not null) {
                Error(path: path + ".index", message: "A variable target cannot carry an index.");
            }

            if (!m_variables.Contains(item: variable)) {
                Error(path: path + ".variable", message: $"Unknown state variable '{variable}'.");
            }

            return;
        }

        Element(array: target.Array!, index: target.Index, path: path);
    }

    private void Value(CartridgeValue? value, string path) {
        if (value is null) { Error(path: path, message: "Supply exactly one of constant, variable or array."); return; }
        var supplied = (value.Constant is not null ? 1 : 0) + (value.Variable is not null ? 1 : 0) + (value.Array is not null ? 1 : 0);
        if (supplied != 1) {
            Error(path: path, message: "Supply exactly one of constant, variable or array.");
            return;
        }

        if (value.Constant is { } constant) {
            if (constant is < 0 or > 255) {
                Error(path: path + ".constant", message: "Expected a byte in 0..255.");
            }

            if (value.Index is not null) {
                Error(path: path + ".index", message: "A constant cannot carry an index.");
            }

            return;
        }

        if (value.Variable is { } name) {
            if (value.Index is not null) {
                Error(path: path + ".index", message: "A variable cannot carry an index.");
            }

            if (!m_variables.Contains(item: name)) {
                Error(path: path + ".variable", message: $"Unknown state variable '{name}'.");
            }

            return;
        }

        Element(array: value.Array!, index: value.Index, path: path);
    }

    private void Element(string array, CartridgeValue? index, string path) {
        if (!m_arrays.ContainsKey(key: array)) {
            Error(path: path + ".array", message: $"Unknown array '{array}'.");
        }

        if (index is null) {
            Error(path: path + ".index", message: "An array access requires an index.");
            return;
        }

        Value(value: index, path: path + ".index");
        if (index.Constant is { } literal && m_arrays.TryGetValue(key: array, value: out var length) && literal >= length) {
            Error(path: path + ".index", message: $"Index {literal} is outside array '{array}' of length {length}.");
        }
    }


    private void Error(string path, string message) => m_errors.Add(item: new DocumentValidationError(Path: path, Message: message));
    private bool Count<T>([NotNullWhen(returnValue: true)] T[]? items, string path, int min, int max) {
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
    private static bool Ascii(string? value, int min, int max) => value is not null && value.Length >= min && value.Length <= max && value.All(predicate: static ch => ch is >= ' ' and <= '~');
    internal static int Digit(char digit) => digit switch { >= '0' and <= '9' => digit - '0', >= 'A' and <= 'F' => digit - 'A' + 10, >= 'a' and <= 'f' => digit - 'a' + 10, _ => -1 };
}
