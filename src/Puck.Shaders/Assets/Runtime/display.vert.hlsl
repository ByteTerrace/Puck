// The display encode's fullscreen triangle, drawn with no vertex buffer: three clip-space corners by vertex index that
// cover the whole target. The encode reads its source by fragment coordinate, so it never depends on which way up the
// clip space runs.
struct VSOutput {
    float4 position : SV_Position;
};

VSOutput VSMain(uint vertexId : SV_VertexID) {
    float2 positions[3] = { float2(-1.0, -1.0), float2(-1.0, 3.0), float2(3.0, -1.0) };
    VSOutput output;
    output.position = float4(positions[vertexId], 0.0, 1.0);
    return output;
}
