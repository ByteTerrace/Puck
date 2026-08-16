using System.Text.Json;
using Puck.Assets;

namespace Puck.Text;

/// <summary>Writes a <see cref="FontAtlas"/>'s image and loader-compatible JSON metadata.</summary>
public static class FontAtlasArtifactWriter {
    private static string KindName(FontAtlasKind kind) {
        return kind switch {
            FontAtlasKind.HardMask => "hardmask",
            FontAtlasKind.SoftMask => "softmask",
            FontAtlasKind.Sdf => "sdf",
            FontAtlasKind.Psdf => "psdf",
            FontAtlasKind.Msdf => "msdf",
            FontAtlasKind.Mtsdf => "mtsdf",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(kind)),
        };
    }
    private static void WriteBounds(Utf8JsonWriter writer, string propertyName, FontAtlasBounds? bounds) {
        if (bounds is not { } value) {
            return;
        }

        writer.WriteStartObject(propertyName: propertyName);
        writer.WriteNumber(
            propertyName: "left",
            value: value.Left
        );
        writer.WriteNumber(
            propertyName: "bottom",
            value: value.Bottom
        );
        writer.WriteNumber(
            propertyName: "right",
            value: value.Right
        );
        writer.WriteNumber(
            propertyName: "top",
            value: value.Top
        );
        writer.WriteEndObject();
    }

    /// <summary>Writes the atlas to sibling <c>.json</c> and <c>.png</c> files.</summary>
    /// <param name="jsonPath">The metadata output path. The image uses the same path with a <c>.png</c> extension.</param>
    /// <param name="atlas">The atlas to write. It must carry in-memory image data.</param>
    /// <exception cref="ArgumentException"><paramref name="jsonPath"/> is empty, or <paramref name="atlas"/> has no image data.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is <see langword="null"/>.</exception>
    public static void Write(string jsonPath, FontAtlas atlas) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: jsonPath);
        ArgumentNullException.ThrowIfNull(atlas);

        if (atlas.ImageData is not FontAtlasImageData imageData) {
            throw new ArgumentException(
                message: "Writing a font atlas artifact requires in-memory image data.",
                paramName: nameof(atlas)
            );
        }

        var fullJsonPath = Path.GetFullPath(path: jsonPath);
        var directory = Path.GetDirectoryName(path: fullJsonPath)!;

        Directory.CreateDirectory(path: directory);
        WriteMetadata(
            atlas: atlas,
            jsonPath: fullJsonPath
        );
        WriteImage(
            imageData: imageData,
            pngPath: Path.ChangeExtension(
                extension: ".png",
                path: fullJsonPath
            )
        );
    }
    /// <summary>Writes packed atlas image data as an 8-bit RGBA PNG.</summary>
    /// <param name="pngPath">The image output path.</param>
    /// <param name="imageData">The pixels to write.</param>
    /// <exception cref="ArgumentException"><paramref name="pngPath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="imageData"/> is <see langword="null"/>.</exception>
    public static void WriteImage(string pngPath, FontAtlasImageData imageData) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: pngPath);
        ArgumentNullException.ThrowIfNull(imageData);

        var fullPath = Path.GetFullPath(path: pngPath);

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
        PngEncoder.Write(
            height: imageData.Height,
            path: fullPath,
            rgba: imageData.RgbaPixels,
            width: imageData.Width
        );
    }
    /// <summary>Writes loader-compatible JSON metadata without writing the atlas image.</summary>
    /// <param name="jsonPath">The metadata output path.</param>
    /// <param name="atlas">The atlas whose metadata is written.</param>
    /// <exception cref="ArgumentException"><paramref name="jsonPath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is <see langword="null"/>.</exception>
    public static void WriteMetadata(string jsonPath, FontAtlas atlas) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: jsonPath);
        ArgumentNullException.ThrowIfNull(atlas);

        var fullPath = Path.GetFullPath(path: jsonPath);
        var directory = Path.GetDirectoryName(path: fullPath)!;

        Directory.CreateDirectory(path: directory);

        using var stream = File.Create(path: fullPath);
        using var writer = new Utf8JsonWriter(
            utf8Json: stream,
            options: new JsonWriterOptions { Indented = true }
        );

        writer.WriteStartObject();
        writer.WriteStartObject(propertyName: "atlas");
        writer.WriteString(
            propertyName: "type",
            value: KindName(kind: atlas.Kind)
        );
        writer.WriteNumber(
            propertyName: "distanceRange",
            value: atlas.DistanceRange
        );
        writer.WriteNumber(
            propertyName: "size",
            value: atlas.Size
        );
        writer.WriteNumber(
            propertyName: "width",
            value: atlas.Width
        );
        writer.WriteNumber(
            propertyName: "height",
            value: atlas.Height
        );
        writer.WriteString(
            propertyName: "yOrigin",
            value: "top"
        );
        writer.WriteEndObject();
        writer.WriteStartObject(propertyName: "metrics");
        writer.WriteNumber(
            propertyName: "lineHeight",
            value: atlas.Metrics.LineHeight
        );
        writer.WriteNumber(
            propertyName: "ascender",
            value: atlas.Metrics.Ascender
        );
        writer.WriteNumber(
            propertyName: "descender",
            value: atlas.Metrics.Descender
        );
        writer.WriteNumber(
            propertyName: "underlineY",
            value: atlas.Metrics.UnderlineY
        );
        writer.WriteNumber(
            propertyName: "underlineThickness",
            value: atlas.Metrics.UnderlineThickness
        );
        writer.WriteEndObject();
        writer.WriteStartArray(propertyName: "glyphs");

        foreach (var glyph in atlas.Glyphs.OrderBy(keySelector: static glyph => glyph.Unicode)) {
            writer.WriteStartObject();
            writer.WriteNumber(
                propertyName: "unicode",
                value: glyph.Unicode
            );

            if (glyph.GlyphId >= 0) {
                writer.WriteNumber(
                    propertyName: "index",
                    value: glyph.GlyphId
                );
            }

            writer.WriteNumber(
                propertyName: "advance",
                value: glyph.Advance
            );
            WriteBounds(
                writer: writer,
                propertyName: "planeBounds",
                bounds: glyph.PlaneBounds
            );
            WriteBounds(
                writer: writer,
                propertyName: "atlasBounds",
                bounds: glyph.AtlasBounds
            );

            if (glyph.EmRange is float emRange) {
                writer.WriteNumber(
                    propertyName: "emRange",
                    value: emRange
                );
            }

            if (glyph.PxRange is float pxRange) {
                writer.WriteNumber(
                    propertyName: "pxRange",
                    value: pxRange
                );
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray(propertyName: "kerning");

        foreach (var pair in atlas.KerningPairs.OrderBy(keySelector: static pair => pair.Unicode1).ThenBy(keySelector: static pair => pair.Unicode2)) {
            writer.WriteStartObject();
            writer.WriteNumber(
                propertyName: "unicode1",
                value: pair.Unicode1
            );
            writer.WriteNumber(
                propertyName: "unicode2",
                value: pair.Unicode2
            );
            writer.WriteNumber(
                propertyName: "advance",
                value: pair.AdvanceAdjustment
            );
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }
}
