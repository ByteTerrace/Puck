using System.Globalization;
using Puck.Text;

namespace Puck.Cli.FontAtlas;

// The Puck-owned font authoring surface. The production generator and artifact writer live in Puck.Text; this verb
// owns only argument parsing, file paths, and operator-facing diagnostics.
internal static class FontAtlasCommand {
    private const string HelpText =
        """
        puck font-atlas — generate a loader-compatible MTSDF atlas from a font

        Usage: puck font-atlas [options] <font-file>

        Options:
          -o, --output <path>       output JSON path; the PNG uses the same base name
                                    (default: <font-name>.sdf.json in the current directory)
          -r, --range <token>       included Unicode range; repeatable, using U+0020-U+007E,
                                    U+E0A0, or * for the Basic Multilingual Plane
          --characters <text>       additional non-whitespace Unicode scalars to include
          --face-index <index>      zero-based face in a TTC/OTC collection (default: 0)
          --size <pixels>           raster em size (default: 32)
          --distance-range <px>     signed-distance band width (default: 8)
          --padding <pixels>        cell padding; must cover the distance range (default: 8)
          --columns <count>         preferred grid columns (default: 16)
          --max-dimension <pixels>  maximum atlas side (default: 16384)
          --max-pixels <count>      maximum total atlas pixels (default: 67108864)
          -h, --help                this text

        The generator is fully managed and deterministic. It accepts TrueType quadratic,
        CFF, and CFF2 outlines in standalone OpenType fonts and TTC/OTC collections. It
        does not use an installed system font, native rasterizer, shaping engine, or Python.

        Exit codes: 0 generated, 1 font or I/O failure, 2 usage error.
        """;

    private static int UsageError(string message) {
        Console.Error.WriteLine(value: $"font-atlas: {message}");
        Console.Error.WriteLine(value: "Run 'puck font-atlas --help' for usage.");

        return 2;
    }

    public static int Run(string[] args) {
        var scanner = new ArgScanner()
            .Flag(name: "h")
            .Flag(name: "help")
            .Value(name: "o")
            .Value(name: "output")
            .Value(name: "r")
            .Value(name: "range")
            .Value(name: "characters")
            .Value(name: "face-index")
            .Value(name: "size")
            .Value(name: "distance-range")
            .Value(name: "padding")
            .Value(name: "columns")
            .Value(name: "max-dimension")
            .Value(name: "max-pixels");

        if (!scanner.Parse(args: args)) {
            return UsageError(message: scanner.Error!);
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        if (scanner.Positionals.Count != 1) {
            return UsageError(message: "expected exactly one source font path.");
        }

        var fontPath = Path.GetFullPath(path: scanner.Positionals[0]);

        if (!File.Exists(path: fontPath)) {
            Console.Error.WriteLine(value: $"font-atlas: source font not found: {fontPath}");

            return 1;
        }

        var outputArgument = (scanner.Get(name: "output") ?? scanner.Get(name: "o"));
        var outputPath = Path.GetFullPath(path: (outputArgument ?? $"{Path.GetFileNameWithoutExtension(path: fontPath)}.sdf.json"));
        var ranges = scanner.GetAll(name: "range").Concat(second: scanner.GetAll(name: "r")).ToArray();
        var options = new FontAtlasGenerationOptions();

        if (ranges.Length > 0) {
            options.AllowedCodePointRanges = ranges;
        }

        options.AllowedCharacters = (scanner.Get(name: "characters") ?? string.Empty);

        if (!TryApplyInt(scanner: scanner, name: "face-index", setter: value => options.FaceIndex = value) ||
            !TryApplyInt(scanner: scanner, name: "size", setter: value => options.FontPixelSize = value) ||
            !TryApplyFloat(scanner: scanner, name: "distance-range", setter: value => options.DistanceRange = value) ||
            !TryApplyInt(scanner: scanner, name: "padding", setter: value => options.Padding = value) ||
            !TryApplyInt(scanner: scanner, name: "columns", setter: value => options.Columns = value) ||
            !TryApplyInt(scanner: scanner, name: "max-dimension", setter: value => options.MaxAtlasDimension = value) ||
            !TryApplyLong(scanner: scanner, name: "max-pixels", setter: value => options.MaxAtlasPixels = value)) {
            return 2;
        }

        try {
            var atlas = new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
                FontBytes = File.ReadAllBytes(path: fontPath),
                FontIdentifier = fontPath,
                ImageIdentifier = Path.ChangeExtension(extension: ".png", path: outputPath),
                Options = options,
            });

            FontAtlasArtifactWriter.Write(atlas: atlas, jsonPath: outputPath);
            Console.Out.WriteLine(value: $"font-atlas: wrote {outputPath} and {Path.ChangeExtension(extension: ".png", path: outputPath)} ({atlas.Glyphs.Count} glyphs, {atlas.Width}x{atlas.Height}).");

            return 0;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"font-atlas: {exception.Message}");

            return 1;
        }
    }

    private static bool TryApplyFloat(ArgScanner scanner, string name, Action<float> setter) {
        var text = scanner.Get(name: name);

        if (text is null) {
            return true;
        }

        if (!float.TryParse(s: text, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var value) || !float.IsFinite(f: value)) {
            Console.Error.WriteLine(value: $"font-atlas: --{name} requires a finite decimal number.");

            return false;
        }

        setter(obj: value);
        return true;
    }
    private static bool TryApplyInt(ArgScanner scanner, string name, Action<int> setter) {
        var text = scanner.Get(name: name);

        if (text is null) {
            return true;
        }

        if (!int.TryParse(s: text, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var value)) {
            Console.Error.WriteLine(value: $"font-atlas: --{name} requires a whole number.");

            return false;
        }

        setter(obj: value);
        return true;
    }
    private static bool TryApplyLong(ArgScanner scanner, string name, Action<long> setter) {
        var text = scanner.Get(name: name);

        if (text is null) {
            return true;
        }

        if (!long.TryParse(s: text, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var value)) {
            Console.Error.WriteLine(value: $"font-atlas: --{name} requires a whole number.");

            return false;
        }

        setter(obj: value);
        return true;
    }
}
