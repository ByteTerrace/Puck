# Puck.Text

**Render-agnostic font atlas generation and text layout.** Puck.Text turns a font file
into a queryable `FontAtlas`, lays a string out against it into positioned glyph quads,
and hands a renderer the exact distance-field sampling math it needs to anti-alias edges
at any size.

It carries **no GPU or windowing concepts** — all geometry is computed in a scaled *em
space* and the mapping to screen pixels is left to the caller. It also carries **no
rasterizer**: the actual glyph-to-image step is an extension point you plug in.

```text
namespace Puck.Text
target     net10.0
deps       Puck.Assets   (content-addressed LRU cache + content hashing)
```

---

## Pipeline

```
 font file ──► IFontAtlasSourceResolver ──► IFontAtlasGenerator ──► FontAtlas
   (path)        (reads + caches)            (rasterize, you supply)   (data model)
                                                                          │
 string ─────────────────► TextLayout.Layout ─────────────────────► TextLayoutResult
                            (against an atlas)                       (positioned quads)
                                                                          │
 per glyph ──► TextGlyphSampling.Create / MtsdfSampling ──► sampling params for the shader
```

1. **Resolve** — `IFontAtlasSourceResolver.Resolve(path, options, basePath)` reads the
   font and produces (or returns a cached) `FontAtlas`.
2. **Generate** — on a cache miss the resolver calls your `IFontAtlasGenerator`, the seam
   that decouples the data model from any rasterization / distance-field backend.
3. **Lay out** — `TextLayout.Layout(atlas, text, scale, maxLineWidth?)` walks the string
   and emits a `TextLayoutResult` of `TextGlyphPlacement`s.
4. **Sample** — `TextGlyphSampling.Create(...)` / `MtsdfSampling` translate the atlas's
   encoded distance band into the screen-pixel quantities a shader needs.

---

## Core types

| Type | Role |
|------|------|
| `FontAtlas` | Immutable data model: kind, image (path + optional bytes), em size, distance range, metrics, glyphs, kerning. Constant-time glyph & kerning lookups. |
| `FontAtlasKind` | How the image encodes coverage → how it must be sampled (see below). |
| `FontAtlasGlyph` | One glyph: advance, em-space quad, atlas rectangle, optional per-glyph range overrides. |
| `FontAtlasMetrics` | Font-wide vertical metrics (line height, ascender, descender, underline), in em units. |
| `FontAtlasBounds` | A left/top/right/bottom rectangle (em or texel space depending on use). |
| `FontKerningPair` | A left→right code-point pair and its advance adjustment. |
| `FontAtlasImageData` | Optional in-memory atlas image bytes. |
| `IFontAtlasGenerator` | Extension point: font bytes + options → `FontAtlas`. |
| `FontAtlasGenerationRequest` / `…Options` | Inputs to generation (bytes, identifiers, glyph set, sizing). |
| `IFontAtlasSourceResolver` / `FontAtlasSourceResolver` | Path → atlas, with a content-addressed LRU cache. |
| `UnicodeCodePointRangeExpander` | Parses `U+XXXX` / `U+XXXX-U+YYYY` / `*` range tokens into code points. |
| `TextLayout` / `TextLayoutResult` | String → positioned glyph quads + overall bounds. |
| `TextGlyphPlacement` | One positioned glyph: its quad in layout space + atlas rectangle to sample. |
| `MtsdfSampling` | The shared distance-field ↔ screen-pixel sampling math. |
| `TextGlyphSampling` / `TextGlyphSamplingMode` | Resolved per-glyph sampling parameters + the decode strategy. |

---

## Atlas kinds

`FontAtlasKind` records how the image stores coverage, which determines how a shader must
decode it:

| Kind | Encoding | Sampling mode |
|------|----------|---------------|
| `HardMask` | 1-bit coverage | `Mask` |
| `SoftMask` | anti-aliased alpha coverage | `Mask` |
| `Sdf` | single-channel signed distance | `Sdf` |
| `Psdf` | single-channel pseudo SDF (sharper corners) | `Sdf` |
| `Msdf` | 3-channel SDF, combined by **median** | `Msdf` |
| `Mtsdf` | MSDF **+** true distance in a 4th channel | `Mtsdf` |

Distance-field kinds stay crisp under scaling because edges are reconstructed from encoded
distances rather than rasterized coverage. `MtsdfSampling.UsesDistanceField(kind)` and
`MtsdfSampling.ExpectedMode(kind)` classify a kind for you.

---

## Coordinate conventions

- **Em units.** `1.0` = one em. `FontAtlas.Size` is the pixels-per-em the atlas was
  rasterized at; multiply em-space measurements by a layout `scale` to get your units.
- **y-up, baseline-relative.** The first line's baseline is at `y = 0`; each subsequent
  line steps **down** (more negative `y`) by `FontAtlasMetrics.LineHeight × scale`.
  Ascending values are positive, descending negative.
- **Glyphs with no area** (spaces, control glyphs) advance the pen but contribute no
  placement.

---

## Laying out text

```csharp
using Puck.Text;

// 1. Resolve an atlas (your generator does the rasterization).
IFontAtlasSourceResolver resolver = new FontAtlasSourceResolver(
    fontAtlasGenerator: myGenerator   // your IFontAtlasGenerator
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

Layout walks one Unicode scalar at a time, advances the pen by each glyph's `Advance`,
applies kerning between consecutive glyphs, ignores `\r`, breaks lines on `\n`, and skips
code points the atlas doesn't contain. Wrapping is **greedy and glyph-granular** (it breaks
before the overflowing glyph, not at word boundaries).

---

## Distance-field sampling

A renderer needs the *screen pixel range* — the width of the encoded distance band in
destination pixels — to set the anti-aliasing ramp. `TextGlyphSampling.Create` bundles it
all up:

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

In the shader, `MtsdfSampling.ComputeCoverage(signedDistance, screenPixelRange)` recenters
on the edge (`0.5`) and clamps to `[0, 1]`; `MtsdfSampling.Median(r, g, b)` reconstructs the
true distance for multi-channel fields. For **mask** atlases the distance-field fields are
inert (`UnitRange == 0`, `ScreenPixelRange == 1`).

---

## Text enrichment — markup, effects, per-glyph channels

Enrichment is an optional layer that **composes WITH** `TextLayout`, never around it: an
author marks text up, layout carries each glyph's effect onto its placement, and a consumer
resolves a per-glyph transform/colour channel at a **deterministic content tick**. One atlas
and one layout serve every text tier.

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
| `TextEnrichmentTags` | The control-char grammar + single-pass `Stack<TextEffect>` scan (start pushes, end pops, `reset` clears, innermost **shadows**; malformed/unknown → literal). |
| `BbCodeTextMarkup` | The human front-end: compiles `[wave]…[/wave]` / `[color=#f00]…[/color]` BBCode down to the control-char stream. |
| `TextEffect` / `TextEffectKind` | An effect kind + its (late-bindable) parameters. Motion: `Shake`/`Wave`/`Pulse`/`Jitter`/`Dissolve`; static delight: `Color`/`Weight`; pacing: `Reveal`. `IsMotion` classifies. |
| `TextEffectParameter` / `TextEnrichmentVariable` | Numeric params that may late-bind a named **content-time channel** (additive/multiplicative/replacement) — no wall clock, no RNG. |
| `TextGlyphChannel` | The tier-agnostic per-glyph output (offset/scale/coverage/weight/tint). `Resolve(...)` turns an effect + content tick into one. |
| `TextEffectRune` | A visible rune paired with the effect in force at it — the enrichment-aware layout input. |

**Determinism.** `TextGlyphChannel.Resolve` is a pure function of the caller's content tick;
motion kinds are gated by `motionEnabled` (settle to rest when off, reveals complete), while
`Color`/`Weight` always apply — the reduced-motion contract. **DELIGHT ≠ MOTION:** motion is
opt-out; the default emphasis is semantic colour/weight/reveal.

---

## Generation options & glyph selection

`FontAtlasGenerationOptions` controls which glyphs are included and how the atlas is sized.
The glyph set is the union of `AllowedCharacters` and the expansion of
`AllowedCodePointRanges`, filtered to what the font actually maps.

| Option | Default | Meaning |
|--------|---------|---------|
| `AllowedCharacters` | `""` | Extra characters to include (whitespace ignored). |
| `AllowedCodePointRanges` | ASCII + Powerline + PUA | Range tokens: `U+0020-U+007E`, `U+E0A0`, or `*` (all BMP). |
| `FontPixelSize` | `32` | Em size, in pixels, glyphs are rasterized at. |
| `Columns` | `16` | Preferred glyph columns in the grid. |
| `Padding` | `8` | Pixels reserved around each glyph cell. |
| `MaxAtlasDimension` | `16384` | Max image width/height, in pixels. |
| `MaxAtlasPixels` | `67108864` | Max total pixel count (≈ 8192²). |

Range tokens are parsed by `UnicodeCodePointRangeExpander`: a single code point
(`U+0041`, `U+` optional, hex), an inclusive range (`U+0020-U+007E`), or `*` for every BMP
scalar. Surrogates (`U+D800`–`U+DFFF`) and values above `U+10FFFF` are rejected.

### Caching

`FontAtlasSourceResolver` keys an LRU cache on a hash of the **font contents** combined with
a **normalized** hash of the options — so the same font referenced through different paths
resolves to one shared atlas, and equivalent options reuse the same `FontAtlas`. It retains
up to 256 of the most recently used atlases.

---

## Plugging in a generator

Puck.Text deliberately ships **no rasterizer**. Implement `IFontAtlasGenerator` to wrap your
backend (e.g. the font-atlas bake pipeline, `tools/font-atlas` — the naming here follows its
conventions):

```csharp
public sealed class MyGenerator : IFontAtlasGenerator {
    public FontAtlas Generate(FontAtlasGenerationRequest request) {
        // request.FontBytes, request.FontIdentifier, request.Options
        // ... rasterize / derive distance field ...
        return new FontAtlas(/* kind, imagePath, size, distanceRange, w, h, metrics, glyphs, kerning */);
    }
}
```

---

## Notes for agents

- **Two front doors.** `IFontAtlasGenerator` works from font *bytes in memory*;
  `IFontAtlasSourceResolver` owns the *file I/O and caching* and delegates to a generator.
  Add caching at the resolver layer.
- **Render-agnostic by design.** Nothing here references a GPU API. Don't add windowing or
  texture types to this library — derive pixel-space geometry in the renderer from the
  em-space `PlaneBounds` / `AtlasBounds`.
- **em space, y-up, baseline at 0.** Keep this straight when converting to a y-down screen
  space.
- **Kind drives sampling.** Always route the `FontAtlasKind` through
  `MtsdfSampling.ExpectedMode` / `TextGlyphSampling.Create` rather than hard-coding a decode
  path; mask atlases must use the inert `Mask` path.
- **Lookups are O(1).** `FontAtlas.TryGetGlyph` and `GetKerningAdjustment` are dictionary
  lookups keyed by scalar value / ordered code-point pair.
- See the [generated API reference](../../docs/api) for full member docs.
