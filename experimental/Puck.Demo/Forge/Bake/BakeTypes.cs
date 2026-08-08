using Puck.Cameras;
using Puck.SdfVm;

namespace Puck.Demo.Forge.Bake;

/// <summary>What a bake plan produces: a full-screen background layer or a metasprite set.</summary>
internal enum BakeIntent {
    /// <summary>A 160×144 background — tiles + a 32×32 tilemap (and, on CGB, an attribute map).</summary>
    Background = 0,
    /// <summary>A sprite subject — trimmed OAM metasprite frames sharing one jointly-fitted palette set.</summary>
    Sprite = 1,
}

/// <summary>The brick hardware generation the bake targets.</summary>
internal enum BakeTarget {
    /// <summary>The monochrome brick: one 4-shade ramp, no attribute map, the luma curve is the knob.</summary>
    Dmg = 0,
    /// <summary>The colour brick: up to 8 background + 8 object palettes of 4 RGB555 colours each.</summary>
    Cgb = 1,
}

/// <summary>One view the rasterizer renders: a self-contained static SDF program and the camera that frames it.</summary>
/// <param name="Program">The static program (every transform folded — no dynamic slots).</param>
/// <param name="Camera">The camera snapshot framing the subject.</param>
/// <param name="Name">The view's name (a sprite pose id, or the background scene name).</param>
internal sealed record BakeView(SdfProgram Program, CameraSnapshot Camera, string Name);

/// <summary>One rasterized view: the GPU render + readback at SUPERSAMPLED resolution, before any CPU stage.</summary>
/// <param name="Name">The source view's name.</param>
/// <param name="Rgba">The tightly packed RGBA8 pixels, row-major.</param>
/// <param name="Width">The raster width in pixels (native × supersample).</param>
/// <param name="Height">The raster height in pixels (native × supersample).</param>
internal sealed record RasterizedView(string Name, byte[] Rgba, int Width, int Height);

/// <summary>The hardware budgets the bake reports against. Warnings REPORT an exceeded budget; the bake never
/// silently drops content to fit one.</summary>
/// <param name="MaxTiles">The VRAM tile budget (single-byte tile ids).</param>
/// <param name="MaxBackgroundPalettes">The background palette budget (CGB palette RAM).</param>
/// <param name="MaxObjectPalettes">The object palette budget (CGB palette RAM).</param>
/// <param name="OamTotal">The hardware OAM entry total per frame.</param>
/// <param name="OamPerLine">The hardware per-scanline sprite limit.</param>
internal sealed record BakeBudget(
    int MaxTiles = 256,
    int MaxBackgroundPalettes = 8,
    int MaxObjectPalettes = 8,
    int OamTotal = 40,
    int OamPerLine = 10
);

/// <summary>A complete bake plan: what to rasterize and how to crush it. The rasterizer renders each view at
/// (<see cref="NativeWidth"/> × <see cref="BakeStyle.SupersampleFactor"/>) and the CPU half grades, reduces,
/// palette-fits, and assembles tiles from the result.</summary>
/// <param name="Views">The views to rasterize (one for a background; facings × poses for a sprite).</param>
/// <param name="Intent">Whether the plan bakes a background layer or a sprite set.</param>
/// <param name="Target">The hardware generation to fit.</param>
/// <param name="Style">The grading/dither/outline recipe.</param>
/// <param name="Budget">The hardware budgets to report against.</param>
/// <param name="NativeWidth">The native (post-reduce) width in pixels.</param>
/// <param name="NativeHeight">The native (post-reduce) height in pixels.</param>
internal sealed record BakePlan(
    IReadOnlyList<BakeView> Views,
    BakeIntent Intent,
    BakeTarget Target,
    BakeStyle Style,
    BakeBudget Budget,
    int NativeWidth,
    int NativeHeight
);
