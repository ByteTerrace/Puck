// Fullscreen triangle (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for DirectX). Three
// clip-space float2 positions from the compositor's vertex buffer, passed straight through; the blit happens
// entirely in the pixel stage. [[vk::location(0)]] pins the Vulkan vertex input to location 0.
struct VSOutput {
    float4 position : SV_Position;
};

VSOutput VSMain([[vk::location(0)]] float2 inPosition : POSITION) {
    VSOutput output;

    output.position = float4(inPosition, 0.0, 1.0);

    return output;
}
