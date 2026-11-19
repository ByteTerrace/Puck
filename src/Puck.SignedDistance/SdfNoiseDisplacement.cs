using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>A noise relief (<see cref="SdfOp.NoiseDisplace"/>): bounded hash-lattice fBm value noise added to the field
/// accumulated so far, decoded once from its instruction. Its normalization is derived, never carried: the octave sum
/// divided by it stays in <c>[-1, 1]</c>, so the relief reaches no farther than its amplitude and its slope is
/// <see cref="ReliefSlope"/>. <see cref="SdfProgramBuilder.NoiseDisplace"/> packs the instruction this value describes,
/// and <see cref="SdfProgram"/> refuses one whose packed normalization is not <see cref="Normalization"/>.</summary>
/// <param name="Frequency">The base lattice frequency, cells per unit of the relief's frame; its sign is absorbed.</param>
/// <param name="Amplitude">The peak displacement, in field units; its sign reverses the relief.</param>
/// <param name="Octaves">The octave count, in 1..<see cref="SdfProgramBuilder.MaxNoiseOctaves"/>.</param>
/// <param name="Gain">The per-octave amplitude factor, finite and above zero.</param>
/// <param name="Lacunarity">The per-octave frequency factor, finite and above zero.</param>
/// <param name="Seed">The hash seed folded into every lattice corner.</param>
public readonly record struct SdfNoiseDisplacement(float Frequency, float Amplitude, int Octaves, float Gain, float Lacunarity, uint Seed) {
    // The quintic blend's slope bound over the [-1, 1] corner span, (15/8)·2 per axis, combined Euclidean: (15/4)·√3.
    private const float NoiseGradientBound = 6.49519053f;

    /// <summary>Gets the normalization the relief multiplies its octave sum by: one over the sum of the octaves' gains.</summary>
    public float Normalization => (1.0f / GainSum(octaves: Octaves, gain: Gain));
    /// <summary>Gets the bound on the relief's own slope per unit of the frame it samples: its amplitude times its
    /// frequency times the normalized octave sum's gradient bound
    /// (<see cref="Slope(float, float, int, float, float)"/>).</summary>
    public float ReliefSlope => Slope(
        amplitude: MathF.Abs(x: Amplitude),
        frequency: MathF.Abs(x: Frequency),
        gain: Gain,
        lacunarity: Lacunarity,
        octaves: Octaves
    );

    /// <summary>Returns the bound on the relief's slope per unit of the frame it samples once masks weight it: its own
    /// slope plus its amplitude times the masks' summed slope (<see cref="SdfReliefMask.SlopeOf"/>). A masked relief's
    /// gradient is <c>a·(w∇n + n∇w)</c> with <c>|n| ≤ 1</c> and <c>0 ≤ w ≤ 1</c>.</summary>
    /// <param name="maskSlope">The masks' summed slope, per unit of the frame the relief samples; zero for none.</param>
    /// <returns>The slope bound.</returns>
    public float MaskedSlope(float maskSlope) => ((maskSlope > 0.0f) ? (ReliefSlope + (MathF.Abs(x: Amplitude) * maskSlope)) : ReliefSlope);
    /// <summary>Returns the bound on a noise relief's slope per unit of the frame it samples:
    /// <c>amplitude · frequency · (15/4)·√3 · Σ(gain·lacunarity)ᵏ / Σ gainᵏ</c>. One octave of quintic-blended trilinear
    /// value noise over corner values in <c>[-1, 1]</c> changes by at most <c>(15/8)·2</c> per unit cell on each axis;
    /// octave <c>k</c> scales by <c>gainᵏ·lacunarityᵏ</c>, and the normalization divides by <c>Σ gainᵏ</c>.</summary>
    /// <param name="frequency">The base lattice frequency, non-negative.</param>
    /// <param name="amplitude">The peak displacement, non-negative.</param>
    /// <param name="octaves">The octave count.</param>
    /// <param name="gain">The per-octave amplitude factor.</param>
    /// <param name="lacunarity">The per-octave frequency factor.</param>
    /// <returns>The slope bound; zero for a zero amplitude.</returns>
    public static float Slope(float frequency, float amplitude, int octaves, float gain, float lacunarity) {
        if (amplitude == 0.0f) {
            return 0.0f;
        }

        var gradientSum = 0.0f;
        var termPower = 1.0f;

        for (var octave = 0; (octave < octaves); octave++) {
            gradientSum += termPower;
            termPower *= (gain * lacunarity);
        }

        return (((amplitude * (1.0f / GainSum(octaves: octaves, gain: gain))) * frequency) * (NoiseGradientBound * gradientSum));
    }
    /// <summary>Decodes a noise relief from its instruction and checks every lane against the domain
    /// <see cref="SdfProgramBuilder.NoiseDisplace"/> enforces: the frequency and amplitude finite, the gain and lacunarity
    /// finite and above zero, the octave count in range, the normalization the derived one bit for bit, the mask row
    /// count in range (the program door checks it against the rows before the relief), and the unused lanes zero.</summary>
    /// <param name="instruction">The <see cref="SdfOp.NoiseDisplace"/> instruction.</param>
    /// <returns>The relief.</returns>
    /// <exception cref="ArgumentException"><paramref name="instruction"/> is not a noise relief, or a lane lies outside
    /// its domain.</exception>
    public static SdfNoiseDisplacement Of(SdfInstruction instruction) {
        if (instruction.Op != SdfOp.NoiseDisplace) {
            throw new ArgumentException(message: $"A {instruction.Op} instruction is not a noise relief.", paramName: nameof(instruction));
        }
        if (instruction.Blend is < 1u or > ((uint)SdfProgramBuilder.MaxNoiseOctaves)) {
            throw new ArgumentException(message: $"A noise relief declares {instruction.Blend} octaves; the count must lie in 1..{SdfProgramBuilder.MaxNoiseOctaves}.", paramName: nameof(instruction));
        }

        var relief = new SdfNoiseDisplacement(
            Amplitude: instruction.Data0.Y,
            Frequency: instruction.Data0.X,
            Gain: instruction.Data0.Z,
            Lacunarity: instruction.Data0.W,
            Octaves: ((int)instruction.Blend),
            Seed: instruction.Shape
        );

        relief.Validate();

        if (BitConverter.SingleToUInt32Bits(value: instruction.Data1.X) != BitConverter.SingleToUInt32Bits(value: relief.Normalization)) {
            throw new ArgumentException(message: $"A noise relief carries normalization {instruction.Data1.X} in Data1.x, but its {relief.Octaves} octaves of gain {relief.Gain} derive {relief.Normalization}: a larger one lets the relief reach past its amplitude and steeper than its step bound.", paramName: nameof(instruction));
        }
        if (instruction.Material > ((uint)SdfReliefMask.MaxRows)) {
            throw new ArgumentException(message: $"A noise relief counts {instruction.Material} mask rows before it in its Material lane; a relief reads at most {SdfReliefMask.MaxRows}.", paramName: nameof(instruction));
        }
        if ((instruction.Data1 with { X = 0f }) != Vector4.Zero) {
            throw new ArgumentException(message: "A noise relief carries a nonzero Data1.y, Data1.z or Data1.w lane; its operation leaves them zero.", paramName: nameof(instruction));
        }

        return relief;
    }
    /// <summary>Refuses a relief outside the builder's domains.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The frequency or amplitude is not finite, the gain or lacunarity is not
    /// finite and above zero, or the octave count lies outside 1..<see cref="SdfProgramBuilder.MaxNoiseOctaves"/>.</exception>
    public void Validate() {
        if (!float.IsFinite(f: Frequency)) { throw new ArgumentOutOfRangeException(paramName: nameof(Frequency), message: $"A noise relief's frequency must be finite; got {Frequency}."); }
        if (!float.IsFinite(f: Amplitude)) { throw new ArgumentOutOfRangeException(paramName: nameof(Amplitude), message: $"A noise relief's amplitude must be finite; got {Amplitude}."); }
        if (!(float.IsFinite(f: Gain) && (Gain > 0f))) { throw new ArgumentOutOfRangeException(paramName: nameof(Gain), message: $"A noise relief's gain must be finite and above zero; got {Gain}."); }
        if (!(float.IsFinite(f: Lacunarity) && (Lacunarity > 0f))) { throw new ArgumentOutOfRangeException(paramName: nameof(Lacunarity), message: $"A noise relief's lacunarity must be finite and above zero; got {Lacunarity}."); }
        if (Octaves is < 1 or > SdfProgramBuilder.MaxNoiseOctaves) { throw new ArgumentOutOfRangeException(paramName: nameof(Octaves), message: $"A noise relief's octave count must lie in 1..{SdfProgramBuilder.MaxNoiseOctaves}; got {Octaves}."); }
        if (!float.IsFinite(f: ReliefSlope)) { throw new ArgumentOutOfRangeException(paramName: nameof(Amplitude), message: "A noise relief's slope bound must be finite."); }
    }
    /// <summary>Returns the instruction this relief packs as.</summary>
    /// <param name="rows">The count of mask rows directly before the relief (<see cref="SdfReliefMask.RowCount"/>), which
    /// its Material lane carries; zero for an unmasked relief.</param>
    /// <returns>The instruction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> lies outside 0..<see cref="SdfReliefMask.MaxRows"/>.</exception>
    public SdfInstruction ToInstruction(int rows = 0) => new(
        Blend: ((uint)Octaves),
        Data0: new Vector4(x: Frequency, y: Amplitude, z: Gain, w: Lacunarity),
        Data1: new Vector4(x: Normalization, y: 0f, z: 0f, w: 0f),
        Material: (((rows >= 0) && (rows <= SdfReliefMask.MaxRows)) ? ((uint)rows) : throw new ArgumentOutOfRangeException(paramName: nameof(rows), message: $"A relief reads 0..{SdfReliefMask.MaxRows} mask rows; got {rows}.")),
        Op: SdfOp.NoiseDisplace,
        Shape: Seed
    );

    private static float GainSum(int octaves, float gain) {
        var gainSum = 0.0f;
        var gainPower = 1.0f;

        for (var octave = 0; (octave < octaves); octave++) {
            gainSum += gainPower;
            gainPower *= gain;
        }

        return gainSum;
    }
}
