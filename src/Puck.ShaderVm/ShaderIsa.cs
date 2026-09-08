namespace Puck.ShaderVm;

/// <summary>Defines the packed Shader VM program header shared by the host and GPU interpreter.</summary>
public static class ShaderIsa {
    /// <summary>The packed program magic, ASCII <c>SHVM</c> in little-endian word order.</summary>
    public const uint Magic = 0x4D564853u;
    /// <summary>The current packed instruction-set version.</summary>
    public const uint Version = 2u;
    /// <summary>The number of words before the instruction stream: magic, version, instruction count, and constant count.</summary>
    public const int HeaderWordCount = 4;
    /// <summary>The largest instruction stream the interpreter admits.</summary>
    public const int MaxInstructions = 16384;
    /// <summary>The largest constant pool the interpreter admits.</summary>
    public const int MaxConstants = 512;
    /// <summary>The largest value stack the interpreter admits.</summary>
    public const int MaxStackDepth = 64;
    /// <summary>The number of local registers the interpreter provides.</summary>
    public const int MaxLocals = 64;
    /// <summary>The most octaves one <see cref="ShaderOp.Fbm2"/> instruction sums.</summary>
    public const int MaxOctaves = 8;
    /// <summary>The multiplier of the PCG3D hash <see cref="ShaderOp.Hash3"/> evaluates.</summary>
    public const uint PcgMultiplier = 1664525u;
    /// <summary>The increment of the PCG3D hash <see cref="ShaderOp.Hash3"/> evaluates.</summary>
    public const uint PcgIncrement = 1013904223u;
    /// <summary>The scale that carries a full unsigned range into the unit interval.</summary>
    public const float InverseTwoPow32 = (1f / 4294967296f);

    /// <summary>Packs four lane selectors into one <see cref="ShaderOp.Swizzle"/> operand.</summary>
    /// <param name="x">The source lane of the result's first lane.</param>
    /// <param name="y">The source lane of the result's second lane.</param>
    /// <param name="z">The source lane of the result's third lane.</param>
    /// <param name="w">The source lane of the result's fourth lane.</param>
    /// <returns>The packed operand.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A selector names a lane outside the four-lane value.</exception>
    public static uint PackSwizzle(int x, int y, int z, int w) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: ((uint)x), other: 3u);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: ((uint)y), other: 3u);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: ((uint)z), other: 3u);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: ((uint)w), other: 3u);

        return ((uint)x) | (((uint)y) << 2) | (((uint)z) << 4) | (((uint)w) << 6);
    }
    /// <summary>Reads one lane selector out of a <see cref="ShaderOp.Swizzle"/> operand.</summary>
    /// <param name="operand">The packed operand.</param>
    /// <param name="lane">The result lane whose source is read.</param>
    /// <returns>The source lane index.</returns>
    public static int UnpackSwizzle(uint operand, int lane) => ((int)((operand >> (lane * 2)) & 3u));
    /// <summary>Evaluates the PCG3D hash the ISA pins, over three unsigned lanes.</summary>
    /// <param name="x">The first lane.</param>
    /// <param name="y">The second lane.</param>
    /// <param name="z">The third lane.</param>
    /// <returns>The three hashed lanes.</returns>
    public static (uint X, uint Y, uint Z) Pcg3d(uint x, uint y, uint z) {
        unchecked {
            x = ((x * PcgMultiplier) + PcgIncrement);
            y = ((y * PcgMultiplier) + PcgIncrement);
            z = ((z * PcgMultiplier) + PcgIncrement);
            x += (y * z); y += (z * x); z += (x * y);
            x ^= (x >> 16); y ^= (y >> 16); z ^= (z >> 16);
            x += (y * z); y += (z * x); z += (x * y);

            return (x, y, z);
        }
    }
}
