// Ink is advected through a slowly turning flow and retained in a floating-point history image.
void mainImage(out vec4 color, in vec2 pixel) {
    vec2 uv = pixel / iResolution.xy;
    vec2 center = uv - 0.5;
    vec2 flow = vec2(-center.y, center.x) * 0.004;
    vec4 previous = texture(iChannel0, clamp(uv - flow, vec2(0), vec2(1)));
    vec2 pen = 0.5 + 0.27 * vec2(cos(iTime * 0.91), sin(iTime * 1.17));
    if (iMouse.z > 0.0) { pen = iMouse.xy / iResolution.xy; }
    float ink = exp(-dot(uv - pen, uv - pen) * 1700.0);
    float value = max(previous.r * 0.987, ink);
    color = vec4(value, previous.r, 0, 1);
}
