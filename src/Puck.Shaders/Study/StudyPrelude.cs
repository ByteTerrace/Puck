namespace Puck.Shaders.Study;

/// <summary>
/// The fixed GLSL text wrapped around an author's Shadertoy-dialect study source before it reaches the toolchain,
/// making it a COMPUTE kernel: one 8×8 workgroup per tile writing a storage image (the same-device, general-layout
/// storage image a hosted child hands the world compositor — never a graphics render target, which neither backend
/// can bind as the compositor's storage-image source), a <c>PuckStudy</c> push-constant block (112 bytes, identical
/// on both backends — the C# mirror is <see cref="StudyPushConstants"/>) feeding the <c>iXxx</c> globals Shadertoy
/// authors already expect, plus Puck's own paired camera under <c>PUCK_STUDY</c>, and a fixed <c>main()</c> that calls
/// the author's <c>mainImage</c> once per pixel. Field order and byte offsets are pinned by std430/HLSL
/// constant-buffer packing (every field on a 4-byte boundary, a vector bumped past a 16-byte row it would straddle) —
/// <c>StudyPushConstants</c> must mirror this struct word for word.
/// </summary>
public static class StudyPrelude {
    /// <summary>The kernel's workgroup edge (<c>local_size_x</c>/<c>local_size_y</c>) — <see cref="StudyPassNode"/>
    /// dispatches <c>ceil(width / 8) × ceil(height / 8)</c> groups.</summary>
    public const uint WorkgroupSize = 8;
    /// <summary>
    /// Prepended to the author's source before compilation. Ends with a blank line so <see cref="PreludeLineCount"/>
    /// (the count of '\n' in this text) is exactly the 1-based line number immediately before the author's first
    /// source line — a diagnostic's reported line, less this count, is the line in the author's own file.
    /// </summary>
    public const string Text = """
        #version 450

        #define PUCK_STUDY 1

        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
        layout(set = 0, binding = 0, rgba8) uniform writeonly image2D puckStudyImage;

        layout(push_constant) uniform PuckStudy {
            vec3 iResolution;
            float iTime;
            float iTimeDelta;
            int iFrame;
            vec2 _pad0;
            vec4 iMouse;
            vec4 iDate;
            vec3 iCameraPos;
            float iCameraFov;
            vec3 iCameraTarget;
            float _pad1;
            vec3 iCameraUp;
            float _pad2;
        } puck;

        #define iResolution puck.iResolution
        #define iTime puck.iTime
        #define iTimeDelta puck.iTimeDelta
        #define iFrame puck.iFrame
        #define iMouse puck.iMouse
        #define iDate puck.iDate
        #define iCameraPos puck.iCameraPos
        #define iCameraTarget puck.iCameraTarget
        #define iCameraUp puck.iCameraUp
        #define iCameraFov puck.iCameraFov
        // iCameraFov is 0 (and iCameraPos/iCameraTarget/iCameraUp zero) when the host pairs no camera with this
        // study — branch on it to keep a Shadertoy iMouse orbit alive: if (iCameraFov > 0.) { ... } else { ... }


        """;
    /// <summary>
    /// Appended after the author's source. Shadertoy's <c>fragCoord</c> origin is bottom-left with pixel centres at
    /// <c>+0.5</c>; the storage image's row 0 is the TOP of the pane (both backends composite it top-down), so the Y
    /// flip below is what puts a study upright rather than mirrored.
    /// </summary>
    public const string Postlude = """


        void main() {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            if ((pixel.x >= int(iResolution.x)) || (pixel.y >= int(iResolution.y))) { return; }
            vec2 fragCoord = vec2(float(pixel.x) + 0.5, iResolution.y - (float(pixel.y) + 0.5));
            vec4 c = vec4(0);
            mainImage(c, fragCoord);
            imageStore(puckStudyImage, pixel, c);
        }
        """;
    /// <summary>The number of lines <see cref="Text"/> occupies ahead of the author's own first source line — the
    /// offset a diagnostic's reported line number is reduced by to land back on the author's file.</summary>
    public static readonly int PreludeLineCount = Text.Count(predicate: static c => (c == '\n'));
}
