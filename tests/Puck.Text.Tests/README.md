# Puck.Text.Tests

This suite checks the managed font and text contracts in `Puck.Text`.
`OpenTypeOutlineTests` covers TrueType and CFF/CFF2 outlines,
`ManagedFontKerningTests` covers face selection and kerning,
`IndependentFontOracleTests` checks JetBrains Mono glyph mapping and metrics
against a checked-in fontTools snapshot,
`MtsdfContractTests` covers distance-field sampling, and
`TextContractTests` covers layout, wrapping, alignment, and loader round
trips.

`TextSafetyTests` covers overlapping, nested, and coincident boundaries,
CFF execution limits, boundary/raster work limits, near-endpoint intersections, atlas ownership, and
weighted-cache eviction. `AtlasPackingTests` covers non-overlapping shelves,
size refusals, cache-set equivalence, and equality between a glyph's isolated
raster and its rectangle in a packed atlas. `GenerationBudgetTests` checks
cumulative whole-job limits, overflow-safe reservations, and cancellation.
`CffDistanceOracleTests` compares real Source Serif CFF and CFF2 fonts with
independent mapping, metric, outline-bound, and signed-distance samples.

The oracle fixture is derived from the vendored OFL font with fontTools
4.53.0. Its exact command and expected values are recorded in
`Assets/Fonts/jetbrains-mono-oracle.json`; normal test runs are fully offline
and do not require Python, FreeType, or another native runtime.

The two unmodified Source Serif fonts and their OFL license come from
[Adobe's pinned release](https://github.com/adobe-fonts/source-serif/tree/5f220b17d27ed64873f22cde0dd593685387bd19).
`source-serif-oracle.json` pins both complete SHA-256 hashes. Its external
derivation used fontTools 4.53.0's default-coordinate glyph set and BoundsPen
for A, g, and O at a 32px em. Each cubic was sampled into 1,024 uniform chords;
Shapely 2.1.2 noded and polygonized those segments, retained faces with nonzero
winding, unioned them, and measured distance to that union's boundary. The
half-pixel sample lattice has a three-pixel stride and extends two pixels beyond
the rounded ink bounds. Distances are positive inside, in atlas pixels.

Tests compare alpha distances after clamping to the eight-pixel encoded band;
the 0.06px allowance covers Puck's cubic/quadratic chord approximation and RGBA8
quantization. Bounds have a separate 0.04px allowance. This caught a Type 2
implicit-close current-point bug and exercises CFF2's overlapping A contours.
It is a small geometric oracle, not a full rasterizer, hinting, RGB corner-coloring,
or cross-backend pixel-conformance corpus. Production font tools remain CLI-based;
external Python dependencies are used only to derive the checked-in reference data.

## Running

```powershell
dotnet test tests/Puck.Text.Tests/Puck.Text.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Text](../../src/Puck.Text/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
