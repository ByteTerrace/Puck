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
// The band whose run holds a slot, or -1: a search over the bands in the order of their first slots, which the tables
// sort them in (SdfWorldTables.SortFormBands).
int sdfFormBandOf(uint slot) {
    uint count = sdfFormBandCount();

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
// A band's flags (SDF_FORM_BAND_SWEPT_FLAG, SDF_FORM_BAND_PARTED_FLAG).
uint sdfFormBandFlags(uint band) {
    return sdfFrameSampled[sdfFormBandWord(band) + SDF_FORM_BAND_FLAGS_WORD];
}
// How far a world point lies ahead of a band's front, metres along its axis: positive where the front has not reached,
// negative where it has passed. The front runs from its start to its end over its seconds, and the lattice noise at the
// point, keyed on the band's seed, moves it by up to the band's roughness, so the cut and the edge are ragged on the real
// surface rather than a flat plane.
float sdfFormBandAhead(uint band, float3 p) {
    uint word = sdfFormBandWord(band);
    float4 origin = sdfFormBandFloats(word + SDF_FORM_BAND_ORIGIN_WORD);
    float4 axis = sdfFormBandFloats(word + SDF_FORM_BAND_AXIS_WORD);
    float4 sweep = sdfFormBandFloats(word + SDF_FORM_BAND_SWEEP_WORD);
    float progress = saturate(sweep.z / max(sweep.w, 1.0e-6));
    float front = (origin.w + ((axis.w - origin.w) * progress));
    float rough = ((sweep.x > 0.0) ? (sweep.x * sdfLatticeNoise3((p * sweep.y), sdfFrameSampled[word + SDF_FORM_BAND_SEED_WORD])) : 0.0);

    return ((dot((p - origin.xyz), axis.xyz) + rough) - front);
}
// How far a world point lies into the side of a band that stays drawn: behind the front for a materializing run, ahead
// of it for a dissolving one; negative on the side that is cut.
float sdfFormBandKept(uint band, float3 p) {
    float ahead = sdfFormBandAhead(band, p);

    return (((sdfFormBandFlags(band) & SDF_FORM_BAND_SWEPT_FLAG) != 0u) ? -ahead : ahead);
}
// Discards a raster fragment at world point `p` of a draw on slot `slot` that its run's band cuts: on the cut side of
// the front, or any fragment of a skinned draw (`skinned`) in a run whose slots move apart.
void sdfFormBandClip(uint slot, bool skinned, float3 p) {
    int band = sdfFormBandOf(slot);

    if (band < 0) { return; }
    if (skinned && ((sdfFormBandFlags(uint(band)) & SDF_FORM_BAND_PARTED_FLAG) != 0u)) { clip(-1.0); }
    clip(sdfFormBandKept(uint(band), p));
}
#ifdef SDF_MESH_HLSLI
// Discards a mesh raster fragment at world point `p` of draw `draw` that its frame slot's band cuts (sdfFormBandClip),
// before the fragment writes depth or a glass sum, as the stamp fade does.
void sdfMeshFormBandClip(uint draw, float3 p) {
    uint record = sdfMeshRecord(draw);

    sdfFormBandClip(sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD), ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_SKIN_FLAG) != 0u), p);
}
#endif
// The light a band's edge emits at a surface point of slot `slot`, in nits: the edge's light, falling off over its
// width behind the front on the side that stays; black for a slot no band holds.
float3 sdfFormBandEdge(int slot, float3 p) {
    if (slot < 0) { return float3(0.0, 0.0, 0.0); }

    int band = sdfFormBandOf(uint(slot));

    if (band < 0) { return float3(0.0, 0.0, 0.0); }

    float4 edge = sdfFormBandFloats(sdfFormBandWord(uint(band)) + SDF_FORM_BAND_EDGE_WORD);

    if (!(edge.w > 0.0) || !any(edge.xyz > 0.0)) { return float3(0.0, 0.0, 0.0); }

    float fall = (1.0 - saturate(sdfFormBandKept(uint(band), p) / edge.w));

    return (edge.xyz * (fall * fall));
}

#endif
