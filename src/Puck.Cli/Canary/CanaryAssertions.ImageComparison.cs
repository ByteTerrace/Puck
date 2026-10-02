using System.Globalization;
using Puck.Assets;

namespace Puck.Cli.Canary;

internal static partial class CanaryAssertions {
    private static CanaryAssertionResult EvaluateImageComparison(CanaryImageComparisonAssertion assertion, CanaryTranscript transcript, CanaryTranscript? otherLeg) {
        if (assertion.ReferenceOtherLeg && (otherLeg is null)) {
            return new CanaryAssertionResult(Detail: $"{assertion.Name}: the opposite leg has no capture transcript", Passed: false);
        }
        var referenceDirectory = (assertion.ReferenceOtherLeg ? otherLeg!.RunDirectory : transcript.RunDirectory);
        PngImage capture;
        PngImage reference;

        try {
            capture = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: Path.Combine(path1: transcript.RunDirectory, path2: assertion.Capture)));
            reference = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: Path.Combine(path1: referenceDirectory, path2: assertion.Reference)));
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or UnauthorizedAccessException)) {
            return new CanaryAssertionResult(Detail: $"{assertion.Name}: capture pair could not be decoded ({exception.Message.ReplaceLineEndings(replacementText: " ")})", Passed: false);
        }
        var scale = assertion.ReferenceScale;

        if ((capture.Width != assertion.Width) || (capture.Height != assertion.Height) || (scale < 1) ||
            (reference.Width != (((long)capture.Width) * scale)) || (reference.Height != (((long)capture.Height) * scale))) {
            return new CanaryAssertionResult(Detail: $"{assertion.Name}: capture/reference extents {capture.Width}x{capture.Height}/{reference.Width}x{reference.Height} do not match {assertion.Width}x{assertion.Height} at reference scale {scale}", Passed: false);
        }
        var count = RegionPixelCount(left: assertion.Left, top: assertion.Top, right: assertion.Right, bottom: assertion.Bottom, width: capture.Width, height: capture.Height);

        if (count == 0) { return new CanaryAssertionResult(Detail: $"{assertion.Name}: region holds no pixel center", Passed: false); }
        var current = new byte[checked((((int)count) * 4))];
        var reduced = new byte[current.Length];
        var offset = 0;
        var samples = (((long)scale) * scale);

        for (var y = 0; (y < capture.Height); y++) {
            if (!CenterWithin(index: y, extent: capture.Height, low: assertion.Top, high: assertion.Bottom)) { continue; }
            for (var x = 0; (x < capture.Width); x++) {
                if (!CenterWithin(index: x, extent: capture.Width, low: assertion.Left, high: assertion.Right)) { continue; }
                capture.RgbaPixels.AsSpan(start: (((y * capture.Width) + x) * 4), length: 4).CopyTo(destination: current.AsSpan(start: offset));
                for (var channel = 0; (channel < 3); channel++) {
                    var sum = 0L;

                    for (var dy = 0; (dy < scale); dy++) {
                        for (var dx = 0; (dx < scale); dx++) {
                            sum += reference.RgbaPixels[(((((((y * scale) + dy) * reference.Width) + (x * scale)) + dx) * 4) + channel)];
                        }
                    }
                    // Box-filter capture codes with nearest-integer rounding; exact half codes round up.
                    reduced[(offset + channel)] = ((byte)((sum + (samples / 2)) / samples));
                }
                offset += 4;
            }
        }
        var difference = RgbaFrameDifference.Measure(after: current, before: reduced);
        var holds = ((difference.MaxDelta <= assertion.MaxErrorCodes) && (difference.MeanAbsoluteDelta <= assertion.MeanErrorCodes));

        return new CanaryAssertionResult(Detail: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"{assertion.Name}: {count} pixels, reference box {scale}x{scale}, RGB max {difference.MaxDelta} and mean {difference.MeanAbsoluteDelta:0.######} codes; limits {assertion.MaxErrorCodes}/{assertion.MeanErrorCodes} — bounds {(holds ? "hold" : "do not hold")}"), Passed: (holds == assertion.Holds));
    }
}
