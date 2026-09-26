// Draws a rulepush board from the level's tiles row: one tile a cell, letterboxed into the pane with square cells.
// The generated interface declares the frame group, the pass block (extent, width, height), the output and the World
// group's tiles array, which a views.graphs parameter binds to state.tiles.
#include "board.interface.hlsli"

// Looks, as rules.puck writes them into tiles: 0 an empty cell, noun + 1 an object, WORD_LOOK + word a word tile. The
// nouns count from IMP, the words from a noun word (noun + 1) through IS to the six properties.
#define LOOK_IMP 1
#define LOOK_HEDGE 2
#define LOOK_BOULDER 3
#define LOOK_FLOWER 4
#define LOOK_MIRE 5
#define LOOK_THORN 6
#define WORD_LOOK 16
#define WORD_IS 7
#define WORD_YOU 8
#define WORD_PUSH 9
#define WORD_STOP 10
#define WORD_SINK 11
#define WORD_DEFEAT 12
#define WORD_WIN 13

// The palette: dusk tones for the board, and one hue a noun or property. Every object is flat at its cell's centre.
static const float3 Margin = float3(0.035, 0.040, 0.055);
static const float3 Floor = float3(0.105, 0.115, 0.145);
static const float3 Seam = float3(0.150, 0.165, 0.205);
static const float3 Ember = float3(0.950, 0.480, 0.180);
static const float3 Soot = float3(0.090, 0.050, 0.040);
static const float3 Moss = float3(0.200, 0.520, 0.260);
static const float3 MossDeep = float3(0.120, 0.360, 0.170);
static const float3 Stone = float3(0.500, 0.460, 0.420);
static const float3 StoneDeep = float3(0.330, 0.300, 0.280);
static const float3 Rose = float3(0.930, 0.450, 0.660);
static const float3 Gold = float3(0.980, 0.830, 0.300);
static const float3 Murk = float3(0.140, 0.300, 0.300);
static const float3 Ripple = float3(0.230, 0.430, 0.410);
static const float3 Violet = float3(0.550, 0.280, 0.700);
static const float3 Plate = float3(0.200, 0.180, 0.240);
static const float3 Cream = float3(0.930, 0.900, 0.800);
static const float3 Coral = float3(0.960, 0.360, 0.460);
static const float3 Amber = float3(0.900, 0.660, 0.220);
static const float3 Slate = float3(0.400, 0.550, 0.850);
static const float3 Lagoon = float3(0.250, 0.700, 0.720);
static const float3 Crimson = float3(0.850, 0.200, 0.200);

// Coverage of a signed distance in cell units, antialiased over about one pixel.
float cover(float distance, float pixel) {
    return saturate(0.5 - (distance / pixel));
}
float circle(float2 q, float2 centre, float radius) {
    return (length(q - centre) - radius);
}
float roundedBox(float2 q, float2 halfSize, float radius) {
    float2 d = ((abs(q) - halfSize) + radius);

    return ((length(max(d, 0.0)) + min(max(d.x, d.y), 0.0)) - radius);
}
float segment(float2 q, float2 a, float2 b) {
    float2 pa = (q - a);
    float2 ba = (b - a);

    return length(pa - (ba * saturate(dot(pa, ba) / dot(ba, ba))));
}
// A star of `points` spikes between an inner and an outer radius, pointing up.
float star(float2 q, float points, float inner, float outer) {
    float angle = atan2(q.x, -q.y);
    float sector = (6.2831853 / points);
    float along = (abs(frac((angle / sector) + 0.5) - 0.5) * 2.0);

    return (length(q) - lerp(outer, inner, along));
}

// Paints one object look at q in [-1, 1] over the colour beneath it.
float3 paintObject(int look, float2 q, float pixel, float3 under) {
    float3 color = under;

    switch (look) {
        case LOOK_IMP: {
            // An ember sprite: a round body, two short horns and two soot eyes set clear of the centre.
            color = lerp(color, Ember, cover(min(circle(q, float2(0.0, 0.08), 0.62), min(segment(q, float2(-0.32, -0.42), float2(-0.46, -0.78)) - 0.08, segment(q, float2(0.32, -0.42), float2(0.46, -0.78)) - 0.08)), pixel));
            color = lerp(color, Soot, cover(min(circle(q, float2(-0.24, -0.12), 0.09), circle(q, float2(0.24, -0.12), 0.09)), pixel));
            break;
        }
        case LOOK_HEDGE: {
            // A clipped hedge: a rounded block with deeper leaf clusters at its corners.
            color = lerp(color, Moss, cover(roundedBox(q, float2(0.86, 0.86), 0.28), pixel));
            float2 corner = (abs(q) - float2(0.5, 0.5));

            color = lerp(color, MossDeep, cover(length(corner) - 0.16, pixel));
            break;
        }
        case LOOK_BOULDER: {
            // A boulder: a lumpy stone with a crack running off one shoulder.
            float lumps = (circle(q, float2(0.0, 0.05), 0.70) + (0.05 * sin((atan2(q.y, q.x) * 5.0) + 0.7)));

            color = lerp(color, Stone, cover(lumps, pixel));
            color = lerp(color, StoneDeep, cover(segment(q, float2(0.30, -0.55), float2(0.52, -0.12)) - 0.045, pixel));
            break;
        }
        case LOOK_FLOWER: {
            // A flower: five rose petals round a gold heart.
            float petals = 10.0;

            for (int petal = 0; (petal < 5); petal++) {
                float angle = (1.2566371 * petal);

                petals = min(petals, circle(q, (0.44 * float2(sin(angle), -cos(angle))), 0.30));
            }

            color = lerp(color, Rose, cover(petals, pixel));
            color = lerp(color, Gold, cover(circle(q, float2(0.0, 0.0), 0.26), pixel));
            break;
        }
        case LOOK_MIRE: {
            // A mire: the whole cell murky, with two slow ripples across it.
            color = lerp(color, Murk, cover(roundedBox(q, float2(0.98, 0.98), 0.06), pixel));
            float ripple = min(abs((q.y + 0.45) - (0.10 * sin(q.x * 5.0))), abs((q.y - 0.45) - (0.10 * sin((q.x * 5.0) + 2.0))));

            color = lerp(color, Ripple, cover(ripple - 0.05, pixel));
            break;
        }
        case LOOK_THORN: {
            // A thorn: a violet burst of six spikes.
            color = lerp(color, Violet, cover(star(q, 6.0, 0.30, 0.86), pixel));
            break;
        }
    }

    return color;
}
// Paints one property word's mark at q in [-1, 1] over the colour beneath it.
float3 paintProperty(int word, float2 q, float pixel, float3 under) {
    switch (word) {
        case WORD_YOU:
            return lerp(under, Coral, cover(abs(circle(q, float2(0.0, 0.0), 0.42)) - 0.13, pixel));
        case WORD_PUSH:
            return lerp(under, Amber, cover(min(segment(q, float2(-0.30, -0.45), float2(0.25, 0.0)), segment(q, float2(0.25, 0.0), float2(-0.30, 0.45))) - 0.13, pixel));
        case WORD_STOP:
            return lerp(under, Slate, cover(roundedBox(q, float2(0.50, 0.20), 0.06), pixel));
        case WORD_SINK: {
            float ripple = min(abs((q.y + 0.22) - (0.12 * sin(q.x * 6.0))), abs((q.y - 0.22) - (0.12 * sin(q.x * 6.0))));

            return lerp(under, Lagoon, cover(ripple - 0.08, pixel));
        }
        case WORD_DEFEAT:
            return lerp(under, Crimson, cover(min(segment(q, float2(-0.40, -0.40), float2(0.40, 0.40)), segment(q, float2(-0.40, 0.40), float2(0.40, -0.40))) - 0.12, pixel));
        case WORD_WIN:
            return lerp(under, Gold, cover(star(q, 5.0, 0.24, 0.58), pixel));
        default:
            return under;
    }
}
// Paints a word tile: a dark plate edged in the word's hue, carrying its noun's small likeness, two bars for IS, or its
// property's mark.
float3 paintWord(int word, float2 q, float pixel, float3 under) {
    static const float3 Hues[14] = {
        Cream, Ember, Moss, Stone, Rose, Murk, Violet, Cream, Coral, Amber, Slate, Lagoon, Crimson, Gold,
    };
    float plate = roundedBox(q, float2(0.86, 0.86), 0.18);
    float3 color = lerp(under, Hues[clamp(word, 0, 13)], cover(plate, pixel));

    color = lerp(color, Plate, cover(plate + 0.10, pixel));
    if (word < WORD_IS) {
        return paintObject(word, (q / 0.55), (pixel / 0.55), color);
    }
    if (word == WORD_IS) {
        return lerp(color, Cream, cover(min(roundedBox(q - float2(0.0, 0.20), float2(0.46, 0.09), 0.04), roundedBox(q + float2(0.0, 0.20), float2(0.46, 0.09), 0.04)), pixel));
    }

    return paintProperty(word, q, pixel, color);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    uint2 cells = max(uint2(passGroup.width, passGroup.height), uint2(1, 1));
    float2 extent = float2(passGroup.extent);
    // Square cells as large as the pane allows, the board centred in it.
    float size = min((extent.x / cells.x), (extent.y / cells.y));
    float2 origin = (0.5 * (extent - (size * float2(cells))));
    float2 at = (((float2(id.xy) + 0.5) - origin) / size);

    if (any(at < 0.0) || any(at >= float2(cells))) {
        board[id.xy] = float4(Margin, 1.0);
        return;
    }

    uint2 cell = uint2(at);
    float2 q = ((frac(at) * 2.0) - 1.0);
    // One pixel in q's units, which the coverage antialiases over.
    float pixel = (2.0 / size);
    float seam = (max(abs(q.x), abs(q.y)) - 0.94);
    float3 color = lerp(Floor, Seam, cover(-seam, pixel));
    int look = tilesAt((cell.y * cells.x) + cell.x);

    if (look >= WORD_LOOK) {
        color = paintWord((look - WORD_LOOK), q, pixel, color);
    } else {
        color = paintObject(look, q, pixel, color);
    }

    board[id.xy] = float4(color, 1.0);
}
