#version 450

// The terminal's blit: sample the one surface the root node produced, 1:1, onto the swapchain. The
// source texture matches the framebuffer extent, so screen UV is fragment coord over texture size.
layout(set = 0, binding = 0) uniform sampler2D sourceTexture;

layout(location = 0) out vec4 outColor;

void main() {
    vec2 uv = (gl_FragCoord.xy / vec2(textureSize(sourceTexture, 0)));

    outColor = vec4(texture(sourceTexture, uv).rgb, 1.0);
}
