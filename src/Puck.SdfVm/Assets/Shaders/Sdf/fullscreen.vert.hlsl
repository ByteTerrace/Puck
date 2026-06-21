// Fullscreen triangle — HLSL port of fullscreen.vert (dual-source; the GLSL stays the Vulkan form). Three
// clip-space float2 positions from the vertex buffer, passed straight through; the SDF raymarch and the
// composite happen entirely in the pixel stage.
struct VSOutput {
    float4 position : SV_Position;
};

VSOutput VSMain(float2 inPosition : POSITION) {
    VSOutput output;

    output.position = float4(inPosition, 0.0, 1.0);

    return output;
}
