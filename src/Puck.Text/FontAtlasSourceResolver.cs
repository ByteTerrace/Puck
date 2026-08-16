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
    private readonly FontAtlasLoader m_fontAtlasLoader = new();

    /// <summary>Initializes a resolver with Puck's in-process font generator.</summary>
    /// <param name="assetSource">The source from which font bytes are read.</param>
    public FontAtlasSourceResolver(IAssetSource assetSource)
        : this(
        fontAtlasGenerator: new ManagedFontAtlasGenerator(),
        assetSource: assetSource
    ) { }

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
            options.DistanceRange.ToString(provider: CultureInfo.InvariantCulture),
            options.FaceIndex,
            options.FontPixelSize,
            options.MaxAtlasDimension,
            options.MaxAtlasPixels,
            options.Padding
        ));

        return AssetContentHash.Compute(content: content);
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
    private static string ResolveAgainstBase(string path, string basePath) {
        return (Path.IsPathRooted(path: path)
            ? path
            : Path.Combine(
                path1: basePath,
                path2: path
            )
        );
    }
    private static string ResolveContainedPath(string path, string basePath) {
        if (Path.IsPathRooted(path: path)) {
            throw new ArgumentException(
                message: "A contained font asset path must be relative to its document.",
                paramName: nameof(path)
            );
        }

        var root = Path.GetFullPath(path: basePath);
        var resolved = Path.GetFullPath(path: Path.Combine(
            path1: root,
            path2: path
        ));
        var relative = Path.GetRelativePath(
            path: resolved,
            relativeTo: root
        );

        if (
            Path.IsPathRooted(path: relative) ||
            string.Equals(
            a: relative,
            b: "..",
            comparisonType: StringComparison.Ordinal
        ) ||
            relative.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: $"..{Path.DirectorySeparatorChar}"
        )
        ) {
            throw new ArgumentException(
                message: "A contained font asset path must stay beneath its document directory.",
                paramName: nameof(path)
            );
        }

        return resolved;
    }
    private static string ToCanonicalRangeToken(int start, int end) {
        return ((start == end)
            ? $"U+{start:X}"
            : $"U+{start:X}-U+{end:X}"
        );
    }
    private static string ToContentAddress(string scheme, AssetContentHash hash) {
        return $"{scheme}://sha256-64/{hash.Value:x16}";
    }

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

        var resolvedPath = ResolveAgainstBase(
            basePath: basePath,
            path: fontPath
        );

        return LoadFromFont(
            fontBytes: m_assetSource.Read(path: resolvedPath),
            generationOptions: generationOptions
        );
    }
    /// <summary>Resolves and packs an entire hash-pinned font catalog beneath one document directory.</summary>
    public PackedFontAtlasCatalog ResolveCatalog(TextFontCatalogDefinition definition, string basePath) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: definition.DefaultFont);
        ArgumentNullException.ThrowIfNull(definition.Fonts);

        var fonts = new Dictionary<string, FontAtlas>(
            capacity: definition.Fonts.Count,
            comparer: StringComparer.Ordinal
        );

        foreach (var row in definition.Fonts) {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: row.Name);

            if (fonts.ContainsKey(key: row.Name)) {
                throw new InvalidDataException(message: $"Font name '{row.Name}' is duplicated.");
            }

            fonts.Add(
                key: row.Name,
                value: ResolvePinnedContained(
                    fontPath: row.Source,
                    expectedHash: row.Hash,
                    generationOptions: row.ToGenerationOptions(),
                    basePath: basePath
                )
            );
        }

        return FontAtlasCatalogPacker.Pack(
            defaultFont: definition.DefaultFont,
            fonts: fonts
        );
    }
    /// <summary>Resolves a hash-pinned font that must remain beneath <paramref name="basePath"/>.</summary>
    /// <param name="fontPath">The world-relative font asset path.</param>
    /// <param name="expectedHash">The canonical <c>sha256-64/{16 lowercase hex}</c> content pin.</param>
    /// <param name="generationOptions">The generation options.</param>
    /// <param name="basePath">The containing document directory.</param>
    public FontAtlas ResolvePinnedContained(string fontPath, string expectedHash, FontAtlasGenerationOptions generationOptions, string basePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: fontPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: expectedHash);
        ArgumentNullException.ThrowIfNull(generationOptions);

        var resolvedPath = ResolveContainedPath(
            basePath: basePath,
            path: fontPath
        );
        var fontBytes = m_assetSource.Read(path: resolvedPath);
        var actualHash = AssetContentHash.Compute(content: fontBytes.Span).ToString();

        if (!string.Equals(
            a: actualHash,
            b: expectedHash,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new InvalidDataException(message: $"Font asset '{fontPath}' has content hash '{actualHash}', not its declared '{expectedHash}'.");
        }

        return LoadFromFont(
            fontBytes: fontBytes,
            generationOptions: generationOptions
        );
    }
    /// <summary>Resolves a pre-baked font atlas metadata file through <see cref="FontAtlasLoader"/> instead of generating one.</summary>
    /// <remarks>
    /// The atlas image is expected alongside <paramref name="atlasPath"/>, sharing its base name with a
    /// <c>.png</c> extension. Like <see cref="Resolve(string, FontAtlasGenerationOptions, string)"/>, the
    /// result is cached under a hash of the metadata and image contents plus the resolved image identifier, so
    /// repeated resolution of the same pre-baked files is free after the first call. Only the resolved image path is
    /// recorded on the returned <see cref="FontAtlas"/> — no image pixels are decoded; use an
    /// <see cref="IFontAtlasImageDataLoader"/> when the pixels are actually needed.
    /// </remarks>
    /// <param name="atlasPath">The path to the font atlas's JSON metadata file. May be absolute or relative to <paramref name="basePath"/>.</param>
    /// <param name="basePath">The base directory used to resolve a relative <paramref name="atlasPath"/>.</param>
    /// <returns>The resolved <see cref="FontAtlas"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="atlasPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">The metadata file or its atlas image was not found.</exception>
    public FontAtlas ResolvePrebaked(
        string atlasPath,
        string basePath
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: atlasPath);

        var resolvedAtlasPath = ResolveAgainstBase(
            basePath: basePath,
            path: atlasPath
        );
        var resolvedImagePath = Path.ChangeExtension(
            extension: ".png",
            path: resolvedAtlasPath
        );

        if (!m_assetSource.Exists(path: resolvedImagePath)) {
            throw new FileNotFoundException(
                fileName: resolvedImagePath,
                message: "Font atlas image file was not found."
            );
        }

        var atlasBytes = m_assetSource.Read(path: resolvedAtlasPath);
        var imageBytes = m_assetSource.Read(path: resolvedImagePath);
        var cacheHash = CombineHashes(
            first: CombineHashes(
                first: AssetContentHash.Compute(content: atlasBytes.Span),
                second: AssetContentHash.Compute(content: imageBytes.Span)
            ),
            second: AssetContentHash.Compute(content: Encoding.UTF8.GetBytes(s: resolvedImagePath))
        );

        return m_fontAtlasCache.GetOrAdd(
            hash: cacheHash,
            valueFactory: () => m_fontAtlasLoader.Load(
                atlasIdentifier: resolvedAtlasPath,
                imagePath: resolvedImagePath,
                jsonContent: atlasBytes
            )
        );
    }
}
