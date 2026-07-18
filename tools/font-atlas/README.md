# Font atlas bake pipeline

Puck ships **pre-baked MTSDF font atlases** — there is no font-rasterization
step at runtime. This directory holds the source TrueType files, their
licenses, and the bake script that produces the committed outputs in
[`src/Puck.Demo/Assets/Fonts/`](../../src/Puck.Demo/Assets/Fonts/):

- `puck-fonts-mtsdf.png` — one combined atlas image for all four faces.
- `inter-regular.json`, `inter-medium.json`, `inter-semibold.json`,
  `jetbrains-mono-regular.json` — one glyph-layout/kerning JSON per face,
  each pointing into the SAME combined PNG (`atlas.width`/`atlas.height` are
  the combined image's dimensions; every glyph's `atlasBounds` is already
  offset into combined-image coordinates).

## The manifest

The bake is driven by [`manifest.json`](manifest.json): one entry per output
atlas, naming its source TTF, output key, whether it needs a uniform
glyph-cell grid, and — optionally — a declared code-point subset. The
committed manifest bakes all four faces at **full cmap coverage** (no
`ranges` declared on any entry); subsetting is a capability the pipeline
carries, not something exercised in the current committed outputs.

```json
{
  "key": "inter-regular",
  "ttf": "Inter-Regular.ttf",
  "family": "Inter",
  "weight": 400,
  "style": "Regular",
  "uniformGrid": false
}
```

### Subsetting via `ranges`

An entry may add a `"ranges"` array to restrict the atlas to a declared
code-point subset instead of the font's full cmap, e.g.:

```json
{
  "key": "inter-regular-ascii",
  "ttf": "Inter-Regular.ttf",
  "family": "Inter",
  "weight": 400,
  "style": "Regular",
  "uniformGrid": false,
  "ranges": ["U+0020-U+007E", "U+2018", "U+2019"]
}
```

The token syntax is the **same one** `Puck.Text`'s
`FontAtlasGenerationOptions.AllowedCodePointRanges` uses (see
`src/Puck.Text/UnicodeCodePointRangeExpander.cs`), so a manifest subset
reads exactly like a run-doc's declared allowed ranges: a single code point
(`U+0041`), an inclusive range (`U+0020-U+007E`), or the wildcard `*` (the
full Basic Multilingual Plane). Multiple tokens in one string are separated
by commas, semicolons, or whitespace; the `U+` prefix is case-insensitive;
surrogate code points (`U+D800`-`U+DFFF`) are rejected.

**Semantics:** the declared ranges are expanded and then **intersected**
with the font's own (filtered) cmap — a requested code point the font
doesn't map is silently skipped (reported as a count in the bake log), not
an error. Glyph coverage, kerning-pair generation (see below), and the
self-verify glyph-count check all key off this resolved, intersected set —
not the raw request. When `ranges` is absent, the resolved set is the
font's full cmap, matching the pipeline's original (pre-subsetting)
behavior.

Add a `GLYPH_COUNT_FLOORS` (and, if the subset is expected to carry
kerning, `KERNING_COUNT_FLOORS`) entry in `bake.py` for any new manifest
key — the self-verify floors are keyed by manifest key and skipped
(not defaulted) for keys that don't have one declared.

## Fonts baked

| Face | Source | Role | Weight |
| --- | --- | --- | --- |
| Inter Regular | [rsms/inter](https://github.com/rsms/inter), release v4.1 (embedded `Version 4.001;git-9221beed3`) | UI text | 400 |
| Inter Medium | same | UI text (emphasis) | 500 |
| Inter SemiBold | same | UI text (headings) | 600 |
| JetBrains Mono Regular | [JetBrains/JetBrainsMono](https://github.com/JetBrains/JetBrainsMono), `master` (embedded `Version 2.305`) | Console / diegetic terminal | 400 |

Both families are **SIL Open Font License 1.1**. The exact license text each
project ships is committed here as `Inter-LICENSE.txt` and
`JetBrainsMono-LICENSE.txt` (fetched from the fonts' own repos — see
`THIRD-PARTY-NOTICES.md` at the repo root for the redistribution entry).
`fonts/*.ttf` are the four source files baked; nothing else in this
directory is font data.

## Why GPOS, not the atlas tool's own kerning reader

`msdf-atlas-gen` only reads the **legacy `kern` table** for its `kerning[]`
output. Inter and JetBrains Mono don't ship one — like effectively every
modern font, their kerning lives entirely in **GPOS** (`kern` feature,
`PairPos` lookups). Left alone, `msdf-atlas-gen` would emit an atlas with
zero kerning pairs. `bake.py` extracts the GPOS `kern` feature itself
(`PairPos` formats 1 and 2, `Value1.XAdvance` of the pair, em-normalized by
`unitsPerEm`) and merges the result into each JSON's `kerning[]` array in
the same `{"unicode1", "unicode2", "advance"}` shape `msdf-atlas-gen` itself
would have used.

**Kerning is scoped to `codepoint <= U+024F`** (Basic Latin, Latin-1
Supplement, Latin Extended-A, Latin Extended-B — essentially the full
Latin-script European repertoire), even though **glyph coverage is the
font's full cmap**. This isn't a shortcut: Inter's GPOS `kern` feature is
class-based (`PairPos` format 2), and its classes group glyphs by **shape**,
not by script — the "round-bodied lowercase" class holds Latin `o` right
next to its Cyrillic and Greek lookalikes, because they kern identically.
Flattening that to literal codepoint pairs across the *entire* cmap
multiplies every nonzero class1×class2 combination by
`|class1 members| x |class2 members|`; for Inter Regular that's ~430,000
pairs — tens of megabytes of JSON — almost all of it kerning behavior
between scripts that will essentially never sit adjacent in real Puck UI or
console text. Capping the codepoints eligible for the *flattened pair
table* to the Latin repertoire keeps kerning far past ASCII (the actual
ask) while keeping each JSON in the low megabytes; codepoints outside that
range still get full glyph coverage in the atlas; an unlisted pair defaults
to zero kerning adjustment, which is also exactly how those scripts render
today with no kerning pipeline at all. See `KERNING_MAX_CODEPOINT` in
`bake.py` for the constant and the full derivation.

JetBrains Mono has **no `kern` GPOS feature at all** (only `mark`/`mkmk` for
diacritic placement) — expected for a monospace face, where kerning would
defeat the fixed-width column grid. Its `kerning[]` array is legitimately
empty; that's not a bug in the extractor.

## The uniform grid for the console face

The three Inter weights bake **tight-packed** (msdf-atlas-gen's default):
every glyph gets a cell sized to its own bounds, which is what
`Puck.Text`'s general glyph-decal/world-text consumers expect (they read
each glyph's own UV rect).

`jetbrains-mono-regular` bakes with **`-uniformgrid`** instead: every glyph
cell is the same fixed size (`grid.cellWidth` x `grid.cellHeight` in the
JSON). `src/Puck.Demo/Text/SharedGlyphSdfPack.cs` extracts glyph cells from
the console/mono face assuming a uniform monospace grid — a tight-packed
atlas would break that consumer's fixed-stride cell math. This costs some
atlas density (fixed cells waste space around narrow glyphs like `i` or
`|`), which is why the four faces are repacked into a **2x2 grid** rather
than one vertical stack — a single column would have pushed the combined
image past the 8192px side cap.

## Repacking into one atlas

`msdf-atlas-gen` runs once per font, each producing its own PNG + JSON.
`bake.py` then:

1. Packs the four atlas images into a 2x2 shelf grid (top-aligned within
   each row, left-aligned within each column) into one combined PNG.
2. Rewrites each JSON's `atlas.width`/`atlas.height` to the combined
   image's dimensions, and shifts every glyph's `atlasBounds` by that
   glyph's placement offset.

The critical subtlety: `atlasBounds` are **yOrigin-bottom** — `bottom` and
`top` are measured from the *bottom* row of the atlas image (so
numerically `top > bottom`), the opposite of the PNG's own top-down row
order. A reader recovers the top-down pixel row via
`row = atlas.height - value`. Because that flip is anchored on
`atlas.height`, repacking must recompute both `bottom` and `top` in terms
of the *combined* image's height, not the source atlas's — otherwise
every glyph would sample the wrong row once the JSON's `atlas.height`
changes. `bake.py` derives the exact per-atlas additive shift (`dy` for
bottom/top, `dx` for left/right) from each atlas's placement rectangle; see
the doc comment on `repack_atlases()` in `bake.py` for the derivation.

## Self-verification

`bake.py` doesn't just trust its own output — after writing the committed
files it reloads them and asserts:

- Every JSON's `atlas.width`/`atlas.height` matches the combined PNG's
  actual dimensions.
- Glyph counts: Inter (each weight) >= 2800, JetBrains Mono >= 1300.
- `inter-regular.json`'s `kerning[]` length > 800.
- A small proof strip, rendered with the SAME bottom-up sampling +
  median(R,G,B) + `screenPxRange` reconstruction the engine's read path
  uses, produces a nonzero count of text pixels (i.e. the combined atlas +
  rewritten JSON actually rasterize readable text, not garbage coordinates
  from a repack bug). The proof image (`bake-proof.png`) is written to the
  (gitignored) `tools/font-atlas/build/` directory — it's a diagnostic, not
  a shipped asset.

If the combined PNG would exceed **8192px on a side** or **24MB**, the
script stops *before* writing any committed output and reports the
overage instead — better to rescope (fewer weights, a smaller `-size`, a
narrower charset) than ship a bloated atlas.

## Re-baking

### Prerequisites

- **msdf-atlas-gen v1.4** (Chlumsky/msdf-atlas-gen) — **not committed**.
  Download the Windows release binary from the project's GitHub releases:
  <https://github.com/Chlumsky/msdf-atlas-gen/releases> (pin v1.4; later
  versions may change CLI flags or JSON shape). Point `bake.py` at it with
  `--msdf-atlas-gen`.
- **Python 3.11+** with `fontTools`, `Pillow`, and `numpy`:

  ```
  pip install fonttools Pillow numpy
  ```

### Command

From the repo root:

```
python tools/font-atlas/bake.py --msdf-atlas-gen "C:\path\to\msdf-atlas-gen.exe"
```

Optional flags: `--manifest`, `--fonts-dir`, `--build-dir`, `--out-dir` (all
default to this directory's `manifest.json`, `fonts/`, `build/`, and
`src/Puck.Demo/Assets/Fonts/`, respectively). `build/` holds every
intermediate (per-font PNG/JSON before repacking, charset files, the proof
strip) and is gitignored; only the combined PNG and the four rewritten
JSONs are copied into the committed output directory.

To add a font or weight: add an entry to `manifest.json` (set
`"uniformGrid": true` only if a consumer needs fixed-size cells, as
`SharedGlyphSdfPack` does for the console face), drop its `.ttf` (and
license) into `fonts/`, and re-run. The repack step and self-verify are
generic over the font list; only the glyph/kerning-count floors
(`GLYPH_COUNT_FLOORS`, `KERNING_COUNT_FLOORS`) in `bake.py` are per-font and
may need a new entry (self-verify skips a check whose key has no floor
declared, so this is opt-in, not required).
