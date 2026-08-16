namespace Puck.Text;

/// <summary>Packs named logical atlases into one deterministically ordered GPU texture.</summary>
public static class FontAtlasCatalogPacker {
    private readonly record struct Placement(string Name, FontAtlas Atlas, int X, int Y);

    private static void CopyImage(FontAtlasImageData source, byte[] destination, int destinationWidth, int x, int y) {
        var sourceRowBytes = checked((source.Width * 4));
        var destinationRowBytes = checked((destinationWidth * 4));

        for (var row = 0; (row < source.Height); row++) {
            source.RgbaPixels.AsSpan(
                length: sourceRowBytes,
                start: (row * sourceRowBytes)
            ).CopyTo(destination: destination.AsSpan(
                length: sourceRowBytes,
                start: (((y + row) * destinationRowBytes) + (x * 4))
            ));
        }
    }

    /// <summary>Packs <paramref name="fonts"/> in ordinal name order using deterministic shelves.</summary>
    public static PackedFontAtlasCatalog Pack(string defaultFont, IReadOnlyDictionary<string, FontAtlas> fonts, int maxDimension = 16_384, long maxPixels = 67_108_864) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: defaultFont);
        ArgumentNullException.ThrowIfNull(fonts);

        if (fonts.Count == 0) {
            throw new ArgumentException(
                message: "At least one font atlas must be provided.",
                paramName: nameof(fonts)
            );
        }

        if (!fonts.ContainsKey(key: defaultFont)) {
            throw new ArgumentException(
                message: $"Default font '{defaultFont}' is not declared.",
                paramName: nameof(defaultFont)
            );
        }

        if (
            (maxDimension <= 0) ||
            (maxPixels <= 0)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(maxDimension),
                message: "Atlas limits must be greater than zero."
            );
        }

        var placements = new List<Placement>(capacity: fonts.Count);
        var x = 0;
        var y = 0;
        var rowHeight = 0;
        var width = 0;

        foreach (var pair in fonts.OrderBy(
            keySelector: static pair => pair.Key,
            comparer: StringComparer.Ordinal
        )) {
            var atlas = (pair.Value ?? throw new ArgumentException(
                message: $"Font '{pair.Key}' has no atlas.",
                paramName: nameof(fonts)
            ));

            if (atlas.ImageData is null) {
                throw new ArgumentException(
                    message: $"Font '{pair.Key}' has no in-memory image data.",
                    paramName: nameof(fonts)
                );
            }

            if (
                (atlas.Width > maxDimension) ||
                (atlas.Height > maxDimension)
            ) {
                throw new ArgumentException(
                    message: $"Font '{pair.Key}' atlas {atlas.Width}x{atlas.Height} exceeds the {maxDimension}px packed-atlas dimension.",
                    paramName: nameof(fonts)
                );
            }

            if (
                (x > 0) &&
                ((x + atlas.Width) > maxDimension)
            ) {
                y = checked((y + rowHeight));
                x = 0;
                rowHeight = 0;
            }

            placements.Add(item: new Placement(
                Name: pair.Key,
                Atlas: atlas,
                X: x,
                Y: y
            ));
            x = checked((x + atlas.Width));
            rowHeight = Math.Max(
                val1: rowHeight,
                val2: atlas.Height
            );
            width = Math.Max(
                val1: width,
                val2: x
            );
        }

        var height = checked((y + rowHeight));

        if (
            (height > maxDimension) ||
            (checked((((long)width) * height)) > maxPixels)
        ) {
            throw new ArgumentException(
                message: $"The font catalog requires a {width}x{height} atlas, exceeding its configured limits.",
                paramName: nameof(fonts)
            );
        }

        var rgba = new byte[checked(((width * height) * 4))];
        var remapped = new Dictionary<string, FontAtlas>(
            capacity: fonts.Count,
            comparer: StringComparer.Ordinal
        );

        foreach (var placement in placements) {
            CopyImage(
                source: placement.Atlas.ImageData!,
                destination: rgba,
                destinationWidth: width,
                x: placement.X,
                y: placement.Y
            );
        }

        var imageData = new FontAtlasImageData(
            rgbaPixels: rgba,
            height: height,
            width: width
        );

        foreach (var placement in placements) {
            remapped.Add(
                key: placement.Name,
                value: new FontAtlas(
                    kind: placement.Atlas.Kind,
                    imagePath: $"catalog://{imageData.ContentHash}/{placement.Name}",
                    size: placement.Atlas.Size,
                    distanceRange: placement.Atlas.DistanceRange,
                    width: width,
                    height: height,
                    metrics: placement.Atlas.Metrics,
                    glyphs: placement.Atlas.Glyphs.Select(selector: glyph => new FontAtlasGlyph(
                        unicode: glyph.Unicode,
                        advance: glyph.Advance,
                        planeBounds: glyph.PlaneBounds,
                        atlasBounds: ((glyph.AtlasBounds is { } bounds)
                ? new FontAtlasBounds(
                                Left: (bounds.Left + placement.X),
                                Bottom: (bounds.Bottom + placement.Y),
                                Right: (bounds.Right + placement.X),
                                Top: (bounds.Top + placement.Y)
                            )
                : null),
                        emRange: glyph.EmRange,
                        pxRange: glyph.PxRange,
                        glyphId: glyph.GlyphId
                    )),
                    kerningPairs: placement.Atlas.KerningPairs,
                    imageData: imageData
                )
            );
        }

        return new PackedFontAtlasCatalog(
            defaultFont: defaultFont,
            fonts: remapped,
            imageData: imageData
        );
    }
}
