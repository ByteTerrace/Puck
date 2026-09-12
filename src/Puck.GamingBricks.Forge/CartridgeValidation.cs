using System.Diagnostics.CodeAnalysis;
using Puck.Assets.Documents;

using Puck.State;
namespace Puck.GamingBricks.Forge;

internal sealed class CartridgeValidation(CartridgeDocument document) {
    private readonly List<DocumentValidationError> m_errors = [];
    private readonly Dictionary<string, int> m_arrays = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, (int Width, int Height)> m_screens = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_variables = new(comparer: StringComparer.Ordinal);
    // Each declared slot's byte width, so a constant can be refused against the width of what it is written to.
    private readonly Dictionary<string, int> m_widths = new(comparer: StringComparer.Ordinal);
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
        // A slot spends one or two bytes of the variable window depending on its declared ceiling, so the window is
        // what a wide document runs out of rather than the slot count.
        var windowBytes = 0;
        for (var index = 0; index < document.Variables.Length; ++index) {
            var variable = document.Variables[index];
            if (variable is null) { Error(path: $"variables[{index}]", message: "A variable cannot be null."); continue; }
            Name(name: variable.Name, names: m_variables, path: $"variables[{index}]");
            if (variable.Max is { } ceiling && ceiling is < 1 or > CartridgeLimits.WideMaximum) {
                Error(path: $"variables[{index}].max", message: $"Expected a ceiling in 1..{CartridgeLimits.WideMaximum}.");

                continue;
            }

            if ((variable.Initial < 0) || (variable.Initial > variable.Ceiling)) {
                Error(path: $"variables[{index}].initial", message: $"Expected a value in 0..{variable.Ceiling}.");
            }

            m_widths[variable.Name] = variable.Width;
            windowBytes += variable.Width;
        }

        if (windowBytes > CartridgeLimits.VariableCount) {
            Error(path: "variables", message: $"The declared slots need {windowBytes} bytes; the variable window holds {CartridgeLimits.VariableCount}.");
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
        Scene();
        if (m_errors.Count == 0) {
            // Rules that cannot share a frame do not share the queue either, so a set of phase arms needs room for the
            // heaviest of them rather than for all. This is the same partition the cost estimate reads.
            var guards = new (string? Name, int Value)[document.Rules.Length];
            for (var index = 0; index < document.Rules.Length; ++index) {
                guards[index] = CartridgeCost.Guard(rule: document.Rules[index]);
            }

            var exclusive = CartridgeCost.ExclusiveGuards(rules: document.Rules, guards: guards, scene: document.Scene, document: document);
            var writes = 0;
            var arms = new Dictionary<string, Dictionary<int, int>>(comparer: StringComparer.Ordinal);
            for (var index = 0; index < document.Rules.Length; ++index) {
                var count = CartridgeEffects.MapWrites(statements: document.Rules[index].Body);
                if (guards[index].Name is not { } name || !exclusive.Contains(item: name)) {
                    writes = CartridgeEffects.AddMapWrites(writes, count);
                    continue;
                }

                if (!arms.TryGetValue(key: name, value: out var byValue)) {
                    byValue = [];
                    arms[key: name] = byValue;
                }

                byValue[key: guards[index].Value] = CartridgeEffects.AddMapWrites(byValue.GetValueOrDefault(key: guards[index].Value), count);
            }

            foreach (var byValue in arms.Values) {
                writes = CartridgeEffects.AddMapWrites(writes, byValue.Values.Max());
            }

            if (writes > CartridgeLimits.MapWriteCount) {
                Error(path: "rules", message: $"A frame can execute at least {writes} map writes against a queue of {CartridgeLimits.MapWriteCount}; gate the redraws behind branches or spread them across frames.");
            }
        }

        names.Clear();
        for (var index = 0; index < document.Sprites.Length; ++index) {
            var sprite = document.Sprites[index];
            var path = $"sprites[{index}]";
            if (sprite is null) { Error(path: path, message: "A sprite cannot be null."); continue; }
            Name(name: sprite.Name, names: names, path: path);
            Value(value: sprite.Tile, path: path + ".tile");
            if (Literal(value: sprite.Tile) >= document.Tiles.Length) {
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
                if (Literal(value: slot) >= (document.Palettes?.Object?.Length ?? 0)) {
                    Error(path: path + ".palette", message: "Sprite names an object palette that is not declared.");
                }
            }
        }
        Value(value: document.ScrollX, path: "scrollX"); Value(value: document.ScrollY, path: "scrollY");
        return m_errors;
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
        if (Literal(value: affine.Scale) == 0) {
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

    // One voice part of a music track: a voice named once, its own document, and a waveform exactly when the voice
    // it plays on is the one that needs one.
    private void MusicVoice(CartridgeMusicVoice? part, string path, HashSet<string> voices) {
        if (part is null) {
            Error(path: path, message: "A voice part cannot be null.");

            return;
        }

        if (part.Voice is not (Puck.Assets.Documents.AudioEffectDocument.VoicePulse1
            or Puck.Assets.Documents.AudioEffectDocument.VoiceNoise
            or Puck.Assets.Documents.AudioEffectDocument.VoiceWave
            or Puck.Assets.Documents.AudioEffectDocument.VoicePulse2)) {
            Error(path: path + ".voice", message: "Expected pulse1, pulse2, wave or noise.");
        } else if (!voices.Add(item: part.Voice)) {
            Error(path: path + ".voice", message: "A track gives each voice at most one part.");
        }

        var wave = part.Voice == Puck.Assets.Documents.AudioEffectDocument.VoiceWave;
        if (wave != (part.Waveform is not null)) {
            Error(path: path + ".waveform", message: "A part on the wave voice carries a waveform, and no other voice may.");
        } else if (part.Waveform is { } pattern && (pattern.Length != 32 || pattern.Any(predicate: static level => level is < 0 or > 15))) {
            Error(path: path + ".waveform", message: "Expected 32 four-bit levels in 0..15.");
        }

        if (part.Part is null) {
            Error(path: path + ".part", message: "A voice part carries a document.");

            return;
        }

        foreach (var failure in Puck.Assets.Documents.AudioCanonicalizer.Validate(document: part.Part)) {
            Error(path: $"{path}.part.{failure.Path}", message: failure.Message);
        }
    }

    private void Sounds() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        // The voices the cartridge's music occupies. An effect may not land on one of them, so they are gathered
        // before any effect is judged rather than as the sounds are walked.
        var occupied = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var sound in document.Sounds) {
            foreach (var part in (sound?.Music ?? [])) {
                if (part?.Voice is { } voice) {
                    occupied.Add(item: voice);
                }
            }
        }

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

            if (sound.Music is { } parts) {
                if (sound.Frames is not null) {
                    Error(path: path + ".frames", message: "A music track takes its pacing from each part's own tempo.");
                }

                if (parts.Length is < 1 or > CartridgeLimits.SoundVoiceCount) {
                    Error(path: path + ".music", message: $"A track carries 1 to {CartridgeLimits.SoundVoiceCount} voice parts.");
                    continue;
                }

                var voices = new HashSet<string>(comparer: StringComparer.Ordinal);
                for (var part = 0; part < parts.Length; ++part) {
                    MusicVoice(part: parts[part], path: $"{path}.music[{part}]", voices: voices);
                }
            }

            if (sound.Effect is { } effect) {
                var wave = effect.Voice == Puck.Assets.Documents.AudioEffectDocument.VoiceWave;
                if (effect.Voice is not (Puck.Assets.Documents.AudioEffectDocument.VoicePulse1 or Puck.Assets.Documents.AudioEffectDocument.VoiceNoise or Puck.Assets.Documents.AudioEffectDocument.VoiceWave)) {
                    Error(path: path + ".effect.voice", message: "Expected pulse1, noise or wave.");
                } else if (occupied.Contains(item: effect.Voice!)) {
                    Error(path: path + ".effect.voice", message: $"The {effect.Voice} voice carries a part of this cartridge's music; an effect there would cut it off.");
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
                payload += m_widths.GetValueOrDefault(name, 1);
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

            if (screen.Palettes is { } shades) {
                if (shades.Length != screen.Tiles.Length) {
                    Error(path: path + ".palettes", message: "Expected one palette index per tile.");
                } else if (shades.Any(predicate: shade => shade < 0 || shade >= (document.Palettes?.Background?.Length ?? 0))) {
                    Error(path: path + ".palettes", message: "A tile names a background palette that is not declared.");
                }
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

    // The declared scene names an ordinary variable. Nothing else is asked of it: which values are scenes is decided by
    // the rules that guard on it, and a value no rule guards is simply a frame in which only the ungated rules run.
    private void Scene() {
        if (document.Scene is not { } scene) {
            return;
        }

        if (!m_variables.Contains(item: scene)) {
            Error(path: "scene", message: $"'{scene}' names no declared variable.");

            return;
        }

        // The frame's snapshot is one byte.
        if (m_widths.TryGetValue(key: scene, value: out var width) && (width != 1)) {
            Error(path: "scene", message: $"'{scene}' is a wide slot; the frame's scene snapshot is one byte.");
        }
    }

    private void Rules() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < document.Rules.Length; ++index) {
            var rule = document.Rules[index];
            var path = $"rules[{index}]";
            if (rule is null) { Error(path: path, message: "A rule cannot be null."); continue; }
            Name(name: rule.Name, names: names, path: path);
            if (!Count(items: rule.Body, path: path + ".body", min: 1, max: CartridgeLimits.StatementCount)) {
                continue;
            }

            Comparisons(gate: rule.When, path: path + ".when");
            Gate(value: rule.When, path: path + ".when");

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
                if (!Count(items: value.Then, path: path + ".then", min: 1, max: CartridgeLimits.StatementCount)) {
                    break;
                }

                Comparisons(gate: value.When, path: path + ".when");
                Gate(value: value.When, path: path + ".when");

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
                Reject(value: value, path: path, allowed: "row column tile palette");
                Value(value: value.Row, path: path + ".row");
                Value(value: value.Column, path: path + ".column");
                Value(value: value.Tile, path: path + ".tile");
                if (value.Palette is { } cellPalette) {
                    Value(value: cellPalette, path: path + ".palette");
                    if (Literal(value: cellPalette) >= (document.Palettes?.Background?.Length ?? 0)) {
                        Error(path: path + ".palette", message: "The write names a background palette that is not declared.");
                    }
                }

                break;
            case "blit":
                Reject(value: value, path: path, allowed: "screen row column");
                if (value.Screen is not { } screen || !m_screens.TryGetValue(key: screen, value: out var size)) {
                    Error(path: path + ".screen", message: $"Unknown screen '{value.Screen}'.");
                    break;
                }

                // A blit paints with the display off, so its destination is fixed at build time rather than sampled.
                if ((Literal(value: value.Row) is not { } row) || (Literal(value: value.Column) is not { } column)) {
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
                if (Literal(value: value.Weight) > CartridgeLimits.BlendWeights) {
                    Error(path: path + ".weight", message: $"A blend weight runs 0 through {CartridgeLimits.BlendWeights}.");
                }

                break;
            case "fade":
                Reject(value: value, path: path, allowed: "amount toward");
                Value(value: value.Amount, path: path + ".amount");
                if (Literal(value: value.Amount) > CartridgeLimits.FadeSteps) {
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
        var wideTarget = ((value.Target is { Key: null } destination) && m_widths.TryGetValue(key: destination.State, value: out var destinationWidth) && (destinationWidth != 1));

        if (wideTarget && (value.Operation is not (null or ExpressionOp.Add or ExpressionOp.Subtract))) {
            Error(path: path + ".operation", message: $"'{value.Operation}' has no sixteen-bit form; a wide slot takes assignment, {nameof(ExpressionOp.Add)} or {nameof(ExpressionOp.Subtract)}.");
        }

        if (!CartridgeOperations.AdmitsCombine(operation: value.Operation)) {
            Error(path: path + ".operation", message: $"Expected one of {CartridgeOperations.CombineNames}, or no operation to assign.");
        }

        Expression(value: value.Value, path: path + ".value", wide: wideTarget);
        ConstantFits(value: value.Value, width: (wideTarget ? 2 : 1), path: path + ".value");
        // A literal zero divisor or an out-of-range literal shift is always a defect; the runtime forms are total.
        if ((value.Operation is (ExpressionOp.Divide or ExpressionOp.Modulo)) && (Literal(value: value.Value) == 0)) {
            Error(path: path + ".value", message: "A literal zero divisor is refused; a runtime zero divisor yields zero.");
        }

        if (CartridgeOperations.Shifts(operation: value.Operation) && (Literal(value: value.Value) >= 8)) {
            Error(path: path + ".value", message: "A literal shift of eight or more is refused; it can only produce zero.");
        }
    }

    private void Comparisons(ActionPredicate? gate, string path) {
        var count = Reached(gate: gate);

        if (count > CartridgeLimits.ConditionCount) {
            Error(path: path, message: $"A gate reaches at most {CartridgeLimits.ConditionCount} comparisons; this one reaches {count}.");
        }
    }

    private static int Reached(ActionPredicate? gate) => (gate switch {
        null => 0,
        ActionPredicate.All all => all.Predicates?.Sum(selector: Reached) ?? 0,
        ActionPredicate.Any any => any.Predicates?.Sum(selector: Reached) ?? 0,
        ActionPredicate.Not not => Reached(gate: not.Predicate),
        _ => 1,
    });

    private void Gate(ActionPredicate? value, string path) {
        switch (value) {
            case null:
                return;
            case ActionPredicate.All all:
                Composite(predicates: all.Predicates, path: path + ".predicates");
                return;
            case ActionPredicate.Any any:
                Composite(predicates: any.Predicates, path: path + ".predicates");
                return;
            case ActionPredicate.Not not:
                Gate(value: not.Predicate, path: path + ".predicate");
                return;
            case ActionPredicate.CompareValue compare:
                if (compare.Kind != CellKind.Int) {
                    Error(path: path + ".kind", message: "A cartridge compares whole numbers; the fixed-point domain has no representation on either machine.");
                }

                WideValue(value: compare.Left, path: path + ".left");
                WideValue(value: compare.Right, path: path + ".right");
                ConstantFits(value: compare.Right, width: WidthOf(value: compare.Left), path: path + ".right");
                ConstantFits(value: compare.Left, width: WidthOf(value: compare.Right), path: path + ".left");
                return;
            default:
                Error(path: path, message: $"A cartridge gate compares two expressions, or composes those through all, any and not; '{value.GetType().Name}' is not one of them.");
                return;
        }
    }

    private void Composite(IReadOnlyList<ActionPredicate> predicates, string path) {
        if ((predicates is null) || (predicates.Count == 0)) {
            Error(path: path, message: "A composed gate needs at least one predicate.");
            return;
        }

        for (var index = 0; index < predicates.Count; ++index) {
            Gate(value: predicates[index], path: $"{path}[{index}]");
        }
    }

    private void Target(CartridgeTarget? target, string path) {
        if (target is null) { Error(path: path, message: "Supply a target state or array element."); return; }
        if (target.Key is null) {
            if (m_arrays.ContainsKey(key: target.State)) {
                Error(path: path, message: $"'{target.State}' is an array; a write to it requires an index.");
            }
            else if (!m_variables.Contains(item: target.State)) {
                Error(path: path + ".state", message: $"Unknown state variable '{target.State}'.");
            }

            return;
        }

        Element(array: target.State, key: target.Key, path: path);
    }

    // How many bytes an operand occupies: a bare slot read carries the slot's own width, and everything else is a
    // byte (an array element, a literal, a computed result). An undeclared name is reported elsewhere and reads narrow.
    private int WidthOf(ValueExpression? value) =>
        (((Slot(value: value) is { } name) && m_widths.TryGetValue(key: name, value: out var width)) ? width : 1);

    // The single-token forms: a bare slot read, and a bare literal. Both are what a pairing rule is stated against.
    private static string? Slot(ValueExpression? value) =>
        ((value?.Tokens is [ValueToken.State { Key: null } state]) ? state.Name : null);

    private static int? Literal(ValueExpression? value) =>
        (((value?.Tokens is [ValueToken.Constant constant]) && (decimal.Truncate(d: constant.Value) == constant.Value))
            ? (int)constant.Value
            : null);

    // A literal wider than the slot it is paired with is a defect rather than an always-false comparison or a silently
    // truncated write, so it is refused where the pairing is known.
    private void ConstantFits(ValueExpression? value, int width, string path) {
        if ((Literal(value: value) is not { } constant) || (width != 1) || (constant <= CartridgeLimits.NarrowMaximum)) {
            return;
        }

        Error(path: path, message: $"{constant} does not fit the one-byte slot it is paired with; declare that slot a wider max.");
    }

    private void Value(ValueExpression? value, string path) => Expression(value: value, path: path, wide: false);

    private void WideValue(ValueExpression? value, string path) => Expression(value: value, path: path, wide: true);

    // A wide slot is two bytes and the evaluator works a byte at a time, so one is admitted only as a whole operand in
    // the three places that read a pair — a set step's target and value, and a comparison's operands. Inside a
    // composed expression, and in every byte-wide field, it is refused by name rather than truncated to its low half.
    private void Expression(ValueExpression? value, string path, bool wide) {
        if ((value?.Tokens is null) || (value.Tokens.Count == 0)) { Error(path: path, message: "Supply an expression."); return; }
        if (value.Tokens.Count > CartridgeExpressions.MaxTokens) {
            Error(path: path, message: $"An expression carries at most {CartridgeExpressions.MaxTokens} tokens; this one carries {value.Tokens.Count}.");
            return;
        }

        int depth;
        try {
            depth = CartridgeExpressions.Depth(expression: value);
        }
        catch (ArgumentException error) {
            Error(path: path, message: error.Message);
            return;
        }

        if (depth > CartridgeExpressions.MaxDepth) {
            Error(path: path, message: $"An expression holds at most {CartridgeExpressions.MaxDepth} values at once; this one holds {depth}.");
        }

        var bare = (value.Tokens.Count == 1);
        foreach (var token in value.Tokens) {
            switch (token) {
                case ValueToken.Constant constant:
                    if ((!wide || !bare) && constant.Value > CartridgeLimits.NarrowMaximum) {
                        Error(path: path, message: $"'{constant.Value}' does not fit a byte expression; only a whole operand paired with a wide slot admits more than {CartridgeLimits.NarrowMaximum}.");
                    }
                    if (decimal.Truncate(d: constant.Value) != constant.Value) {
                        Error(path: path, message: $"'{constant.Value}' is not a whole number; a cartridge carries no fraction.");
                    }
                    else if (constant.Value is < 0 or > CartridgeLimits.WideMaximum) {
                        Error(path: path, message: $"Expected a value in 0..{CartridgeLimits.WideMaximum}.");
                    }

                    break;
                case ValueToken.State state:
                    Read(state: state, path: path, wide: (wide && bare));
                    break;
                default:
                    if (ExpressionVocabulary.Operation(token: token) is not { } operation) {
                        Error(path: path, message: $"'{CartridgeExpressions.Spell(token: token)}' is not an expression a cartridge evaluates.");
                    }
                    else if (!CartridgeExpressions.Admits(operation: operation)) {
                        Error(path: path, message: $"The rule language evaluates '{ExpressionVocabulary.Spelling(operation: operation)}'; a cartridge does not.");
                    }

                    break;
            }
        }
    }

    private void Read(ValueToken.State state, string path, bool wide) {
        if (state.Key is not null) {
            Element(array: state.Name, key: state.Key, path: path);

            return;
        }

        if (CartridgeExpressions.TryKey(name: state.Name, button: out var button, mode: out var mode)) {
            if (button is not ("a" or "b" or "start" or "select" or "up" or "down" or "left" or "right")) {
                Error(path: path, message: $"Unknown joypad button '{button}'.");
            }

            if (mode is not ("held" or "pressed" or "released")) {
                Error(path: path, message: $"Expected held, pressed or released; found '{mode}'.");
            }

            return;
        }

        if (state.Name.StartsWith(value: "$", comparisonType: StringComparison.Ordinal)) {
            Error(path: path, message: $"'{state.Name}' is not a channel a cartridge answers; input reads through '{CartridgeExpressions.KeyPrefix}<button>:<mode>'.");

            return;
        }

        if (!m_variables.Contains(item: state.Name)) {
            Error(path: path, message: $"Unknown state variable '{state.Name}'.");

            return;
        }

        if (!wide && m_widths.TryGetValue(key: state.Name, value: out var width) && (width != 1)) {
            Error(path: path, message: $"'{state.Name}' is a wide slot; this field reads a byte.");
        }
    }

    private void Element(string array, string key, string path) {
        if (!m_arrays.ContainsKey(key: array)) {
            Error(path: path, message: (m_variables.Contains(item: array)
                ? $"'{array}' is a state slot; it cannot carry an index."
                : $"Unknown array '{array}'."));
        }

        ValueExpression? index;
        try {
            index = CartridgeExpressions.Index(key: key);
        }
        catch (FormatException error) {
            Error(path: path, message: $"The index of '{array}' does not parse: {error.Message}");
            return;
        }

        if (index is null) {
            Error(path: path, message: "An array access requires an index.");
            return;
        }

        Value(value: index, path: path + ".index");
        if ((Literal(value: index) is { } literal) && m_arrays.TryGetValue(key: array, value: out var length) && (literal >= length)) {
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
