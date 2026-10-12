// Flat-shaded geometry: each vertex carries a clip-space position (attribute 0) and a color (attribute 1), every vertex
// of a triangle has the same color, and the fragment stage writes that color opaquely.
struct Fragment {
    float4 position : SV_Position;
    nointerpolation float3 color : COLOR0;
};

Fragment vs(float3 position : POSITION0, float3 color : POSITION1) {
    Fragment result;

    result.position = float4(position, 1.0);
    result.color = color;
    return result;
}

float4 ps(Fragment input) : SV_Target0 {
    return float4(input.color, 1.0);
}
