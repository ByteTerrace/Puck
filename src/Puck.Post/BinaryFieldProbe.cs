using System.Globalization;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using Puck.Maths;

namespace Puck.Post;

/// <summary>The CPU-only <c>--binary-field-probe</c> child mode behind <see cref="BinaryFieldStage"/>: it runs one
/// fixed binary-field workload, folds every result into a single digest, and prints that digest together with the
/// instruction-set support vector the process actually observed. Instruction-set support is fixed for a process
/// lifetime, so the only way to execute the whole ladder against its fallbacks is to relaunch under the runtime's
/// feature-suppression knobs and compare digests; the printed support vector is what proves a knob still bites.</summary>
internal static class BinaryFieldProbe {
    /// <summary>The line prefix carrying the workload digest.</summary>
    public const string DigestPrefix = "BINARY-FIELD-PROBE digest ";
    /// <summary>The line prefix carrying the observed instruction-set support vector.</summary>
    public const string InstructionSetPrefix = "BINARY-FIELD-PROBE isa ";
    /// <summary>The number of scalar trials the workload runs at each of the five canonical fields.</summary>
    private const int ScalarTrials = 2_048;

    /// <summary>Gets the region lengths the workload sweeps, spanning every alignment of the widest vector rung.</summary>
    private static ReadOnlySpan<int> RegionLengths =>
        [0, 1, 3, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 259, 1_024];

    /// <summary>Runs the fixed workload and folds every result into one digest.</summary>
    /// <returns>The workload digest, which every instruction-set configuration must reproduce exactly.</returns>
    /// <remarks>
    /// The workload spans scalar multiplication, squaring, square roots, inversion, division, and exponentiation at all
    /// five canonical fields, then bulk region addition, scaling, in-place scaling, and multiply-accumulate over a
    /// length sweep at each carrier. Every value is drawn from a fixed integer mixing step and folded in a fixed order,
    /// so the digest depends only on the arithmetic — no clock, no randomness, and no floating point enters it.
    /// </remarks>
    public static ulong ComputeDigest() {
        var digest = 0xCBF2_9CE4_8422_2325UL;
        var state = 0x243F_6A88_85A3_08D3UL;

        FoldScalar(field: BinaryFields.Degree8, digest: ref digest, state: ref state);
        FoldScalar(field: BinaryFields.Degree16, digest: ref digest, state: ref state);
        FoldScalar(field: BinaryFields.Degree32, digest: ref digest, state: ref state);
        FoldScalar(field: BinaryFields.Degree64, digest: ref digest, state: ref state);
        FoldScalar(field: BinaryFields.Degree128, digest: ref digest, state: ref state);
        FoldRegions(field: BinaryFields.Degree8, digest: ref digest, state: ref state);
        FoldRegions(field: BinaryFields.Degree16, digest: ref digest, state: ref state);
        FoldRegions(field: BinaryFields.Degree32, digest: ref digest, state: ref state);
        FoldRegions(field: BinaryFields.Degree64, digest: ref digest, state: ref state);
        FoldRegions(field: BinaryFields.Degree128, digest: ref digest, state: ref state);
        // Narrower-than-carrier degrees exercise the reduction's masked split, which the canonical fields never reach
        // because each of them fills its carrier exactly.
        FoldScalar(field: BinaryField<byte>.Create(degree: 5, reductionTail: 0x05), digest: ref digest, state: ref state);
        FoldScalar(field: BinaryField<ushort>.Create(degree: 12, reductionTail: 0x09), digest: ref digest, state: ref state);
        FoldRegions(field: BinaryField<byte>.Create(degree: 5, reductionTail: 0x05), digest: ref digest, state: ref state);
        FoldRegions(field: BinaryField<ushort>.Create(degree: 12, reductionTail: 0x09), digest: ref digest, state: ref state);

        return digest;
    }
    /// <summary>Formats the instruction-set support vector this process observes.</summary>
    /// <returns>The support vector as semicolon-separated <c>name=0</c> / <c>name=1</c> pairs.</returns>
    public static string DescribeInstructionSets() =>
        string.Join(separator: ';', values: InstructionSets().Select(selector: static entry => $"{entry.Key}={(entry.Value ? 1 : 0)}"));
    /// <summary>Gets the instruction-set support vector this process observes.</summary>
    /// <returns>Each gate the binary-field ladder consults, keyed by the name the probe prints.</returns>
    /// <remarks>Every gate is the narrowest applicable one: a 256-bit Galois-field leaf implies neither its 128-bit nor its 512-bit sibling, so a suppression knob that stops flipping one of them has to be visible here rather than inferred.</remarks>
    public static IReadOnlyDictionary<string, bool> InstructionSets() =>
        new Dictionary<string, bool>(comparer: StringComparer.Ordinal) {
            ["Avx2"] = Avx2.IsSupported,
            ["Avx512BW"] = Avx512BW.IsSupported,
            ["Bmi2"] = Bmi2.IsSupported,
            ["Gfni"] = Gfni.IsSupported,
            ["Gfni.V256"] = Gfni.V256.IsSupported,
            ["Gfni.V512"] = Gfni.V512.IsSupported,
            ["Pclmulqdq"] = Pclmulqdq.IsSupported,
            ["Pclmulqdq.V256"] = Pclmulqdq.V256.IsSupported,
            ["Pclmulqdq.V512"] = Pclmulqdq.V512.IsSupported,
            ["Ssse3"] = Ssse3.IsSupported,
        };
    /// <summary>Reads the digest a probe child printed.</summary>
    /// <param name="output">The child's combined output.</param>
    /// <returns>The digest, or <see langword="null"/> when the child printed no digest line.</returns>
    public static ulong? ParseDigest(string output) {
        foreach (var line in output.Split(separator: '\n')) {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(value: DigestPrefix, comparisonType: StringComparison.Ordinal) &&
                ulong.TryParse(s: trimmed[DigestPrefix.Length..], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var digest)) {
                return digest;
            }
        }

        return null;
    }
    /// <summary>Reads the instruction-set support vector a probe child printed.</summary>
    /// <param name="output">The child's combined output.</param>
    /// <returns>The child's support vector, or <see langword="null"/> when the child printed no support line.</returns>
    public static IReadOnlyDictionary<string, bool>? ParseInstructionSets(string output) {
        foreach (var line in output.Split(separator: '\n')) {
            var trimmed = line.Trim();

            if (!trimmed.StartsWith(value: InstructionSetPrefix, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var parsed = new Dictionary<string, bool>(comparer: StringComparer.Ordinal);

            foreach (var pair in trimmed[InstructionSetPrefix.Length..].Split(separator: ';', options: StringSplitOptions.RemoveEmptyEntries)) {
                var separator = pair.IndexOf(value: '=', comparisonType: StringComparison.Ordinal);

                if (0 < separator) {
                    parsed[pair[..separator]] = string.Equals(a: pair[(separator + 1)..], b: "1", comparisonType: StringComparison.Ordinal);
                }
            }

            return parsed;
        }

        return null;
    }
    /// <summary>Runs the probe when the command line selects it.</summary>
    /// <param name="args">The process command line.</param>
    /// <param name="exitCode">The exit code to return when the probe ran.</param>
    /// <returns><see langword="true"/> when the probe ran and the process should exit; otherwise <see langword="false"/>.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        if (!args.Contains(value: "--binary-field-probe", comparer: StringComparer.OrdinalIgnoreCase)) {
            exitCode = 0;
            return false;
        }

        Console.Out.WriteLine(value: $"{DigestPrefix}{ComputeDigest():X16}");
        Console.Out.WriteLine(value: $"{InstructionSetPrefix}{DescribeInstructionSets()}");

        exitCode = 0;
        return true;
    }

    /// <summary>Folds one packed value into the running digest.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="digest">The running digest.</param>
    /// <param name="value">The value to fold.</param>
    private static void Fold<T>(ref ulong digest, T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var wide = UInt128.CreateTruncating(value: value);

        Mix(digest: ref digest, value: ((ulong)wide));
        Mix(digest: ref digest, value: ((ulong)(wide >>> 64)));
    }
    /// <summary>Folds a whole region into the running digest.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="digest">The running digest.</param>
    /// <param name="values">The region to fold.</param>
    private static void FoldRegion<T>(ref ulong digest, ReadOnlySpan<T> values) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        foreach (var value in values) {
            Fold(digest: ref digest, value: value);
        }
    }
    /// <summary>Runs the region workload at one field and folds every written element into the running digest.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="field">The field to operate in.</param>
    /// <param name="digest">The running digest.</param>
    /// <param name="state">The mixing state the workload's values are drawn from.</param>
    private static void FoldRegions<T>(BinaryField<T> field, ref ulong digest, ref ulong state) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        foreach (var length in RegionLengths) {
            var destination = new T[length];
            var source = new T[length];

            for (var index = 0; (index < length); ++index) {
                destination[index] = field.Reduce(value: NextCarrier<T>(state: ref state));
                source[index] = field.Reduce(value: NextCarrier<T>(state: ref state));
            }

            var scalar = field.Reduce(value: NextCarrier<T>(state: ref state));

            field.MultiplyAccumulateRegion(destination: destination, source: source, scalar: scalar);
            FoldRegion(digest: ref digest, values: destination);
            field.AddRegion(destination: destination, source: source);
            FoldRegion(digest: ref digest, values: destination);
            field.ScaleRegion(destination: destination, source: source, scalar: scalar);
            FoldRegion(digest: ref digest, values: destination);
            field.ScaleRegionInPlace(values: destination, scalar: scalar);
            FoldRegion(digest: ref digest, values: destination);
        }
    }
    /// <summary>Runs the scalar workload at one field and folds every result into the running digest.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="field">The field to operate in.</param>
    /// <param name="digest">The running digest.</param>
    /// <param name="state">The mixing state the workload's values are drawn from.</param>
    private static void FoldScalar<T>(BinaryField<T> field, ref ulong digest, ref ulong state) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        for (var trial = 0; (trial < ScalarTrials); ++trial) {
            var left = field.Reduce(value: NextCarrier<T>(state: ref state));
            var right = field.Reduce(value: NextCarrier<T>(state: ref state));

            Fold(digest: ref digest, value: field.Multiply(left: left, right: right));
            Fold(digest: ref digest, value: field.Square(value: left));
            Fold(digest: ref digest, value: field.SquareRoot(value: right));
            Fold(digest: ref digest, value: field.Exponentiate(value: left, exponent: (Next(state: ref state) & 0xFFFFUL)));

            if (T.Zero != right) {
                Fold(digest: ref digest, value: field.Inverse(value: right));
                Fold(digest: ref digest, value: field.Divide(left: left, right: right));
            }
        }
    }
    /// <summary>Mixes one 64-bit word into the running digest.</summary>
    /// <param name="digest">The running digest.</param>
    /// <param name="value">The word to mix.</param>
    private static void Mix(ref ulong digest, ulong value) {
        digest = ((digest ^ value) * 0x9E37_79B9_7F4A_7C15UL);
        digest ^= (digest >>> 29);
    }
    /// <summary>Draws the next 64-bit word from the workload's mixing state.</summary>
    /// <param name="state">The mixing state, advanced in place.</param>
    /// <returns>The drawn word.</returns>
    private static ulong Next(ref ulong state) {
        state += 0x9E37_79B9_7F4A_7C15UL;

        var mixed = state;

        mixed = ((mixed ^ (mixed >>> 30)) * 0xBF58_476D_1CE4_E5B9UL);
        mixed = ((mixed ^ (mixed >>> 27)) * 0x94D0_49BB_1331_11EBUL);

        return (mixed ^ (mixed >>> 31));
    }
    /// <summary>Draws the next packed carrier value from the workload's mixing state.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="state">The mixing state, advanced in place.</param>
    /// <returns>The drawn value, truncated to the carrier.</returns>
    private static T NextCarrier<T>(ref ulong state) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var low = Next(state: ref state);

        // The widest carrier needs two draws, and taking them unconditionally would desynchronize the narrower
        // carriers' value streams from this one — the digest is only meaningful because the draw order is fixed.
        if (typeof(T) == typeof(UInt128)) {
            return T.CreateTruncating(value: ((((UInt128)Next(state: ref state)) << 64) | low));
        }

        return T.CreateTruncating(value: low);
    }
}
