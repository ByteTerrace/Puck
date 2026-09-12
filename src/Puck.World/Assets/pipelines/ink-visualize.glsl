void mainImage(out vec4 color, in vec2 pixel) {
    vec2 uv = pixel / iResolution.xy;
    float ink = texture(iChannel0, uv).r;
    vec3 background = vec3(0.018, 0.027, 0.060);
    vec3 pigment = 0.5 + 0.5 * cos(vec3(0.0, 1.8, 3.6) + ink * 4.0 + uv.y * 1.4);
    color = vec4(mix(background, pigment, smoothstep(0.015, 0.8, ink)), 1);
}
