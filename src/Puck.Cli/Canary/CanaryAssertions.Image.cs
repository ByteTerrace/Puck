using System.Globalization;
using Puck.Assets;

namespace Puck.Cli.Canary;

internal static partial class CanaryAssertions {
    private static bool CenterWithin(int index, int extent, double low, double high) {
        var center = ((index + 0.5) / extent);

        return ((center >= low) && (center <= high));
    }
    private static CanaryAssertionResult EvaluateImageRegion(CanaryImageRegionAssertion assertion, CanaryTranscript transcript) {
        var path = Path.Combine(
            path1: transcript.RunDirectory,
            path2: assertion.Capture
        );

        if (!File.Exists(path: path)) {
            return new CanaryAssertionResult(
                Detail: $"{assertion.Name}: missing capture {assertion.Capture}",
                Passed: false
            );
        }

        PngImage image;

        try {
            image = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: path));
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or UnauthorizedAccessException)) {
            return new CanaryAssertionResult(
                Detail: $"{assertion.Name}: {assertion.Capture} could not be decoded ({exception.Message.ReplaceLineEndings(replacementText: " ")})",
                Passed: false
            );
        }

        // A wrong extent means the claim was made about a different image, so neither direction can be decided.
        if (
            (image.Width != assertion.Width) ||
            (image.Height != assertion.Height)
        ) {
            return new CanaryAssertionResult(
                Detail: $"{assertion.Name}: {assertion.Capture} is {image.Width}x{image.Height}, expected {assertion.Width}x{assertion.Height}",
                Passed: false
            );
        }

        var pixels = image.RgbaPixels;
        var sums = new long[4];
        var count = 0L;
        var outside = 0L;
        var worst = 0.0;

        for (var y = 0; (y < image.Height); y++) {
            if (!CenterWithin(
                extent: image.Height,
                high: assertion.Bottom,
                index: y,
                low: assertion.Top
            )) { continue; }
            for (var x = 0; (x < image.Width); x++) {
                if (!CenterWithin(
                    extent: image.Width,
                    high: assertion.Right,
                    index: x,
                    low: assertion.Left
                )) { continue; }
                var offset = (((y * image.Width) + x) * 4);
                var pixelOutside = false;

                count++;
                for (var channel = 0; (channel < 4); channel++) {
                    var code = pixels[(offset + channel)];

                    sums[channel] += code;
                    if (assertion.Reduce == CanaryImageReduce.Every) {
                        var excess = Excess(
                            assertion: assertion,
                            channel: channel,
                            code: code
                        );

                        worst = Math.Max(
                            val1: worst,
                            val2: excess
                        );
                        pixelOutside |= (excess > 0);
                    }
                }
                if (pixelOutside) { outside++; }
            }
        }

        string observed;

        if (assertion.Reduce == CanaryImageReduce.Every) {
            observed = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{outside} of {count} pixel(s) outside the bounds by up to {worst:0.###} code(s)"
            );
        } else {
            var means = new double[4];

            for (var channel = 0; (channel < 4); channel++) {
                means[channel] = (((double)sums[channel]) / count);
                worst = Math.Max(
                    val1: worst,
                    val2: Excess(
                        assertion: assertion,
                        channel: channel,
                        code: means[channel]
                    )
                );
            }

            outside = ((worst > 0)
                ? 1
                : 0);
            observed = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"mean of {count} pixel(s) ({means[0]:0.###}, {means[1]:0.###}, {means[2]:0.###}, {means[3]:0.###}) codes, outside the bounds by up to {worst:0.###} code(s)"
            );
        }

        var holds = (outside == 0);

        return new CanaryAssertionResult(
            Detail: $"{assertion.Name}: {assertion.Capture} {observed} — the bounds {(holds
                ? "hold"
                : "do not hold")}",
            Passed: (holds == assertion.Holds)
        );
    }
    // How far one channel code lies outside its bound, in codes, after the tolerance; zero when it is inside.
    private static double Excess(CanaryImageRegionAssertion assertion, int channel, double code) {
        var below = ((assertion.Minimum is { } minimum)
            ? (((minimum[channel] * 255.0) - assertion.ToleranceCodes) - code)
            : 0.0);
        var above = ((assertion.Maximum is { } maximum)
            ? (code - ((maximum[channel] * 255.0) + assertion.ToleranceCodes))
            : 0.0);

        return Math.Max(
            val1: 0.0,
            val2: Math.Max(
                val1: below,
                val2: above
            )
        );
    }

    /// <summary>Counts the pixels of a width-by-height image whose centers lie inside a normalized region.</summary>
    /// <param name="left">The region's left edge, normalized.</param>
    /// <param name="top">The region's top edge, normalized.</param>
    /// <param name="right">The region's right edge, normalized.</param>
    /// <param name="bottom">The region's bottom edge, normalized.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The number of pixels an <see cref="CanaryImageRegionAssertion"/> over that region reads.</returns>
    public static long RegionPixelCount(double left, double top, double right, double bottom, int width, int height) {
        var columns = Enumerable.Range(
            count: width,
            start: 0
        ).Count(predicate: x => CenterWithin(
            extent: width,
            high: right,
            index: x,
            low: left
        ));
        var rows = Enumerable.Range(
            count: height,
            start: 0
        ).Count(predicate: y => CenterWithin(
            extent: height,
            high: bottom,
            index: y,
            low: top
        ));

        return (((long)columns) * rows);
    }
}
