// The Direct3D 12 surface compositor's fullscreen triangle, drawn with no vertex buffer (DirectXSurfaceCompositor
// creates its pipeline with no input layout). Vertex 1 and vertex 2 sit at texture coordinate 2 on one axis each, so
// the triangle covers the whole back buffer and its visible part spans coordinates 0 to 1, top-left first.
struct VSOutput {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOutput VSMain(uint id : SV_VertexID) {
    float2 uv = float2(((id == 1u) ? 2.0 : 0.0), ((id == 2u) ? 2.0 : 0.0));
    VSOutput output;

    output.uv = uv;
    output.position = float4(((uv.x * 2.0) - 1.0), (1.0 - (uv.y * 2.0)), 0.0, 1.0);

    return output;
}
