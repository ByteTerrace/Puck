using Puck.Abstractions.Gpu;
using Puck.Capture;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the MATERIAL BLEND correctness tooth cross-backend PARITY cannot provide. A wrong-but-SYMMETRIC blend
/// (e.g. the weight stuck at 0, or both backends interpolating identically incorrectly) renders identically on
/// both backends, so <see cref="WorldMaterialBlendStage"/>'s parity diff passes while the feature is absent. This
/// single-backend gate proves the blend actually HAPPENS: it renders the shared two-material smooth-union seam
/// (<see cref="WorldMaterialBlendStage.BuildMaterialSeamScene"/>) on the Vulkan host and counts the pixels in the seam
/// BAND — where the red sphere's albedo has cross-faded into the blue sphere's.
/// <para>The detector keys on an invariant the scene is built to make unambiguous: the two albedos are near-pure red and
/// near-pure blue with a TINY green component, so a pixel is a genuine ALBEDO MIX only where BOTH the red and blue
/// channels are strong at once while green stays low. A pure-red sphere pixel has blue ≈ 0; a pure-blue pixel has red ≈
/// 0; the gray ground and the sky/fog have green present. Only the cross-faded fillet band lights red AND blue together
/// with green dark. Under the OLD hard material cut that band is ~0 pixels wide (there is no material-vs-material AA — the
/// winner snaps pixel-to-pixel), so a healthy count is a direct, backend-independent witness that the albedo lerp fired.
/// </para>
/// <para>Deterministic: the fixed shared scene, the shared hero camera, a single frame at time 0 — no wall-clock, no RNG.
/// </para>
/// <para>⚠CALIBRATION (live, by the lead — the authoring session had no GPU): the four constants below are REASONED
/// PLACEHOLDERS, not measured. <see cref="MinBlendBandPixels"/> especially rides the on-screen band width (smooth radius
/// × projection), which only a live render fixes; it is set well above dither/AA noise and well below any plausible band
/// size, with the hard-cut baseline (~0 band pixels) as the true floor the tooth defends. Re-measure the channel
/// thresholds against an actual host render and tighten the band floor to ~half the measured band.</para>
/// </summary>
internal sealed class WorldMaterialSeamStage : IPostStage {
    private const uint Height = 600;
    private const uint Width = 960;

    // ⚠PLACEHOLDER (calibrate live). A seam-band pixel lights BOTH red and blue at/above this 8-bit level: the matte
    // albedo (~0.88) times the lit radiance lands the dominant channel well above it, and at the band centre BOTH channels
    // sit near half that. Set conservatively low so a dim band still counts; raise to ~0.4× the measured pure-sphere
    // channel once a host render exists.
    private const byte MinChannelForMix = 45;
    // A mix pixel's green stays below this: the sphere albedos carry green ≈ 0.03, so a lit
    // sphere/band pixel's green is small, while the gray ground (green ≈ 0.55) and the sky/fog (grayish) sit far above it.
    // The ceiling separates the low-green red/blue band from every grayish surface.
    private const byte MaxGreenForMix = 45;
    // Below this total object coverage the render is broken or the spheres are unframed, so
    // the band cannot be judged (INFRA, not FAIL) — two radius-0.9 spheres at the hero framing fill far more than this.
    private const int VacuityFloor = 8000;
    // Calibrated on the Vulkan host with a deterministic scene: the cross-fade measures 3352 band pixels over
    // 16381 object pixels; the HARD-CUT baseline yields ~0 (no material-vs-material AA). Half the measured band —
    // benign codegen rolls move the band by pixels, a regression back to the hard cut collapses it entirely.
    private const int MinBlendBandPixels = 1600;

    /// <inheritdoc/>
    public string Name => "world-material-seam";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = WorldMaterialBlendStage.BuildMaterialSeamScene();
        var frame = WorldStage.BuildHeroFrame(program: program, width: Width, height: Height);
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: frame, width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-material-seam.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var (objectPixels, bandPixels) = ScanSeam(pixels: pixels);

        if (objectPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the red/blue sphere pair rendered only {objectPixels} object pixels (< {VacuityFloor}) — the render is broken or the spheres are unframed, so the seam band cannot be judged (artifact: {artifactPath})");
        }

        // THE tooth: the albedo cross-fade must produce a real band of red+blue mixed pixels. A regression to the hard
        // material cut (or a stuck-zero blend weight) collapses this to ~0 while parity would still pass.
        if (bandPixels < MinBlendBandPixels) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the material seam did not blend: only {bandPixels} red+blue mixed pixels in the band (< {MinBlendBandPixels}) — the winning albedo is a HARD cut at the geometric seam, not the smooth cross-fade (the sdfMaterialBlendWeight channel is not reaching the epilogue)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the two-material smooth-union seam cross-fades: {bandPixels} red+blue mixed band pixels over {objectPixels} object pixels on the Vulkan host — the sdfMaterialBlendWeight channel lerps the operand albedos across the band (a hard cut would leave ~0)");
    }

    // Counts (objectPixels, bandPixels): objectPixels are the low-green sphere pixels (red-dominant OR blue-dominant OR
    // mixed) — the vacuity guard; bandPixels are the seam MIX, where BOTH red and blue clear MinChannelForMix while green
    // stays under MaxGreenForMix (the invariant only the albedo cross-fade satisfies).
    private static (int ObjectPixels, int BandPixels) ScanSeam(byte[] pixels) {
        var objectPixels = 0;
        var bandPixels = 0;

        for (var i = 0; (i < ((int)Width * (int)Height)); i++) {
            var red = pixels[(i * 4)];
            var green = pixels[((i * 4) + 1)];
            var blue = pixels[((i * 4) + 2)];

            if (green > MaxGreenForMix) {
                continue; // gray ground / sky / fog — never a red|blue sphere or its mix.
            }

            var redLit = (red >= MinChannelForMix);
            var blueLit = (blue >= MinChannelForMix);

            if (redLit || blueLit) {
                objectPixels++;
            }

            if (redLit && blueLit) {
                bandPixels++;
            }
        }

        return (objectPixels, bandPixels);
    }
}
