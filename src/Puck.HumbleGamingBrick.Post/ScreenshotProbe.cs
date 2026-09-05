using Puck.Assets;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Runs a case for its suite's fixed frame budget, packs the resulting framebuffer through <see cref="FramebufferRgba"/>,
/// and compares it pixel-exact against the first of a case's expected-image candidates that exists on disk (a
/// suite's device-tag fallback chain, e.g. mealybug's <c>_dmg_blob.png</c> then <c>_dmg_b.png</c>). This framebuffer's
/// DMG shades and CGB <c>(X&lt;&lt;3)|(X&gt;&gt;2)</c> channel expansion already match the shared "common palette" the
/// corpus's screenshots are rendered under, so no color conversion happens here — a mismatch is either a rendering
/// divergence or a resolution mismatch, both reported as a failure.
/// </summary>
internal static class ScreenshotProbe {
    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="ledgerCase">The case to run; <see cref="LedgerCase.ExpectedImageCandidates"/> must be non-empty.</param>
    /// <returns>The probe outcome, carrying the differing-pixel count on a pixel mismatch.</returns>
    public static ProbeOutcome Run(LedgerCase ledgerCase) {
        var candidates = ledgerCase.ExpectedImageCandidates;

        if (
            (candidates is null) ||
            (candidates.Count == 0)
        ) {
            return new ProbeOutcome(
                Detail: "no expected-image candidate configured for this case",
                Verdict: ProbeVerdict.Inconclusive
            );
        }

        var imagePath = candidates.FirstOrDefault(predicate: File.Exists);

        if (imagePath is null) {
            return new ProbeOutcome(
                Detail: $"none of the expected images exist: {string.Join(separator: ", ", values: candidates.Select(selector: Path.GetFileName))}",
                Verdict: ProbeVerdict.Inconclusive
            );
        }

        var rom = File.ReadAllBytes(path: ledgerCase.FullPath);

        using var machine = PostMachine.Build(
            model: ledgerCase.Model,
            rom: rom
        );

        PostMachine.RunFrames(
            frames: ledgerCase.FrameCap,
            instance: machine
        );

        var framebuffer = machine.GetRequiredService<IFramebuffer>();
        var actualRgba = FramebufferRgba.Pack(pixels: framebuffer.Pixels);
        PngImage expected;

        try {
            expected = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: imagePath));
        } catch (InvalidDataException exception) {
            // A handful of the corpus's shipped screenshots are sub-8-bit PNGs (1/2/4-bit grayscale or palette);
            // Puck.Assets.PngDecoder is deliberately 8-bit-only (it reads what Puck itself writes and bakes), so
            // these cases cannot be checked through it. Recorded inconclusive with the decoder's own reason rather
            // than throwing, which would otherwise take the whole suite's stage down as an infrastructure failure.
            return new ProbeOutcome(
                Detail: $"expected image {Path.GetFileName(path: imagePath)} could not be decoded: {exception.Message}",
                Verdict: ProbeVerdict.Inconclusive
            );
        }

        if (
            (expected.Width != framebuffer.Width) ||
            (expected.Height != framebuffer.Height)
        ) {
            return new ProbeOutcome(
                Detail: $"expected image {Path.GetFileName(path: imagePath)} is {expected.Width}x{expected.Height}; framebuffer is {framebuffer.Width}x{framebuffer.Height}",
                Verdict: ProbeVerdict.Fail
            );
        }

        var diffPixels = 0;

        for (var offset = 0; (offset < actualRgba.Length); offset += 4) {
            if (
                (actualRgba[offset] != expected.RgbaPixels[offset]) ||
                (actualRgba[(offset + 1)] != expected.RgbaPixels[(offset + 1)]) ||
                (actualRgba[(offset + 2)] != expected.RgbaPixels[(offset + 2)])
            ) {
                ++diffPixels;
            }
        }

        return ((diffPixels == 0)
            ? new ProbeOutcome(
                Detail: $"pixel-exact against {Path.GetFileName(path: imagePath)} after {ledgerCase.FrameCap} frames",
                Verdict: ProbeVerdict.Pass
            )
            : new ProbeOutcome(
                DiffPixelCount: diffPixels,
                Detail: $"{diffPixels} differing pixel(s) against {Path.GetFileName(path: imagePath)} after {ledgerCase.FrameCap} frames",
                Verdict: ProbeVerdict.Fail
            ));
    }
}
