// The pattern kind: a painted pattern over the layer frame's azimuth and elevation, measured in cells: Cells (rounded to a
// whole number, at least one, so the pattern closes on itself) across a turn of azimuth, and as many per turn of
// elevation, so a cell is as tall as it is wide at the horizon. A checker alternates ColorA and ColorB; stripes draw
// ColorB bands Line of a cell wide across elevation; a grid draws ColorB lines along both. Every edge is softened over
// Softness of a cell, so the pattern stays band-limited for the field extent the sky evaluates it at. It scrolls one cell
// in azimuth a cycle of the layer's clock. Opaque.
#ifndef SKY_KINDS_PATTERN_HLSLI
#define SKY_KINDS_PATTERN_HLSLI

// The share of a line Line of a cell wide about each whole cell boundary at a cell coordinate, softened over soft.
float sdfSkyPatternLine(float coordinate, float width, float soft) {
    float distance = abs(frac(coordinate + 0.5) - 0.5);

    return (1.0 - sdfSkyRise(((0.5 * width) + soft), soft, distance));
}
float4 sdfSkyPatternLayer(SdfSkyPattern pattern, SdfSkyLayer layer, SdfSkySample sample) {
    sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, 0u);

    float3 direction = sample.local;
    float cells = max(round(pattern.Cells), 1.0);
    float u = ((((atan2(direction.z, direction.x) * 0.15915494) + 0.5) * cells) + layer.Phase);
    float v = ((asin(clamp(direction.y, -1.0, 1.0)) * 0.15915494) * cells);
    float soft = max(pattern.Softness, 0.0);
    float t;

    if (pattern.Shape == SDF_SKY_PATTERN_STRIPES) {
        t = sdfSkyPatternLine(v, pattern.Line, soft);
    } else if (pattern.Shape == SDF_SKY_PATTERN_GRID) {
        t = max(sdfSkyPatternLine(u, pattern.Line, soft), sdfSkyPatternLine(v, pattern.Line, soft));
    } else {
        // A smoothed square wave on each axis, minus one on the cell's first half and one on its second, multiplied.
        float width = max((0.5 * soft), 1.0e-4);
        float waveU = clamp(((frac(u) - 0.5) / width), -1.0, 1.0) * clamp(((0.5 - abs(frac(u) - 0.5)) / width), 0.0, 1.0);
        float waveV = clamp(((frac(v) - 0.5) / width), -1.0, 1.0) * clamp(((0.5 - abs(frac(v) - 0.5)) / width), 0.0, 1.0);

        t = (0.5 + (0.5 * (waveU * waveV)));
    }

    return float4(lerp(pattern.ColorA, pattern.ColorB, t), 1.0);
}
#endif
