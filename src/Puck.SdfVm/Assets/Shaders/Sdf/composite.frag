#version 450

// The compositor: place up to four rendered viewport textures into their current screen regions, with
// a shader-driven ripple while a "Warp" layout transition is in flight. Each region's rect is animated
// on the CPU (eased per the layout's curve); this stage scales each view into its rect and, where a
// Warp transition is active, distorts the sampling coordinates. Back-to-front: later viewports win, so
// a picture-in-picture inset composites on top of the full-screen view.
layout(set = 0, binding = 0) uniform sampler2D viewTextures[4];

layout(push_constant) uniform CompositePushConstants {
    vec4 rects[4];    // per viewport: xy = top-left, zw = size (normalized screen space)
    vec4 params;      // x = viewport count, y = warp amount, z = time
    vec4 resolution;  // xy = framebuffer size in pixels
} pc;

layout(location = 0) out vec4 outColor;

// Constant-indexed sampling so no dynamic sampler-array indexing feature is required.
vec3 sampleView(int index, vec2 uv) {
    if (index == 0) { return texture(viewTextures[0], uv).rgb; }
    if (index == 1) { return texture(viewTextures[1], uv).rgb; }
    if (index == 2) { return texture(viewTextures[2], uv).rgb; }

    return texture(viewTextures[3], uv).rgb;
}

void main() {
    vec2 uv = (gl_FragCoord.xy / pc.resolution.xy);
    int count = int(pc.params.x + 0.5);
    float warp = pc.params.y;
    float time = pc.params.z;
    vec3 color = vec3(0.015, 0.017, 0.022); // the gutter between/behind panes

    for (int index = 0; (index < 4); index++) {
        if (index >= count) {
            break;
        }

        vec4 rect = pc.rects[index];

        if ((rect.z <= 0.0001) || (rect.w <= 0.0001)) {
            continue; // hidden viewport
        }

        vec2 local = ((uv - rect.xy) / rect.zw);

        if (warp > 0.0) {
            local.x += (warp * 0.05 * sin((local.y * 28.0) + (time * 6.0)));
            local.y += (warp * 0.05 * sin((local.x * 24.0) - (time * 5.0)));
        }

        if ((local.x >= 0.0) && (local.x <= 1.0) && (local.y >= 0.0) && (local.y <= 1.0)) {
            color = sampleView(index, local);
        }
    }

    outColor = vec4(color, 1.0);
}
