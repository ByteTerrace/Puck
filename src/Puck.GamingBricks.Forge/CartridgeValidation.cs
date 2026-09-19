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

    internal static int Digit(char digit) => digit switch { >= '0' and <= '9' => (digit - '0'), >= 'A' and <= 'F' => ((digit - 'A') + 10), >= 'a' and <= 'f' => ((digit - 'a') + 10), _ => -1 };

    private void Action(CartridgeStatement value, string path) {
        Target(
            target: value.Target,
            path: (path + ".target")
        );
        var wideTarget = ((value.Target is { Key: null } destination) && m_widths.TryGetValue(
            key: destination.State,
            value: out var destinationWidth
        ) && (destinationWidth != 1));

        if (
            wideTarget &&
            (value.Operation is not (null or ExpressionOp.Add or ExpressionOp.Subtract))
        ) {
            Error(
                path: (path + ".operation"),
                message: $"'{value.Operation}' has no sixteen-bit form; a wide slot takes assignment, {nameof(ExpressionOp.Add)} or {nameof(ExpressionOp.Subtract)}."
            );
        }

        if (!CartridgeOperations.AdmitsCombine(operation: value.Operation)) {
            Error(
                path: (path + ".operation"),
                message: $"Expected one of {CartridgeOperations.CombineNames}, or no operation to assign."
            );
        }

        Expression(
            value: value.Value,
            path: (path + ".value"),
            wide: wideTarget
        );
        ConstantFits(
            value: value.Value,
            width: (wideTarget
            ? 2
            : 1),
            path: (path + ".value")
        );
        // A literal zero divisor or an out-of-range literal shift is always a defect; the runtime forms are total.
        if (
            (value.Operation is (ExpressionOp.Divide or ExpressionOp.Modulo)) &&
            (Literal(value: value.Value) == 0)
        ) {
            Error(
                message: "A literal zero divisor is refused; a runtime zero divisor yields zero.",
                path: (path + ".value")
            );
        }

        if (
            CartridgeOperations.Shifts(operation: value.Operation) &&
            (Literal(value: value.Value) >= 8)
        ) {
            Error(
                message: "A literal shift of eight or more is refused; it can only produce zero.",
                path: (path + ".value")
            );
        }
    }
    private void Affine() {
        if (document.Affine is not { } affine) {
            return;
        }

        if (document.Target != "agb") {
            Error(
                message: "A rotating background needs the advanced machine; the cgb target's backgrounds only scroll.",
                path: "affine"
            );
            return;
        }

        // A square map of one of the four sizes the hardware knows.
        if (
            (affine.Map is null) ||
            (affine.Map.Length is not (256 or 1024 or 4096 or 16384))
        ) {
            Error(
                message: "Expected 256, 1024, 4096 or 16384 tile indices, forming a square map.",
                path: "affine.map"
            );
        } else if (affine.Map.Any(predicate: tile => ((tile < 0) || (tile >= document.Tiles.Length)))) {
            Error(
                message: "Tile index is outside the authored tile bank.",
                path: "affine.map"
            );
        }

        Value(
            value: affine.Angle,
            path: "affine.angle"
        );
        Value(
            value: affine.Scale,
            path: "affine.scale"
        );
        Value(
            value: affine.CentreX,
            path: "affine.centreX"
        );
        Value(
            value: affine.CentreY,
            path: "affine.centreY"
        );
        Value(
            value: affine.Visible,
            path: "affine.visible"
        );
        if (Literal(value: affine.Scale) == 0) {
            Error(
                message: "A zero scale has no inverse; use one or more.",
                path: "affine.scale"
            );
        }
    }
    private void Arrays() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var total = 0;

        for (var index = 0; (index < document.Arrays.Length); ++index) {
            var array = document.Arrays[index];
            var path = $"arrays[{index}]";

            if (array is null) {
                Error(
                message: "An array cannot be null.",
                path: path
            ); continue;
            }
            Name(
                name: array.Name,
                names: names,
                path: path
            );
            if (!Count(
                items: array.Initial,
                path: (path + ".initial"),
                min: 1,
                max: CartridgeLimits.ArrayLength
            )) {
                continue;
            }

            if (array.Initial.Any(predicate: static value => (value is < 0 or > 255))) {
                Error(
                    message: "Expected bytes in 0..255.",
                    path: (path + ".initial")
                );
            }

            total += array.Initial.Length;
            if (m_variables.Contains(item: array.Name)) {
                Error(
                    message: "An array cannot reuse a variable name.",
                    path: (path + ".name")
                );
            } else {
                m_arrays[key: array.Name] = array.Initial.Length;
            }
        }
        if (total > CartridgeLimits.ArrayByteCount) {
            Error(
                message: $"Arrays total {total} bytes; the shared state budget is {CartridgeLimits.ArrayByteCount}.",
                path: "arrays"
            );
        }
    }
    private static bool Ascii(string? value, int min, int max) => ((value is not null) && (value.Length >= min) && (value.Length <= max) && value.All(predicate: static ch => (ch is >= ' ' and <= '~')));
    private void Bank(int[][]? bank, int colors, int limit, string path) {
        if (!Count(
            items: bank,
            max: limit,
            min: 1,
            path: path
        )) {
            return;
        }

        for (var index = 0; (index < bank.Length); ++index) {
            var entry = bank[index];

            if (
                (entry is null) ||
                (entry.Length != colors)
            ) {
                Error(
                    message: $"Expected {colors} RGB555 integers.",
                    path: $"{path}[{index}]"
                );
                continue;
            }

            if (entry.Any(predicate: static color => (color is < 0 or > 32767))) {
                Error(
                    message: "Expected RGB555 integers in 0..32767.",
                    path: $"{path}[{index}]"
                );
            }
        }
    }
    private void Bitmap() {
        if (document.Bitmap is not { } bitmap) {
            return;
        }

        if (document.Target != "agb") {
            Error(
                message: "A per-pixel surface needs the advanced machine; the cgb target draws only from tiles.",
                path: "bitmap"
            );
            return;
        }

        // The surface takes the whole picture, so a tile background declared beside it would never be seen.
        if (
            (document.Window is not null) ||
            (document.Affine is not null) ||
            (document.Layers.Length != 0)
        ) {
            Error(
                message: "A per-pixel surface replaces the tile background, so it cannot share a document with a panel, a turning background or extra layers.",
                path: "bitmap"
            );
        }

        if (bitmap.Clear is not null) {
            Value(
                value: bitmap.Clear,
                path: "bitmap.clear"
            );
        }
    }
    private void Clock() {
        if (document.Clock is not { } clock) {
            return;
        }

        m_clocks = true;

        // The two machines carry different clocks: one counts days since the cartridge was started, the other keeps a
        // calendar date. Naming a field the target's device does not have would silently report a made-up value.
        if (
            (document.Target == "agb") &&
            (clock.Days is not null)
        ) {
            Error(
                message: "The agb clock keeps a calendar date rather than a day count; name day, month and year instead.",
                path: "clock.days"
            );
        }

        foreach (var (value, field) in new[] { (clock.Day, "day"), (clock.Month, "month"), (clock.Year, "year") }) {
            if (
                (value is not null) &&
                (document.Target != "agb")
            ) {
                Error(
                    message: $"A calendar {field} needs the advanced machine; the cgb clock counts days rather than dates.",
                    path: ("clock." + field)
                );
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
                Error(
                    message: $"Unknown state variable '{name}'.",
                    path: $"clock.{field}"
                );
            }
        }

        if (named == 0) {
            Error(
                message: "A clock names at least one state slot to fill.",
                path: "clock"
            );
        }
    }
    private void Comparisons(ActionPredicate? gate, string path) {
        var count = Reached(gate: gate);

        if (count > CartridgeLimits.ConditionCount) {
            Error(
                message: $"A gate reaches at most {CartridgeLimits.ConditionCount} comparisons; this one reaches {count}.",
                path: path
            );
        }
    }
    private void Composite(IReadOnlyList<ActionPredicate> predicates, string path) {
        if (
            (predicates is null) ||
            (predicates.Count == 0)
        ) {
            Error(
                message: "A composed gate needs at least one predicate.",
                path: path
            );
            return;
        }

        for (var index = 0; (index < predicates.Count); ++index) {
            Gate(
                value: predicates[index],
                path: $"{path}[{index}]"
            );
        }
    }
    // A literal wider than the slot it is paired with is a defect rather than an always-false comparison or a silently
    // truncated write, so it is refused where the pairing is known.
    private void ConstantFits(ExpressionProgram? value, int width, string path) {
        if (
            (Literal(value: value) is not { } constant) ||
            (width != 1) ||
            (constant <= CartridgeLimits.NarrowMaximum)
        ) {
            return;
        }

        Error(
            message: $"{constant} does not fit the one-byte slot it is paired with; declare that slot a wider max.",
            path: path
        );
    }
    private bool Count<T>([NotNullWhen(returnValue: true)] T[]? items, string path, int min, int max) {
        if (
            (items is not null) &&
            (items.Length >= min) &&
            (items.Length <= max)
        ) {
            return true;
        }

        Error(
            message: $"Expected an array with {min}..{max} entries.",
            path: path
        );
        return false;
    }
    private void Element(string array, string key, string path) {
        if (!m_arrays.ContainsKey(key: array)) {
            Error(
                path: path,
                message: (m_variables.Contains(item: array)
                ? $"'{array}' is a state slot; it cannot carry an index."
                : $"Unknown array '{array}'.")
            );
        }

        ExpressionProgram? index;

        try {
            index = CartridgeExpressions.Index(key: key);
        } catch (FormatException error) {
            Error(
                path: path,
                message: $"The index of '{array}' does not parse: {error.Message}"
            );
            return;
        }

        if (index is null) {
            Error(
                message: "An array access requires an index.",
                path: path
            );
            return;
        }

        Value(
            path: (path + ".index"),
            value: index
        );
        if (
            (Literal(value: index) is { } literal) &&
            m_arrays.TryGetValue(
            key: array,
            value: out var length
        ) &&
            (literal >= length)
        ) {
            Error(
                message: $"Index {literal} is outside array '{array}' of length {length}.",
                path: (path + ".index")
            );
        }
    }
    private void Error(string path, string message) => m_errors.Add(item: new DocumentValidationError(
        Message: message,
        Path: path
    ));
    // A wide slot is two bytes and the evaluator works a byte at a time, so one is admitted only as a whole operand in
    // the three places that read a pair — a set step's target and value, and a comparison's operands. Inside a
    // composed expression, and in every byte-wide field, it is refused by name rather than truncated to its low half.
    private void Expression(ExpressionProgram? value, string path, bool wide) {
        if (
            (value?.Instructions is null) ||
            (value.Instructions.Count == 0)
        ) {
            Error(
            message: "Supply an expression.",
            path: path
        ); return;
        }
        if (value.Instructions.Count > CartridgeExpressions.MaxTokens) {
            Error(
                path: path,
                message: $"An expression carries at most {CartridgeExpressions.MaxTokens} tokens; this one carries {value.Instructions.Count}."
            );
            return;
        }

        int depth;

        try {
            depth = CartridgeExpressions.Depth(expression: value);
        } catch (ArgumentException error) {
            Error(
                path: path,
                message: error.Message
            );
            return;
        }

        if (depth > CartridgeExpressions.MaxDepth) {
            Error(
                message: $"An expression holds at most {CartridgeExpressions.MaxDepth} values at once; this one holds {depth}.",
                path: path
            );
        }

        var bare = (value.Instructions.Count == 1);

        foreach (var token in value.Instructions) {
            switch (token) {
                case { Payload: InstructionPayload.Constant constant }:
                    if (
                        (!wide || !bare) &&
                        (constant.Value > CartridgeLimits.NarrowMaximum)
                    ) {
                        Error(
                            path: path,
                            message: $"'{constant.Value}' does not fit a byte expression; only a whole operand paired with a wide slot admits more than {CartridgeLimits.NarrowMaximum}."
                        );
                    }
                    if (decimal.Truncate(d: constant.Value) != constant.Value) {
                        Error(
                            path: path,
                            message: $"'{constant.Value}' is not a whole number; a cartridge carries no fraction."
                        );
                    } else if (constant.Value is < 0 or > CartridgeLimits.WideMaximum) {
                        Error(
                            message: $"Expected a value in 0..{CartridgeLimits.WideMaximum}.",
                            path: path
                        );
                    }

                    break;
                case { Payload: InstructionPayload.State state }:
                    Read(
                        path: path,
                        state: state,
                        wide: (wide && bare)
                    );
                    break;
                default:
                    if (ExpressionVocabulary.Operation(instruction: token) is not { } operation) {
                        Error(
                            path: path,
                            message: $"'{CartridgeExpressions.Spell(token: token)}' is not an expression a cartridge evaluates."
                        );
                    } else if (!CartridgeExpressions.Admits(operation: operation)) {
                        Error(
                            path: path,
                            message: $"The rule language evaluates '{ExpressionVocabulary.Spelling(operation: operation)}'; a cartridge does not."
                        );
                    }

                    break;
            }
        }
    }
    private void Gate(ActionPredicate? value, string path) {
        switch (value) {
            case null:
                return;
            case ActionPredicate.All all:
                Composite(
                    predicates: all.Predicates,
                    path: (path + ".predicates")
                );
                return;
            case ActionPredicate.Any any:
                Composite(
                    predicates: any.Predicates,
                    path: (path + ".predicates")
                );
                return;
            case ActionPredicate.Not not:
                Gate(
                    value: not.Predicate,
                    path: (path + ".predicate")
                );
                return;
            case ActionPredicate.CompareValue compare:
                if (compare.Kind != CellKind.Int) {
                    Error(
                        message: "A cartridge compares whole numbers; the fixed-point domain has no representation on either machine.",
                        path: (path + ".kind")
                    );
                }

                WideValue(
                    value: compare.Left,
                    path: (path + ".left")
                );
                WideValue(
                    value: compare.Right,
                    path: (path + ".right")
                );
                ConstantFits(
                    value: compare.Right,
                    width: WidthOf(value: compare.Left),
                    path: (path + ".right")
                );
                ConstantFits(
                    value: compare.Left,
                    width: WidthOf(value: compare.Right),
                    path: (path + ".left")
                );
                return;
            default:
                Error(
                    path: path,
                    message: $"A cartridge gate compares two expressions, or composes those through all, any and not; '{value.GetType().Name}' is not one of them."
                );
                return;
        }
    }
    private void Layers() {
        if (document.Layers.Length == 0) {
            return;
        }

        if (document.Target != "agb") {
            Error(
                message: "Extra scrolling backgrounds need the advanced machine; the cgb target has one background and a panel.",
                path: "layers"
            );
            return;
        }

        // The nearer of the two hardware surfaces is where a turning background lives, so the two cannot both claim it.
        var room = ((document.Affine is null)
            ? CartridgeLimits.LayerCount
            : (CartridgeLimits.LayerCount - 1)
        );

        if (document.Layers.Length > room) {
            Error(
                message: $"A turning background occupies one of the two surfaces, leaving room for {room}.",
                path: "layers"
            );
        }

        for (var index = 0; (index < document.Layers.Length); ++index) {
            var layer = document.Layers[index];
            var path = $"layers[{index}]";

            if (layer is null) {
                Error(
                message: "A layer cannot be null.",
                path: path
            ); continue;
            }
            if (!Count(
                items: layer.Map,
                path: (path + ".map"),
                min: 1024,
                max: 1024
            )) { continue; }
            if (layer.Map.Any(predicate: tile => ((tile < 0) || (tile >= document.Tiles.Length)))) {
                Error(
                    message: "Tile index is outside the authored tile bank.",
                    path: (path + ".map")
                );
            }

            if (layer.MapPalettes is { } cells) {
                var background = (document.Palettes?.Background?.Length ?? 0);

                if (cells.Length != 1024) {
                    Error(
                        message: "Expected 1024 entries, one per cell.",
                        path: (path + ".mapPalettes")
                    );
                } else if (cells.Any(predicate: entry => ((entry < 0) || (entry >= background)))) {
                    Error(
                        message: "A cell names a background palette that is not declared.",
                        path: (path + ".mapPalettes")
                    );
                }
            }

            if (layer.Priority is < 0 or > 3) {
                Error(
                    message: "Expected a priority in 0..3, nearest to furthest.",
                    path: (path + ".priority")
                );
            }

            Value(
                value: layer.ScrollX,
                path: (path + ".scrollX")
            );
            Value(
                value: layer.ScrollY,
                path: (path + ".scrollY")
            );
            Value(
                value: layer.Visible,
                path: (path + ".visible")
            );
        }
    }
    private static int? Literal(ExpressionProgram? value) =>
        (((value?.Instructions is [{ Payload: InstructionPayload.Constant constant }]) && (decimal.Truncate(d: constant.Value) == constant.Value))
            ? (int)constant.Value
            : null
        );
    // One voice part of a music track: a voice named once, its own document, and a waveform exactly when the voice
    // it plays on is the one that needs one.
    private void MusicVoice(CartridgeMusicVoice? part, string path, HashSet<string> voices) {
        if (part is null) {
            Error(
                message: "A voice part cannot be null.",
                path: path
            );

            return;
        }

        if (part.Voice is not (Puck.Assets.Documents.AudioEffectDocument.VoicePulse1
            or Puck.Assets.Documents.AudioEffectDocument.VoiceNoise
            or Puck.Assets.Documents.AudioEffectDocument.VoiceWave
            or Puck.Assets.Documents.AudioEffectDocument.VoicePulse2)) {
            Error(
                message: "Expected pulse1, pulse2, wave or noise.",
                path: (path + ".voice")
            );
        } else if (!voices.Add(item: part.Voice)) {
            Error(
                message: "A track gives each voice at most one part.",
                path: (path + ".voice")
            );
        }

        var wave = (part.Voice == Puck.Assets.Documents.AudioEffectDocument.VoiceWave);

        if (wave != (part.Waveform is not null)) {
            Error(
                message: "A part on the wave voice carries a waveform, and no other voice may.",
                path: (path + ".waveform")
            );
        } else if (
            (part.Waveform is { } pattern) &&
            ((pattern.Length != 32) || pattern.Any(predicate: static level => (level is < 0 or > 15)))
        ) {
            Error(
                message: "Expected 32 four-bit levels in 0..15.",
                path: (path + ".waveform")
            );
        }

        if (part.Part is null) {
            Error(
                message: "A voice part carries a document.",
                path: (path + ".part")
            );

            return;
        }

        foreach (var failure in Puck.Assets.Documents.AudioCanonicalizer.Validate(document: part.Part)) {
            Error(
                path: $"{path}.part.{failure.Path}",
                message: failure.Message
            );
        }
    }
    private void Name(string? name, HashSet<string> names, string path) {
        if (
            string.IsNullOrEmpty(value: name) ||
            (name.Length > 64) ||
            name.Any(predicate: static ch => (!char.IsAsciiLetterOrDigit(c: ch) && (ch is not ('_' or '-')))) ||
            !names.Add(item: name)
        ) {
            Error(
                message: "Use a unique 1..64 character name containing ASCII letters, digits, underscore or hyphen.",
                path: (path + ".name")
            );
        }
    }
    private void Palettes(int colors, int limit) {
        if (document.Palettes is not { } palettes) {
            Error(
                message: "A cartridge declares its palettes.",
                path: "palettes"
            );
            return;
        }

        Bank(
            bank: palettes.Background,
            colors: colors,
            limit: limit,
            path: "palettes.background"
        );
        Bank(
            bank: palettes.Object,
            colors: colors,
            limit: limit,
            path: "palettes.object"
        );
    }
    private void Raster() {
        var previous = -1;

        for (var index = 0; (index < document.Raster.Length); ++index) {
            var row = document.Raster[index];
            var path = $"raster[{index}]";

            if (row is null) {
                Error(
                message: "A raster row cannot be null.",
                path: path
            ); continue;
            }
            if (row.Line is < 1 or > 143) {
                // Line zero is the document's own scroll, so a row there would name a band that has nothing above it.
                Error(
                    message: "Expected a scanline in 1..143.",
                    path: (path + ".line")
                );
            } else if (row.Line <= previous) {
                // The handler walks the rows in order, so an unsorted list would silently skip some of them.
                Error(
                    message: "Rows run in ascending scanline order, each past the one before.",
                    path: (path + ".line")
                );
            }

            previous = row.Line;
            Value(
                value: row.ScrollX,
                path: (path + ".scrollX")
            );
            Value(
                value: row.ScrollY,
                path: (path + ".scrollY")
            );
        }
    }
    private static int Reached(ActionPredicate? gate) => (gate switch {
        null => 0,
        ActionPredicate.All all => (all.Predicates?.Sum(selector: Reached) ?? 0),
        ActionPredicate.Any any => (any.Predicates?.Sum(selector: Reached) ?? 0),
        ActionPredicate.Not not => Reached(gate: not.Predicate),
        _ => 1,
    });
    private void Read(InstructionPayload.State state, string path, bool wide) {
        if (state.Key is not null) {
            Element(
                array: state.Name,
                key: state.Key,
                path: path
            );

            return;
        }

        if (CartridgeExpressions.TryKey(
            name: state.Name,
            button: out var button,
            mode: out var mode
        )) {
            if (button is not ("a" or "b" or "start" or "select" or "up" or "down" or "left" or "right")) {
                Error(
                    message: $"Unknown joypad button '{button}'.",
                    path: path
                );
            }

            if (mode is not ("held" or "pressed" or "released")) {
                Error(
                    message: $"Expected held, pressed or released; found '{mode}'.",
                    path: path
                );
            }

            return;
        }

        if (state.Name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "$"
        )) {
            Error(
                path: path,
                message: $"'{state.Name}' is not a channel a cartridge answers; input reads through '{CartridgeExpressions.KeyPrefix}<button>:<mode>'."
            );

            return;
        }

        if (!m_variables.Contains(item: state.Name)) {
            Error(
                path: path,
                message: $"Unknown state variable '{state.Name}'."
            );

            return;
        }

        if (
            !wide &&
            m_widths.TryGetValue(
            key: state.Name,
            value: out var width
        ) &&
            (width != 1)
        ) {
            Error(
                path: path,
                message: $"'{state.Name}' is a wide slot; this field reads a byte."
            );
        }
    }
    // Every field outside the step's own kind must be absent, so a mistyped kind cannot silently drop authored data.
    private void Reject(CartridgeStatement value, string path, string allowed) {
        foreach (var (name, present) in new[] {
            ("target", (value.Target is not null)), ("operation", (value.Operation is not null)), ("value", (value.Value is not null)),
            ("when", (value.When is not null)), ("then", (value.Then is not null)), ("else", (value.Else is not null)),
            ("count", (value.Count is not null)), ("index", (value.Index is not null)), ("body", (value.Body is not null)),
            ("row", (value.Row is not null)), ("column", (value.Column is not null)), ("tile", (value.Tile is not null)),
            ("screen", (value.Screen is not null)), ("sound", (value.Sound is not null)), ("rate", (value.Rate is not null)),
            ("amount", (value.Amount is not null)), ("toward", (value.Toward is not null)), ("colour", (value.Colour is not null)),
            ("surface", (value.Surface is not null)), ("weight", (value.Weight is not null)),
        }) {
            if (
                present &&
                !allowed.Contains(
                comparisonType: StringComparison.Ordinal,
                value: name
            )
            ) {
                Error(
                    path: $"{path}.{name}",
                    message: $"A {value.Kind} step cannot carry '{name}'."
                );
            }
        }
    }
    private void Rules() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < document.Rules.Length); ++index) {
            var rule = document.Rules[index];
            var path = $"rules[{index}]";

            if (rule is null) {
                Error(
                message: "A rule cannot be null.",
                path: path
            ); continue;
            }
            Name(
                name: rule.Name,
                names: names,
                path: path
            );
            if (!Count(
                items: rule.Body,
                path: (path + ".body"),
                min: 1,
                max: CartridgeLimits.StatementCount
            )) {
                continue;
            }

            Comparisons(
                gate: rule.When,
                path: (path + ".when")
            );
            Gate(
                value: rule.When,
                path: (path + ".when")
            );

            if (Steps(statements: rule.Body) > CartridgeLimits.StatementCount) {
                Error(
                    message: $"A rule body holds at most {CartridgeLimits.StatementCount} steps in total.",
                    path: (path + ".body")
                );
            }

            Statements(
                statements: rule.Body,
                path: (path + ".body"),
                depth: 0,
                inLoop: false
            );
        }
    }
    private void Save() {
        if (document.Save is not { } save) {
            return;
        }

        m_saves = true;
        if (save.Version is < 0 or > 255) {
            Error(
                message: "Expected a version byte in 0..255.",
                path: "save.version"
            );
        }

        var payload = 0;
        var named = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var name in (save.Variables ?? [])) {
            if (!m_variables.Contains(item: name)) {
                Error(
                    message: $"Unknown state variable '{name}'.",
                    path: "save.variables"
                );
            } else if (!named.Add(item: name)) {
                Error(
                    message: $"'{name}' is persisted more than once.",
                    path: "save.variables"
                );
            } else {
                payload += m_widths.GetValueOrDefault(
                    defaultValue: 1,
                    key: name
                );
            }
        }

        foreach (var name in (save.Arrays ?? [])) {
            if (!m_arrays.TryGetValue(
                key: name,
                value: out var length
            )) {
                Error(
                    message: $"Unknown array '{name}'.",
                    path: "save.arrays"
                );
            } else if (!named.Add(item: name)) {
                Error(
                    message: $"'{name}' is persisted more than once.",
                    path: "save.arrays"
                );
            } else {
                payload += length;
            }
        }

        if (payload == 0) {
            Error(
                message: "A save declares at least one variable or array.",
                path: "save"
            );
        }

        if (payload > CartridgeLimits.SaveByteCount) {
            Error(
                message: $"The save payload is {payload} bytes; the battery-backed mirror holds {CartridgeLimits.SaveByteCount}.",
                path: "save"
            );
        }
    }
    // The declared scene names an ordinary variable. Nothing else is asked of it: which values are scenes is decided by
    // the rules that guard on it, and a value no rule guards is simply a frame in which only the ungated rules run.
    private void Scene() {
        if (document.Scene is not { } scene) {
            return;
        }

        if (!m_variables.Contains(item: scene)) {
            Error(
                message: $"'{scene}' names no declared variable.",
                path: "scene"
            );

            return;
        }

        // The frame's snapshot is one byte.
        if (
            m_widths.TryGetValue(
            key: scene,
            value: out var width
        ) &&
            (width != 1)
        ) {
            Error(
                message: $"'{scene}' is a wide slot; the frame's scene snapshot is one byte.",
                path: "scene"
            );
        }
    }
    private void Screens() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < document.Screens.Length); ++index) {
            var screen = document.Screens[index];
            var path = $"screens[{index}]";

            if (screen is null) {
                Error(
                message: "A screen cannot be null.",
                path: path
            ); continue;
            }
            Name(
                name: screen.Name,
                names: names,
                path: path
            );
            if (screen.Width is < 1 or > 32) {
                Error(
                    message: "Expected a width in 1..32.",
                    path: (path + ".width")
                );
                continue;
            }

            if (
                (screen.Tiles is null) ||
                (screen.Tiles.Length == 0) ||
                ((screen.Tiles.Length % screen.Width) != 0)
            ) {
                Error(
                    message: "Expected a whole number of rows of width entries.",
                    path: (path + ".tiles")
                );
                continue;
            }

            if ((screen.Tiles.Length / screen.Width) > 32) {
                Error(
                    message: "A screen is at most 32 rows tall.",
                    path: (path + ".tiles")
                );
                continue;
            }

            if (screen.Tiles.Any(predicate: tile => ((tile < 0) || (tile >= document.Tiles.Length)))) {
                Error(
                    message: "Tile index is outside the authored tile bank.",
                    path: (path + ".tiles")
                );
            }

            if (screen.Palettes is { } shades) {
                if (shades.Length != screen.Tiles.Length) {
                    Error(
                        message: "Expected one palette index per tile.",
                        path: (path + ".palettes")
                    );
                } else if (shades.Any(predicate: shade => ((shade < 0) || (shade >= (document.Palettes?.Background?.Length ?? 0))))) {
                    Error(
                        message: "A tile names a background palette that is not declared.",
                        path: (path + ".palettes")
                    );
                }
            }

            m_screens[key: screen.Name] = (screen.Width, (screen.Tiles.Length / screen.Width));
        }
    }
    // The single-token forms: a bare slot read, and a bare literal. Both are what a pairing rule is stated against.
    private static string? Slot(ExpressionProgram? value) =>
        ((value?.Instructions is [{ Payload: InstructionPayload.State { Key: null } state }])
            ? state.Name
            : null
        );
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

        for (var index = 0; (index < document.Sounds.Length); ++index) {
            var sound = document.Sounds[index];
            var path = $"sounds[{index}]";

            if (sound is null) {
                Error(
                message: "A sound cannot be null.",
                path: path
            ); continue;
            }
            Name(
                name: sound.Name,
                names: names,
                path: path
            );
            var supplied = ((((sound.Music is not null)
                ? 1
                : 0) + ((sound.Effect is not null)
                ? 1
                : 0)) + ((sound.Sample is not null)
                ? 1
                : 0));

            if (supplied != 1) {
                Error(
                    message: "Supply exactly one of music, effect or sample.",
                    path: path
                );
                continue;
            }

            if (
                (sound.Waveform is not null) &&
                (sound.Effect is null)
            ) {
                Error(
                    message: "Only an effect on the wave voice carries a waveform.",
                    path: (path + ".waveform")
                );
                continue;
            }

            if (sound.Sample is { } pcm) {
                if (document.Target != "agb") {
                    Error(
                        message: "Recorded sound needs the advanced machine's digital sound path; the cgb target has none.",
                        path: (path + ".sample")
                    );
                    continue;
                }

                if (!Count(
                    items: pcm,
                    max: CartridgeLimits.SampleLength,
                    min: 1,
                    path: (path + ".sample")
                )) {
                    continue;
                }

                if (pcm.Any(predicate: static value => (value is < -128 or > 127))) {
                    Error(
                        message: "Expected signed eight-bit samples in -128..127.",
                        path: (path + ".sample")
                    );
                }

                if (sound.Frames is not null) {
                    Error(
                        message: "A recorded one-shot plays at the mixer's own rate.",
                        path: (path + ".frames")
                    );
                }

                m_sounds.Add(item: sound.Name);
                m_recordings.Add(item: sound.Name);
                continue;
            }

            if (sound.Music is { } parts) {
                if (sound.Frames is not null) {
                    Error(
                        message: "A music track takes its pacing from each part's own tempo.",
                        path: (path + ".frames")
                    );
                }

                if (parts.Length is < 1 or > CartridgeLimits.SoundVoiceCount) {
                    Error(
                        message: $"A track carries 1 to {CartridgeLimits.SoundVoiceCount} voice parts.",
                        path: (path + ".music")
                    );
                    continue;
                }

                var voices = new HashSet<string>(comparer: StringComparer.Ordinal);

                for (var part = 0; (part < parts.Length); ++part) {
                    MusicVoice(
                        part: parts[part],
                        path: $"{path}.music[{part}]",
                        voices: voices
                    );
                }
            }

            if (sound.Effect is { } effect) {
                var wave = (effect.Voice == Puck.Assets.Documents.AudioEffectDocument.VoiceWave);

                if (effect.Voice is not (Puck.Assets.Documents.AudioEffectDocument.VoicePulse1 or Puck.Assets.Documents.AudioEffectDocument.VoiceNoise or Puck.Assets.Documents.AudioEffectDocument.VoiceWave)) {
                    Error(
                        message: "Expected pulse1, noise or wave.",
                        path: (path + ".effect.voice")
                    );
                } else if (occupied.Contains(item: effect.Voice!)) {
                    Error(
                        path: (path + ".effect.voice"),
                        message: $"The {effect.Voice} voice carries a part of this cartridge's music; an effect there would cut it off."
                    );
                } else if (wave != (sound.Waveform is not null)) {
                    Error(
                        message: "A wave effect carries a waveform, and no other voice may.",
                        path: (path + ".waveform")
                    );
                } else if (
                    (sound.Waveform is { } pattern) &&
                    ((pattern.Length != 32) || pattern.Any(predicate: static level => (level is < 0 or > 15)))
                ) {
                    Error(
                        message: "Expected 32 four-bit levels in 0..15.",
                        path: (path + ".waveform")
                    );
                }

                if (effect.Rows is not { Count: > 0 }) {
                    Error(
                        message: "An effect carries at least one row.",
                        path: (path + ".effect.rows")
                    );
                }

                if (
                    (sound.Frames is not { } frames) ||
                    (frames is < 1 or > 255)
                ) {
                    Error(
                        message: "Expected a per-row frame count in 1..255.",
                        path: (path + ".frames")
                    );
                }
            }

            m_sounds.Add(item: sound.Name);
        }
    }
    private void Statement(CartridgeStatement? value, string path, int depth, bool inLoop) {
        if (value is null) {
            Error(
            message: "A step cannot be null.",
            path: path
        ); return;
        }
        switch (value.Kind) {
            case "set":
                Reject(
                    allowed: "target operation value",
                    path: path,
                    value: value
                );
                Action(
                    path: path,
                    value: value
                );
                break;
            case "if":
                Reject(
                    allowed: "when then else",
                    path: path,
                    value: value
                );
                if (!Count(
                    items: value.Then,
                    path: (path + ".then"),
                    min: 1,
                    max: CartridgeLimits.StatementCount
                )) {
                    break;
                }

                Comparisons(
                    gate: value.When,
                    path: (path + ".when")
                );
                Gate(
                    value: value.When,
                    path: (path + ".when")
                );

                Statements(
                    statements: value.Then,
                    path: (path + ".then"),
                    depth: (depth + 1),
                    inLoop: inLoop
                );
                if (
                    (value.Else is not null) &&
                    Count(
                    items: value.Else,
                    path: (path + ".else"),
                    min: 1,
                    max: CartridgeLimits.StatementCount
                )
                ) {
                    Statements(
                        statements: value.Else,
                        path: (path + ".else"),
                        depth: (depth + 1),
                        inLoop: inLoop
                    );
                }

                break;
            case "repeat":
                Reject(
                    allowed: "count index body",
                    path: path,
                    value: value
                );
                if (
                    (value.Count is not { } count) ||
                    (count < 1) ||
                    (count > CartridgeLimits.RepeatCount)
                ) {
                    Error(
                        message: $"Expected a literal iteration count in 1..{CartridgeLimits.RepeatCount}.",
                        path: (path + ".count")
                    );
                }

                if (value.Index is not { } loopIndex) {
                    Error(
                        message: "A repeat requires an index variable.",
                        path: (path + ".index")
                    );
                } else if (!m_variables.Contains(item: loopIndex)) {
                    Error(
                        message: $"Unknown state variable '{loopIndex}'.",
                        path: (path + ".index")
                    );
                }

                if (Count(
                    items: value.Body,
                    path: (path + ".body"),
                    min: 1,
                    max: CartridgeLimits.StatementCount
                )) {
                    Statements(
                        statements: value.Body,
                        path: (path + ".body"),
                        depth: (depth + 1),
                        inLoop: true
                    );
                }

                break;
            case "map":
                Reject(
                    allowed: "row column tile palette",
                    path: path,
                    value: value
                );
                Value(
                    value: value.Row,
                    path: (path + ".row")
                );
                Value(
                    value: value.Column,
                    path: (path + ".column")
                );
                Value(
                    value: value.Tile,
                    path: (path + ".tile")
                );
                if (value.Palette is { } cellPalette) {
                    Value(
                        path: (path + ".palette"),
                        value: cellPalette
                    );
                    if (Literal(value: cellPalette) >= (document.Palettes?.Background?.Length ?? 0)) {
                        Error(
                            message: "The write names a background palette that is not declared.",
                            path: (path + ".palette")
                        );
                    }
                }

                break;
            case "blit":
                Reject(
                    allowed: "screen row column",
                    path: path,
                    value: value
                );
                if (
                    (value.Screen is not { } screen) ||
                    !m_screens.TryGetValue(
                    key: screen,
                    value: out var size
                )
                ) {
                    Error(
                        path: (path + ".screen"),
                        message: $"Unknown screen '{value.Screen}'."
                    );
                    break;
                }

                // A blit paints with the display off, so its destination is fixed at build time rather than sampled.
                if (
                    (Literal(value: value.Row) is not { } row) ||
                    (Literal(value: value.Column) is not { } column)
                ) {
                    Error(
                        message: "A blit takes a literal row and column.",
                        path: path
                    );
                    break;
                }

                if (
                    ((row + size.Height) > 32) ||
                    ((column + size.Width) > 32)
                ) {
                    Error(
                        message: $"Screen '{screen}' at row {row}, column {column} runs past the 32 by 32 map.",
                        path: path
                    );
                }

                if ((size.Width * size.Height) > CartridgeLimits.BlitCellCount) {
                    Error(
                        message: $"Screen '{screen}' covers {(size.Width * size.Height)} cells; a blit paints at most {CartridgeLimits.BlitCellCount} before the display-off window costs frames.",
                        path: (path + ".screen")
                    );
                }

                break;
            case "play":
                Reject(
                    allowed: "sound rate",
                    path: path,
                    value: value
                );
                if (
                    (value.Sound is not { } track) ||
                    !m_sounds.Contains(item: track)
                ) {
                    Error(
                        path: (path + ".sound"),
                        message: $"Unknown sound '{value.Sound}'."
                    );
                } else if (
                    (value.Rate is not null) &&
                    !m_recordings.Contains(item: track)
                ) {
                    // Only a recording is resampled; the melodic voices take their pitch from the track's own rows.
                    Error(
                        message: $"Sound '{track}' is not a recording, so it has no playback rate.",
                        path: (path + ".rate")
                    );
                }

                if (value.Rate is not null) {
                    Value(
                        value: value.Rate,
                        path: (path + ".rate")
                    );
                }

                break;
            case "stop":
                Reject(
                    allowed: "",
                    path: path,
                    value: value
                );
                if (m_sounds.Count == 0) {
                    Error(
                        message: "A stop step requires a declared sound.",
                        path: path
                    );
                }

                break;
            case "plot":
                Reject(
                    allowed: "row column colour",
                    path: path,
                    value: value
                );
                if (document.Bitmap is null) {
                    Error(
                        message: "A plot step requires a declared bitmap.",
                        path: path
                    );
                }

                Value(
                    value: value.Row,
                    path: (path + ".row")
                );
                Value(
                    value: value.Column,
                    path: (path + ".column")
                );
                Value(
                    value: value.Colour,
                    path: (path + ".colour")
                );
                break;
            case "blend":
                Reject(
                    allowed: "surface weight",
                    path: path,
                    value: value
                );
                if (document.Target != "agb") {
                    // The Color machine has no blend unit; its fade works by rebaking palettes, which cannot mix two
                    // surfaces because a palette entry knows nothing about what is drawn beneath it.
                    Error(
                        message: "Blending two surfaces is an agb capability; the cgb target has no blend unit.",
                        path: path
                    );
                }

                if (
                    (value.Surface is null) ||
                    (Array.IndexOf(
                    array: CartridgeLimits.BlendSurfaces,
                    value: value.Surface
                ) < 0)
                ) {
                    Error(
                        message: "Expected background, panel, middle, far, sprites or backdrop.",
                        path: (path + ".surface")
                    );
                } else if (
                    (value.Surface == "panel") &&
                    (document.Window is null)
                ) {
                    Error(
                        message: "Blending the panel needs a declared window.",
                        path: (path + ".surface")
                    );
                } else if (
                    (value.Surface == "middle") &&
                    (document.Affine is null) &&
                    (document.Layers.Length < 1)
                ) {
                    Error(
                        message: "Blending the middle surface needs a turning background or a declared layer.",
                        path: (path + ".surface")
                    );
                } else if (
                    (value.Surface == "far") &&
                    (document.Layers.Length < ((document.Affine is null)
                    ? 2
                    : 1))
                ) {
                    Error(
                        message: "Blending the far surface needs a layer behind the middle one.",
                        path: (path + ".surface")
                    );
                }

                Value(
                    value: value.Weight,
                    path: (path + ".weight")
                );
                if (Literal(value: value.Weight) > CartridgeLimits.BlendWeights) {
                    Error(
                        message: $"A blend weight runs 0 through {CartridgeLimits.BlendWeights}.",
                        path: (path + ".weight")
                    );
                }

                break;
            case "fade":
                Reject(
                    allowed: "amount toward",
                    path: path,
                    value: value
                );
                Value(
                    value: value.Amount,
                    path: (path + ".amount")
                );
                if (Literal(value: value.Amount) > CartridgeLimits.FadeSteps) {
                    Error(
                        message: $"A fade runs 0 through {CartridgeLimits.FadeSteps}.",
                        path: (path + ".amount")
                    );
                }

                if (value.Toward is not ("black" or "white")) {
                    Error(
                        message: "Expected black or white.",
                        path: (path + ".toward")
                    );
                }

                break;
            case "clock":
                Reject(
                    allowed: "",
                    path: path,
                    value: value
                );
                if (!m_clocks) {
                    Error(
                        message: "A clock step requires a declared clock.",
                        path: path
                    );
                }

                break;
            case "save":
            case "load":
                Reject(
                    allowed: "",
                    path: path,
                    value: value
                );
                if (!m_saves) {
                    Error(
                        path: path,
                        message: $"A {value.Kind} step requires a declared save."
                    );
                }

                break;
            case "break":
                Reject(
                    allowed: "",
                    path: path,
                    value: value
                );
                if (!inLoop) {
                    Error(
                        message: "A break must sit inside a repeat.",
                        path: path
                    );
                }

                break;
            default:
                Error(
                message: "Expected set, if, repeat, break, map, blit, plot, save, load, play, stop, clock, fade or blend.",
                path: (path + ".kind")
            ); break;
        }
    }
    private void Statements(CartridgeStatement[]? statements, string path, int depth, bool inLoop) {
        if (statements is null) {
            return;
        }

        if (depth > CartridgeLimits.StatementDepth) {
            Error(
                message: $"Steps nest at most {CartridgeLimits.StatementDepth} deep.",
                path: path
            );
            return;
        }

        for (var index = 0; (index < statements.Length); ++index) {
            Statement(
                value: statements[index],
                path: $"{path}[{index}]",
                depth: depth,
                inLoop: inLoop
            );
        }
    }
    private static int Steps(CartridgeStatement[]? statements) {
        if (statements is null) {
            return 0;
        }

        var total = 0;

        foreach (var statement in statements) {
            total += (((1 + Steps(statements: statement?.Then)) + Steps(statements: statement?.Else)) + Steps(statements: statement?.Body));
        }

        return total;
    }
    private void Target(CartridgeTarget? target, string path) {
        if (target is null) {
            Error(
            message: "Supply a target state or array element.",
            path: path
        ); return;
        }
        if (target.Key is null) {
            if (m_arrays.ContainsKey(key: target.State)) {
                Error(
                    path: path,
                    message: $"'{target.State}' is an array; a write to it requires an index."
                );
            } else if (!m_variables.Contains(item: target.State)) {
                Error(
                    path: (path + ".state"),
                    message: $"Unknown state variable '{target.State}'."
                );
            }

            return;
        }

        Element(
            array: target.State,
            key: target.Key,
            path: path
        );
    }
    private void Value(ExpressionProgram? value, string path) => Expression(
        path: path,
        value: value,
        wide: false
    );
    private void WideValue(ExpressionProgram? value, string path) => Expression(
        path: path,
        value: value,
        wide: true
    );
    // How many bytes an operand occupies: a bare slot read carries the slot's own width, and everything else is a
    // byte (an array element, a literal, a computed result). An undeclared name is reported elsewhere and reads narrow.
    private int WidthOf(ExpressionProgram? value) =>
        (((Slot(value: value) is { } name) && m_widths.TryGetValue(
            key: name,
            value: out var width
        ))
            ? width
            : 1
        );
    private void Window() {
        if (document.Window is not { } window) {
            return;
        }

        if (!Count(
            items: window.Map,
            path: "window.map",
            min: 1024,
            max: 1024
        )) {
            return;
        }

        if (window.Map.Any(predicate: tile => ((tile < 0) || (tile >= document.Tiles.Length)))) {
            Error(
                message: "Tile index is outside the authored tile bank.",
                path: "window.map"
            );
        }

        if (window.MapPalettes is { } cells) {
            var background = (document.Palettes?.Background?.Length ?? 0);

            if (cells.Length != 1024) {
                Error(
                    message: "Expected 1024 entries, one per cell.",
                    path: "window.mapPalettes"
                );
            } else if (cells.Any(predicate: entry => ((entry < 0) || (entry >= background)))) {
                Error(
                    message: "A cell names a background palette that is not declared.",
                    path: "window.mapPalettes"
                );
            }
        }

        Value(
            value: window.X,
            path: "window.x"
        );
        Value(
            value: window.Y,
            path: "window.y"
        );
        Value(
            value: window.Visible,
            path: "window.visible"
        );
    }

    public IReadOnlyList<DocumentValidationError> Run() {
        if (DocumentCanonicalizer.SchemaViolationMessage(
            declared: document.Schema,
            recognized: CartridgeDocument.SchemaId
        ) is { } schemaError) {
            Error(
                message: schemaError,
                path: "schema"
            );
            return m_errors;
        }
        if (document.Target is not ("cgb" or "agb")) {
            Error(
                message: "Expected cgb or agb.",
                path: "target"
            );
        }

        if (!Ascii(
            value: document.Title,
            min: 1,
            max: 12
        )) {
            Error(
                message: "Use 1 to 12 printable ASCII characters.",
                path: "title"
            );
        }

        if (!Ascii(
            value: document.GameCode,
            min: 4,
            max: 4
        )) {
            Error(
                message: "Use exactly four printable ASCII characters.",
                path: "gameCode"
            );
        }

        var colors = ((document.Target == "agb")
            ? 16
            : 4
        );
        var paletteCount = ((document.Target == "agb")
            ? CartridgeLimits.AdvancedPaletteCount
            : CartridgeLimits.HumblePaletteCount
        );

        Palettes(
            colors: colors,
            limit: paletteCount
        );
        if (
            !Count(
            items: document.Tiles,
            path: "tiles",
            min: 1,
            max: CartridgeLimits.TileCount
        ) ||
            !Count(
            items: document.Map,
            path: "map",
            min: 1024,
            max: 1024
        ) ||
            !Count(
            items: document.Variables,
            path: "variables",
            min: 0,
            max: CartridgeLimits.VariableCount
        ) ||
            !Count(
            items: document.Arrays,
            path: "arrays",
            min: 0,
            max: CartridgeLimits.ArrayCount
        ) ||
            !Count(
            items: document.Screens,
            path: "screens",
            min: 0,
            max: CartridgeLimits.ScreenCount
        ) ||
            !Count(
            items: document.Sounds,
            path: "sounds",
            min: 0,
            max: CartridgeLimits.SoundCount
        ) ||
            !Count(
            items: document.Raster,
            path: "raster",
            min: 0,
            max: CartridgeLimits.RasterRowCount
        ) ||
            !Count(
            items: document.Layers,
            path: "layers",
            min: 0,
            max: CartridgeLimits.LayerCount
        ) ||
            !Count(
            items: document.Rules,
            path: "rules",
            min: 0,
            max: CartridgeLimits.RuleCount
        ) ||
            !Count(
            items: document.Sprites,
            path: "sprites",
            min: 0,
            max: CartridgeLimits.SpriteCount
        )
        ) {
            return m_errors;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < document.Tiles.Length); ++index) {
            var tile = document.Tiles[index];
            var path = $"tiles[{index}]";

            if (tile is null) {
                Error(
                message: "A tile cannot be null.",
                path: path
            ); continue;
            }
            Name(
                name: tile.Name,
                names: names,
                path: path
            );
            if (
                (tile.Pixels is not { Length: 8 }) ||
                tile.Pixels.Any(predicate: row => ((row is not { Length: 8 }) || row.Any(predicate: digit => ((Digit(digit: digit) is var value) && ((value < 0) || (value >= colors))))))
            ) {
                Error(
                    message: $"Use eight rows of eight hexadecimal palette indices below {colors}.",
                    path: (path + ".pixels")
                );
            }
        }
        for (var index = 0; (index < document.Map.Length); ++index) {
            if (
                (document.Map[index] < 0) ||
                (document.Map[index] >= document.Tiles.Length)
            ) {
                Error(
                    message: "Tile index is outside the authored tile bank.",
                    path: $"map[{index}]"
                );
            }
        }
        if (document.MapPalettes is { } cells) {
            var background = (document.Palettes?.Background?.Length ?? 0);

            if (cells.Length != 1024) {
                Error(
                    message: "Expected 1024 entries, one per map cell.",
                    path: "mapPalettes"
                );
            } else if (cells.Any(predicate: entry => ((entry < 0) || (entry >= background)))) {
                Error(
                    message: "A cell names a background palette that is not declared.",
                    path: "mapPalettes"
                );
            }
        }
        // A slot spends one or two bytes of the variable window depending on its declared ceiling, so the window is
        // what a wide document runs out of rather than the slot count.
        var windowBytes = 0;

        for (var index = 0; (index < document.Variables.Length); ++index) {
            var variable = document.Variables[index];

            if (variable is null) {
                Error(
                message: "A variable cannot be null.",
                path: $"variables[{index}]"
            ); continue;
            }
            Name(
                name: variable.Name,
                names: m_variables,
                path: $"variables[{index}]"
            );
            if (
                (variable.Max is { } ceiling) &&
                (ceiling is < 1 or > CartridgeLimits.WideMaximum)
            ) {
                Error(
                    message: $"Expected a ceiling in 1..{CartridgeLimits.WideMaximum}.",
                    path: $"variables[{index}].max"
                );

                continue;
            }

            if (
                (variable.Initial < 0) ||
                (variable.Initial > variable.Ceiling)
            ) {
                Error(
                    path: $"variables[{index}].initial",
                    message: $"Expected a value in 0..{variable.Ceiling}."
                );
            }

            m_widths[variable.Name] = variable.Width;
            windowBytes += variable.Width;
        }

        if (windowBytes > CartridgeLimits.VariableCount) {
            Error(
                message: $"The declared slots need {windowBytes} bytes; the variable window holds {CartridgeLimits.VariableCount}.",
                path: "variables"
            );
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

            for (var index = 0; (index < document.Rules.Length); ++index) {
                guards[index] = CartridgeCost.Guard(rule: document.Rules[index]);
            }

            var exclusive = CartridgeCost.ExclusiveGuards(
                rules: document.Rules,
                guards: guards,
                scene: document.Scene,
                document: document
            );
            var writes = 0;
            var arms = new Dictionary<string, Dictionary<int, int>>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < document.Rules.Length); ++index) {
                var count = CartridgeEffects.MapWrites(statements: document.Rules[index].Body);

                if (
                    (guards[index].Name is not { } name) ||
                    !exclusive.Contains(item: name)
                ) {
                    writes = CartridgeEffects.AddMapWrites(
                        left: writes,
                        right: count
                    );
                    continue;
                }

                if (!arms.TryGetValue(
                    key: name,
                    value: out var byValue
                )) {
                    byValue = [];
                    arms[key: name] = byValue;
                }

                byValue[key: guards[index].Value] = CartridgeEffects.AddMapWrites(
                    left: byValue.GetValueOrDefault(key: guards[index].Value),
                    right: count
                );
            }

            foreach (var byValue in arms.Values) {
                writes = CartridgeEffects.AddMapWrites(
                    left: writes,
                    right: byValue.Values.Max()
                );
            }

            if (writes > CartridgeLimits.MapWriteCount) {
                Error(
                    message: $"A frame can execute at least {writes} map writes against a queue of {CartridgeLimits.MapWriteCount}; gate the redraws behind branches or spread them across frames.",
                    path: "rules"
                );
            }
        }

        names.Clear();
        for (var index = 0; (index < document.Sprites.Length); ++index) {
            var sprite = document.Sprites[index];
            var path = $"sprites[{index}]";

            if (sprite is null) {
                Error(
                message: "A sprite cannot be null.",
                path: path
            ); continue;
            }
            Name(
                name: sprite.Name,
                names: names,
                path: path
            );
            Value(
                value: sprite.Tile,
                path: (path + ".tile")
            );
            if (Literal(value: sprite.Tile) >= document.Tiles.Length) {
                Error(
                    message: "Tile is outside the authored bank.",
                    path: (path + ".tile")
                );
            }

            Value(
                value: sprite.X,
                path: (path + ".x")
            ); Value(
                value: sprite.Y,
                path: (path + ".y")
            ); Value(
                value: sprite.Visible,
                path: (path + ".visible")
            );
            foreach (var (flag, name) in new[] { (sprite.FlipX, "flipX"), (sprite.FlipY, "flipY"), (sprite.BehindBackground, "behindBackground") }) {
                if (flag is not null) {
                    Value(
                        path: $"{path}.{name}",
                        value: flag
                    );
                }
            }

            if (sprite.Turn is { } turn) {
                if (document.Target != "agb") {
                    Error(
                        message: "Turning an object needs the advanced machine; the cgb target can only mirror one.",
                        path: (path + ".turn")
                    );
                } else {
                    Value(
                        path: (path + ".turn"),
                        value: turn
                    );
                }
            }

            if (sprite.Palette is { } slot) {
                Value(
                    path: (path + ".palette"),
                    value: slot
                );
                if (Literal(value: slot) >= (document.Palettes?.Object?.Length ?? 0)) {
                    Error(
                        message: "Sprite names an object palette that is not declared.",
                        path: (path + ".palette")
                    );
                }
            }
        }
        Value(
            value: document.ScrollX,
            path: "scrollX"
        ); Value(
            value: document.ScrollY,
            path: "scrollY"
        );
        return m_errors;
    }
}
