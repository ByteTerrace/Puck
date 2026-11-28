// The frame's form bands (SdfFormBand), read from the frame's sampled table (SdfFrameSampled's band layout): a front that
// sweeps over one run of dynamic-transform slots, cutting the run's draws on the side it has not built or has already
// dissolved, an emissive edge on the side that stays, and the motes the mote pass flies off or onto the surface it crosses.
// The front's place at a surface point is one formula (sdfFormBandAhead), which the raster's cut, the views pass's edge and
// every mote's birth read alike, so the three agree. Requires sdfFrameSampled from the including interface.
#ifndef SDF_FORM_BAND_HLSLI
#define SDF_FORM_BAND_HLSLI
#include "../isa/sdf-render.hlsli"
#include "../field/sdf-noise.hlsli"

// The bands the table holds: none for a table shorter than its header or than the bands it names (a test's or a stand-in
// table), at most SDF_FORM_BAND_MAX_BANDS.
uint sdfFormBandCount() {
    uint words;
    uint stride;

    sdfFrameSampled.GetDimensions(words, stride);
    if (words < SDF_FRAME_SAMPLED_TABLE_HEADER_WORDS) { return 0u; }

    uint bands = min(sdfFrameSampled[SDF_FRAME_SAMPLED_BAND_COUNT_WORD], SDF_FORM_BAND_MAX_BANDS);
    uint base = sdfFrameSampled[SDF_FRAME_SAMPLED_BAND_BASE_WORD];

    return (((base <= words) && ((bands * SDF_FORM_BAND_WORDS) <= (words - base))) ? bands : 0u);
}
// The first word of band `band`.
uint sdfFormBandWord(uint band) {
    return (sdfFrameSampled[SDF_FRAME_SAMPLED_BAND_BASE_WORD] + (band * SDF_FORM_BAND_WORDS));
}
// Four float words of a band from `word` on.
float4 sdfFormBandFloats(uint word) {
    return asfloat(uint4(sdfFrameSampled[word], sdfFrameSampled[word + 1u], sdfFrameSampled[word + 2u], sdfFrameSampled[word + 3u]));
}
// The bands keyed by a run of slots, which lead the table; the rest are keyed by a displacement handle.
uint sdfFormBandSlotCount(uint count) {
    return min(sdfFrameSampled[SDF_FRAME_SAMPLED_SLOT_BAND_COUNT_WORD], count);
}
// The band whose run holds a slot, or -1: a search over the slot bands in the order of their first slots, which the
// tables sort them in (SdfWorldTables.SortFormBands).
int sdfFormBandOf(uint slot) {
    uint count = sdfFormBandSlotCount(sdfFormBandCount());

    if (count == 0u) { return -1; }

    int low = 0;
    int high = (int(count) - 1);

    [loop]
    for (uint step = 0u; ((step < 11u) && (low <= high)); step++) {
        int middle = ((low + high) >> 1);
        uint word = sdfFormBandWord(uint(middle));
        uint first = sdfFrameSampled[word + SDF_FORM_BAND_FIRST_SLOT_WORD];

        if (slot < first) {
            high = (middle - 1);
        } else if ((slot - first) >= sdfFrameSampled[word + SDF_FORM_BAND_SLOT_COUNT_WORD]) {
            low = (middle + 1);
        } else {
            return middle;
        }
    }
    return -1;
}
// The band keyed by a displacement handle (a draw record's SDF_MESH_DISPLACEMENT_WORD), or -1: a walk over the bands
// after the slot bands, which are few (a net parting), so an undisplaced draw pays one comparison.
int sdfFormBandOfDisplacement(uint handle) {
    if (handle == SDF_FRAME_SAMPLED_NONE) { return -1; }

    uint count = sdfFormBandCount();

    [loop]
    for (uint band = sdfFormBandSlotCount(count); (band < count); band++) {
        if (sdfFrameSampled[sdfFormBandWord(band) + SDF_FORM_BAND_FIRST_SLOT_WORD] == handle) { return int(band); }
    }
    return -1;
}
// A band's flags (SDF_FORM_BAND_SWEPT_FLAG, SDF_FORM_BAND_PARTED_FLAG, SDF_FORM_BAND_WHOLE_FLAG,
// SDF_FORM_BAND_DISPLACED_FLAG, SDF_FORM_BAND_RADIAL_FLAG).
uint sdfFormBandFlags(uint band) {
    return sdfFrameSampled[sdfFormBandWord(band) + SDF_FORM_BAND_FLAGS_WORD];
}
// How far a world point lies ahead of a band's front, metres along its axis (or out from its origin, for a radial
// front): positive where the front has not reached, negative where it has passed. The front runs from its start to its
// end over its seconds, and the lattice noise at the point, keyed on the band's seed, moves it by up to the band's
// roughness, so the cut and the edge are ragged on the real surface rather than a flat plane or a true sphere.
float sdfFormBandAhead(uint band, float3 p) {
    uint word = sdfFormBandWord(band);
    float4 origin = sdfFormBandFloats(word + SDF_FORM_BAND_ORIGIN_WORD);
    float4 axis = sdfFormBandFloats(word + SDF_FORM_BAND_AXIS_WORD);
    float4 sweep = sdfFormBandFloats(word + SDF_FORM_BAND_SWEEP_WORD);
    float progress = saturate(sweep.z / max(sweep.w, 1.0e-6));
    float front = (origin.w + ((axis.w - origin.w) * progress));
    float rough = ((sweep.x > 0.0) ? (sweep.x * sdfLatticeNoise3((p * sweep.y), sdfFrameSampled[word + SDF_FORM_BAND_SEED_WORD])) : 0.0);
    float along = (((sdfFormBandFlags(band) & SDF_FORM_BAND_RADIAL_FLAG) != 0u) ? length(p - origin.xyz) : dot((p - origin.xyz), axis.xyz));

    return ((along + rough) - front);
}
// How far a world point lies into the side of a band that stays drawn: behind the front for a materializing run, ahead
// of it for a dissolving one, and its distance from the front either way for a run that stays whole; negative on the
// side that is cut.
float sdfFormBandKept(uint band, float3 p) {
    float ahead = sdfFormBandAhead(band, p);
    uint flags = sdfFormBandFlags(band);

    if ((flags & SDF_FORM_BAND_WHOLE_FLAG) != 0u) { return abs(ahead); }
    return (((flags & SDF_FORM_BAND_SWEPT_FLAG) != 0u) ? -ahead : ahead);
}
// Discards a raster fragment at world point `p` that band `band` cuts (none for -1): on the cut side of the front, or
// any fragment of a skinned draw (`skinned`) in a run whose slots move apart.
void sdfFormBandClip(int band, bool skinned, float3 p) {
    if (band < 0) { return; }
    if (skinned && ((sdfFormBandFlags(uint(band)) & SDF_FORM_BAND_PARTED_FLAG) != 0u)) { clip(-1.0); }
    clip(sdfFormBandKept(uint(band), p));
}
// The light band `band` (none for -1) emits at a surface point, in nits: the edge's light, falling off over its width
// behind the front on the side that stays.
float3 sdfFormBandEdgeOf(int band, float3 p) {
    if (band < 0) { return float3(0.0, 0.0, 0.0); }

    float4 edge = sdfFormBandFloats(sdfFormBandWord(uint(band)) + SDF_FORM_BAND_EDGE_WORD);

    if (!(edge.w > 0.0) || !any(edge.xyz > 0.0)) { return float3(0.0, 0.0, 0.0); }

    float fall = (1.0 - saturate(sdfFormBandKept(uint(band), p) / edge.w));

    return (edge.xyz * (fall * fall));
}
#ifdef SDF_MESH_HLSLI
// The band a draw record's draws ride: the one keyed by the displacement it is drawn through (a net's sheet, whose
// static draw rides no slot), else the one whose run holds its frame slot, else -1. The one door every reader of a
// draw's band takes, so the cut, the edge and the motes' rows (SdfWorldTables.PackFormBandRows, in the same order) agree.
int sdfMeshFormBand(uint record, int slot) {
    int band = sdfFormBandOfDisplacement(sdfMeshRecordWord(record + SDF_MESH_DISPLACEMENT_WORD));

    return (((band < 0) && (slot >= 0)) ? sdfFormBandOf(uint(slot)) : band);
}
// Discards a mesh raster fragment at world point `p` of draw `draw` that its band cuts (sdfMeshFormBand), before the
// fragment writes depth or a glass sum, as the stamp fade does.
void sdfMeshFormBandClip(uint draw, float3 p) {
    uint record = sdfMeshRecord(draw);

    sdfFormBandClip(sdfMeshFormBand(record, asint(sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD))),
        ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_SKIN_FLAG) != 0u), p);
}
// The light a resolved hit's band emits at a surface point (sdfMeshFormBand), `slot` being the slot the hit shades in
// this view.
float3 sdfMeshFormBandEdge(SdfMeshHit hit, int slot, float3 p) {
    return sdfFormBandEdgeOf(sdfMeshFormBand(hit.record, slot), p);
}
#endif

#endif
