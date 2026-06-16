using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Puck.Assets;

namespace Puck.Text;

/// <summary>
/// The content-addressed, caching <see cref="IFontAtlasSourceResolver"/>: it reads the font from disk,
/// keys a least-recently-used cache on a hash of the font contents combined with a normalized hash of the
/// generation options, and delegates a cache miss to an <see cref="IFontAtlasGenerator"/>.
/// </summary>
/// <remarks>
/// Because the cache key is derived from font content rather than from the path, the same font referenced
/// through different paths resolves to a single shared atlas, and a change to the font or to the options
/// produces a distinct entry. The cache retains at most a fixed number of the most recently used atlases.
/// </remarks>
/// <param name="fontAtlasGenerator">The generator invoked to produce an atlas on a cache miss.</param>
/// <param name="assetSource">The source from which font bytes are read.</param>
/// <exception cref="ArgumentNullException"><paramref name="fontAtlasGenerator"/> or <paramref name="assetSource"/> is <see langword="null"/>.</exception>
public sealed class FontAtlasSourceResolver(
    IFontAtlasGenerator fontAtlasGenerator,
    IAssetSource assetSource
)
    : IFontAtlasSourceResolver {
    private const int MaxCachedFonts = 256;

    private readonly IAssetSource m_assetSource = (assetSource ?? throw new ArgumentNullException(paramName: nameof(assetSource)));
    private readonly IFontAtlasGenerator m_fontAtlasGenerator = (fontAtlasGenerator ?? throw new ArgumentNullException(paramName: nameof(fontAtlasGenerator)));
    private readonly ContentAddressedLruCache<FontAtlas> m_fontAtlasCache = new(MaxCachedFonts);

    /// <inheritdoc/>
    /// <remarks>
    /// A relative <paramref name="fontPath"/> is combined with <paramref name="basePath"/>; an absolute
    /// path is used as-is. The font file is read in full, and the resulting atlas is cached so that
    /// subsequent calls for the same font content and equivalent options avoid regeneration.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="fontPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="generationOptions"/> is <see langword="null"/>.</exception>
    public FontAtlas Resolve(
        string fontPath,
        FontAtlasGenerationOptions generationOptions,
        string basePath
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: fontPath);
        ArgumentNullException.ThrowIfNull(generationOptions);

        var resolvedPath = (Path.IsPathRooted(path: fontPath)
            ? fontPath
            : Path.Combine(
                path1: basePath,
                path2: fontPath
            ));

        return LoadFromFont(
            fontBytes: m_assetSource.Read(path: resolvedPath),
            generationOptions: generationOptions
        );
    }

    private FontAtlas LoadFromFont(ReadOnlyMemory<byte> fontBytes, FontAtlasGenerationOptions generationOptions) {
        var fontHash = AssetContentHash.Compute(content: fontBytes.Span);
        var cacheHash = CombineHashes(
            first: fontHash,
            second: ComputeGenerationOptionsHash(options: generationOptions)
        );

        return m_fontAtlasCache.GetOrAdd(
            hash: cacheHash,
            valueFactory: () => {
                var fontIdentifier = ToContentAddress(
                    hash: fontHash,
                    scheme: "font"
                );

                return m_fontAtlasGenerator.Generate(request: new FontAtlasGenerationRequest {
                    FontBytes = fontBytes,
                    FontIdentifier = fontIdentifier,
                    ImageIdentifier = $"{fontIdentifier}#generated-atlas.png",
                    Options = generationOptions,
                });
            }
        );
    }
    private static string ToContentAddress(string scheme, AssetContentHash hash) {
        return $"{scheme}://sha256-64/{hash.Value:x16}";
    }
    private static AssetContentHash CombineHashes(AssetContentHash first, AssetContentHash second) {
        Span<byte> bytes = stackalloc byte[16];

        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: bytes[..8],
            value: first.Value
        );
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: bytes[8..],
            value: second.Value
        );
        return AssetContentHash.Compute(content: bytes);
    }
    private static AssetContentHash ComputeGenerationOptionsHash(FontAtlasGenerationOptions options) {
        if (options.AllowedCodePointRanges is null) {
            throw new ArgumentException(
                message: "Allowed code point ranges must be provided.",
                paramName: nameof(options)
            );
        }

        var normalizedCodePointRanges = CanonicalizeAllowedCodePointRanges(ranges: options.AllowedCodePointRanges);
        var normalizedAllowedCharacters = NormalizeAllowedCharacters(allowedCharacters: options.AllowedCharacters);
        var content = Encoding.UTF8.GetBytes(s: string.Join(
            '|',
            normalizedAllowedCharacters,
            string.Join(
                separator: ',',
                values: normalizedCodePointRanges
            ),
            options.Columns,
            options.FontPixelSize,
            options.MaxAtlasDimension,
            options.MaxAtlasPixels,
            options.Padding
        ));

        return AssetContentHash.Compute(content: content);
    }
    private static IReadOnlyList<string> CanonicalizeAllowedCodePointRanges(IReadOnlyList<string> ranges) {
        var expanded = UnicodeCodePointRangeExpander.Expand(
            ranges: ranges,
            wildcardSelected: out var wildcardSelected
        );

        if (wildcardSelected) {
            foreach (var codePoint in UnicodeCodePointRangeExpander.EnumerateBmpCodePoints()) {
                expanded.Add(item: codePoint);
            }
        }

        return BuildCanonicalRangeTokens(codePoints: expanded);
    }
    private static IReadOnlyList<string> BuildCanonicalRangeTokens(HashSet<int> codePoints) {
        if (codePoints.Count == 0) {
            return [];
        }

        var orderedCodePoints = codePoints.OrderBy(keySelector: static codePoint => codePoint).ToArray();
        var normalized = new List<string>();
        var rangeStart = orderedCodePoints[0];
        var previous = orderedCodePoints[0];

        for (var index = 1; (index < orderedCodePoints.Length); index++) {
            var current = orderedCodePoints[index];

            if (current == (previous + 1)) {
                previous = current;
                continue;
            }

            normalized.Add(item: ToCanonicalRangeToken(
                end: previous,
                start: rangeStart
            ));
            rangeStart = current;
            previous = current;
        }

        normalized.Add(item: ToCanonicalRangeToken(
            end: previous,
            start: rangeStart
        ));
        return normalized;
    }
    private static string ToCanonicalRangeToken(int start, int end) {
        return ((start == end)
            ? $"U+{start:X}"
            : $"U+{start:X}-U+{end:X}");
    }
    private static string NormalizeAllowedCharacters(string? allowedCharacters) {
        if (string.IsNullOrWhiteSpace(value: allowedCharacters)) {
            return string.Empty;
        }

        var codePoints = new HashSet<int>();

        foreach (var rune in allowedCharacters.EnumerateRunes()) {
            if (Rune.IsWhiteSpace(value: rune)) {
                continue;
            }

            codePoints.Add(item: rune.Value);
        }

        return string.Join(
            separator: ',',
            values: codePoints.OrderBy(keySelector: static value => value).Select(selector: static value => value.ToString(
                format: "X",
                provider: CultureInfo.InvariantCulture
            ))
        );
    }
}
