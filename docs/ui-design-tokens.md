# Puck UI design tokens

This document is the design contract implemented by
`src/Puck.Overlays/DesignTokens.cs` — the library home every 2D overlay
surface (World's `UnifiedOverlayNode` first) reads instead of hand-picked
literals. (Demo's `src/Puck.Demo/Ui/DesignTokens.cs` carries its own frozen
copy until Demo retires — see the retirement trajectory — and is not this
contract's implementation.) Values are specified in hexadecimal, RGBA,
pixels, or milliseconds and map directly to named C# constants.

Identity: precision-tool minimalism. Near-neutral graphite surfaces, ONE
electric accent used sparingly and semantically, hairline outlines as the
primary edge language, a strict 4px grid, small radii, weights 400/500/600
only. Phosphor is a **material**, quoted only in diegetic/console contexts —
never as chrome. Dark theme only; the lit world is the theme. Delight comes
from color, depth, and typography rather than motion.

---

## 1 · Spacing grid (strict 4px)

| Token | C# constant | px | Use |
|---|---|---|---|
| `space.0` | `Space0` | 0 | flush hairline seams |
| `space.1` | `Space1` | 4 | chip gap, badge↔label, cell gap |
| `space.2` | `Space2` | 8 | chip-cluster gap, inner icon gap |
| `space.3` | `Space3` | 12 | strip inner pad, panel section pad |
| `space.4` | `Space4` | 16 | card gutters |
| `space.5` | `Space5` | 20 | float↔float min gutter |
| `space.6` | `Space6` | 24 | major block gap |
| `space.8` | `Space8` | 32 | stage margin (floats inset from world edge) |

Grid-locked component heights (all multiples of 2 on the 4px lattice):

| C# constant | px | Component |
|---|---|---|
| `HeightChip` | 40 | binding chip |
| `HeightBadge` | 24 | chip badge |
| `HeightModeRow` | 30 | hub mode row |
| `HeightTrackerCell` | 26 | tracker step cell |
| `HeightConsoleHead` | 38 | console title row |
| `HeightPromptRow` | 34 | console prompt row |
| `HeightBindBar` | 64 | binding bar |
| `HeightTrackerBar` | 52 | tracker strip |

---

## 2 · Radii (3 steps, small)

| Token | C# constant | px | Use |
|---|---|---|---|
| `r.1` | `Radius1` | 3 | chips, badges, tracker cells, mode rows |
| `r.2` | `Radius2` | 6 | strips (binding bar, tracker), toast, plaque |
| `r.3` | `Radius3` | 9 | panels (console, hub, diegetic) |

Diegetic hardware (bezel/plate 5px, screen 2px) is a rendered world object,
exempt from `r.*`.

---

## 3 · Type scale (5 steps · MSDF-safe floor)

**MSDF floor rule (Cabinet graft):** primary chip labels are **12px**; the
absolute minimum anywhere in the UI is **11px** (eyebrows, legends, badge
glyphs, micro readouts). **Nothing renders at 10px or below** — below 11px,
MSDF glyph coverage degrades over the moving world.

| Token | C# constant (Size/Weight/Line) | px | weight | line | tracking | transform | Use |
|---|---|---|---|---|---|---|---|
| `type.title` | `TypeTitleSize=16, TypeTitleWeight=600, TypeTitleLine=18` | 16 | 600 | 18px | 0.01em | none | hub value, plaque heading |
| `type.body` | `TypeBodySize=13, TypeBodyWeight=400, TypeBodyLine=18` | 13 | 400 | 18px | 0 | none | mode rows, toast message, prose |
| `type.label` | `TypeLabelSize=12, TypeLabelWeight=500, TypeLabelLine=24` | 12 | 500 | 24px¹ | 0.01em | none | **chip labels**, console title |
| `type.micro` | `TypeMicroSize=11, TypeMicroWeight=500, TypeMicroLine=13` | 11 | 500 | 13px | 0.08em | UPPERCASE | eyebrows, legends, tk labels |
| `type.mono` | `TypeMonoSize=12, TypeMonoWeight=400, TypeMonoLine=18` | 12 | 400 | 18px | 0.04em | none | console body, meta, cells |

¹ Chip-label line-height equals `HeightBadge` (24) so the label baseline locks
to the badge center.

Mono size variants (same family): tracker position readout **15px**
(`TypeMonoReadoutSize=15`); badge glyphs **11px** (`TypeMonoBadgeSize=11`).

Families: `PuckUI` (currently Inter) + `PuckMono` (currently JetBrains Mono).
The token set is typeface-agnostic — identity lives in proportion, not the
face. Weights: **400 / 500 / 600** only; no 700 anywhere.

---

## 4 · Color roles (12 semantic roles)

### Surfaces (graphite, slightly cool-neutral)

| Token | C# constant | Value | Use |
|---|---|---|---|
| `surface.base` | `SurfaceBase` | `#0E1013` | deepest recessed field, held-chip fill |
| `surface.panel` | `SurfacePanel` | `#15181C` | primary float body (opaque reference) |
| `surface.raised` | `SurfaceRaised` | `#1D2126` | chips, tracker cells, selected mode |
| `surface.inset` | `SurfaceInset` | `#0B0D0F` | wells: scrollback, badge, cart slot |

### Scrims (the opacity floats paint over the lit world)

| Token | C# constant | Value | Use |
|---|---|---|---|
| `scrim.panel` | `ScrimPanel` | `rgba(18,21,25,0.90)` | panels |
| `scrim.strip` | `ScrimStrip` | `rgba(18,21,25,0.86)` | binding bar, tracker |
| `scrim.chip` | `ScrimChip` | `rgba(23,27,31,0.94)` | toast (small, dense) |

### Outlines (hairline-first edge language)

| Token | C# constant | Value | Use |
|---|---|---|---|
| `line.hair` | `LineHair` | `rgba(255,255,255,0.09)` | default 1px float & chip edge |
| `line.soft` | `LineSoft` | `rgba(255,255,255,0.06)` | quiet dividers, rails |
| `line.strong` | `LineStrong` | `rgba(255,255,255,0.16)` | focus emphasis (non-lit) |
| `line.inset` | `LineInset` | `rgba(0,0,0,0.55)` | engraved lower-shadow seam |

### Text

| Token | C# constant | Value | contrast over `scrim.panel` |
|---|---|---|---|
| `text.primary` | `TextPrimary` | `#EDEFF2` | ≥ 13.5:1 |
| `text.dim` | `TextDim` | `#9BA3AB` | ≥ 5.6:1 |
| `text.mute` | `TextMute` | `#5C646C` | ≥ 2.9:1 (decorative/non-text only) |

### Accent — ONE signal (electric amber-orange)

| Token | C# constant | Value | Use |
|---|---|---|---|
| `accent` | `Accent` | `#FF6A2B` | context-primary badge, caret, hub tick, tracker play/hit |
| `accent.quiet` | `AccentQuiet` | `rgba(255,106,43,0.14)` | accent chip fill, caret glow |
| `accent.line` | `AccentLine` | `rgba(255,106,43,0.45)` | 1px accent outline (rest tier) |
| `accent.ink` | `AccentInk` | `#160A04` | glyph ink on accent fills |

Accent budget: one primary control per surface. Never a border run, never a
background field.

### State semantics

| Token | C# constant | Value | Use |
|---|---|---|---|
| `positive` | `Positive` | `#5BC98C` | `[OK]`, toast success |
| `warning` | `Warning` | `#E8B341` | `[WARN]` |
| `danger` | `Danger` | `#F2565B` | `[ERR]`, destructive |

### Phosphor — a MATERIAL, not chrome (diegetic + console echo only)

| Token | C# constant | Value | Use |
|---|---|---|---|
| `phosphor` | `Phosphor` | `#5CFAA0` | CRT screen text, console echoed input, status dot |
| `phosphor.dim` | `PhosphorDim` | `rgba(92,250,160,0.42)` | phosphor glow falloff |
| `phosphor.cyan` | `PhosphorCyan` | `#5EEBE0` | secondary CRT tint, gallery kicker |

Rule: phosphor never paints a UI border, fill, label, or accent. It appears
only (a) behind glass in the diegetic material, (b) as the user's echoed input
line in the console, (c) the console status dot.

---

## 5 · Elevation — the two-tier rule (Phosphor graft)

**Tier 0 · RESTING = flat fill + hairline. No glow, ever.**
**Tier 1 · LIT (active / held / selected / transient) = SDF distance-falloff
bloom in the element's OWN semantic hue.**

An element is Tier 1 exactly while it is: the context-primary action (accent),
physically held, the current selection, or a transient echo (toast). Everything
else is Tier 0. There is no third tier.

### Bloom geometry (one geometry, hue varies)

| Token | C# constant | Value |
|---|---|---|
| `bloom.halo.blur` | `BloomHaloBlur` | `18px` |
| `bloom.halo.spread` | `BloomHaloSpread` | `-3px` |
| `bloom.halo.alpha` | `BloomHaloAlpha` | `0.42` |
| `bloom.ring.width` | `BloomRingWidth` | `1px` |
| `bloom.ring.alpha` | `BloomRingAlpha` | `0.55` |
| `bloom.held.inset` | `BloomHeldInset` | `inset 0 0 12px -3px @ alpha 0.50` |

Composite: `bloom(hue) = 0 0 0 1px rgba(hue, 0.55), 0 0 18px -3px rgba(hue, 0.42)`.

### Bloom hue table (derived from the 12 roles — no new hues)

| Token | C# constant | ring (1px) | halo |
|---|---|---|---|
| `bloom.accent` | `BloomAccent` | `rgba(255,106,43,0.55)` | `0 0 18px -3px rgba(255,106,43,0.42)` |
| `bloom.positive` | `BloomPositive` | `rgba(91,201,140,0.55)` | `0 0 18px -3px rgba(91,201,140,0.42)` |
| `bloom.warning` | `BloomWarning` | `rgba(232,179,65,0.55)` | `0 0 18px -3px rgba(232,179,65,0.42)` |
| `bloom.danger` | `BloomDanger` | `rgba(242,86,91,0.55)` | `0 0 18px -3px rgba(242,86,91,0.42)` |
| `bloom.neutral` | `BloomNeutral` | `rgba(237,239,242,0.30)` | `0 0 18px -3px rgba(237,239,242,0.22)` |

Neutral (held chips with no semantic hue) blooms quieter by design: ring 0.30,
halo 0.22 — pressed metal, not a signal.

### Tier recipes

| Element state | Recipe |
|---|---|
| Float (Tier 0) | `scrim.panel` fill + 1px `line.hair` + `shadow.seat` + `catchlight` (optional) |
| Strip (Tier 0) | `scrim.strip` fill + 1px `line.hair` + `shadow.seat.strip` |
| Chip rest (Tier 0) | `surface.raised` fill + inset 1px `line.hair` ring |
| Chip accent (Tier 1) | `accent.quiet` fill + `ring.status(accent)` **or** `bloom.accent` ring + `bloom.accent` halo; badge fills `accent`, glyph `accent.ink` |
| Chip held (Tier 1) | `surface.base` fill + `bloom.neutral` ring + halo + `press.held` + `translateY(1px)` |
| Chip disabled (Tier 0) | transparent fill + inset 1px `line.soft` + `opacity 0.45` |
| Hub mode selected (Tier 1) | `surface.raised` fill + `bloom.accent` ring (inset) + halo + 2px `accent` leading tick |
| Toast (Tier 1) | `scrim.chip` fill + `bloom.positive` ring + halo + 2px `positive` left rail |

| Token | C# constant | Value |
|---|---|---|
| `press.held` | `PressHeld` | `inset 0 2px 6px rgba(0,0,0,0.60), inset 0 0 12px -3px rgba(237,239,242,0.24), translateY(1px)` |
| `shadow.seat` | `ShadowSeat` | `0 18px 44px -18px rgba(0,0,0,0.72)` |
| `shadow.seat.strip` | `ShadowSeatStrip` | `0 18px 44px -20px rgba(0,0,0,0.75)` |

### Status ring — OPTIONAL second separation (Cabinet graft)

| Token | C# constant | Value |
|---|---|---|
| `ring.status.width` | `RingStatusWidth` | `2px` |
| `ring.status.alpha` | `RingStatusAlpha` | `0.60` |

`ring.status(hue) = 0 0 0 2px rgba(hue, 0.60)` — a uniform 2px outline band in
the element's semantic hue (accent/positive/warning/danger), used on accent and
status chips as a second, hue-based separation that survives any backdrop. It
is **optional**: dropping it degrades separation redundancy only, never
legibility (the scrim + hairline contract still holds). When present it
replaces the 1px bloom ring (never both).

### Panel catchlight

Panels use an optional inset hairline catchlight rather than a header gradient.

| Token | C# constant | Value |
|---|---|---|
| `catchlight` | `Catchlight` | `inset 0 1px 0 rgba(255,255,255,0.05)` |

Applied to Tier-0 floats and strips only. **Optional/droppable**: it is
materiality, not contrast — removing it changes nothing about legibility.

### Edge-width law

All edges are 1px hairlines by default. Exactly three 2px signals exist:
`ring.status`, the hub selection tick, and the toast state rail. Nothing is
wider than 2px.

---

## 6 · Diegetic material — emboss / engrave physics (Phosphor graft, stricter)

Plate: brushed metal `#2C2F33 → #24272B → #1C1F22` (2-stop vertical, optional)
with 1px × 3px 90° hairline stripe `rgba(255,255,255,0.018)` (optional).
Plate reference value for the physics rule: `PlateMid = #24272B`.

| | C# constant | Fill | Text-shadow |
|---|---|---|---|
| **Emboss** (raised) | `EmbossFill` | `#DFE6E1` — **brighter than plate** | `0 -1px 0 rgba(255,255,255,0.30)` (lit top edge), `0 2px 2px rgba(0,0,0,0.80)` (drop below) |
| **Engrave** (recessed) | `EngraveFill` | `#14171A` — **darker than plate** | `0 -1px 1px rgba(0,0,0,0.85)` (recess shadow above), `0 1px 0 rgba(255,255,255,0.16)` (light lip below) |

Law: raised fill is strictly brighter than the plate; engraved fill is strictly
darker than the plate; the two shadows carry **opposite polarity** (emboss:
highlight above + shadow below; engrave: shadow above + light below). The two
reliefs are never coplanar and both appear on the plate together.

CRT quote (the other half of the diegetic swatch): screen well
`radial #0C221A → #05100C`, text `phosphor` with glow
`0 0 6px phosphor.dim`, bezel `#23282B → #14181A` with `#05070A` 1px edge.

---

## 7 · Contrast story (floats over a lit, moving world)

> Every float paints its scrim — `scrim.panel rgba(18,21,25,0.90)`, strips
> `scrim.strip 0.86`, toasts `scrim.chip 0.94` — and is ringed by 1px
> `line.hair rgba(255,255,255,0.09)`. Body text uses `text.primary #EDEFF2`.
> This guarantees AA (≥ 4.5:1) for all label/body text over BOTH a dark room
> corner AND a lit CRT (cyan/green/magenta): the ≥0.86 scrim collapses any
> backdrop to an effective `#15181C`-band fill (text.primary ≥ 13:1, text.dim
> ≥ 5.6:1), and the hairline ring is the silhouette guarantee where scrim value
> meets a same-value world. **Minimum scrim opacity under text = 0.84**
> (`ScrimMinAlpha = 0.84`); below that the token contract is violated.
> Tier-1 bloom and `ring.status` add hue separation on top of — never instead
> of — this rule.

---

## 8 · Motion (delight ≠ motion; `text.motion = calm`)

Text never translates under the cursor-equivalent; only opacity/color/glow
tween on text. Bloom ramps on `dur.fast`; the accent bloom is state, not
animation.

| Token | C# constant | Value | Use |
|---|---|---|---|
| `dur.fast` | `DurFast` | `120ms` | chip press, bloom ramp, mode select |
| `dur.med` | `DurMed` | `180ms` | toast in, hover settle |
| `dur.panel` | `DurPanel` | `280ms` | console slide-down / dock |
| `ease.std` | `EaseStd` | `cubic-bezier(0.2,0,0,1)` | entrances & state |
| `ease.out` | `EaseOut` | `cubic-bezier(0.4,0,1,1)` | exits / dismiss |
| `caret.blink` | `CaretBlink` | `1080ms steps(1)` | prompt caret only |

Caps: interactions ≤ 180ms, panel transitions ≤ 320ms.

---

## 9 · GPU implementability (SDF overlay vocabulary, bloom tier included)

Every token decomposes to: solid fills, alpha scrims, rounded rects,
uniform-stroke outline bands, SDF distance-falloff glows/soft shadows, MSDF
text.

- **Scrims** → solid rounded-rect fills at role alpha.
- **Hairline / strong / status-ring edges** → uniform SDF outline bands
  (1px or 2px), inset or centered; no geometry shift.
- **Tier-1 bloom** → ONE extra SDF pass per lit element: an outer
  distance-falloff halo (`blur 18, spread −3, alpha 0.42` in the semantic hue)
  plus a 1px lit ring — exactly the "colored soft shadow" primitive. Tier-0
  elements pay zero glow cost; at most a handful of elements are lit at once
  (accent chip, held chips, one selection, one toast), so the bloom budget is
  bounded by design.
- **Held inset glow** → inner distance falloff (`12px, −3, 0.50` hue /
  `0.24` neutral) + inner top shadow band.
- **Seated shadows & catchlight** → outer falloff + a 1px inner band; the
  catchlight is a single inner stroke, droppable.
- **Emboss/engrave** → two MSDF text draws with opposite 1px/2px shadow
  polarity; fills are solids sitting brighter/darker than the plate solid.
- **Phosphor glow** → additive falloff around MSDF glyph coverage, clamped,
  gated to phosphor-role text.
- **Brushed-metal stripe & plate gradient** → optional 2-stop vertical ramp +
  1px repeating stroke (shader `mod`); decorative, never load-bearing.
- **No backdrop blur, no bitmap textures, no multi-stop load-bearing
  gradients anywhere.**

---

## 10 · Icon repertoire (the procedural glyph grammar's feel scalars)

Added directly in C# (`DesignTokens.Icon`) ahead of this document — the
reverse of every section above, so the values here are a convenience mirror,
**not** the spec: `src/Puck.Overlays/DesignTokens.cs`'s XML doc comments on
`DesignTokens.Icon` are authoritative.

| Token | C# constant | Value | Use |
|---|---|---|---|
| `icon.stroke.halfWidth` | `StrokeHalfWidth` | `0.08` (glyph-local units, box `[-1,1]`) | the hairline capsule stroke every procedural glyph/icon draws with — the icon language's one stroke weight |
| `icon.aaRamp` | `AaRamp` | `0.10` (glyph-local units) | the procedural glyph/icon anti-alias ramp |
| `icon.edgeAaRamp` | `EdgeAaRamp` | `1.25px` | the anti-alias ramp for hairline/rounded-rect edges |

The icon grammar itself (hairline capsule strokes on a shared glyph grid)
lives in `overlay-unified.frag.hlsl`; these three scalars are its only
authored feel knobs, uploaded through the `OverlayTokenBlock` storage slab
beside the rest of the palette.

---

## 11 · World feedback tints (presentation-only render pulls)

Also added directly in C# (`DesignTokens.Feedback`) — same convention as
§10: `src/Puck.Overlays/DesignTokens.cs`'s XML doc comments are
authoritative, this table mirrors them. These tints are NOT UI chrome — they
are the World editor's presentation-only SDF material pulls (a lerp toward
the tint, never a new material system), fed to the SDF program CPU-side from
this one token source, deliberately outside the chrome palette's accent
budget.

| Token | C# constant | Value | Use |
|---|---|---|---|
| `feedback.changeShimmer.tint` | `ChangeShimmerTint` | cool cyan | a delivery-changed row's albedo pulses toward this hue (mutation feedback; the undo spectacle) |
| `feedback.changeShimmer.blendMax` | `ChangeShimmerBlendMax` | `0.6` | the shimmer's peak albedo blend (eases from this toward zero) |
| `feedback.changeShimmer.pulseSeconds` | `ChangeShimmerPulseSeconds` | `0.9s` | one shimmer pulse's duration (render clock, never simulation state) |
| `feedback.selection.tint` | `SelectionTint` | amber | the selected row's albedo pulls toward this hue |
| `feedback.selection.tintBlend` | `SelectionTintBlend` | `0.65` | the selection tint's albedo blend |

Distinct from the change-shimmer cyan and the chrome `danger` red by design —
three readable hues for three readable meanings.
