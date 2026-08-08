#!/usr/bin/env python3
"""Bake Puck's committed MTSDF font atlas.

The bake is driven by a manifest (see manifest.json next to this script)
declaring one entry per output atlas: font file, output name, whether it
needs a uniform glyph-cell grid, and an optional set of declared code-point
ranges (same token syntax as Puck.Text's
FontAtlasGenerationOptions.AllowedCodePointRanges - see "Subsetting via the
manifest" in README.md). Per manifest entry this script:
  (a) resolves the codepoints to bake - the font's full cmap coverage
      (minus true control/surrogate codepoints) by default, or the declared
      ranges intersected with the font's cmap when given - into an
      msdf-atlas-gen charset file (bracket-range syntax);
  (b) runs msdf-atlas-gen (`-type mtsdf -size 48 -pxrange 6 -format png -json
      -imageout`) to rasterize the per-font atlas;
  (c) flattens the font's GPOS `kern` feature (PairPos formats 1 and 2) —
      msdf-atlas-gen only reads the legacy `kern` table, and Inter / JetBrains
      Mono carry their kerning in GPOS only — into em-normalized pairs
      restricted to codepoints present in that font's atlas, and merges them
      into the JSON's `kerning[]` array;
  (d) repacks all the per-font atlases into ONE combined PNG (a simple 2x2
      shelf grid for the current 4 fonts) and rewrites each JSON so
      atlas.width/height are the combined image's dimensions and every
      glyph's atlasBounds is offset into combined-image coordinates, in the
      SAME bottom-up (yOrigin: bottom) convention msdf-atlas-gen emits
      per-atlas;
  (e) self-verifies: reloads every output JSON+PNG, checks dimensions agree,
      checks glyph/kerning counts against floors, and renders a small
      median-of-3 proof strip (bake-proof.png) to confirm the combined atlas
      actually rasterizes text.

See tools/font-atlas/README.md for provenance, licensing, and the exact bake
command. Run: `python tools/font-atlas/bake.py --msdf-atlas-gen <path-to-exe>`
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import unicodedata
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from fontTools.ttLib import TTFont
from PIL import Image

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
DEFAULT_FONTS_DIR = SCRIPT_DIR / "fonts"
DEFAULT_BUILD_DIR = SCRIPT_DIR / "build"
DEFAULT_OUT_DIR = REPO_ROOT / "src" / "Puck.Demo" / "Assets" / "Fonts"
DEFAULT_MANIFEST_PATH = SCRIPT_DIR / "manifest.json"

ATLAS_SIZE_PX = 48
ATLAS_PXRANGE = 6

MAX_ATLAS_SIDE_PX = 8192
MAX_ATLAS_FILE_BYTES = 24 * 1024 * 1024

# Codepoint categories that are never real, renderable glyphs: true C0/C1
# control characters and (theoretical, BMP-only cmaps never carry these)
# surrogate halves. Everything else in the font's cmap - including format
# characters, private-use glyphs, symbols, combining marks - ships, because
# the brief is full cmap coverage, not a curated subset.
_EXCLUDED_CATEGORIES = {"Cc", "Cs"}


@dataclass(frozen=True)
class FontSpec:
    key: str
    ttf_name: str
    family: str
    weight: int
    style: str
    # msdf-atlas-gen tight-packs variable per-glyph bounds by default. The
    # UI-facing Inter weights are consumed by TextLayout world glyphs/decals,
    # which read per-glyph UV rects and are fine with that. The console/mono
    # face is consumed by SharedGlyphSdfPack (src/Puck.Demo/Text/), which
    # extracts glyph cells assuming a UNIFORM monospace grid - every cell the
    # same size - so its atlas is baked with -uniformgrid instead.
    uniform_grid: bool = False
    # Optional declared code-point subset, in the SAME token syntax as
    # Puck.Text's FontAtlasGenerationOptions.AllowedCodePointRanges (see
    # UnicodeCodePointRangeExpander in src/Puck.Text): "U+0020-U+007E"
    # (inclusive range), "U+E0A0" (single code point), or "*" (the full BMP).
    # When absent (the default for every font in the committed manifest),
    # the atlas covers the font's FULL cmap, unchanged from before this
    # capability existed.
    ranges: tuple[str, ...] | None = None


def load_manifest(path: Path) -> list[FontSpec]:
    """Load the bake manifest (see manifest.json next to this script and
    the "Subsetting via the manifest" section of README.md)."""
    raw = json.loads(path.read_text(encoding="utf-8"))
    specs: list[FontSpec] = []
    for entry in raw["fonts"]:
        ranges = entry.get("ranges")
        specs.append(
            FontSpec(
                key=entry["key"],
                ttf_name=entry["ttf"],
                family=entry["family"],
                weight=entry["weight"],
                style=entry["style"],
                uniform_grid=entry.get("uniformGrid", False),
                ranges=tuple(ranges) if ranges else None,
            )
        )
    return specs

# Self-verification floors (see the arc's bake brief).
GLYPH_COUNT_FLOORS = {
    "inter-regular": 2800,
    "inter-medium": 2800,
    "inter-semibold": 2800,
    "jetbrains-mono-regular": 1300,
}
KERNING_COUNT_FLOORS = {
    "inter-regular": 800,
}

# GPOS class-based kerning (PairPos format 2) groups glyphs by SHAPE, not by
# script: Inter's own tables put Latin, Cyrillic, and Greek look-alikes in
# the same class (e.g. the "round left/lowercase-o-shaped" class holds 'o'
# next to Cyrillic 'о' and Greek lookalikes) because they kern identically.
# Flattening that to literal codepoint pairs across the FULL cmap multiplies
# class1-member-count x class2-member-count for every nonzero class pair -
# for Inter Regular that is ~430K pairs, i.e. tens of megabytes of JSON, for
# kerning behavior between scripts that will essentially never sit adjacent
# in real Puck UI/console text. Capping the codepoints eligible for the
# flattened kerning table to the Latin repertoire (Basic Latin, Latin-1
# Supplement, Latin Extended-A/B - i.e. essentially all Latin-script
# European languages) keeps kerning far past ASCII (the brief's ask) while
# avoiding that combinatorial blow-up; codepoints outside this range still
# get full glyph coverage in the atlas, just without a flattened kerning
# entry (an unlisted pair already defaults to zero adjustment, which is
# also how these scripts render today with no kerning pipeline at all).
KERNING_MAX_CODEPOINT = 0x24F

COMBINED_PNG_NAME = "puck-fonts-mtsdf.png"
PROOF_PNG_NAME = "bake-proof.png"


# --------------------------------------------------------------------------
# cmap -> msdf-atlas-gen charset file
# --------------------------------------------------------------------------


def collect_codepoints(font: TTFont) -> list[int]:
    cmap = font.getBestCmap()
    out = []
    for cp in cmap:
        ch = chr(cp) if cp < 0x110000 else None
        cat = unicodedata.category(ch) if ch is not None else "Cn"
        if cat in _EXCLUDED_CATEGORIES:
            continue
        out.append(cp)
    return sorted(out)


# --------------------------------------------------------------------------
# Declared code-point ranges (FontSpec.ranges) - mirrors the token syntax and
# parsing semantics of src/Puck.Text/UnicodeCodePointRangeExpander.cs, so a
# manifest entry's "ranges" reads the same as a run-doc's
# FontAtlasGenerationOptions.AllowedCodePointRanges.
# --------------------------------------------------------------------------

_RANGE_TOKEN_SPLIT_RE = re.compile(r"[,;\s]+")


def _parse_code_point(token: str) -> int:
    normalized = token[2:] if token[:2].upper() == "U+" else token
    try:
        code_point = int(normalized, 16)
    except ValueError as exc:
        raise ValueError(
            f"Unsupported code point token '{token}'. Use U+XXXX or "
            "U+XXXX-U+YYYY, or '*' to select the full BMP (U+0000-U+FFFF)."
        ) from exc
    if code_point > 0x10FFFF:
        raise ValueError(f"Code point '{token}' exceeded the Unicode maximum U+10FFFF.")
    if 0xD800 <= code_point <= 0xDFFF:
        raise ValueError(
            f"Code point '{token}' is within UTF-16 surrogate space "
            "(U+D800-U+DFFF) and is not a valid Unicode scalar value."
        )
    return code_point


def expand_code_point_ranges(ranges: tuple[str, ...]) -> tuple[set[int], bool]:
    """Expand declared range tokens into a set of code points, mirroring
    UnicodeCodePointRangeExpander.Expand: entries may hold multiple tokens
    separated by commas/semicolons/whitespace; a token is a single code
    point (`U+0041`), an inclusive range (`U+0020-U+007E`), or the wildcard
    `*`. Returns (expanded, wildcard_selected) - the wildcard itself is NOT
    expanded here (mirroring the C# API); the caller adds
    enumerate_bmp_code_points() when wildcard_selected is True."""
    wildcard_selected = False
    expanded: set[int] = set()
    for entry in ranges:
        if not entry or not entry.strip():
            continue
        tokens = [t for t in _RANGE_TOKEN_SPLIT_RE.split(entry.strip()) if t]
        for token in tokens:
            if token == "*":
                wildcard_selected = True
                continue
            if "-" in token:
                separator_index = token.index("-")
                start_cp = _parse_code_point(token[:separator_index])
                end_cp = _parse_code_point(token[separator_index + 1 :])
                if start_cp > end_cp:
                    raise ValueError(
                        f"Range '{token}' had a start value greater than its end value."
                    )
                if start_cp <= 0xDFFF and end_cp >= 0xD800:
                    raise ValueError(
                        f"Range '{token}' included UTF-16 surrogate code points "
                        "(U+D800-U+DFFF), which are not valid Unicode scalar values."
                    )
                expanded.update(range(start_cp, end_cp + 1))
                continue
            expanded.add(_parse_code_point(token))
    return expanded, wildcard_selected


def enumerate_bmp_code_points() -> set[int]:
    """Every Unicode scalar value in the Basic Multilingual Plane, excluding
    the surrogate range - the wildcard `*` token's expansion."""
    return {cp for cp in range(0, 0x10000) if not (0xD800 <= cp <= 0xDFFF)}


def resolve_font_codepoints(font: TTFont, spec: FontSpec) -> tuple[list[int], int]:
    """Resolve the codepoints to bake for `spec`: the font's full (filtered)
    cmap by default, or - when `spec.ranges` is declared - the requested
    ranges INTERSECTED with the font's cmap (a requested code point the font
    lacks is skipped, not an error). Returns (codepoints, dropped_count)."""
    cmap_codepoints = set(collect_codepoints(font))
    if not spec.ranges:
        return sorted(cmap_codepoints), 0

    requested, wildcard_selected = expand_code_point_ranges(spec.ranges)
    if wildcard_selected:
        requested |= enumerate_bmp_code_points()

    resolved = requested & cmap_codepoints
    dropped = len(requested) - len(resolved)
    return sorted(resolved), dropped


def format_charset(codepoints: list[int]) -> str:
    """Bracket-range syntax: `[0x0020, 0x007e], 0x00a9, ...`."""
    ranges: list[tuple[int, int]] = []
    start = prev = None
    for cp in codepoints:
        if start is None:
            start = prev = cp
        elif cp == prev + 1:
            prev = cp
        else:
            ranges.append((start, prev))
            start = prev = cp
    if start is not None:
        ranges.append((start, prev))

    parts = []
    for a, b in ranges:
        if a == b:
            parts.append(f"0x{a:04x}")
        else:
            parts.append(f"[0x{a:04x}, 0x{b:04x}]")
    return ",\n".join(parts) + "\n"


# --------------------------------------------------------------------------
# GPOS kerning (kern feature, PairPos formats 1 + 2), full-cmap intersection
# --------------------------------------------------------------------------


def extract_gpos_kerning(
    font: TTFont, codepoints: set[int]
) -> dict[tuple[int, int], float]:
    """Flatten the GPOS 'kern' feature to {(cp1, cp2): XAdvance / unitsPerEm}.

    Extends the ASCII-only reference implementation to the full set of
    codepoints given (already filtered to the kerning-eligible repertoire by
    the caller - see KERNING_MAX_CODEPOINT), so kerning coverage tracks well
    past ASCII without following the font's class tables into a
    multi-script combinatorial explosion (see the comment at
    KERNING_MAX_CODEPOINT for why that matters).
    """
    upm = font["head"].unitsPerEm
    cmap = font.getBestCmap()
    glyph_to_cp: dict[str, int] = {}
    for cp, gname in cmap.items():
        if cp in codepoints and gname not in glyph_to_cp:
            glyph_to_cp[gname] = cp

    out: dict[tuple[int, int], float] = {}
    if "GPOS" not in font:
        return out
    gpos = font["GPOS"].table
    if not gpos.LookupList or not gpos.FeatureList:
        return out

    kern_lookups: set[int] = set()
    for fr in gpos.FeatureList.FeatureRecord:
        if fr.FeatureTag == "kern":
            kern_lookups.update(fr.Feature.LookupListIndex)

    for li in sorted(kern_lookups):
        lookup = gpos.LookupList.Lookup[li]
        subtables = lookup.SubTable
        if lookup.LookupType == 9:  # Extension positioning
            subtables = [st.ExtSubTable for st in subtables]
            lt = subtables[0].LookupType if subtables else 0
        else:
            lt = lookup.LookupType
        if lt != 2:  # Pair adjustment only
            continue

        for st in subtables:
            cov = st.Coverage.glyphs
            if st.Format == 1:
                for gname, pairset in zip(cov, st.PairSet):
                    lcp = glyph_to_cp.get(gname)
                    if lcp is None:
                        continue
                    for pvr in pairset.PairValueRecord:
                        rcp = glyph_to_cp.get(pvr.SecondGlyph)
                        if rcp is None:
                            continue
                        adv = (
                            getattr(pvr.Value1, "XAdvance", 0)
                            if pvr.Value1
                            else 0
                        )
                        if adv:
                            out.setdefault((lcp, rcp), adv / upm)
            elif st.Format == 2:
                cd1 = st.ClassDef1.classDefs if st.ClassDef1 else {}
                cd2 = st.ClassDef2.classDefs if st.ClassDef2 else {}
                cov_set = set(cov)
                by_c1: dict[int, list[int]] = {}
                for gname, cp in glyph_to_cp.items():
                    if gname in cov_set:
                        by_c1.setdefault(cd1.get(gname, 0), []).append(cp)
                by_c2: dict[int, list[int]] = {}
                for gname, cp in glyph_to_cp.items():
                    by_c2.setdefault(cd2.get(gname, 0), []).append(cp)
                for c1, rec1 in enumerate(st.Class1Record):
                    lcps = by_c1.get(c1)
                    if not lcps:
                        continue
                    for c2, rec2 in enumerate(rec1.Class2Record):
                        adv = (
                            getattr(rec2.Value1, "XAdvance", 0)
                            if rec2.Value1
                            else 0
                        )
                        if not adv:
                            continue
                        for lcp in lcps:
                            for rcp in by_c2.get(c2, []):
                                out.setdefault((lcp, rcp), adv / upm)
    return out


# --------------------------------------------------------------------------
# msdf-atlas-gen invocation
# --------------------------------------------------------------------------


def run_msdf_atlas_gen(
    msdf_exe: str,
    ttf_path: Path,
    charset_path: Path,
    png_path: Path,
    json_path: Path,
    uniform_grid: bool = False,
) -> None:
    cmd = [
        msdf_exe,
        "-font",
        str(ttf_path),
        "-charset",
        str(charset_path),
        "-type",
        "mtsdf",
        "-size",
        str(ATLAS_SIZE_PX),
        "-pxrange",
        str(ATLAS_PXRANGE),
        "-format",
        "png",
        "-imageout",
        str(png_path),
        "-json",
        str(json_path),
    ]
    if uniform_grid:
        cmd.append("-uniformgrid")
    print(f"  $ {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True, check=False)
    sys.stdout.write(result.stdout)
    sys.stderr.write(result.stderr)
    if result.returncode != 0:
        raise RuntimeError(
            f"msdf-atlas-gen failed ({result.returncode}) for {ttf_path.name}"
        )


# --------------------------------------------------------------------------
# Repacking: N per-font atlases -> one combined PNG + rewritten JSONs
# --------------------------------------------------------------------------


def repack_atlases(
    per_font: list[tuple[FontSpec, dict, Image.Image]], cols: int = 2
) -> tuple[Image.Image, dict[str, dict]]:
    """Pack the per-font atlas images into a `cols`-wide shelf grid (2x2 for
    the current 4 fonts) and rebase every glyph's atlasBounds into the
    combined image's bottom-up (yOrigin: bottom) coordinate space.

    A plain vertical stack of all 4 atlases would work too, but the mono
    face's -uniformgrid atlas is markedly less pixel-dense than the tight-
    packed Inter atlases (fixed-size cells waste space around narrow
    glyphs), so stacking all 4 in one column can push the combined height
    past the 8192px cap; a 2x2 grid keeps both sides well under it for the
    same total pixel budget.

    Within each row, images are top-aligned to the row's top edge (rows can
    have mismatched heights; any gap is transparent padding at the bottom of
    the shorter image's slot) and left-aligned to their column's left edge.

    msdf-atlas-gen's atlasBounds are yOrigin-BOTTOM: bottom < top numerically
    per glyph, measured from the bottom row of ITS OWN atlas image. Placing
    an atlas of height H_src at top-down pixel-row offset `oy` (and
    horizontal offset `ox`) inside a combined canvas of height H_combined
    shifts every bottom-up y-coordinate by the same constant, and every
    x-coordinate by `ox`:

        dy = H_combined - oy - H_src
        new_bottom = old_bottom + dy
        new_top    = old_top    + dy
        new_left   = old_left   + ox
        new_right  = old_right  + ox

    (Derivation: a pixel at bottom-up value v in the source atlas sits at
    top-down row H_src - v there, hence at top-down row oy + H_src - v in
    the combined canvas, hence at combined bottom-up value H_combined - oy -
    H_src + v.)
    """
    rows = [per_font[i : i + cols] for i in range(0, len(per_font), cols)]
    row_heights = [max(img.height for _, _, img in row) for row in rows]
    row_widths = [sum(img.width for _, _, img in row) for row in rows]
    row_y_offsets = [sum(row_heights[:r]) for r in range(len(rows))]

    combined_w = max(row_widths)
    combined_h = sum(row_heights)
    combined = Image.new("RGBA", (combined_w, combined_h), (0, 0, 0, 0))

    rewritten: dict[str, dict] = {}
    for r, row in enumerate(rows):
        oy = row_y_offsets[r]
        ox = 0
        for spec, data, img in row:
            combined.paste(img, (ox, oy))
            h_src = img.height
            w_src = img.width
            dy = combined_h - oy - h_src

            data = json.loads(json.dumps(data))  # deep copy
            data["atlas"]["width"] = combined_w
            data["atlas"]["height"] = combined_h
            for glyph in data["glyphs"]:
                ab = glyph.get("atlasBounds")
                if ab is None:
                    continue
                ab["bottom"] = ab["bottom"] + dy
                ab["top"] = ab["top"] + dy
                ab["left"] = ab["left"] + ox
                ab["right"] = ab["right"] + ox
            rewritten[spec.key] = data
            ox += w_src

    return combined, rewritten


# --------------------------------------------------------------------------
# Self-verification: reload, check counts, render a proof strip
# --------------------------------------------------------------------------


def _bilinear(img: np.ndarray, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    h, w = img.shape[:2]
    x0 = np.clip(np.floor(xs).astype(int), 0, w - 2)
    y0 = np.clip(np.floor(ys).astype(int), 0, h - 2)
    fx = np.clip(xs - x0, 0, 1)[..., None]
    fy = np.clip(ys - y0, 0, 1)[..., None]
    c00 = img[y0, x0]
    c10 = img[y0, x0 + 1]
    c01 = img[y0 + 1, x0]
    c11 = img[y0 + 1, x0 + 1]
    return (c00 * (1 - fx) + c10 * fx) * (1 - fy) + (c01 * (1 - fx) + c11 * fx) * fy


class VerifyAtlas:
    """Minimal MTSDF sampler mirroring the engine's expected read path:
    bilinear-sample, median(R,G,B), map through screenPxRange. Mirrors
    render_samples.py so the proof strip exercises the SAME convention the
    runtime will."""

    def __init__(self, data: dict, img: Image.Image):
        self.img = np.asarray(img.convert("RGBA"), dtype=np.float32) / 255.0
        self.h, self.w = self.img.shape[:2]
        a = data["atlas"]
        self.size = a["size"]
        self.range = a["distanceRange"]
        self.glyphs = {g["unicode"]: g for g in data["glyphs"]}
        self.kern = {
            (k["unicode1"], k["unicode2"]): k["advance"]
            for k in data.get("kerning", [])
        }

    def draw(self, canvas, text, x, y, size_px, color, use_kern=True):
        H, W = canvas.shape[:2]
        screen_px_range = size_px / self.size * self.range
        pen = float(x)
        prev = None
        for ch in text:
            g = self.glyphs.get(ord(ch))
            if g is None:
                prev = None
                continue
            if use_kern and prev is not None:
                pen += self.kern.get((prev, ord(ch)), 0.0) * size_px
            pb, ab = g.get("planeBounds"), g.get("atlasBounds")
            if pb and ab:
                gl = pen + pb["left"] * size_px
                gr = pen + pb["right"] * size_px
                gt = y - pb["top"] * size_px
                gb = y - pb["bottom"] * size_px
                px0, px1 = int(np.floor(gl)), int(np.ceil(gr)) + 1
                py0, py1 = int(np.floor(gt)), int(np.ceil(gb)) + 1
                px0c, px1c = max(px0, 0), min(px1, W)
                py0c, py1c = max(py0, 0), min(py1, H)
                if px1c > px0c and py1c > py0c:
                    xs = np.arange(px0c, px1c) + 0.5
                    ys = np.arange(py0c, py1c) + 0.5
                    gx, gy = np.meshgrid(xs, ys)
                    u = (gx - gl) / (gr - gl)
                    v = (gy - gt) / (gb - gt)
                    ax = ab["left"] + u * (ab["right"] - ab["left"])
                    ay_bottomup = ab["bottom"] + (1 - v) * (ab["top"] - ab["bottom"])
                    ay = self.h - ay_bottomup
                    ax = np.clip(ax, ab["left"] + 0.5, ab["right"] - 0.5)
                    lo = self.h - ab["top"] + 0.5
                    hi = self.h - ab["bottom"] - 0.5
                    ay = np.clip(ay, lo, hi)
                    rgba = _bilinear(self.img, ax, ay)
                    med = np.median(rgba[..., :3], axis=-1)
                    alpha = np.clip(
                        screen_px_range * (med - 0.5) + 0.5, 0.0, 1.0
                    )[..., None]
                    region = canvas[py0c:py1c, px0c:px1c]
                    region[:] = region * (1 - alpha) + np.asarray(
                        color, dtype=np.float32
                    ) * alpha
            pen += g["advance"] * size_px
            prev = ord(ch)
        return pen


def render_proof_strip(out_dir: Path, build_dir: Path, jsons: dict[str, dict]) -> int:
    """Render a small sample strip using the combined atlas and return the
    count of pixels the text touched (vs. background). Reads the committed
    combined PNG from out_dir but writes the proof image to the (gitignored)
    build_dir - it's a diagnostic, not a shipped asset."""
    combined_png = out_dir / COMBINED_PNG_NAME
    img = Image.open(combined_png)

    ui = VerifyAtlas(jsons["inter-regular"], img)
    ui_med = VerifyAtlas(jsons["inter-semibold"], img)
    mono = VerifyAtlas(jsons["jetbrains-mono-regular"], img)

    W, H = 900, 220
    bg = (0.055, 0.063, 0.078)
    fg = (0.93, 0.94, 0.95)
    canvas = np.zeros((H, W, 3), dtype=np.float32)
    canvas[:] = bg

    ui_med.draw(canvas, "Puck font-atlas bake proof", 24, 60, 34, fg)
    ui.draw(canvas, "Interact: insert the cartridge and boot", 24, 110, 18, fg)
    mono.draw(canvas, "puck> forge avatar --seed 0x11  [OK]", 24, 160, 18, fg)

    diff = np.abs(canvas - np.asarray(bg, dtype=np.float32)).sum(axis=-1)
    text_pixels = int(np.count_nonzero(diff > 0.02))

    proof_img = Image.fromarray((np.clip(canvas, 0, 1) * 255).astype(np.uint8))
    proof_img.save(build_dir / PROOF_PNG_NAME)
    return text_pixels


def self_verify(font_specs: list[FontSpec], out_dir: Path, build_dir: Path) -> None:
    print("\n--- self-verify ---")
    combined_png_path = out_dir / COMBINED_PNG_NAME
    combined_img = Image.open(combined_png_path)
    combined_w, combined_h = combined_img.size
    print(f"combined atlas: {combined_w}x{combined_h} px, "
          f"{combined_png_path.stat().st_size / (1024 * 1024):.2f} MiB")

    jsons: dict[str, dict] = {}
    for spec in font_specs:
        json_path = out_dir / f"{spec.key}.json"
        data = json.loads(json_path.read_text(encoding="utf-8"))
        jsons[spec.key] = data

        assert data["atlas"]["width"] == combined_w, (
            f"{spec.key}: atlas.width {data['atlas']['width']} != "
            f"combined width {combined_w}"
        )
        assert data["atlas"]["height"] == combined_h, (
            f"{spec.key}: atlas.height {data['atlas']['height']} != "
            f"combined height {combined_h}"
        )

        glyph_count = len(data["glyphs"])
        floor = GLYPH_COUNT_FLOORS.get(spec.key)
        if floor is not None:
            assert glyph_count >= floor, (
                f"{spec.key}: glyph count {glyph_count} < floor {floor}"
            )

        kerning_count = len(data.get("kerning", []))
        kfloor = KERNING_COUNT_FLOORS.get(spec.key)
        if kfloor is not None:
            assert kerning_count >= kfloor, (
                f"{spec.key}: kerning count {kerning_count} < floor {kfloor}"
            )
        print(f"  {spec.key}: glyphs={glyph_count} kerning={kerning_count}")

    text_pixels = render_proof_strip(out_dir, build_dir, jsons)
    print(f"  proof strip text pixels: {text_pixels}")
    assert text_pixels > 0, "proof strip rendered zero text pixels"
    print("self-verify OK")


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--msdf-atlas-gen",
        required=True,
        help="Path to msdf-atlas-gen(.exe) v1.4 (downloaded per README.md, not committed).",
    )
    ap.add_argument("--fonts-dir", default=str(DEFAULT_FONTS_DIR))
    ap.add_argument("--build-dir", default=str(DEFAULT_BUILD_DIR))
    ap.add_argument("--out-dir", default=str(DEFAULT_OUT_DIR))
    ap.add_argument(
        "--manifest",
        default=str(DEFAULT_MANIFEST_PATH),
        help="Path to the bake manifest (default: tools/font-atlas/manifest.json).",
    )
    args = ap.parse_args()

    fonts_dir = Path(args.fonts_dir)
    build_dir = Path(args.build_dir)
    out_dir = Path(args.out_dir)
    build_dir.mkdir(parents=True, exist_ok=True)
    out_dir.mkdir(parents=True, exist_ok=True)

    font_specs = load_manifest(Path(args.manifest))

    per_font: list[tuple[FontSpec, dict, Image.Image]] = []

    for spec in font_specs:
        print(f"\n=== {spec.key} ({spec.family} {spec.style}/{spec.weight}) ===")
        ttf_path = fonts_dir / spec.ttf_name
        font = TTFont(str(ttf_path))

        codepoints, dropped = resolve_font_codepoints(font, spec)
        if spec.ranges:
            print(
                f"  declared ranges resolved to {len(codepoints)} codepoints "
                f"present in the font's cmap ({dropped} requested-but-absent skipped)"
            )
        else:
            print(f"  cmap coverage (filtered): {len(codepoints)} codepoints")
        charset_path = build_dir / f"{spec.key}.charset.txt"
        charset_path.write_text(format_charset(codepoints), encoding="utf-8")

        png_path = build_dir / f"{spec.key}.png"
        json_path = build_dir / f"{spec.key}.json"
        run_msdf_atlas_gen(
            args.msdf_atlas_gen,
            ttf_path,
            charset_path,
            png_path,
            json_path,
            uniform_grid=spec.uniform_grid,
        )

        data = json.loads(json_path.read_text(encoding="utf-8"))
        atlas_codepoints = {g["unicode"] for g in data["glyphs"]}
        print(f"  atlas glyphs: {len(atlas_codepoints)}")

        kerning_codepoints = {
            cp for cp in atlas_codepoints if cp <= KERNING_MAX_CODEPOINT
        }
        kerning = extract_gpos_kerning(font, kerning_codepoints)
        data["kerning"] = [
            {"unicode1": cp1, "unicode2": cp2, "advance": round(adv, 6)}
            for (cp1, cp2), adv in sorted(kerning.items())
        ]
        print(f"  GPOS kerning pairs merged: {len(data['kerning'])}")

        img = Image.open(png_path).convert("RGBA")
        per_font.append((spec, data, img))

    print("\n=== repacking into one combined atlas ===")
    combined, rewritten = repack_atlases(per_font)
    print(f"  combined dimensions: {combined.width}x{combined.height}")

    if max(combined.width, combined.height) > MAX_ATLAS_SIDE_PX:
        print(
            f"STOP: combined atlas side {max(combined.width, combined.height)}px "
            f"exceeds the {MAX_ATLAS_SIDE_PX}px cap. Not writing outputs."
        )
        return 1

    combined_out_path = build_dir / COMBINED_PNG_NAME
    combined.save(combined_out_path)
    combined_bytes = combined_out_path.stat().st_size
    if combined_bytes > MAX_ATLAS_FILE_BYTES:
        print(
            f"STOP: combined atlas file size {combined_bytes / (1024 * 1024):.2f} MiB "
            f"exceeds the {MAX_ATLAS_FILE_BYTES / (1024 * 1024):.0f} MiB cap. "
            "Not writing outputs."
        )
        return 1

    for spec in font_specs:
        # Compact (no indent) - the kerning arrays run into the thousands of
        # entries and this is a machine-read runtime asset, not hand-edited,
        # so pretty-printing would just be wasted bytes.
        (build_dir / f"{spec.key}.json").write_text(
            json.dumps(rewritten[spec.key], separators=(",", ":")),
            encoding="utf-8",
        )

    print(f"\n=== copying outputs to {out_dir} ===")
    shutil.copy2(combined_out_path, out_dir / COMBINED_PNG_NAME)
    for spec in font_specs:
        shutil.copy2(build_dir / f"{spec.key}.json", out_dir / f"{spec.key}.json")

    self_verify(font_specs, out_dir, build_dir)

    print(f"\nBake proof strip: {build_dir / PROOF_PNG_NAME} (build-only, not committed)")
    print("Done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
