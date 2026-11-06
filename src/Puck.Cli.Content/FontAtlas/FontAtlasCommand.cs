using System.CommandLine;
using Puck.Text;

namespace Puck.Cli.FontAtlas;

// The Puck-owned font authoring surface. The production generator and artifact writer live in Puck.Text; this verb
// owns only argument binding, file paths, and operator-facing diagnostics.
public static class FontAtlasCommand {
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
        int? size,
        FontAtlasGenerationLimits limits,
        CancellationToken cancellationToken
    ) {
        var fontPath = Path.GetFullPath(path: fontFile);

        if (!File.Exists(path: fontPath)) {
            return CliExit.Refuse(verb: "font-atlas", what: CliPaths.ToDisplay(fullPath: fontPath), why: "names no source font.");
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
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path: fontPath);

            if (stream.Length > limits.MaxFontBytes) {
                throw new ArgumentException(message: "The supplied font exceeds the whole-job input byte limit.");
            }
            var fontBytes = new byte[checked((int)stream.Length)];

            stream.ReadExactly(buffer: fontBytes);
            var atlas = new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
                FontBytes = fontBytes,
                Limits = limits,
                CancellationToken = cancellationToken,
                FontIdentifier = fontPath,
                ImageIdentifier = Path.ChangeExtension(
                extension: ".png",
                path: outputPath
            ),
                Options = options,
            });

            FontAtlasArtifactWriter.Write(
                atlas: atlas,
                jsonPath: outputPath
            );
            Console.Out.WriteLine(value: $"font-atlas: wrote {CliPaths.ToDisplay(fullPath: outputPath)} and {CliPaths.ToDisplay(fullPath: Path.ChangeExtension(
                extension: ".png",
                path: outputPath
            ))} ({atlas.Glyphs.Count} glyphs, {atlas.Width}x{atlas.Height}).");

            return CliExit.Success;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)) {
            return CliExit.Refuse(verb: "font-atlas", what: CliPaths.ToDisplay(fullPath: fontPath), why: exception.Message);
        }
    }

    public static Command Create() {
        var charactersOption = new Option<string>(name: "--characters") { DefaultValueFactory = static _ => string.Empty, Description = "Additional non-whitespace Unicode scalars to include." };
        var columnsOption = CliOptions.OptionalCount(description: "Preferred glyphs per shelf row (default: 16).", minimum: 1, name: "--columns");
        var maxWorkOption = CliOptions.Count(defaultValue: new FontAtlasGenerationLimits().MaxWork, description: "Whole-job processing work units.", minimum: 1L, name: "--max-work");
        var maxGeometryOption = CliOptions.Count(defaultValue: new FontAtlasGenerationLimits().MaxGeometryElements, description: "Whole-job geometry elements, including composite copies and boundaries.", minimum: 1, name: "--max-geometry");
        var maxPairsOption = CliOptions.Count(defaultValue: new FontAtlasGenerationLimits().MaxKerningPairs, description: "Kerning entries processed or emitted, including zero matches and Unicode aliases.", minimum: 0, name: "--max-kerning-pairs");
        var maxFontBytesOption = CliOptions.Count(defaultValue: new FontAtlasGenerationLimits().MaxFontBytes, description: "Maximum source font bytes, checked before reading.", minimum: 1, name: "--max-font-bytes");
        var maxGlyphsOption = CliOptions.Count(defaultValue: new FontAtlasGenerationLimits().MaxGlyphs, description: "Maximum distinct mapped source glyphs.", minimum: 1, name: "--max-glyphs");
        var distanceRangeOption = CliOptions.Amount(description: "Signed-distance band width in pixels (default: 8).", maximum: float.MaxValue, minimum: double.Epsilon, name: "--distance-range");
        var faceIndexOption = CliOptions.OptionalCount(description: "Zero-based face in a TTC/OTC collection (default: 0).", minimum: 0, name: "--face-index");
        var fontArgument = new Argument<string>(name: "font-file") { Description = "The source font: a standalone OpenType font or a TTC/OTC collection." };
        var maxDimensionOption = CliOptions.OptionalCount(description: "Maximum atlas side in pixels (default: 16384).", minimum: 1, name: "--max-dimension");
        var maxPixelsOption = CliOptions.OptionalCount(description: "Maximum total atlas pixels (default: 67108864).", minimum: 1L, name: "--max-pixels");
        var outputOption = CliOptions.Output(description: "Output JSON path; the PNG uses the same base name (default: <font-name>.sdf.json in the current directory).");
        var paddingOption = CliOptions.OptionalCount(description: "Cell padding in pixels; must cover the distance range (default: 8).", minimum: 0, name: "--padding");
        var rangeOption = new Option<string[]>(name: "--range") { DefaultValueFactory = static _ => [], Description = "Included Unicode range; repeatable, using U+0020-U+007E, U+E0A0, or * for the Basic Multilingual Plane." };
        var sizeOption = CliOptions.OptionalCount(description: "Raster em size in pixels (default: 32).", minimum: 1, name: "--size");
        var command = new Command(
            description: "Generate a loader-compatible MTSDF atlas from a font.",
            name: "font-atlas"
        ) {
            fontArgument,
            charactersOption,
            columnsOption,
            distanceRangeOption,
            faceIndexOption,
            maxDimensionOption,
            maxPixelsOption,
            maxWorkOption,
            maxGeometryOption,
            maxPairsOption,
            maxFontBytesOption,
            maxGlyphsOption,
            outputOption,
            paddingOption,
            rangeOption,
            sizeOption,
        };

        command.Detail(detail: """
            The generator is fully managed and deterministic. It accepts TrueType quadratic,
            CFF, and CFF2 outlines in standalone OpenType fonts and TTC/OTC collections. It
            does not use an installed system font, native rasterizer, shaping engine, or Python.

            Exit codes: 0 generated, 2 a font, range or I/O refusal, 130 cancelled.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.FromResult(result: Run(
            cancellationToken: cancellationToken,
            limits: new FontAtlasGenerationLimits {
                MaxWork = parseResult.GetValue(option: maxWorkOption),
                MaxGeometryElements = parseResult.GetValue(option: maxGeometryOption),
                MaxKerningPairs = parseResult.GetValue(option: maxPairsOption),
                MaxFontBytes = parseResult.GetValue(option: maxFontBytesOption),
                MaxGlyphs = parseResult.GetValue(option: maxGlyphsOption),
            },
            characters: parseResult.GetValue(option: charactersOption),
            columns: parseResult.GetValue(option: columnsOption),
            distanceRange: ((float?)parseResult.GetValue(option: distanceRangeOption)),
            faceIndex: parseResult.GetValue(option: faceIndexOption),
            fontFile: parseResult.GetRequiredValue(argument: fontArgument),
            maxDimension: parseResult.GetValue(option: maxDimensionOption),
            maxPixels: parseResult.GetValue(option: maxPixelsOption),
            output: parseResult.GetValue(option: outputOption),
            padding: parseResult.GetValue(option: paddingOption),
            ranges: (parseResult.GetValue(option: rangeOption) ?? []),
            size: parseResult.GetValue(option: sizeOption)
        )));
        return command;
    }
}
