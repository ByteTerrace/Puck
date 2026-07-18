using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Platform.Windows.Recording;

// Packs a tightly packed RGBA/BGRA CPU frame into planar NV12 (a full-resolution Y plane followed by an interleaved
// half-resolution Cb/Cr plane) — the pixel layout the hardware encoder MFTs consume. BT.709 limited-range coefficients
// (HD content); the container's colour metadata is the muxer's concern, flagged in the handoff. The Y plane has a
// vectorized fast path (AVX2 deinterleave of the BGRA channels); chroma is 2x2-subsampled scalar (a quarter of the
// work). A scalar path carries correctness on any CPU.
internal static class PixelToNv12Converter {
    // BT.709 limited range, Q8 fixed point. Y = 16 + (Kr*R + Kg*G + Kb*B); Cb/Cr = 128 + (...).
    private const int Shift = 8;
    private const int Half = 1 << (Shift - 1);
    private const int YR = 47;    // 0.1826 * 219/255 * 256
    private const int YG = 157;   // 0.6142 * 219/255 * 256
    private const int YB = 16;    // 0.0620 * 219/255 * 256
    private const int YBias = 16 << Shift;
    private const int CbR = -26;  // -0.1006 * 224/255 * 256
    private const int CbG = -87;  // -0.3386 * 224/255 * 256
    private const int CbB = 112;  //  0.4392 * 224/255 * 256
    private const int CrR = 112;  //  0.4392 * 224/255 * 256
    private const int CrG = -102; // -0.3989 * 224/255 * 256
    private const int CrB = -10;  // -0.0403 * 224/255 * 256
    private const int CBias = 128 << Shift;

    /// <summary>The tightly packed NV12 byte count for a frame extent (Y plane + half-size interleaved chroma).</summary>
    public static int Nv12Size(int width, int height) => checked((width * height) + (2 * ((width + 1) / 2) * ((height + 1) / 2)));

    /// <summary>Converts one RGBA/BGRA frame into <paramref name="destination"/> as NV12.</summary>
    /// <param name="pixels">The tightly packed source pixels (4 bytes per pixel).</param>
    /// <param name="format">The source channel order.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="destination">The NV12 output, at least <see cref="Nv12Size"/> bytes.</param>
    public static void Convert(ReadOnlySpan<byte> pixels, SurfaceFormat format, int width, int height, Span<byte> destination) {
        // The two supported layouts differ only in whether byte 0 is red or blue.
        var blueFirst = (format != SurfaceFormat.R8G8B8A8Unorm);
        var chromaWidth = ((width + 1) / 2);
        var chromaHeight = ((height + 1) / 2);
        var ySize = (width * height);
        var yPlane = destination[..ySize];
        var chromaPlane = destination.Slice(start: ySize, length: (2 * chromaWidth * chromaHeight));

        WriteLumaPlane(pixels: pixels, blueFirst: blueFirst, width: width, height: height, yPlane: yPlane);
        WriteChromaPlane(pixels: pixels, blueFirst: blueFirst, width: width, height: height, chromaWidth: chromaWidth, chromaHeight: chromaHeight, chromaPlane: chromaPlane);
    }

    private static void WriteLumaPlane(ReadOnlySpan<byte> pixels, bool blueFirst, int width, int height, Span<byte> yPlane) {
        for (var row = 0; (row < height); row++) {
            var sourceRow = pixels.Slice(start: (row * width * 4), length: (width * 4));
            var destinationRow = yPlane.Slice(start: (row * width), length: width);
            var column = 0;

            if (Avx2.IsSupported) {
                column = WriteLumaRowAvx2(sourceRow: sourceRow, blueFirst: blueFirst, width: width, destinationRow: destinationRow);
            }

            for (; (column < width); column++) {
                var offset = (column * 4);
                int r, g, b;

                if (blueFirst) {
                    b = sourceRow[offset];
                    g = sourceRow[offset + 1];
                    r = sourceRow[offset + 2];
                } else {
                    r = sourceRow[offset];
                    g = sourceRow[offset + 1];
                    b = sourceRow[offset + 2];
                }

                destinationRow[column] = (byte)(((YR * r) + (YG * g) + (YB * b) + YBias + Half) >> Shift);
            }
        }
    }

    // Vectorized Y for eight pixels at a time: gather the interleaved BGRA bytes, widen the three colour channels to
    // int32 lanes, apply the fixed-point luma dot product, narrow back to bytes. Returns the first column not handled.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteLumaRowAvx2(ReadOnlySpan<byte> sourceRow, bool blueFirst, int width, Span<byte> destinationRow) {
        var vectorWidth = (width & ~7);
        var kr = Vector256.Create(blueFirst ? YB : YR);
        var kg = Vector256.Create(YG);
        var kb = Vector256.Create(blueFirst ? YR : YB);
        var bias = Vector256.Create(YBias + Half);
        var column = 0;

        for (; (column < vectorWidth); column += 8) {
            var block = MemoryMarshal.Read<Vector256<byte>>(source: sourceRow.Slice(start: (column * 4), length: 32));
            // Lane i (int32) holds byte4*i (channel 0), channel 1, channel 2 via masked shifts.
            var packed = block.AsInt32();
            var c0 = Vector256.BitwiseAnd(packed, Vector256.Create(0x000000FF));
            var c1 = Vector256.BitwiseAnd(Vector256.ShiftRightLogical(packed, 8), Vector256.Create(0x000000FF));
            var c2 = Vector256.BitwiseAnd(Vector256.ShiftRightLogical(packed, 16), Vector256.Create(0x000000FF));
            var y = Vector256.Add(Vector256.Add(Vector256.Multiply(c0, kr), Vector256.Multiply(c1, kg)), Vector256.Add(Vector256.Multiply(c2, kb), bias));

            y = Vector256.ShiftRightArithmetic(y, Shift);

            for (var lane = 0; (lane < 8); lane++) {
                destinationRow[column + lane] = (byte)y.GetElement(index: lane);
            }
        }

        return column;
    }

    private static void WriteChromaPlane(ReadOnlySpan<byte> pixels, bool blueFirst, int width, int height, int chromaWidth, int chromaHeight, Span<byte> chromaPlane) {
        for (var cy = 0; (cy < chromaHeight); cy++) {
            var y0 = (cy * 2);
            var y1 = Math.Min(val1: (y0 + 1), val2: (height - 1));

            for (var cx = 0; (cx < chromaWidth); cx++) {
                var x0 = (cx * 2);
                var x1 = Math.Min(val1: (x0 + 1), val2: (width - 1));

                // Average the 2x2 RGB neighbourhood, then apply the chroma dot products once.
                Accumulate(pixels: pixels, blueFirst: blueFirst, width: width, x: x0, y: y0, r: out var r00, g: out var g00, b: out var b00);
                Accumulate(pixels: pixels, blueFirst: blueFirst, width: width, x: x1, y: y0, r: out var r10, g: out var g10, b: out var b10);
                Accumulate(pixels: pixels, blueFirst: blueFirst, width: width, x: x0, y: y1, r: out var r01, g: out var g01, b: out var b01);
                Accumulate(pixels: pixels, blueFirst: blueFirst, width: width, x: x1, y: y1, r: out var r11, g: out var g11, b: out var b11);

                var r = ((r00 + r10 + r01 + r11 + 2) >> 2);
                var g = ((g00 + g10 + g01 + g11 + 2) >> 2);
                var b = ((b00 + b10 + b01 + b11 + 2) >> 2);
                var index = ((cy * chromaWidth * 2) + (cx * 2));

                chromaPlane[index] = (byte)Math.Clamp(value: (((CbR * r) + (CbG * g) + (CbB * b) + CBias + Half) >> Shift), min: 0, max: 255);
                chromaPlane[index + 1] = (byte)Math.Clamp(value: (((CrR * r) + (CrG * g) + (CrB * b) + CBias + Half) >> Shift), min: 0, max: 255);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accumulate(ReadOnlySpan<byte> pixels, bool blueFirst, int width, int x, int y, out int r, out int g, out int b) {
        var offset = (((y * width) + x) * 4);

        if (blueFirst) {
            b = pixels[offset];
            g = pixels[offset + 1];
            r = pixels[offset + 2];
        } else {
            r = pixels[offset];
            g = pixels[offset + 1];
            b = pixels[offset + 2];
        }
    }
}
