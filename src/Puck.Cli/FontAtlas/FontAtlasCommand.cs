using System.CommandLine;
using Puck.Text;

namespace Puck.Cli.FontAtlas;

// The Puck-owned font authoring surface. The production generator and artifact writer live in Puck.Text; this verb
// owns only argument binding, file paths, and operator-facing diagnostics.
internal static class FontAtlasCommand {
    public static Command Create() {
        var charactersOption = new Option<string>(name: "--characters") { DefaultValueFactory = static _ => string.Empty, Description = "Additional non-whitespace Unicode scalars to include." };
        var columnsOption = new Option<int?>(name: "--columns") { Description = "Preferred grid columns (default: 16)." };
        var distanceRangeOption = new Option<float?>(name: "--distance-range") { Description = "Signed-distance band width in pixels (default: 8)." };
        var faceIndexOption = new Option<int?>(name: "--face-index") { Description = "Zero-based face in a TTC/OTC collection (default: 0)." };
        var fontArgument = new Argument<string>(name: "font-file") { Description = "The source font: a standalone OpenType font or a TTC/OTC collection." };
        var maxDimensionOption = new Option<int?>(name: "--max-dimension") { Description = "Maximum atlas side in pixels (default: 16384)." };
        var maxPixelsOption = new Option<long?>(name: "--max-pixels") { Description = "Maximum total atlas pixels (default: 67108864)." };
        var outputOption = new Option<string>(name: "--output", aliases: ["-o"]) { Description = "Output JSON path; the PNG uses the same base name (default: <font-name>.sdf.json in the current directory)." };
        var paddingOption = new Option<int?>(name: "--padding") { Description = "Cell padding in pixels; must cover the distance range (default: 8)." };
        var rangeOption = new Option<string[]>(name: "--range", aliases: ["-r"]) { DefaultValueFactory = static _ => [], Description = "Included Unicode range; repeatable, using U+0020-U+007E, U+E0A0, or * for the Basic Multilingual Plane." };
        var sizeOption = new Option<int?>(name: "--size") { Description = "Raster em size in pixels (default: 32)." };
        var command = new Command(description: """
            Generate a loader-compatible MTSDF atlas from a font.

            The generator is fully managed and deterministic. It accepts TrueType quadratic,
            CFF, and CFF2 outlines in standalone OpenType fonts and TTC/OTC collections. It
            does not use an installed system font, native rasterizer, shaping engine, or Python.

            Exit codes: 0 generated, 1 font or I/O failure.
            """, name: "font-atlas") {
            fontArgument,
            charactersOption,
            columnsOption,
            distanceRangeOption,
            faceIndexOption,
            maxDimensionOption,
            maxPixelsOption,
            outputOption,
            paddingOption,
            rangeOption,
            sizeOption,
        };

        command.Validators.Add(item: result => {
            if ((result.GetValue(option: distanceRangeOption) is { } distanceRange) && !float.IsFinite(f: distanceRange)) {
                result.AddError(errorMessage: "--distance-range requires a finite decimal number.");
            }
        });
        command.SetAction(action: parseResult => Run(
            characters: parseResult.GetValue(option: charactersOption),
            columns: parseResult.GetValue(option: columnsOption),
            distanceRange: parseResult.GetValue(option: distanceRangeOption),
            faceIndex: parseResult.GetValue(option: faceIndexOption),
            fontFile: parseResult.GetRequiredValue(argument: fontArgument),
            maxDimension: parseResult.GetValue(option: maxDimensionOption),
            maxPixels: parseResult.GetValue(option: maxPixelsOption),
            output: parseResult.GetValue(option: outputOption),
            padding: parseResult.GetValue(option: paddingOption),
            ranges: (parseResult.GetValue(option: rangeOption) ?? []),
            size: parseResult.GetValue(option: sizeOption)
        ));
        return command;
    }

    // Every unsupplied option is left alone so FontAtlasGenerationOptions' own declared default stands.
    private static int Run(
        string? characters,
        int? columns,
        float? distanceRange,
        int? faceIndex,
        string fontFile,
        int? maxDimension,
        long? maxPixels,
        string? output,
        int? padding,
        string[] ranges,
        int? size
    ) {
        var fontPath = Path.GetFullPath(path: fontFile);

        if (!File.Exists(path: fontPath)) {
            Console.Error.WriteLine(value: $"font-atlas: source font not found: {fontPath}");

            return 1;
        }

        var outputPath = Path.GetFullPath(path: (output ?? $"{Path.GetFileNameWithoutExtension(path: fontPath)}.sdf.json"));
        var options = new FontAtlasGenerationOptions();

        if (ranges.Length > 0) {
            options.AllowedCodePointRanges = ranges;
        }

        options.AllowedCharacters = (characters ?? string.Empty);

        if (columns is { } columnCount) {
            options.Columns = columnCount;
        }
        if (distanceRange is { } band) {
            options.DistanceRange = band;
        }
        if (faceIndex is { } face) {
            options.FaceIndex = face;
        }
        if (maxDimension is { } dimension) {
            options.MaxAtlasDimension = dimension;
        }
        if (maxPixels is { } pixels) {
            options.MaxAtlasPixels = pixels;
        }
        if (padding is { } cellPadding) {
            options.Padding = cellPadding;
        }
        if (size is { } pixelSize) {
            options.FontPixelSize = pixelSize;
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
}
