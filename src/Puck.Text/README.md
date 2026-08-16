# Puck.Text

Puck.Text turns a font file into a queryable `FontAtlas`, lays a string out
against it into positioned glyph quads, and hands a renderer the exact
distance-field sampling math it needs to anti-alias edges at any size. It
carries Puck's production in-process font path: TrueType quadratic and
CFF/CFF2 cubic OpenType outlines are read in managed code and evaluated into a
multi-channel true signed distance field (MTSDF), where the RGB channels
reconstruct sharp corners by median and the alpha channel carries the exact
marchable distance.

It carries no GPU or windowing concepts. All geometry is computed in a scaled
*em space*, and mapping that to screen pixels is left to the caller. The
generator behind atlas production is an extension point rather than a fixed
implementation, so an external tool can serve as an oracle or a third party
can substitute another backend.

## ✨ Key features

- *One managed font reader:* `ManagedFontAtlasGenerator` reads quadratic
  `glyf` outlines (including composites) and CFF/CFF2 Type 2 cubic
  charstrings, including CID-keyed fonts and local/global subroutines, from a
  zero-based face index in TTC/OTC collections.
- *Distance fields, not rasterized coverage:* `FontAtlasKind` spans hard/soft
  masks through SDF, PSDF, MSDF, and MTSDF, so a shader can stay crisp under
  arbitrary scaling.
- *A single layout pass serves every text tier:* `TextLayout.Layout` walks a
  string or an enriched rune stream once and emits positioned glyph quads with
  optional wrapping, alignment, tracking, and a line-height multiplier.
- *Markup composes with layout, never around it:* BBCode compiles to a
  control-char effect stream; layout carries each glyph's effect onto its
  placement; a consumer resolves the per-glyph channel at a deterministic
  content tick.
- *Portable, hash-pinned font catalogs:* a document names its fonts by a
  document-relative path and a `sha256-64/...` content hash, so a world stays
  replayable with no ambient system-font lookup.
- *A content-addressed cache:* `FontAtlasSourceResolver` keys atlas reuse on
  the font's bytes and a normalized hash of the generation options, so the
  same font referenced through different paths — or requested with equivalent
  options — resolves to one shared `FontAtlas`.

## 📐 Pipeline

```text
 font file ──► IFontAtlasSourceResolver ──► IFontAtlasGenerator ──► FontAtlas
   (path)        (reads + caches)           (managed by default)        (data model)
                                                                          │
 string ─────────────────► TextLayout.Layout ─────────────────────► TextLayoutResult
                            (against an atlas)                       (positioned quads)
                                                                          │
 per glyph ──► TextGlyphSampling.Create / MtsdfSampling ──► sampling params for the shader
```

1. **Resolve** — `IFontAtlasSourceResolver.Resolve(fontPath, generationOptions, basePath)`
   reads the font and produces (or returns a cached) `FontAtlas`.
2. **Generate** — on a cache miss the resolver calls `ManagedFontAtlasGenerator`
   by default. The interface preserves a clean backend/oracle seam.
3. **Lay out** — `TextLayout.Layout(atlas, text, scale, maxLineWidth?)` walks
   the string and emits a `TextLayoutResult` of `TextGlyphPlacement`s.
4. **Sample** — `TextGlyphSampling.Create(...)` / `MtsdfSampling` translate the
   atlas's encoded distance band into the screen-pixel quantities a shader
   needs.

## 🎨 Atlas kinds and coordinate conventions

`FontAtlasKind` records how the image stores coverage, which determines how a
shader must decode it:

| Kind | Encoding | Sampling mode |
|------|----------|---------------|
| `HardMask` | 1-bit coverage | `Mask` |
| `SoftMask` | anti-aliased alpha coverage | `Mask` |
| `Sdf` | single-channel signed distance | `Sdf` |
| `Psdf` | single-channel pseudo SDF (sharper corners) | `Sdf` |
| `Msdf` | 3-channel SDF, combined by **median** | `Msdf` |
| `Mtsdf` | MSDF **+** true distance in a 4th channel | `Mtsdf` |

Distance-field kinds stay crisp under scaling because edges are reconstructed
from encoded distances rather than rasterized coverage.
`MtsdfSampling.UsesDistanceField(kind)` and `MtsdfSampling.ExpectedMode(kind)`
classify a kind for you.

Layout space is **em units, y-up, baseline-relative**: `1.0` means one em,
`FontAtlas.Size` is the pixels-per-em the atlas was rasterized at, and the
first line's baseline sits at `y = 0` with each subsequent line stepping
**down** (more negative `y`) by `FontAtlasMetrics.LineHeight × scale`. Glyphs
with no area (spaces, control glyphs) advance the pen but contribute no
placement.

## 🚀 Quick start

```csharp
using Puck.Assets;
using Puck.Text;

// 1. Resolve an atlas through Puck's in-process generator.
IFontAtlasSourceResolver resolver = new FontAtlasSourceResolver(
    assetSource: new FileSystemAssetSource()
);
FontAtlas atlas = resolver.Resolve(
    fontPath: "fonts/Inter.ttf",
    generationOptions: new FontAtlasGenerationOptions {
        FontPixelSize = 48,
        AllowedCodePointRanges = ["U+0020-U+007E"],   // printable ASCII
    },
    basePath: AppContext.BaseDirectory
);

// 2. Lay out a string at a chosen scale, optionally wrapping.
TextLayoutResult layout = new TextLayout().Layout(
    atlas: atlas,
    text: "Hello,\nPuck!",
    scale: 1.0f,
    maxLineWidth: null   // or a width in scaled units for greedy glyph-level wrapping
);

Console.WriteLine(value: $"{layout.Width} x {layout.Height}, {layout.Placements.Count} glyphs");

// 3. Per placement: build a quad from PlaneBounds, sample from AtlasBounds.
foreach (TextGlyphPlacement p in layout.Placements) {
    // p.PlaneBounds  -> where the glyph sits in layout space (pen-offset, scaled)
    // p.AtlasBounds  -> the glyph's texels in the atlas image
}
```

Layout walks one Unicode scalar at a time, advances the pen by each glyph's
`Advance`, applies kerning between consecutive glyphs, ignores `\r`, breaks
lines on `\n`, and skips code points the atlas doesn't contain. Wrapping is
**greedy and glyph-granular** (it breaks before the overflowing glyph, not at
word boundaries). The `TextLayoutOptions` overload adds block alignment
(`Left`, `Center`, or `Right`), tracking in em units, and a line-height
multiplier while preserving the option-free overload's defaults.

`ManagedFontAtlasGenerator` deliberately maps scalars directly and does not
perform OpenType shaping, ligature substitution, bidirectional reordering, or
script-specific positioning. CFF2 variable fonts are evaluated at their
default design coordinates; authored variation-axis values are not yet part
of the contract. Generated glyph rows retain their source glyph IDs, Unicode
aliases share one raster cell, and the atlas model admits glyph-ID-only rows
with no direct scalar mapping. The current generator still includes only
directly mapped glyphs; future GSUB-closure generation can add ligatures and
contextual glyphs without another atlas-contract change. The atlas also
carries a flattened pair-kerning table read from GPOS pair positioning (the
`kern` feature, PairPos formats 1 and 2, extension lookups included) or, when
GPOS yields none, the legacy horizontal `kern` table. Until a shaping layer
selects ScriptList/LangSys records, all `kern` features are flattened
together; contextual positioning and lookup mark-filter semantics are not
read. The deprecated CFF1 `seac` endchar form is diagnosed rather than
composed.

## 🔬 Distance-field sampling

A renderer needs the *screen pixel range* — the width of the encoded distance
band in destination pixels — to set the anti-aliasing ramp.
`TextGlyphSampling.Create` bundles it all up:

```csharp
using Puck.Text;

// pixels-per-em for this glyph at its on-screen size
float screenScale = MtsdfSampling.ComputeScreenScale(
    planeWidth: planeW, planeHeight: planeH,
    rectWidthPixels: rectW, rectHeightPixels: rectH
);

TextGlyphSampling s = TextGlyphSampling.Create(
    atlas: atlas, glyph: glyph, screenScale: screenScale
);
// s.Mode             -> Mask | Sdf | Msdf | Mtsdf  (what the shader should do)
// s.ScreenPixelRange -> feed to the AA ramp; >= 1 so the edge always spans a pixel
// s.UnitRange        -> band width in em units (0 for mask atlases)
```

In the shader, `MtsdfSampling.ComputeCoverage(signedDistance, screenPixelRange)`
recenters on the edge (`0.5`) and clamps to `[0, 1]`;
`MtsdfSampling.Median(r, g, b)` reconstructs the true distance for
multi-channel fields. For **mask** atlases the distance-field fields are
inert (`UnitRange == 0`, `ScreenPixelRange == 1`). Always route the
`FontAtlasKind` through `MtsdfSampling.ExpectedMode` / `TextGlyphSampling.Create`
rather than hard-coding a decode path.

## 🖌️ Enrichment — markup, effects, per-glyph channels

Enrichment is an optional layer that composes *with* `TextLayout`, never
around it: an author marks text up, layout carries each glyph's effect onto
its placement, and a consumer resolves a per-glyph transform/colour channel
at a deterministic content tick. One atlas and one layout serve every text
tier.

```csharp
using Puck.Text;

// 1. Authors type BBCode; it compiles to the robust control-char stream.
string markup = "boot [color=#ff6688]PUCK[/color] [wave]online[/wave]";

// 2. Lay out the enriched runes — placements carry their effect.
TextLayoutResult layout = new TextLayout().Layout(
    atlas: atlas,
    runes: BbCodeTextMarkup.EnrichRunes(markup: markup),
    scale: 32.0f
);

// 3. Per placement, resolve the per-glyph channel at a content tick (never the wall clock).
int glyphIndex = 0;
foreach (TextGlyphPlacement p in layout.Placements) {
    TextGlyphChannel ch = TextGlyphChannel.Resolve(
        effect: p.Effect,
        contentTick: tick,           // a deterministic frame/step count you own
        ticksPerSecond: 60.0f,
        glyphPhase: p.BaselineOrigin.X,
        glyphIndex: glyphIndex++,
        motionEnabled: motionEnabled // your reduced-motion switch
    );
    // ch.Offset / ch.Scale / ch.Coverage / ch.WeightBias / ch.Tint (+ HasTint)
}
```

| Type | Role |
|------|------|
| `TextEnrichmentTags` | The control-char grammar + single-pass `Stack<TextEffect>` scan (start pushes, a matching end pops, `reset` clears, innermost **shadows**; malformed or unknown tags are dropped). |
| `BbCodeTextMarkup` | The human front-end: compiles `[wave]…[/wave]` / `[color=#f00]…[/color]` BBCode down to the control-char stream. |
| `TextEffect` / `TextEffectKind` | An effect kind + its (late-bindable) parameters. Motion: `Shake`/`Wave`/`Pulse`/`Jitter`/`Dissolve`; static delight: `Color`/`Weight`; pacing: `Reveal`. `IsMotion` classifies. |
| `TextEffectParameter` / `TextEnrichmentVariable` | Numeric params that may late-bind a named **content-time channel** (additive/multiplicative/replacement) — no wall clock, no RNG. |
| `TextGlyphChannel` | The tier-agnostic per-glyph output (offset/scale/coverage/weight/tint). `Resolve(...)` turns an effect + content tick into one. |
| `TextEffectRune` | A visible rune paired with the effect in force at it — the enrichment-aware layout input. |

`TextGlyphChannel.Resolve` is a pure function of the caller's content tick.
Motion kinds are gated by `motionEnabled` (settling to rest when off; reveals
still complete), while `Color`/`Weight` always apply — the reduced-motion
contract. Delight is not motion: motion is opt-out, and the default emphasis
is semantic colour/weight/reveal.

## ⚙️ Generation options and glyph selection

`FontAtlasGenerationOptions` controls which glyphs are included and how the
atlas is sized. The glyph set is the union of `AllowedCharacters` and the
expansion of `AllowedCodePointRanges`, filtered to what the font actually
maps.

| Option | Default | Meaning |
|--------|---------|---------|
| `AllowedCharacters` | `""` | Extra characters to include (whitespace ignored). |
| `AllowedCodePointRanges` | ASCII + Powerline + PUA | Range tokens: `U+0020-U+007E`, `U+E0A0`, or `*` (all BMP). |
| `FontPixelSize` | `32` | Em size, in pixels, glyphs are rasterized at. |
| `Columns` | `16` | Preferred glyph columns in the grid. |
| `DistanceRange` | `8` | Signed-distance band width in atlas pixels. |
| `FaceIndex` | `0` | Zero-based face in a TTC/OTC collection; standalone fonts accept only 0. |
| `Padding` | `8` | Pixels reserved around each glyph cell. |
| `MaxAtlasDimension` | `16384` | Max image width/height, in pixels. |
| `MaxAtlasPixels` | `67108864` | Max total pixel count (≈ 8192²). |

Range tokens are parsed by `UnicodeCodePointRangeExpander`: a single code
point (`U+0041`, `U+` optional, hex), an inclusive range (`U+0020-U+007E`), or
`*` for every BMP scalar. Surrogates (`U+D800`–`U+DFFF`) and values above
`U+10FFFF` are rejected.

`FontAtlasSourceResolver` keys its LRU cache on a hash of the font contents
combined with a normalized hash of the options, and retains up to 256 of the
most recently used atlases.

## 📌 Portable, pinned font catalogs

`TextFontCatalogDefinition` is the reusable authoring contract for a document
that brings its own fonts. Every `TextFontDefinition` names a
document-relative source, pins its bytes with a `sha256-64/...` hash, and
declares the Unicode scalar ranges to generate. Call
`FontAtlasSourceResolver.ResolveCatalog` to generate every logical atlas and
pack them into one texture through `FontAtlasCatalogPacker`; the returned
`PackedFontAtlasCatalog` retains named font metrics while remapping every
glyph rectangle into the shared image.

World documents use the stricter `ResolvePinnedContained` path: rooted paths
and paths escaping the document directory are refused, and the bytes must
match their declared hash. There is no ambient system-font lookup, which
keeps a world portable and replayable.

```json
"text": {
  "defaultFont": "body",
  "fonts": [{
    "name": "body",
    "source": "fonts/Inter-Regular.ttf",
    "hash": "sha256-64/0123456789abcdef",
    "codePointRanges": ["U+0020-U+007E", "U+00A0-U+024F"],
    "faceIndex": 0,
    "pixelSize": 48,
    "distanceRange": 8
  }]
}
```

## 🛠️ Generating artifacts from the CLI

`puck font-atlas` exposes the production managed generator without creating a
second implementation:

```text
puck font-atlas fonts/Inter-Regular.ttf \
  --range U+0020-U+007E \
  --range U+00A0-U+024F \
  --face-index 0 \
  --size 48 \
  --output artifacts/inter-regular.json
```

The command writes loader-compatible JSON and a sibling PNG through
`FontAtlasArtifactWriter`. Run `puck font-atlas --help` for the complete
limits and packing options.

The source TTFs behind the committed fixed-UI bake (Inter Regular/Medium/SemiBold,
JetBrains Mono Regular) plus their OFL license texts are vendored at
`Assets/Fonts/source/`, beside the bake they produced — OFL-1.1 requires the
license notice to travel with the font data.

## 🔌 Plugging in a generator

Implement `IFontAtlasGenerator` only when the default in-process MTSDF
generator is not the right backend — for example, to compare it with an
imported pre-baked atlas as an oracle:

```csharp
public sealed class MyGenerator : IFontAtlasGenerator {
    public FontAtlas Generate(FontAtlasGenerationRequest request) {
        // request.FontBytes, request.FontIdentifier, request.Options
        // ... rasterize / derive distance field ...
        return new FontAtlas(/* kind, imagePath, size, distanceRange, w, h, metrics, glyphs, kerning */);
    }
}
```

`IFontAtlasGenerator` works from font bytes in memory; `IFontAtlasSourceResolver`
owns the file I/O and caching and delegates to a generator. Add caching at the
resolver layer, not inside a generator.

## 📋 Core types

| Type | Role |
|------|------|
| `FontAtlas` | Immutable data model: kind, image (path + optional bytes), em size, distance range, metrics, glyphs, kerning. Constant-time Unicode, glyph-ID, and kerning lookups. |
| `FontAtlasKind` | How the image encodes coverage → how it must be sampled. |
| `FontAtlasGlyph` | One glyph: advance, em-space quad, atlas rectangle, optional per-glyph range overrides. |
| `FontAtlasMetrics` | Font-wide vertical metrics (line height, ascender, descender, underline), in em units. |
| `FontAtlasBounds` | A left/top/right/bottom rectangle (em or texel space depending on use). |
| `FontKerningPair` | A left→right code-point pair and its advance adjustment. |
| `FontAtlasImageData` | Optional in-memory atlas image bytes. |
| `IFontAtlasGenerator` / `ManagedFontAtlasGenerator` | Extension point (font bytes + options → `FontAtlas`) and the default managed implementation. |
| `FontAtlasGenerationRequest` / `FontAtlasGenerationOptions` | Inputs to generation (bytes, identifiers, glyph set, sizing). |
| `IFontAtlasSourceResolver` / `FontAtlasSourceResolver` | Path → atlas, with a content-addressed LRU cache. |
| `UnicodeCodePointRangeExpander` | Parses `U+XXXX` / `U+XXXX-U+YYYY` / `*` range tokens into code points. |
| `TextLayout` / `TextLayoutResult` / `TextLayoutOptions` | String → positioned glyph quads + overall bounds. |
| `TextGlyphPlacement` | One positioned glyph: its quad in layout space + atlas rectangle to sample. |
| `MtsdfSampling` | The shared distance-field ↔ screen-pixel sampling math. |
| `TextGlyphSampling` / `TextGlyphSamplingMode` | Resolved per-glyph sampling parameters + the decode strategy. |
| `TextFontDefinition` / `TextFontCatalogDefinition` | The authoring contract for a document's own pinned fonts. |
| `FontAtlasCatalogPacker` / `PackedFontAtlasCatalog` | Packs several logical atlases into one shared image. |
| `FontAtlasArtifactWriter` | Writes an atlas's PNG and loader-compatible JSON metadata. |

## 🧪 Verification

```powershell
dotnet test tests/Puck.Text.Tests/Puck.Text.Tests.csproj
```

`OpenTypeOutlineTests` and `ManagedFontKerningTests` exercise the TrueType and
CFF/CFF2 readers (including face selection and GPOS/`kern` kerning) against
synthetic fonts built in-process; `MtsdfContractTests` pins the distance-field
sampling contract; `TextContractTests` covers layout, wrapping, alignment, and
loader round-trips.

## 📦 Packaging

`ByteTerrace.Puck.Text` depends on `Puck.Assets` and `Puck.Maths`.
`FontAtlasArtifactWriter` and `FontAtlasImageDataLoader` write and read an
atlas's raster image through `Puck.Assets`'s PNG codec (`PngEncoder` /
`PngDecoder`) — Puck.Text has no video or audio dependency. The committed
pre-baked fixed-UI assets under `Assets/Fonts` are Puck's own oracle bake for
its overlay HUD; they are not part of the package (runtime-authored fonts
always go through the managed generator instead) and are consumed only by
in-repo project references.
