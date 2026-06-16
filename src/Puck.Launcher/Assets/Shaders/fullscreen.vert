#version 450

// Fullscreen triangle: three clip-space vec2 positions from the compositor's vertex buffer, passed
// straight through. The blit happens entirely in the fragment stage.
layout(location = 0) in vec2 inPosition;

void main() {
    gl_Position = vec4(inPosition, 0.0, 1.0);
}
