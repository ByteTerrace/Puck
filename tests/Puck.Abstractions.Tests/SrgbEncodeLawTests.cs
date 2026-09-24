using System.Numerics;
using Puck.Abstractions.Sources;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="ImageSourceConversion.LinearToSrgb8"/>, the exact 8-bit sRGB encode. Every boundary between two
/// codes is proven exhaustively against an oracle that shares nothing with the encode but the definition: the exact
/// decoded midpoint, scaled by <c>2^1200</c> and floored through an integer fifth root, with the boundary found by
/// bisecting the encode over the bit patterns of doubles. Every code's own decoded value encodes back to it, and the
/// ends saturate. The same oracle holds <see cref="ImageSourceConversion.Srgb8ToLinear"/>, the exact decode a filter
/// averages sRGB texels through, to the smallest double at or above each code's decoded value.
/// </summary>
public sealed class SrgbEncodeLawTests {
    // Every positive double at or below 1 times 2^Scale is an integer: the smallest subnormal is 2^-1074.
    private const int Scale = 1200;

    // floor(n^(1/5)) for n >= 0, by Newton's method on integers from an overestimate.
    private static BigInteger FifthRoot(BigInteger n) {
        if (n.IsZero) {
            return n;
        }

        var x = (BigInteger.One << (((int)(n.GetBitLength() / 5)) + 1));

        while (true) {
            var next = (((4 * x) + (n / BigInteger.Pow(exponent: 4, value: x))) / 5);

            if (next >= x) {
                return x;
            }

            x = next;
        }
    }
    // floor(midpoint 2^Scale), where the midpoint is the exact linear value the encoded value (code - 0.5) / 255
    // decodes to.
    private static BigInteger ScaledMidpoint(int code) =>
        ScaledDecode(k: ((2 * code) - 1));
    // floor(decoded 2^Scale), where decoded is the exact linear value the encoded value k / 510 decodes to: v / 12.92 at
    // or under 0.04045, else ((v + 0.055) / 1.055)^2.4, over exact rationals.
    private static BigInteger ScaledDecode(int k) {
        var encoded = new BigInteger(value: k);
        const int EncodedDenominator = 510;

        if ((encoded * 20000) <= (809 * EncodedDenominator)) {
            return (((encoded * 100) * (BigInteger.One << Scale)) / (EncodedDenominator * 1292));
        }

        // r = (v + 55/1000) / (1055/1000) = ((2 code - 1) 1000 + 55 510) / (510 1055), and midpoint = r^(12/5).
        var numerator = ((encoded * 1000) + (55 * EncodedDenominator));
        var denominator = new BigInteger(value: (EncodedDenominator * 1055));

        return FifthRoot(n: ((BigInteger.Pow(exponent: 12, value: numerator) << (5 * Scale)) / BigInteger.Pow(exponent: 12, value: denominator)));
    }
    private static BigInteger Scaled(double value) {
        var bits = BitConverter.DoubleToInt64Bits(value: value);
        var exponent = ((int)((bits >> 52) & 0x7FF));
        var mantissa = new BigInteger(value: bits & 0xF_FFFF_FFFF_FFFFL);

        if (exponent == 0) {
            exponent = -1074;
        } else {
            mantissa += (BigInteger.One << 52);
            exponent -= 1075;
        }

        return (mantissa << (exponent + Scale));
    }
    // The smallest positive double the encode maps to `code` or above.
    private static double Boundary(int code) {
        var low = BitConverter.DoubleToInt64Bits(value: double.Epsilon);
        var high = BitConverter.DoubleToInt64Bits(value: 1.0);

        while (low < high) {
            var middle = (low + ((high - low) / 2));

            if (ImageSourceConversion.LinearToSrgb8(value: BitConverter.Int64BitsToDouble(value: middle)) >= code) {
                high = middle;
            } else {
                low = (middle + 1);
            }
        }

        return BitConverter.Int64BitsToDouble(value: low);
    }

    [Fact]
    public void EveryBoundaryIsTheSmallestDoubleAtOrAboveTheExactDecodedMidpoint() {
        for (var code = 1; (code <= 255); code++) {
            var boundary = Boundary(code: code);
            var midpoint = ScaledMidpoint(code: code);

            // No midpoint is dyadic, so a double is at or above it exactly when its scaled value exceeds the floor.
            Assert.True(condition: (Scaled(value: boundary) > midpoint), userMessage: $"code {code}: {boundary:R} lies below its midpoint");
            Assert.True(condition: (Scaled(value: Math.BitDecrement(x: boundary)) <= midpoint), userMessage: $"code {code}: {Math.BitDecrement(x: boundary):R} already reaches its midpoint");
            Assert.Equal(expected: code, actual: ImageSourceConversion.LinearToSrgb8(value: boundary));
            Assert.Equal(expected: (code - 1), actual: ImageSourceConversion.LinearToSrgb8(value: Math.BitDecrement(x: boundary)));
        }
    }
    [Fact]
    public void EveryCodesDecodedValueEncodesBackToIt() {
        for (var code = 0; (code <= 255); code++) {
            Assert.Equal(expected: code, actual: ImageSourceConversion.LinearToSrgb8(value: ImageSourceConversion.SrgbToLinear(value: (code / 255.0))));
        }
    }
    [Fact]
    public void EveryCodesExactDecodeIsTheSmallestDoubleAtOrAboveItsDecodedValue() {
        Assert.Equal(expected: 0.0, actual: ImageSourceConversion.Srgb8ToLinear(code: 0));
        Assert.Equal(expected: 1.0, actual: ImageSourceConversion.Srgb8ToLinear(code: 255));

        // Codes 1 to 254 decode to values that are not dyadic, so a double is at or above one exactly when its scaled
        // value exceeds the floor.
        for (var code = 1; (code <= 254); code++) {
            var value = ImageSourceConversion.Srgb8ToLinear(code: ((byte)code));
            var exact = ScaledDecode(k: (2 * code));

            Assert.True(condition: (Scaled(value: value) > exact), userMessage: $"code {code}: {value:R} lies below its decoded value");
            Assert.True(condition: (Scaled(value: Math.BitDecrement(x: value)) <= exact), userMessage: $"code {code}: {Math.BitDecrement(x: value):R} already reaches its decoded value");
        }

        for (var code = 0; (code <= 255); code++) {
            Assert.Equal(expected: code, actual: ImageSourceConversion.LinearToSrgb8(value: ImageSourceConversion.Srgb8ToLinear(code: ((byte)code))));
        }
    }
    [Fact]
    public void TheEndsSaturate() {
        Assert.Equal(expected: 0, actual: ImageSourceConversion.LinearToSrgb8(value: double.NaN));
        Assert.Equal(expected: 0, actual: ImageSourceConversion.LinearToSrgb8(value: double.NegativeInfinity));
        Assert.Equal(expected: 0, actual: ImageSourceConversion.LinearToSrgb8(value: -1.0));
        Assert.Equal(expected: 0, actual: ImageSourceConversion.LinearToSrgb8(value: 0.0));
        Assert.Equal(expected: 255, actual: ImageSourceConversion.LinearToSrgb8(value: 1.0));
        Assert.Equal(expected: 255, actual: ImageSourceConversion.LinearToSrgb8(value: 2.0));
        Assert.Equal(expected: 255, actual: ImageSourceConversion.LinearToSrgb8(value: double.PositiveInfinity));
    }
}
