using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Counting;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? BoundedWordExactRejection() {
        foreach (var bound in new uint[] { 1, 2, 3, 208, 2147483649, uint.MaxValue }) {
            foreach (var word in new uint[] { 0, 1, 2, 17, 0x80000000, uint.MaxValue }) {
                var quotient = BigInteger.DivRem(dividend: (((BigInteger)word) * bound), divisor: (BigInteger.One << 32), remainder: out var remainder);
                var expected = (remainder >= ((BigInteger.One << 32) % bound));

                if ((BoundedRandomSampling.TryMap(exclusiveHigh: bound, value: out var actual, word: word) != expected) || (expected && actual != quotient)) {
                    return $"word {word}, bound {bound}: acceptance or exact quotient differs";
                }
            }
        }
        try { BoundedRandomSampling.TryMap(exclusiveHigh: 0, value: out _, word: 1); return "zero bound was admitted"; } catch (ArgumentOutOfRangeException) { }
        return null;
    }

    private struct ScriptedWords(uint[] words) : IRawWordSource {
        private int m_next;

        public readonly int Position => m_next;

        public bool TryNextWord(out uint word, out string reason) {
            if (m_next >= words.Length) { word = 0; reason = "script ended"; return false; }
            word = words[m_next++];
            reason = string.Empty;
            return true;
        }
    }

    public static string? BoundedBudgetedRejection() {
        // Bound 2147483649 has rejection threshold 2^31 - 1: word 0 (low product 0) is rejected, word 5 is accepted as 2.
        var rejected = 0U;
        var source = new ScriptedWords([rejected, rejected, 5U]);

        if (!BoundedRandomSampling.TrySampleRange(maxAttempts: 4, maximum: 2147483648U, minimum: 0U, reason: out var reason, source: ref source, value: out var value, word: out var word, words: out var words) || (words != 3) || (word != 5U) || (value != 2U)) { return $"scripted rejection: {value}, {word}, {words}, '{reason}'"; }

        source = new ScriptedWords([rejected, rejected, rejected]);
        if (BoundedRandomSampling.TrySampleRange(maxAttempts: 2, maximum: 2147483648U, minimum: 0U, reason: out reason, source: ref source, value: out _, word: out _, words: out words) || (words != 2) || (reason != BoundedRandomSampling.ExhaustedReason)) { return $"budget exhaustion: {words}, '{reason}'"; }

        source = new ScriptedWords([]);
        if (BoundedRandomSampling.TrySampleRange(maxAttempts: 2, maximum: 7U, minimum: 0U, reason: out reason, source: ref source, value: out _, word: out _, words: out words) || (words != 0) || (reason != "script ended")) { return $"source refusal: {words}, '{reason}'"; }

        source = new ScriptedWords([0x12345678U]);
        if (!BoundedRandomSampling.TrySampleRange(maxAttempts: 1, maximum: uint.MaxValue, minimum: 0U, reason: out reason, source: ref source, value: out value, word: out _, words: out words) || (value != 0x12345678U) || (words != 1)) { return "full-width range is not the raw word"; }

        // A 73-bit bound draws three words per candidate, masks to 73 bits, and rejects a candidate at or past the bound.
        var bound = (UInt128.One << 72) + 5U;
        source = new ScriptedWords([0xFFFFFFFFU, 0xFFFFFFFFU, 0xFFFFFFFFU, 1U, 2U, 1U]);
        if (!BoundedRandomSampling.TrySampleWide(exclusiveHigh: bound, maxAttempts: 4, reason: out reason, source: ref source, value: out var wide, words: out words)) { return $"wide draw refused: '{reason}'"; }
        // The first candidate (3 words, masked to 73 bits) is >= bound; the second is (1 << 64) | (2 << 32) | 1.
        if ((words != 6) || (wide != ((UInt128.One << 64) | (((UInt128)2U) << 32) | 1U))) { return $"wide draw: {wide}, {words}"; }

        source = new ScriptedWords([0xFFFFFFFFU, 0xFFFFFFFFU, 0xFFFFFFFFU, 0xFFFFFFFFU, 0xFFFFFFFFU, 0xFFFFFFFFU]);
        if (BoundedRandomSampling.TrySampleWide(exclusiveHigh: bound, maxAttempts: 2, reason: out reason, source: ref source, value: out _, words: out words) || (words != 6) || (reason != BoundedRandomSampling.ExhaustedReason)) { return $"wide exhaustion: {words}, '{reason}'"; }

        source = new ScriptedWords([0xFFFFFFFFU]);
        if (!BoundedRandomSampling.TrySampleWide(exclusiveHigh: 8U, maxAttempts: 2, reason: out reason, source: ref source, value: out wide, words: out words) || (wide != 7U) || (words != 1)) { return "narrow wide bound did not take the one-word path"; }

        try { BoundedRandomSampling.TrySampleWide(exclusiveHigh: UInt128.Zero, maxAttempts: 1, reason: out _, source: ref source, value: out _, words: out _); return "zero wide bound was admitted"; } catch (ArgumentOutOfRangeException) { }
        return null;
    }

    // ---- sampling ----

    // A primitive degree-4 generator (t^4 + t + 1) and a non-primitive irreducible one (t^4 + t^3 + t^2 + t + 1), the
    // pair BinaryPolynomial's own primitivity law already separates; used here only to reach the builder's gates.
    private const ulong SamplingNonPrimitiveDegree4 = 0b11111UL;
    private const ulong SamplingPrimitiveDegree4 = 0b10011UL;
    // t^32 + t^22 + t^2 + t + 1, a maximal-length degree-32 recurrence: the inclusive top of the degree window.
    private const ulong SamplingPrimitiveDegree32 = (1UL << 32) | (1UL << 22) | 0b111UL;

    /// <summary>The direction-number builders' refusal ladder, in the builder's own validation order, each refusal
    /// named against an argument the caller supplied rather than against a local the builder derived.</summary>
    public static string? DigitalNetDirectionNumberRefusals() {
        var count = DigitalNetSampler.DirectionNumberCount;
        var planeCount = DigitalNetSampler.PlaneDirectionNumberCount;

        if (count != 32) { return $"DirectionNumberCount is {count}"; }
        if (planeCount != (2 * count)) { return $"PlaneDirectionNumberCount is {planeCount}"; }

        var destination = new uint[count];
        var plane = new uint[planeCount];
        var generator = DigitalNetSampler.PlaneGenerator;

        if (generator.Bits != 0b11UL) { return $"PlaneGenerator is {generator}"; }
        if (generator.Degree != 1) { return $"PlaneGenerator has degree {generator.Degree}"; }

        // The degree window, at BOTH ends and at both off-by-ones outside it. The degree is derived inside the builder
        // from the polynomial, so the caller has no 'order' argument to correct — the refusal must say 'generator'.
        foreach (var (bits, degree) in SamplingOutOfWindowGenerators) {
            var candidate = new BinaryPolynomial(bits: bits);
            var initial = new uint[Math.Max(
                val1: 0,
                val2: degree
            )];

            initial.AsSpan().Fill(value: 1U);

            if (!ThrowsExactly<ArgumentOutOfRangeException>(
                action: () => DigitalNetSampler.BuildDirectionNumbers(
                    destination: destination,
                    generator: candidate,
                    initialNumbers: initial
                ),
                paramName: "generator"
            )) {
                return $"a degree-{degree} generator refused naming {(RefusedParameter(action: () => DigitalNetSampler.BuildDirectionNumbers(
                    destination: destination,
                    generator: candidate,
                    initialNumbers: initial
                )) ?? "nothing")}";
            }

            if (ActualValueOf(action: () => DigitalNetSampler.BuildDirectionNumbers(
                destination: destination,
                generator: candidate,
                initialNumbers: initial
            )) is not int actual) {
                return $"the degree-{degree} refusal carried no actual value";
            }

            if (actual != degree) { return $"the degree-{degree} refusal reported actual value {actual}"; }
        }

        // Both ends of the window are ACCEPTED, so the gate is a window and not a wall.
        var top = new BinaryPolynomial(bits: SamplingPrimitiveDegree32);
        var topInitial = new uint[32];

        for (var index = 0; (index < topInitial.Length); ++index) {
            topInitial[index] = 1U;
        }

        DigitalNetSampler.BuildDirectionNumbers(
            destination: destination,
            generator: generator,
            initialNumbers: [1U]
        );

        if (destination[0] != (1U << 31)) { return $"the degree-one recurrence's leading direction number is {destination[0]}"; }

        DigitalNetSampler.BuildDirectionNumbers(
            destination: destination,
            generator: top,
            initialNumbers: topInitial
        );

        if (destination[31] == 0U) { return "the degree-32 recurrence produced a zero trailing direction number"; }

        // Validation ORDER, each row pairing the fault under test with a LATER fault the ladder must not reach first.
        var nonPrimitive = new BinaryPolynomial(bits: SamplingNonPrimitiveDegree4);
        var primitive = new BinaryPolynomial(bits: SamplingPrimitiveDegree4);
        var shortDestination = new uint[(count - 1)];

        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => DigitalNetSampler.BuildDirectionNumbers(
                generator: BinaryPolynomial.Zero,
                initialNumbers: [],
                destination: shortDestination
            ),
            paramName: "generator"
        )) { return "the degree gate does not precede the destination gate"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => DigitalNetSampler.BuildDirectionNumbers(
                destination: shortDestination,
                generator: nonPrimitive,
                initialNumbers: []
            ),
            paramName: "destination"
        )) { return "the destination gate does not precede the initial-numerator count gate"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => DigitalNetSampler.BuildDirectionNumbers(
                destination: destination,
                generator: nonPrimitive,
                initialNumbers: [1U]
            ),
            paramName: "initialNumbers"
        )) { return "the initial-numerator count gate does not precede the primitivity gate"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => DigitalNetSampler.BuildDirectionNumbers(
                destination: destination,
                generator: nonPrimitive,
                initialNumbers: [1U, 1U, 1U, 1U]
            ),
            paramName: "generator"
        )) { return "a non-primitive generator was accepted, or refused under the wrong name"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => DigitalNetSampler.BuildDirectionNumbers(
                destination: destination,
                generator: primitive,
                initialNumbers: [2U, 1U, 1U, 1U]
            ),
            paramName: "initialNumbers"
        )) { return "an even initial numerator was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => DigitalNetSampler.BuildDirectionNumbers(
                destination: destination,
                generator: primitive,
                initialNumbers: [1U, 5U, 1U, 1U]
            ),
            paramName: "initialNumbers"
        )) { return "an over-large initial numerator was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => DigitalNetSampler.BuildBitReversalDirectionNumbers(destination: shortDestination),
            paramName: "destination"
        )) { return "the bit-reversal builder accepted a short destination"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => DigitalNetSampler.BuildPlaneDirectionNumbers(destination: destination),
            paramName: "destination"
        )) { return "the plane builder accepted a one-dimension destination"; }

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: plane);

        if (plane[0] != (1U << 31)) { return $"the plane's dimension-zero leading direction number is {plane[0]}"; }
        if (plane[count] != (1U << 31)) { return $"the plane's dimension-one leading direction number is {plane[count]}"; }

        return null;
    }

    // The four rows outside the builder's inclusive [1, 32] degree window: the zero polynomial, the constant one, and
    // both off-by-ones about the top. Each carries the degree the refusal must report as its actual value.
    private static readonly (ulong Bits, int Degree)[] SamplingOutOfWindowGenerators = [
        (Bits: 0UL, Degree: -1),
        (Bits: 1UL, Degree: 0),
        (Bits: (1UL << 33) | 1UL, Degree: 33),
        (Bits: (1UL << 40) | 1UL, Degree: 40),
    ];

    /// <summary>The alias factories' shared refusal ladder — one count contract across all four weight types, settled
    /// before any conversion buffer is asked for — and the fixed-point overloads as draw-for-draw twins of the raw
    /// core they convert into.</summary>
    public static string? AliasTableRefusalsAndFixedTwins() {
        // One count refusal, four weight types: the same exception type, parameter name and message, or the contract
        // is four contracts wearing one sentence in the docs.
        var messages = new string?[4];

        messages[0] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: ReadOnlySpan<(int, ulong)>.Empty));
        messages[1] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: ReadOnlySpan<(int, double)>.Empty));
        messages[2] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: ReadOnlySpan<(int, FixedQ4816)>.Empty));
        messages[3] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: ReadOnlySpan<(int, UFixedQ4816)>.Empty));

        for (var index = 0; (index < messages.Length); ++index) {
            if (messages[index] is null) { return $"overload {index} accepted an empty span"; }
            if (messages[index] != messages[0]) { return $"overload {index} refuses an empty span with \"{messages[index]}\" where overload 0 says \"{messages[0]}\""; }
        }

        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: ReadOnlySpan<(int, ulong)>.Empty),
            paramName: "entries"
        )) { return "the empty refusal is not a plain ArgumentException naming entries"; }

        // The over-limit end, reached without paying for it. The span is SYNTHETIC — a length of 2^30 + 1 over one
        // real element — and is legal precisely because the count refusal precedes every read and every allocation:
        // an over-limit conversion buffer is tens of gigabytes, so a count checked afterwards would reach the caller
        // as OutOfMemoryException instead of the ArgumentException these overloads promise.
        var overLimit = ((1 << 30) + 1);
        var raw = new (int Element, ulong Weight)[1];
        var real = new (int Element, double Weight)[1];
        var signed = new (int Element, FixedQ4816 Weight)[1];
        var unsigned = new (int Element, UFixedQ4816 Weight)[1];

        var overLimitMessages = new string?[4];

        overLimitMessages[0] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
            reference: ref raw[0],
            length: overLimit
        )));
        overLimitMessages[1] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
            reference: ref real[0],
            length: overLimit
        )));
        overLimitMessages[2] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
            reference: ref signed[0],
            length: overLimit
        )));
        overLimitMessages[3] = RefusedMessage(action: () => _ = WeightedSampler.Create<int>(entries: System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
            reference: ref unsigned[0],
            length: overLimit
        )));

        for (var index = 0; (index < overLimitMessages.Length); ++index) {
            if (overLimitMessages[index] != messages[0]) { return $"overload {index} refuses 2^30 + 1 entries with \"{overLimitMessages[index]}\" where the empty span says \"{messages[0]}\""; }
        }

        // The signed overload's negative-weight refusal, priced. A quarter-million entries convert into four megabytes;
        // the refusal is at index zero, so a factory that converted first would be caught by the meter and not merely
        // by the exception type.
        var negative = new (int Element, FixedQ4816 Weight)[(1 << 18)];

        negative[0] = (0, FixedQ4816.FromRawBits(value: -1L));

        for (var index = 1; (index < negative.Length); ++index) {
            negative[index] = (index, FixedQ4816.FromRawBits(value: 1L));
        }

        var refused = true;
        var spent = AllocationWindow.Measure(window: () => refused &= ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: negative),
            paramName: "entries"
        ));

        if (!refused) { return "a negative signed weight was accepted"; }

        if (spent > 65536L) { return $"the negative-weight refusal allocated {spent} bytes, so it converted before it refused"; }

        // The all-zero refusal, still shared, still after the count gate.
        var zeroRaw = new (int Element, ulong Weight)[] { (0, 0UL), (1, 0UL) };
        var zeroReal = new (int Element, double Weight)[] { (0, 0d), (1, 0d) };
        var zeroSigned = new (int Element, FixedQ4816 Weight)[] { (0, FixedQ4816.Zero), (1, FixedQ4816.Zero) };
        var zeroUnsigned = new (int Element, UFixedQ4816 Weight)[] { (0, UFixedQ4816.Zero), (1, UFixedQ4816.Zero) };
        var nonFinite = new (int Element, double Weight)[] { (0, double.NaN), (1, 1d) };
        var negativeReal = new (int Element, double Weight)[] { (0, -1d), (1, 1d) };

        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: zeroRaw),
            paramName: "entries"
        )) { return "an all-zero raw weight set was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: zeroReal),
            paramName: "entries"
        )) { return "an all-zero real weight set was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: zeroSigned),
            paramName: "entries"
        )) { return "an all-zero signed fixed weight set was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: zeroUnsigned),
            paramName: "entries"
        )) { return "an all-zero unsigned fixed weight set was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: nonFinite),
            paramName: "entries"
        )) { return "a non-finite real weight was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = WeightedSampler.Create<int>(entries: negativeReal),
            paramName: "entries"
        )) { return "a negative real weight was accepted"; }

        // The fixed-point overloads are carriage: they hand the raw carrier through unchanged, so their tables must be
        // the raw table's draw-for-draw twins from an identically seeded generator.
        var rawWeights = SamplingAliasWeights;
        var rawEntries = new (int Element, ulong Weight)[rawWeights.Length];
        var signedEntries = new (int Element, FixedQ4816 Weight)[rawWeights.Length];
        var unsignedEntries = new (int Element, UFixedQ4816 Weight)[rawWeights.Length];

        for (var index = 0; (index < rawWeights.Length); ++index) {
            rawEntries[index] = (index, rawWeights[index]);
            signedEntries[index] = (index, FixedQ4816.FromRawBits(value: ((long)rawWeights[index])));
            unsignedEntries[index] = (index, UFixedQ4816.FromRawBits(value: rawWeights[index]));
        }

        var rawTable = WeightedSampler.Create(entries: new ReadOnlySpan<(int, ulong)>(array: rawEntries));
        var signedTable = WeightedSampler.Create(entries: new ReadOnlySpan<(int, FixedQ4816)>(array: signedEntries));
        var unsignedTable = WeightedSampler.Create(entries: new ReadOnlySpan<(int, UFixedQ4816)>(array: unsignedEntries));

        if (rawTable.Count != rawWeights.Length) { return $"the raw table reports {rawTable.Count} entries"; }
        if (signedTable.Count != rawTable.Count) { return $"the signed table reports {signedTable.Count} entries"; }
        if (unsignedTable.Count != rawTable.Count) { return $"the unsigned table reports {unsignedTable.Count} entries"; }

        var rawGenerator = Pcg32XshRr.Create(
            state: 0x5AA5U,
            stream: 7UL
        );
        var signedGenerator = Pcg32XshRr.Create(
            state: 0x5AA5U,
            stream: 7UL
        );
        var unsignedGenerator = Pcg32XshRr.Create(
            state: 0x5AA5U,
            stream: 7UL
        );

        for (var draw = 0; (draw < 4096); ++draw) {
            var expected = rawTable.SampleIndex(generator: ref rawGenerator);
            var fromSigned = signedTable.SampleIndex(generator: ref signedGenerator);
            var fromUnsigned = unsignedTable.SampleIndex(generator: ref unsignedGenerator);

            if (expected < 0) { return $"draw {draw} selected index {expected}"; }
            if (expected >= rawTable.Count) { return $"draw {draw} selected index {expected} at count {rawTable.Count}"; }
            if (rawWeights[expected] == 0UL) { return $"draw {draw} selected zero-weight entry {expected}"; }
            if (fromSigned != expected) { return $"draw {draw}: the signed twin selected {fromSigned} where the raw table selected {expected}"; }
            if (fromUnsigned != expected) { return $"draw {draw}: the unsigned twin selected {fromUnsigned} where the raw table selected {expected}"; }
            if (rawTable.Sample(generator: ref rawGenerator) != rawTable.SampleIndex(generator: ref signedGenerator)) { return $"draw {draw}: Sample and SampleIndex disagree on identically advanced generators"; }
            if (rawGenerator.State != signedGenerator.State) { return $"draw {draw}: Sample and SampleIndex consumed different advance counts"; }

            _ = unsignedTable.SampleIndex(generator: ref unsignedGenerator);
        }

        return null;
    }

    // Element weights for the alias twins: a zero entry, a one, a saturating carrier extreme and a spread between, at
    // a length that is NOT a power of two so the padding columns exist and must stay unsampled.
    private static readonly ulong[] SamplingAliasWeights = [
        0UL,
        1UL,
        65536UL,
        3UL,
        ((ulong)(long.MaxValue >> 1)),
        7UL,
        0UL,
        1024UL,
        ((ulong)long.MaxValue),
        11UL,
        0UL,
    ];

    // The stored-norm measurement is EXACT: every quantity below is a BigInteger at a common scale of 2^400, so no
    // float arithmetic enters the law that judges a float table. A binary32's value is significand·2^scale exactly,
    // and so is its square.
    private const int ConeNormScale = 400;

    // The envelope, derived from the FORMAT rather than measured from a run: two independent binary32 roundings, each
    // at a relative error of at most 2^-24, move the squared norm by at most 2·2^-24 = 2^-23, and the second term
    // absorbs their product and the double-side construction's own rounding with room to spare.
    private static readonly BigInteger ConeNormEnvelope = ((BigInteger.One << (ConeNormScale - 23)) + (BigInteger.One << (ConeNormScale - 40)));
    private static readonly BigInteger ConeNormUnit = (BigInteger.One << ConeNormScale);
    // The half-angle ladder: the admitted zero and its negative twin, a small production angle, two ordinary ones, and
    // the closest double below the open upper bound.
    private static readonly double[] ConeHalfAngles = [
        0.0d,
        -0.0d,
        0.01d,
        (Math.PI / 6.0d),
        (Math.PI / 3.0d),
        Math.BitDecrement(x: (0.5d * Math.PI)),
    ];

    /// <summary>The cone table's consumer-visible contract: the layout constants, the refusal ladder, the stored-float
    /// norm envelope, and the uniqueness statement with its one declared degeneration.</summary>
    public static string? ConeDirectionTableContract() {
        var azimuthCount = ConeDirectionTable.AzimuthEntryCount;
        var azimuthOffset = ConeDirectionTable.AzimuthOffset;
        var directionOffset = ConeDirectionTable.DirectionNumberOffset;
        var radiusCount = ConeDirectionTable.RadiusEntryCount;
        var radiusOffset = ConeDirectionTable.RadiusOffset;
        var indexBits = ConeDirectionTable.TableIndexBitCount;
        var wordCount = ConeDirectionTable.WordCount;

        if (indexBits != 12) { return $"TableIndexBitCount is {indexBits}"; }
        if (azimuthCount != (1 << indexBits)) { return $"AzimuthEntryCount is {azimuthCount}"; }
        if (radiusCount != (1 << indexBits)) { return $"RadiusEntryCount is {radiusCount}"; }
        if (directionOffset != 0) { return $"DirectionNumberOffset is {directionOffset}"; }
        if (azimuthOffset != DigitalNetSampler.PlaneDirectionNumberCount) { return $"AzimuthOffset is {azimuthOffset}"; }
        if (radiusOffset != (azimuthOffset + (2 * azimuthCount))) { return $"RadiusOffset is {radiusOffset}"; }
        if (wordCount != (radiusOffset + (2 * radiusCount))) { return $"WordCount is {wordCount}"; }

        var table = new uint[wordCount];
        var shortTable = new uint[(wordCount - 1)];

        // The angle gate precedes the destination gate: the first row pairs a bad angle with a bad destination.
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => ConeDirectionTable.Build(
                capHalfAngleRadians: -1.0d,
                destination: shortTable
            ),
            paramName: "capHalfAngleRadians"
        )) { return "the angle gate does not precede the destination gate"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => ConeDirectionTable.Build(
                capHalfAngleRadians: double.NaN,
                destination: table
            ),
            paramName: "capHalfAngleRadians"
        )) { return "a not-a-number half-angle was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => ConeDirectionTable.Build(
                capHalfAngleRadians: (0.5d * Math.PI),
                destination: table
            ),
            paramName: "capHalfAngleRadians"
        )) { return "a half-angle of exactly pi/2 was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => ConeDirectionTable.Build(
                capHalfAngleRadians: double.PositiveInfinity,
                destination: table
            ),
            paramName: "capHalfAngleRadians"
        )) { return "an infinite half-angle was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => ConeDirectionTable.Build(
                capHalfAngleRadians: (Math.PI / 3.0d),
                destination: shortTable
            ),
            paramName: "destination"
        )) { return "a short destination was accepted"; }

        var plane = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var seen = new HashSet<ulong>();
        var worst = BigInteger.Zero;

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: plane);

        foreach (var angle in ConeHalfAngles) {
            ConeDirectionTable.Build(
                capHalfAngleRadians: angle,
                destination: table
            );

            for (var index = 0; (index < plane.Length); ++index) {
                if (table[(directionOffset + index)] != plane[index]) { return $"at half-angle {angle.ToString(provider: CultureInfo.InvariantCulture)} the table's direction number {index} is not the shipped plane's"; }
            }

            // The azimuth table: the same two-rounding envelope, and 4096 distinct directions around the axis.
            seen.Clear();

            for (var index = 0; (index < azimuthCount); ++index) {
                var cosineBits = table[(azimuthOffset + (2 * index))];
                var sineBits = table[((azimuthOffset + (2 * index)) + 1)];
                var error = BigInteger.Abs(value: ((ConeScaledSquare(bits: cosineBits) + ConeScaledSquare(bits: sineBits)) - ConeNormUnit));

                if (error > ConeNormEnvelope) { return $"azimuth entry {index} stores a squared norm off by {error} at scale 2^{ConeNormScale}, past the envelope {ConeNormEnvelope}"; }
                if (!seen.Add(item: (((ulong)cosineBits) << 32) | sineBits)) { return $"azimuth entry {index} repeats an earlier direction"; }

                if (error > worst) { worst = error; }
            }

            // The polar table: the envelope again, measured on what a CONSUMER reads back rather than on the
            // pre-rounding double expressions the builder discarded.
            seen.Clear();

            for (var index = 0; (index < radiusCount); ++index) {
                var axialBits = table[(radiusOffset + (2 * index))];
                var radialBits = table[((radiusOffset + (2 * index)) + 1)];
                var error = BigInteger.Abs(value: ((ConeScaledSquare(bits: axialBits) + ConeScaledSquare(bits: radialBits)) - ConeNormUnit));

                if (error > ConeNormEnvelope) { return $"at half-angle {angle.ToString(provider: CultureInfo.InvariantCulture)} polar entry {index} stores a squared norm off by {error} at scale 2^{ConeNormScale}, past the envelope {ConeNormEnvelope}"; }
                if ((axialBits >> 31) != 0U) { return $"at half-angle {angle.ToString(provider: CultureInfo.InvariantCulture)} polar entry {index} stores a negatively signed axial component"; }
                if (ConeScaledSquare(bits: axialBits).IsZero) { return $"at half-angle {angle.ToString(provider: CultureInfo.InvariantCulture)} polar entry {index} stores a zero axial component"; }
                if (
                    ((radialBits >> 31) != 0U) &&
                    !ConeScaledSquare(bits: radialBits).IsZero
                ) { return $"at half-angle {angle.ToString(provider: CultureInfo.InvariantCulture)} polar entry {index} stores a negative radial component"; }

                _ = seen.Add(item: (((ulong)axialBits) << 32) | radialBits);

                if (error > worst) { worst = error; }
            }

            // The zero cap IS one direction, so the whole polar table collapses onto the axis — the declared
            // degeneration. Every other rung of the ladder resolves all 4096 cells into distinct stored pairs.
            var distinct = seen.Count;
            var degenerate = (angle == 0.0d);

            if (
                degenerate &&
                (distinct != 1)
            ) { return $"the zero cap resolved {distinct} distinct polar directions"; }
            if (
                !degenerate &&
                (distinct != radiusCount)
            ) { return $"at half-angle {angle.ToString(provider: CultureInfo.InvariantCulture)} the polar table resolved {distinct} of {radiusCount} cells"; }

            if (degenerate) {
                var axialBits = table[radiusOffset];
                var radialBits = table[(radiusOffset + 1)];

                if (axialBits != 0x3F80_0000U) { return $"the zero cap's axial component has bits {axialBits} rather than one"; }

                // The +0.0/-0.0 policy, spelled out rather than left to whichever the runtime happened to produce: the
                // sign is carried through and NOT canonicalized, and it is unobservable because the magnitude is zero.
                var expectedRadial = (double.IsNegative(d: angle)
                    ? 0x8000_0000U
                    : 0x0000_0000U
                );

                if (radialBits != expectedRadial) {
                    return $"the {(double.IsNegative(d: angle)
                        ? "negative"
                        : "positive")}-zero cap stored radial bits {radialBits}";
                }
            }
        }

        // The envelope must BITE: a run in which every stored pair happened to be exactly unit length would satisfy
        // every row above while proving nothing, so the law asserts that the rounding is visible.
        if (worst.IsZero) { return "no stored pair departed from unit length, so the envelope is vacuous"; }
        if (worst > ConeNormEnvelope) { return $"the worst stored departure was {worst} at scale 2^{ConeNormScale}"; }

        return null;
    }

    // The published opening six draws of the reference generator seeded srandom(42, 54), tabulated outside this tree.
    private static readonly uint[] PcgPublishedDraws = [0xA15C02B7U, 0x7B47F409U, 0xBA1D3330U, 0x83D2F293U, 0xBFA4784BU, 0xCBED606EU];
    // The advance ladder: identity, the first few steps, a byte boundary, and two counts no sequential loop reaches by
    // accident. Every rung is checked forwards against a sequential walk and backwards through the complement.
    private static readonly ulong[] PcgAdvanceLadder = [0UL, 1UL, 2UL, 3UL, 7UL, 64UL, 255UL, 4096UL, 100000UL];

    /// <summary>The generator's published reference vector, its snapshot contract, the logarithmic advance against a
    /// sequential walk, the bounded and fraction adapters, and the refusal ladder that guards its full period.</summary>
    public static string? PcgReferenceVectorAndState() {
        var multiplier = Pcg32XshRr.DefaultMultiplier;
        var maxStream = Pcg32XshRr.MaxStream;

        if (multiplier != 6364136223846793005UL) { return $"DefaultMultiplier is {multiplier}"; }
        if (maxStream != ((1UL << 63) - 1UL)) { return $"MaxStream is {maxStream}"; }

        // The published vector. Both factories must reach it: Create(state, stream) is documented as the reference
        // recipe, and the explicit-multiplier overload with the default multiplier must be the same generator.
        var reference = Pcg32XshRr.Create(
            state: 42UL,
            stream: 54UL
        );
        var explicitly = Pcg32XshRr.Create(
            multiplier: multiplier,
            state: 42UL,
            stream: 54UL
        );

        if (reference.Multiplier != multiplier) { return $"the reference generator carries multiplier {reference.Multiplier}"; }
        if (reference.Increment != ((54UL << 1) | 1UL)) { return $"the reference generator carries increment {reference.Increment}"; }

        for (var index = 0; (index < PcgPublishedDraws.Length); ++index) {
            var drawn = reference.NextUInt32();

            if (drawn != PcgPublishedDraws[index]) { return $"reference draw {index} is 0x{drawn:X8}, not the published 0x{PcgPublishedDraws[index]:X8}"; }
            if (explicitly.NextUInt32() != drawn) { return $"the explicit-multiplier factory diverged at draw {index}"; }
        }

        // The snapshot contract: the three readable words ARE the generator, and FromRawBits continues the sequence.
        var restored = Pcg32XshRr.FromRawBits(
            increment: reference.Increment,
            multiplier: reference.Multiplier,
            state: reference.State
        );

        for (var index = 0; (index < 1024); ++index) {
            if (restored.NextUInt32() != reference.NextUInt32()) { return $"the restored snapshot diverged {index} draws after the capture"; }
            if (restored.State != reference.State) { return $"the restored snapshot's state diverged {index} draws after the capture"; }
        }

        // Advance against a sequential walk, in both directions. Only whole-state advances are counted, which is why
        // the sequential arm calls the RAW draw and not a bounded one.
        foreach (var count in PcgAdvanceLadder) {
            var skipped = Pcg32XshRr.Create(
                state: 9UL,
                stream: 3UL
            );
            var walked = Pcg32XshRr.Create(
                state: 9UL,
                stream: 3UL
            );

            skipped.Advance(count: count);

            for (var step = 0UL; (step < count); ++step) {
                _ = walked.NextUInt32();
            }

            if (skipped.State != walked.State) { return $"Advance({count}) reached state {skipped.State} where {count} sequential draws reached {walked.State}"; }

            skipped.Advance(count: unchecked((0UL - count)));

            if (skipped.State != Pcg32XshRr.Create(
                state: 9UL,
                stream: 3UL
            ).State) { return $"Advance(2^64 - {count}) did not return to the starting state"; }
        }

        // The bounded draw: a singleton range, a full range that IS the raw draw, and invariance under swapped bounds,
        // each read off the STATE as well as the value so the advance count is part of the statement.
        var bounded = Pcg32XshRr.Create(
            state: 17UL,
            stream: 1UL
        );
        var twin = Pcg32XshRr.Create(
            state: 17UL,
            stream: 1UL
        );

        if (bounded.NextUInt32(
            maximum: 5U,
            minimum: 5U
        ) != 5U) { return "a singleton bounded range did not return its only value"; }

        _ = twin.NextUInt32();

        if (bounded.State != twin.State) { return "a singleton bounded range did not consume exactly one advance"; }
        if (bounded.NextUInt32(
            maximum: uint.MaxValue,
            minimum: 0U
        ) != twin.NextUInt32()) { return "a full bounded range is not the raw draw"; }
        if (bounded.State != twin.State) { return "a full bounded range did not consume exactly one advance"; }

        for (var index = 0; (index < 256); ++index) {
            var ordered = Pcg32XshRr.FromRawBits(
                increment: 1UL,
                multiplier: Pcg32XshRr.DefaultMultiplier,
                state: ((ulong)index)
            );
            var swapped = Pcg32XshRr.FromRawBits(
                increment: 1UL,
                multiplier: Pcg32XshRr.DefaultMultiplier,
                state: ((ulong)index)
            );
            var low = ordered.NextUInt32(
                maximum: 40000U,
                minimum: 100U
            );
            var high = swapped.NextUInt32(
                maximum: 100U,
                minimum: 40000U
            );

            if (low != high) { return $"swapping the bounds at state {index} changed the draw from {low} to {high}"; }
            if (
                (low < 100U) ||
                (low > 40000U)
            ) { return $"a bounded draw at state {index} left its interval: {low}"; }
            if (ordered.State != swapped.State) { return $"swapping the bounds at state {index} changed the advance count"; }
        }

        // The fraction adapters ARE the two expressions, so this is carriage; it states the shift and the width.
        var fractions = Pcg32XshRr.Create(
            state: 5UL,
            stream: 5UL
        );
        var raws = Pcg32XshRr.Create(
            state: 5UL,
            stream: 5UL
        );

        if (FractionAdaptersCarryRawDraws(
            narrow: () => fractions.NextUnitFraction16(),
            raw: () => raws.NextUInt32(),
            wide: () => fractions.NextUnitFraction32()
        ) is { } adapterFailure) { return adapterFailure; }

        // The Gaussian pair: exactly two advances, the single draw is the pair's first, and the documented cap holds.
        var gaussian = Pcg32XshRr.Create(
            state: 11UL,
            stream: 2UL
        );
        var gaussianTwin = Pcg32XshRr.Create(
            state: 11UL,
            stream: 2UL
        );
        var cap = (7L << 16);

        for (var index = 0; (index < 4096); ++index) {
            var before = gaussian.State;
            var pair = gaussian.NextGaussianPair();

            _ = gaussianTwin.NextUInt32();

            var after = gaussianTwin.NextUInt32();

            if (gaussian.State != gaussianTwin.State) { return $"NextGaussianPair at draw {index} did not consume exactly two advances (state {before} to {gaussian.State})"; }
            if (Math.Abs(value: pair.First.Value) >= cap) { return $"Gaussian draw {index} has magnitude {pair.First.Value}, past the documented cap"; }
            if (Math.Abs(value: pair.Second.Value) >= cap) { return $"Gaussian draw {index}'s second value has magnitude {pair.Second.Value}, past the documented cap"; }

            _ = after;
        }

        var single = Pcg32XshRr.Create(
            state: 11UL,
            stream: 2UL
        );
        var paired = Pcg32XshRr.Create(
            state: 11UL,
            stream: 2UL
        );

        if (single.NextGaussian().Value != paired.NextGaussianPair().First.Value) { return "NextGaussian is not the pair's first value"; }
        if (single.State != paired.State) { return "NextGaussian and NextGaussianPair consumed different advance counts"; }

        // The refusal ladder, in the factories' own order.
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = Pcg32XshRr.Create(
                multiplier: 3UL,
                state: 0UL,
                stream: (Pcg32XshRr.MaxStream + 1UL)
            ),
            paramName: "stream"
        )) { return "the stream gate does not precede the multiplier gate"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = Pcg32XshRr.Create(
                multiplier: 3UL,
                state: 0UL,
                stream: 0UL
            ),
            paramName: "multiplier"
        )) { return "a multiplier not congruent to one modulo four was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = Pcg32XshRr.FromRawBits(
                increment: 2UL,
                multiplier: 3UL,
                state: 0UL
            ),
            paramName: "increment"
        )) { return "the increment gate does not precede the multiplier gate"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = Pcg32XshRr.FromRawBits(
                increment: 1UL,
                multiplier: 3UL,
                state: 0UL
            ),
            paramName: "multiplier"
        )) { return "FromRawBits accepted a multiplier not congruent to one modulo four"; }

        _ = Pcg32XshRr.Create(
            multiplier: Pcg32XshRr.DefaultMultiplier,
            state: 0UL,
            stream: Pcg32XshRr.MaxStream
        );

        // The default instance is DEGENERATE and documented as such: it is a structural row, not an accident.
        var degenerate = default(Pcg32XshRr);

        if (degenerate.State != 0UL) { return $"the default generator's state is {degenerate.State}"; }
        if (degenerate.Increment != 0UL) { return $"the default generator's increment is {degenerate.Increment}"; }
        if (degenerate.Multiplier != 0UL) { return $"the default generator's multiplier is {degenerate.Multiplier}"; }
        if (degenerate.NextUInt32() != 0U) { return "the default generator drew a non-zero value"; }
        if (degenerate.State != 0UL) { return "the default generator's state moved"; }

        // The shuffle, against a Fisher-Yates walk restated here: same permutation AND same final state.
        var shuffled = new int[64];
        var expected = new int[64];

        for (var index = 0; (index < shuffled.Length); ++index) {
            shuffled[index] = index;
            expected[index] = index;
        }

        var shuffling = Pcg32XshRr.Create(
            state: 99UL,
            stream: 4UL
        );
        var reference2 = Pcg32XshRr.Create(
            state: 99UL,
            stream: 4UL
        );

        shuffling.Shuffle(values: shuffled.AsSpan());

        for (var index = (expected.Length - 1); (index > 0); --index) {
            var other = ((int)reference2.NextUInt32(
                maximum: ((uint)index),
                minimum: 0U
            ));

            (expected[index], expected[other]) = (expected[other], expected[index]);
        }

        if (shuffling.State != reference2.State) { return "Shuffle consumed a different advance count than the bounded-draw walk"; }

        var seenIndices = new HashSet<int>();

        for (var index = 0; (index < shuffled.Length); ++index) {
            if (shuffled[index] != expected[index]) { return $"Shuffle placed {shuffled[index]} at position {index} where the Fisher-Yates walk placed {expected[index]}"; }
            if (!seenIndices.Add(item: shuffled[index])) { return $"Shuffle repeated element {shuffled[index]}"; }
        }

        if (seenIndices.Count != shuffled.Length) { return $"Shuffle left {seenIndices.Count} distinct elements of {shuffled.Length}"; }

        return null;
    }
    /// <summary>The net's index-to-point map against classical references — bit reversal and Pascal's triangle modulo
    /// two — the finite net property, and the index shuffle's aligned-block law.</summary>
    public static string? DigitalNetSampleAndShuffleIdentities() {
        var count = DigitalNetSampler.DirectionNumberCount;
        var bitReversal = new uint[count];
        var plane = new uint[DigitalNetSampler.PlaneDirectionNumberCount];
        var shortSpan = new uint[(count - 1)];

        DigitalNetSampler.BuildBitReversalDirectionNumbers(destination: bitReversal);
        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: plane);

        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = DigitalNetSampler.Sample(
                directionNumbers: shortSpan,
                index: 0U,
                scramble: 0U
            ),
            paramName: "directionNumbers"
        )) { return "Sample accepted a short direction-number span"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => _ = DigitalNetSampler.SamplePlane(
                directionNumbers: bitReversal,
                index: 0U,
                scramble: (0U, 0U)
            ),
            paramName: "directionNumbers"
        )) { return "SamplePlane accepted a one-dimension direction-number span"; }

        // Dimension zero is the van der Corput sequence: the coordinate is the index with its bits reversed. The
        // reference reverses by its own loop rather than through the shipped bit-reversal helper.
        for (var index = 0U; (index < 4096U); ++index) {
            var reversed = 0U;

            for (var bit = 0; (bit < 32); ++bit) {
                reversed |= (((index >> bit) & 1U) << (31 - bit));
            }

            var sampled = DigitalNetSampler.Sample(
                directionNumbers: bitReversal,
                index: index,
                scramble: 0U
            );

            if (sampled != reversed) { return $"the radical inverse of {index} is 0x{sampled:X8}, not the reversed 0x{reversed:X8}"; }
        }

        // Dimension one's direction matrix is Pascal's triangle modulo two. The reference builds it from Lucas's
        // criterion — the binomial coefficient C(k, j) is odd exactly when j is a submask of k — which shares neither
        // the recurrence nor its arithmetic.
        var pascal = new uint[count];

        for (var row = 0; (row < count); ++row) {
            var numerator = 0U;

            for (var column = 0; (column <= row); ++column) {
                if ((column & ~row) == 0) { numerator |= (1U << column); }
            }

            pascal[row] = (numerator << ((count - 1) - row));
        }

        for (var index = 0; (index < count); ++index) {
            if (plane[(count + index)] != pascal[index]) { return $"the plane's dimension-one direction number {index} is 0x{plane[(count + index)]:X8}, not Pascal's 0x{pascal[index]:X8}"; }
        }

        // The digital shift is an exclusive-or on the coordinate, and SamplePlane is the two halves sampled apart.
        for (var index = 0U; (index < 2048U); ++index) {
            var unshifted = DigitalNetSampler.Sample(
                directionNumbers: bitReversal,
                index: index,
                scramble: 0U
            );
            var shifted = DigitalNetSampler.Sample(
                directionNumbers: bitReversal,
                index: index,
                scramble: 0xDEADBEEFU
            );

            if (shifted != (unshifted ^ 0xDEADBEEFU)) { return $"the digital shift is not an exclusive-or at index {index}"; }

            var point = DigitalNetSampler.SamplePlane(
                directionNumbers: plane,
                index: index,
                scramble: (X: 0x1234U, Y: 0x5678U)
            );

            if (point.X != DigitalNetSampler.Sample(
                index: index,
                directionNumbers: plane.AsSpan()[..count],
                scramble: 0x1234U
            )) { return $"SamplePlane's first coordinate is not dimension zero's at index {index}"; }
            if (point.Y != DigitalNetSampler.Sample(
                index: index,
                directionNumbers: plane.AsSpan()[count..],
                scramble: 0x5678U
            )) { return $"SamplePlane's second coordinate is not dimension one's at index {index}"; }
        }

        // The (0, m, 2) net property, finitely: for every m through ten and every dyadic box shape of area 2^-m, the
        // first 2^m points place exactly one point in every box. This is the theorem the whole type exists for.
        var occupied = new bool[(1 << 10)];

        foreach (var scramble in DigitalNetScrambles) {
            for (var m = 0; (m <= 10); ++m) {
                var points = (1 << m);

                for (var xBits = 0; (xBits <= m); ++xBits) {
                    var yBits = (m - xBits);

                    Array.Clear(
                        array: occupied,
                        index: 0,
                        length: points
                    );

                    for (var index = 0; (index < points); ++index) {
                        var point = DigitalNetSampler.SamplePlane(
                            directionNumbers: plane,
                            index: ((uint)index),
                            scramble: scramble
                        );
                        var box = ((int)(((xBits == 0)
                            ? 0U
                            : (point.X >> (32 - xBits))) | ((yBits == 0)
                            ? 0U
                            : ((point.Y >> (32 - yBits)) << xBits))));

                        if (occupied[box]) { return $"at shift ({scramble.X}, {scramble.Y}), m = {m} and shape {xBits}x{yBits}, box {box} took a second point at index {index}"; }

                        occupied[box] = true;
                    }
                }
            }
        }

        // The index shuffle: a bijection whose inverse is exact, and — the property a general mix does not have — one
        // that carries the aligned block [0, 2^m) onto an aligned block of the same size.
        foreach (var seed in DigitalNetShuffleSeeds) {
            for (var index = 0U; (index < 1024U); ++index) {
                var permuted = StratifiedShuffle.Permute(
                    index: index,
                    seed: seed
                );

                if (StratifiedShuffle.Unpermute(
                    index: permuted,
                    seed: seed
                ) != index) { return $"the shuffle at seed {seed} did not invert at index {index}"; }
                if (DigitalNetSampler.ShuffleIndex(
                    index: index,
                    salt: seed
                ) != permuted) { return $"ShuffleIndex is not the shuffle's permutation at index {index}"; }
            }

            var block = new HashSet<uint>();

            for (var m = 0; (m <= 12); ++m) {
                var size = (1U << m);
                var shift = (32 - m);

                block.Clear();

                var expectedPrefix = ((m == 0)
                    ? StratifiedShuffle.Permute(
                        index: 0U,
                        seed: seed
                    )
                    : (StratifiedShuffle.Permute(
                        index: 0U,
                        seed: seed
                    ) >> shift)
                );

                for (var index = 0U; (index < size); ++index) {
                    var permuted = StratifiedShuffle.Permute(
                        index: index,
                        seed: seed
                    );
                    var prefix = ((m == 0)
                        ? permuted
                        : (permuted >> shift)
                    );

                    if (prefix != expectedPrefix) { return $"at seed {seed} and m = {m} the block left its aligned image: index {index} landed under prefix {prefix}, not {expectedPrefix}"; }
                    if (!block.Add(item: permuted)) { return $"at seed {seed} and m = {m} the block collided at index {index}"; }
                }
            }
        }

        // The site key: a refusal that names the coordinate at fault, and injectivity where the packing is honest.
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = DigitalNetSampler.DeriveKey(
                stream: 0U,
                x: 0x10000U,
                y: 0U
            ),
            paramName: "x"
        )) { return "a seventeen-bit first coordinate was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = DigitalNetSampler.DeriveKey(
                stream: 0U,
                x: 0U,
                y: 0x10000U
            ),
            paramName: "y"
        )) { return "a seventeen-bit second coordinate was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = DigitalNetSampler.DeriveKey(
                stream: 0U,
                x: 0x10000U,
                y: 0x10000U
            ),
            paramName: "x"
        )) { return "a doubly out-of-range site did not name the first coordinate"; }

        _ = DigitalNetSampler.DeriveKey(
            stream: 0U,
            x: 0xFFFFU,
            y: 0xFFFFU
        );

        var keys = new HashSet<uint>();

        for (var x = 0U; (x < 64U); ++x) {
            for (var y = 0U; (y < 64U); ++y) {
                if (!keys.Add(item: DigitalNetSampler.DeriveKey(
                    stream: 7U,
                    x: x,
                    y: y
                ))) { return $"site ({x}, {y}) collided with an earlier site's key in stream 7"; }
            }
        }

        // The scramble derivation: the first coordinate's shift IS the key, and the second is a separate bijection of
        // it, so the two coordinates are not shifted by the same vector.
        var scrambles = new HashSet<uint>();
        var identical = 0;

        for (var key = 0U; (key < 4096U); ++key) {
            var derived = DigitalNetSampler.DeriveScramble(key: key);

            if (derived.X != key) { return $"the first coordinate's shift at key {key} is {derived.X}, not the key"; }
            if (!scrambles.Add(item: derived.Y)) { return $"the second coordinate's shift collided at key {key}"; }

            if (derived.Y == derived.X) { ++identical; }
        }

        if (identical > 1) { return $"{identical} of 4096 keys shifted both coordinates by the same vector"; }

        return null;
    }

    private static readonly (uint X, uint Y)[] DigitalNetScrambles = [(X: 0U, Y: 0U), (X: 0x9E3779B9U, Y: 0x85EBCA6BU)];
    private static readonly uint[] DigitalNetShuffleSeeds = [0U, 1U, 0xA5A5A5A5U, uint.MaxValue];

    /// <summary>Pcg3dLatticeNoise.Pcg3d against a wide-integer reference over edge and drawn operands, ValueNoise01's
    /// [0, 1] bound, and ValueNoise01's exact collapse onto the public Pcg3d corner at whole-cell boundaries.</summary>
    public static string? Pcg3dLatticeNoiseReferenceAndCorners() {
        ReadOnlySpan<uint> edges = [0U, 1U, 0x7FFFFFFFU, 0x80000000U, 0xFFFFFFFEU, uint.MaxValue];

        foreach (var x in edges) {
            foreach (var y in edges) {
                foreach (var z in edges) {
                    if (Pcg3dAgreesWithReference(
                        x: x,
                        y: y,
                        z: z
                    ) is { } edgeFailure) { return edgeFailure; }
                }
            }
        }

        var generator = Pcg32XshRr.Create(
            state: 0x9A11B7UL,
            stream: 11UL
        );

        for (var draw = 0; (draw < 256); ++draw) {
            if (Pcg3dAgreesWithReference(
                x: generator.NextUInt32(),
                y: generator.NextUInt32(),
                z: generator.NextUInt32()
            ) is { } drawFailure) { return drawFailure; }
        }

        // ValueNoise01 stays inside its documented [0, 1) band over a drawn operand stream — the whole signed cell
        // domain, negative indices included, at the half-open bound.
        for (var draw = 0; (draw < 256); ++draw) {
            var cellX = unchecked((int)generator.NextUInt32());
            var cellZ = unchecked((int)generator.NextUInt32());
            var noiseCells = ((int)((generator.NextUInt32() % 63U) + 1U));
            var seed = generator.NextUInt32();
            var sample = Pcg3dLatticeNoise.ValueNoise01(
                cellX: cellX,
                cellZ: cellZ,
                noiseCells: noiseCells,
                seed: seed
            );

            if (
                (sample.Value < FixedQ4816.Zero.Value) ||
                (sample.Value >= FixedQ4816.One.Value)
            ) { return $"ValueNoise01({cellX}, {cellZ}, {noiseCells}, {seed}) left [0, 1) at raw {sample.Value}"; }
        }

        // At an exact cell boundary the quintic fade is zero on both axes, so the blend collapses onto the near
        // corner — a corner ValueNoise01 does not compute privately here, but which its own Pcg3d, called directly,
        // reaches the identical way.
        for (var nx = 0; (nx <= 5); ++nx) {
            for (var nz = 0; (nz <= 5); ++nz) {
                foreach (var noiseCells in ((ReadOnlySpan<int>)[1, 7, 16])) {
                    var seed = generator.NextUInt32();
                    var corner = FixedQ4816.FromRawBits(value: ((long)(Pcg3dLatticeNoise.Pcg3d(
                        x: ((uint)nx),
                        y: ((uint)nz),
                        z: seed
                    ).X >> 16)));
                    var sample = Pcg3dLatticeNoise.ValueNoise01(
                        cellX: (nx * noiseCells),
                        cellZ: (nz * noiseCells),
                        noiseCells: noiseCells,
                        seed: seed
                    );

                    if (sample != corner) { return $"ValueNoise01 at cell corner ({nx}, {nz}) with a {noiseCells}-cell noise edge is {sample.Value}, not the corner hash {corner.Value}"; }
                }
            }
        }

        // ValueNoise3 stays inside [-1, 1) over drawn points of the whole signed lattice, and at a whole lattice point,
        // where every fade is zero, collapses onto twice its near corner's public Pcg3d fraction less one.
        for (var draw = 0; (draw < 256); ++draw) {
            var point = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: unchecked((long)((int)generator.NextUInt32()))),
                Y: FixedQ4816.FromRawBits(value: unchecked((long)((int)generator.NextUInt32()))),
                Z: FixedQ4816.FromRawBits(value: unchecked((long)((int)generator.NextUInt32())))
            );
            var (seedX, seedY, seedZ) = (generator.NextUInt32(), generator.NextUInt32(), generator.NextUInt32());
            var sample = Pcg3dLatticeNoise.ValueNoise3(q: point, seedX: seedX, seedY: seedY, seedZ: seedZ);

            if ((sample < -FixedQ4816.One) || (sample >= FixedQ4816.One)) { return $"ValueNoise3({point}) left [-1, 1) at raw {sample.Value}"; }

            var cell = new FixedVector3(
                X: FixedQ4816.FromInteger(value: (point.X.Value >> FixedQ4816.FractionBitCount)),
                Y: FixedQ4816.FromInteger(value: (point.Y.Value >> FixedQ4816.FractionBitCount)),
                Z: FixedQ4816.FromInteger(value: (point.Z.Value >> FixedQ4816.FractionBitCount))
            );
            var corner = FixedQ4816.FromRawBits(value: ((long)(Pcg3dLatticeNoise.Pcg3d(
                x: (unchecked((uint)(cell.X.Value >> FixedQ4816.FractionBitCount)) ^ seedX),
                y: (unchecked((uint)(cell.Y.Value >> FixedQ4816.FractionBitCount)) ^ seedY),
                z: (unchecked((uint)(cell.Z.Value >> FixedQ4816.FractionBitCount)) ^ seedZ)
            ).X >> 16)));
            var atCorner = Pcg3dLatticeNoise.ValueNoise3(q: cell, seedX: seedX, seedY: seedY, seedZ: seedZ);

            if (atCorner != ((corner * FixedQ4816.FromInteger(value: 2)) - FixedQ4816.One)) {
                return $"ValueNoise3 at lattice point {cell} is {atCorner.Value}, not twice the corner hash {corner.Value} less one";
            }

            // Fbm3 is its octaves' weighted sum: each a ValueNoise3 at the point scaled by lacunarity to the octave,
            // seeded by the base seed plus the octave.
            var octaves = ((int)(generator.NextUInt32() % 9U));
            var gain = FixedQ4816.FromRawBits(value: (generator.NextUInt32() % 65537U));
            var lacunarity = FixedQ4816.FromRawBits(value: (65536L + (generator.NextUInt32() % 196609U)));
            var small = (point / FixedQ4816.FromInteger(value: 4096));
            var expected = FixedQ4816.Zero;
            var weight = FixedQ4816.One;
            var at = small;

            for (var octave = 0; (octave < octaves); ++octave) {
                var octaveSeed = unchecked(seedX + ((uint)octave));

                expected += (weight * Pcg3dLatticeNoise.ValueNoise3(q: at, seedX: octaveSeed,
                    seedY: unchecked(octaveSeed * Pcg3dLatticeNoise.HashStreamA), seedZ: unchecked(octaveSeed * Pcg3dLatticeNoise.HashStreamB)));
                at *= lacunarity;
                weight *= gain;
            }

            var fbm = Pcg3dLatticeNoise.Fbm3(gain: gain, lacunarity: lacunarity, octaves: octaves, q: small, seed: seedX);

            if (fbm != expected) { return $"Fbm3({small}, {seedX}, {octaves}, {gain.Value}, {lacunarity.Value}) is {fbm.Value}, not its octaves' sum {expected.Value}"; }
        }

        return null;
    }

    /// <summary>QuinticFade at every Q16 fraction against the exact polynomial rounded once; ValueNoise3 within a raw and
    /// a half of the exact blend at its own fades, the allowance its box bounds charge; ValueNoise3Bounds and Fbm3Bounds
    /// holding every point answer of drawn boxes, a point box within ten raws a term, and a box inside another answering
    /// inside the other's interval.</summary>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? LatticeNoiseFadeAndBoxBounds() {
        var previous = long.MinValue;

        for (var raw = 0L; (raw <= 65536L); raw++) {
            var fade = Pcg3dLatticeNoise.QuinticFade(t: FixedQ4816.FromRawBits(value: raw)).Value;
            var expected = Oracles.QuinticFadeReference(raw: raw);

            if (fade != expected) { return $"QuinticFade at raw {raw} is {fade}, not the exact fade rounded once, {expected}"; }
            if (fade < previous) { return $"QuinticFade falls from {previous} to {fade} at raw {raw}"; }

            previous = fade;
        }

        foreach (var outside in (ReadOnlySpan<long>)[-1L, 65537L, long.MinValue]) {
            try {
                _ = Pcg3dLatticeNoise.QuinticFade(t: FixedQ4816.FromRawBits(value: outside));
                return $"QuinticFade at raw {outside}, outside [0, 1], answered";
            } catch (ArgumentOutOfRangeException) {
            }
        }

        var generator = Pcg32XshRr.Create(state: 0x5EED_B0C5UL, stream: 23UL);
        var halfRaw = (BigInteger.One << 47);

        // The point blend rounds three nested lerps once each: within a raw and a half of the exact blend.
        for (var draw = 0; (draw < 512); ++draw) {
            var point = DrawLatticePoint(generator: ref generator, spread: 30);
            var (seedX, seedY, seedZ) = (generator.NextUInt32(), generator.NextUInt32(), generator.NextUInt32());
            var noise = Pcg3dLatticeNoise.ValueNoise3(q: point, seedX: seedX, seedY: seedY, seedZ: seedZ);
            var blendRaw = ((BigInteger)(noise.Value + 65536L) << 47);
            var exact = Oracles.ValueNoise3ExactBlend(q: point, seedX: seedX, seedY: seedY, seedZ: seedZ);

            if (BigInteger.Abs(value: (blendRaw - exact)) > (3 * halfRaw)) {
                return $"ValueNoise3({point}) blends to {(double)blendRaw / (1L << 48)} raws, more than a raw and a half from the exact {(double)exact / (1L << 48)}";
            }
        }

        if (Pcg3dLatticeNoise.ValueNoise3Bounds(seedX: 1U, seedY: 2U, seedZ: 3U, x: FixedInterval.Entire, y: FixedInterval.Entire, z: FixedInterval.Entire)
            != new FixedInterval(lower: -FixedQ4816.One, upper: FixedQ4816.One)) { return "ValueNoise3Bounds over an unbounded box is not [-1, 1]"; }

        for (var draw = 0; (draw < 384); ++draw) {
            var centre = DrawLatticePoint(generator: ref generator, spread: 20);
            // Half sizes from a point through a few cells, log-spread.
            var half = new FixedVector3(X: DrawHalf(generator: ref generator, draw: draw), Y: DrawHalf(generator: ref generator, draw: draw),
                Z: DrawHalf(generator: ref generator, draw: draw));
            var (lower, upper) = ((centre - half), (centre + half));
            var box = (X: new FixedInterval(lower: lower.X, upper: upper.X), Y: new FixedInterval(lower: lower.Y, upper: upper.Y),
                Z: new FixedInterval(lower: lower.Z, upper: upper.Z));
            var (seedX, seedY, seedZ) = (generator.NextUInt32(), generator.NextUInt32(), generator.NextUInt32());
            var octaves = ((int)(generator.NextUInt32() % 9U));
            var gain = FixedQ4816.FromRawBits(value: (generator.NextUInt32() % 65537U));
            var lacunarity = FixedQ4816.FromRawBits(value: (65536L + (generator.NextUInt32() % 196609U)));
            var noiseBounds = Pcg3dLatticeNoise.ValueNoise3Bounds(seedX: seedX, seedY: seedY, seedZ: seedZ, x: box.X, y: box.Y, z: box.Z);
            var fbmBounds = Pcg3dLatticeNoise.Fbm3Bounds(gain: gain, lacunarity: lacunarity, octaves: octaves, seed: seedX, x: box.X, y: box.Y, z: box.Z);

            for (var sample = 0; (sample < 24); ++sample) {
                var at = ((sample < 8)
                    ? new FixedVector3(X: (((sample & 1) == 0) ? lower.X : upper.X), Y: (((sample & 2) == 0) ? lower.Y : upper.Y), Z: (((sample & 4) == 0) ? lower.Z : upper.Z))
                    : new FixedVector3(X: Inside(generator: ref generator, interval: box.X), Y: Inside(generator: ref generator, interval: box.Y), Z: Inside(generator: ref generator, interval: box.Z)));
                var noise = Pcg3dLatticeNoise.ValueNoise3(q: at, seedX: seedX, seedY: seedY, seedZ: seedZ);
                var fbm = Pcg3dLatticeNoise.Fbm3(gain: gain, lacunarity: lacunarity, octaves: octaves, q: at, seed: seedX);

                if (!noiseBounds.Contains(value: noise)) { return $"ValueNoise3({at}) = {noise} lies outside its box's bounds {noiseBounds}, box [{lower}, {upper}]"; }
                if (!fbmBounds.Contains(value: fbm)) { return $"Fbm3({at}, {octaves} octaves, gain {gain}, lacunarity {lacunarity}) = {fbm} lies outside its box's bounds {fbmBounds}, box [{lower}, {upper}]"; }
            }

            if ((half == FixedVector3.Zero) && ((noiseBounds.Upper.Value - noiseBounds.Lower.Value) > 10L)) {
                return $"ValueNoise3Bounds over the point {centre} spans {noiseBounds}, wider than ten raws";
            }

            // A box inside the box answers inside its interval.
            var innerLower = new FixedVector3(X: Inside(generator: ref generator, interval: box.X), Y: Inside(generator: ref generator, interval: box.Y), Z: Inside(generator: ref generator, interval: box.Z));
            var inner = (X: new FixedInterval(lower: innerLower.X, upper: Inside(generator: ref generator, interval: new FixedInterval(lower: innerLower.X, upper: upper.X))),
                Y: new FixedInterval(lower: innerLower.Y, upper: Inside(generator: ref generator, interval: new FixedInterval(lower: innerLower.Y, upper: upper.Y))),
                Z: new FixedInterval(lower: innerLower.Z, upper: Inside(generator: ref generator, interval: new FixedInterval(lower: innerLower.Z, upper: upper.Z))));

            if (!noiseBounds.Contains(other: Pcg3dLatticeNoise.ValueNoise3Bounds(seedX: seedX, seedY: seedY, seedZ: seedZ, x: inner.X, y: inner.Y, z: inner.Z))) {
                return $"ValueNoise3Bounds over a box inside [{lower}, {upper}] answers outside its {noiseBounds}";
            }
            if (!fbmBounds.Contains(other: Pcg3dLatticeNoise.Fbm3Bounds(gain: gain, lacunarity: lacunarity, octaves: octaves, seed: seedX, x: inner.X, y: inner.Y, z: inner.Z))) {
                return $"Fbm3Bounds over a box inside [{lower}, {upper}] answers outside its {fbmBounds}";
            }
        }

        return null;

        static FixedVector3 DrawLatticePoint(ref Pcg32XshRr generator, int spread) => new(
            X: FixedQ4816.FromRawBits(value: (((long)(int)generator.NextUInt32()) >> (31 - spread))),
            Y: FixedQ4816.FromRawBits(value: (((long)(int)generator.NextUInt32()) >> (31 - spread))),
            Z: FixedQ4816.FromRawBits(value: (((long)(int)generator.NextUInt32()) >> (31 - spread)))
        );
        static FixedQ4816 DrawHalf(ref Pcg32XshRr generator, int draw) => (((draw % 6) == 0)
            ? FixedQ4816.Zero
            : FixedQ4816.FromRawBits(value: ((long)(generator.NextUInt32() % (1U << ((int)(generator.NextUInt32() % 19U)))))));
        static FixedQ4816 Inside(ref Pcg32XshRr generator, FixedInterval interval) {
            var draw = ((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32());

            return FixedQ4816.FromRawBits(value: (interval.Lower.Value + ((long)(draw % (((ulong)(interval.Upper.Value - interval.Lower.Value)) + 1UL)))));
        }
    }
    // Pcg3d against Oracles.Pcg3dReference — the identical Jarzynski & Olano mix, formed independently in
    // BigInteger with the carrier reduction taken explicitly, where the subject relies on unchecked uint wrap.
    /// <summary>Pcg3dLatticeNoise.Fbm01 against its octaves' closed-form weighted mean over a few fills: noise-cell
    /// edges below one, one, odd and wide, one to eight octaves, edge and drawn cell indices and seeds.</summary>
    public static string? Pcg3dLatticeFractalNoiseVersusOctaveSum() {
        var generator = Pcg32XshRr.Create(
            state: 0xFB0001UL,
            stream: 13UL
        );
        ReadOnlySpan<int> frequencies = [-3, 0, 1, 2, 5, 16, 33, 1000];
        ReadOnlySpan<int> octaveCounts = [1, 2, 3, 4, 8];

        foreach (var frequency in frequencies) {
            foreach (var octaves in octaveCounts) {
                for (var draw = 0; (draw < 24); ++draw) {
                    var cellX = ((draw < 8) ? (draw - 4) : unchecked((int)generator.NextUInt32()));
                    var cellZ = ((draw < 8) ? (3 - draw) : unchecked((int)generator.NextUInt32()));
                    var seed = ((draw == 0) ? uint.MaxValue : generator.NextUInt32());
                    var subject = Pcg3dLatticeNoise.Fbm01(
                        cellX: cellX,
                        cellZ: cellZ,
                        frequency: frequency,
                        octaves: octaves,
                        seed: seed
                    );
                    var reference = Oracles.FractalValueNoise01Raw(
                        frequency: frequency,
                        octaves: octaves,
                        seed: seed,
                        octaveRaw: (cells, octaveSeed) => Pcg3dLatticeNoise.ValueNoise01(
                            cellX: cellX,
                            cellZ: cellZ,
                            noiseCells: cells,
                            seed: octaveSeed
                        ).Value
                    );

                    if (subject.Value != ((long)reference)) { return $"Fbm01({cellX}, {cellZ}, {frequency}, {octaves}, {seed}) is raw {subject.Value}, not its octaves' weighted mean {reference}"; }

                    if ((subject.Value < 0L) || (subject.Value >= FixedQ4816.One.Value)) { return $"Fbm01({cellX}, {cellZ}, {frequency}, {octaves}, {seed}) left [0, 1) at raw {subject.Value}"; }
                }
            }
        }

        // One octave is ValueNoise01 itself at the clamped edge: the mean of a single term divides its own weight.
        var single = Pcg3dLatticeNoise.Fbm01(
            cellX: 7,
            cellZ: -9,
            frequency: 4,
            octaves: 1,
            seed: 0xC0FFEEU
        );

        if (single != Pcg3dLatticeNoise.ValueNoise01(
            cellX: 7,
            cellZ: -9,
            noiseCells: 4,
            seed: 0xC0FFEEU
        )) { return "one octave of Fbm01 is not ValueNoise01 at the first octave's edge and seed"; }

        foreach (var octaves in ((ReadOnlySpan<int>)[0, -1])) {
            if (!ThrowsExactly<ArgumentOutOfRangeException>(
                action: () => Pcg3dLatticeNoise.Fbm01(
                    cellX: 0,
                    cellZ: 0,
                    frequency: 4,
                    octaves: octaves,
                    seed: 0U
                ),
                paramName: "octaves"
            )) { return $"Fbm01 admitted {octaves} octaves"; }
        }

        return null;
    }

    private static string? Pcg3dAgreesWithReference(uint x, uint y, uint z) {
        var reference = Oracles.Pcg3dReference(
            x: x,
            y: y,
            z: z
        );
        var subject = Pcg3dLatticeNoise.Pcg3d(
            x: x,
            y: y,
            z: z
        );

        return (((subject.X == reference.X) &&
                 (subject.Y == reference.Y) &&
                 (subject.Z == reference.Z))
            ? null
            : $"Pcg3d({x}, {y}, {z}) = ({subject.X}, {subject.Y}, {subject.Z}), but the wide-integer reference gives ({reference.X}, {reference.Y}, {reference.Z})"
        );
    }

    // The quantile ladder: probability against the published standard-normal deviate, hand-tabulated outside this
    // tree. The central region takes |p - 0.5| <= 0.425; the rest reach the first tail branch.
    private static readonly (double Probability, double Deviate)[] NormalQuantileLadder = [
        (Probability: 0.75d, Deviate: 0.6744897501960817d),
        (Probability: 0.9d, Deviate: 1.2815515655446004d),
        (Probability: 0.95d, Deviate: 1.6448536269514722d),
        (Probability: 0.975d, Deviate: 1.9599639845400545d),
        (Probability: 0.99d, Deviate: 2.3263478740408408d),
        (Probability: 0.995d, Deviate: 2.5758293035489004d),
        (Probability: 0.999d, Deviate: 3.0902323061678132d),
    ];

    /// <summary>The normal quantile's refusal ladder, endpoints, antisymmetry, monotonicity, and a published deviate
    /// ladder compared on exact bit distance rather than on a floating-point difference.</summary>
    public static string? NormalQuantileLadderAndRefusals() {
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = double.NaN.InverseStandardNormalCdf(),
            paramName: "probability"
        )) { return "a not-a-number probability was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = Math.BitDecrement(x: 0.0d).InverseStandardNormalCdf(),
            paramName: "probability"
        )) { return "a probability below zero was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = Math.BitIncrement(x: 1.0d).InverseStandardNormalCdf(),
            paramName: "probability"
        )) { return "a probability above one was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = double.PositiveInfinity.InverseStandardNormalCdf(),
            paramName: "probability"
        )) { return "an infinite probability was accepted"; }

        // The affine wrapper validates its own parameters FIRST, and in its own order: mean, then deviation, then the
        // probability the inner function owns.
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = double.NaN.InverseNormalCdf(
                mean: double.NaN,
                standardDeviation: 0.0d
            ),
            paramName: "mean"
        )) { return "the mean gate does not precede the deviation gate"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = double.NaN.InverseNormalCdf(
                mean: 0.0d,
                standardDeviation: 0.0d
            ),
            paramName: "standardDeviation"
        )) { return "the deviation gate does not precede the probability gate"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = double.NaN.InverseNormalCdf(
                mean: 0.0d,
                standardDeviation: double.PositiveInfinity
            ),
            paramName: "standardDeviation"
        )) { return "an infinite deviation was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = 0.5d.InverseNormalCdf(
                mean: 0.0d,
                standardDeviation: -1.0d
            ),
            paramName: "standardDeviation"
        )) { return "a negative deviation was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = double.NaN.InverseNormalCdf(
                mean: 0.0d,
                standardDeviation: 1.0d
            ),
            paramName: "probability"
        )) { return "the wrapper swallowed the inner probability refusal"; }

        if (0.0d.InverseStandardNormalCdf() != double.NegativeInfinity) { return "the quantile at zero is not negative infinity"; }
        if (1.0d.InverseStandardNormalCdf() != double.PositiveInfinity) { return "the quantile at one is not positive infinity"; }
        if (BitConverter.DoubleToUInt64Bits(value: 0.5d.InverseStandardNormalCdf()) != 0UL) { return "the median is not exactly positive zero"; }

        foreach (var rung in NormalQuantileLadder) {
            var computed = rung.Probability.InverseStandardNormalCdf();
            var distance = UlpDistance(
                left: computed,
                right: rung.Deviate
            );

            if (distance > 256L) { return $"the quantile at {rung.Probability.ToString(provider: CultureInfo.InvariantCulture)} is {distance} units in the last place from the published deviate"; }

            // The affine wrapper is exactly mean + deviation·z at a power-of-two deviation, where no rounding enters.
            var affine = rung.Probability.InverseNormalCdf(
                mean: 10.0d,
                standardDeviation: 2.0d
            );

            if (BitConverter.DoubleToUInt64Bits(value: affine) != BitConverter.DoubleToUInt64Bits(value: (10.0d + (2.0d * computed)))) { return $"the affine wrapper at {rung.Probability.ToString(provider: CultureInfo.InvariantCulture)} is not mean + deviation times the standard deviate"; }
        }

        // Antisymmetry, exactly, at DYADIC probabilities — the only ones whose complement 1 − p is itself exact, so
        // both arms of the tail branch see the identical argument. The central polynomial is odd in q and the tail
        // branches differ only by a copied sign, so the reflected probability must return the negated deviate bit for
        // bit. The 2^-40 rung is the one rung that reaches the third region at all: its r exceeds five.
        // The sweep starts at 2^-2: the median is its own complement and its deviate is positive zero, whose negation
        // is the OTHER zero, so the one probability where the statement is about a sign bit is stated separately above.
        for (var exponent = 2; (exponent <= 40); ++exponent) {
            var probability = Math.ScaleB(
                n: -exponent,
                x: 1.0d
            );
            var complement = (1.0d - probability);
            var deviate = probability.InverseStandardNormalCdf();
            var reflected = complement.InverseStandardNormalCdf();

            if (!double.IsFinite(d: deviate)) { return $"the quantile at 2^-{exponent} is not finite"; }
            if (BitConverter.DoubleToUInt64Bits(value: reflected) != BitConverter.DoubleToUInt64Bits(value: -deviate)) { return $"the quantile at 2^-{exponent} is not the negation of its reflection"; }
        }

        // Monotonicity across all three regions, walked through the double LATTICE rather than by a floating-point
        // step: the bit patterns of the positive doubles are ordered, so a fixed integer stride is a monotone sweep of
        // probabilities and the law's own arithmetic stays exact.
        var low = BitConverter.DoubleToUInt64Bits(value: 1.0e-30d);
        var high = BitConverter.DoubleToUInt64Bits(value: 0.5d);
        var stride = ((high - low) / 4096UL);
        var previous = double.NegativeInfinity;

        for (var bits = low; (bits <= high); bits += stride) {
            var candidate = BitConverter.UInt64BitsToDouble(value: bits);
            var deviate = candidate.InverseStandardNormalCdf();

            if (!(deviate > previous)) { return $"the quantile is not strictly increasing at {candidate.ToString(provider: CultureInfo.InvariantCulture)}"; }
            if (!double.IsFinite(d: deviate)) { return $"the quantile at {candidate.ToString(provider: CultureInfo.InvariantCulture)} is not finite"; }

            previous = deviate;
        }

        return null;
    }

    // The signed distance between two doubles in units in the last place, read off the bit patterns rather than
    // computed as a difference — the ordering of positive doubles is the ordering of their bit patterns.
    private static long UlpDistance(double left, double right) {
        var l = ((long)BitConverter.DoubleToUInt64Bits(value: left));
        var r = ((long)BitConverter.DoubleToUInt64Bits(value: right));

        if (l < 0L) { l = (long.MinValue - l); }
        if (r < 0L) { r = (long.MinValue - r); }

        return Math.Abs(value: (l - r));
    }

    /// <summary>The additive recurrences against a wide-integer oracle, their published first points, and a coverage
    /// envelope that a merely deterministic sequence would fail.</summary>
    public static string? LowDiscrepancyRecurrence() {
        var golden = ((BigInteger)0x9E3779B97F4A7C15UL);
        var plastic = ((BigInteger)13925035116211876495UL);
        var plasticSquared = ((BigInteger)10511698010929265437UL);
        var modulus = (BigInteger.One << 64);

        if (LowDiscrepancy.R1(index: 0UL).Value != 0U) { return "the golden sequence does not start at zero"; }
        if (LowDiscrepancy.R1(index: 1UL).Value != 0x9E3779B9U) { return $"the golden sequence's first point is 0x{LowDiscrepancy.R1(index: 1UL).Value:X8}"; }
        if (LowDiscrepancy.R2(index: 0UL).X.Value != 0U) { return "the plastic sequence does not start at zero"; }
        if (LowDiscrepancy.R2(index: 0UL).Y.Value != 0U) { return "the plastic sequence's second coordinate does not start at zero"; }

        // The oracle forms the whole product in arbitrary width and takes the modulus explicitly, where the subject
        // relies on the carrier's wrap to perform the reduction; different route, same statement.
        var generator = Pcg32XshRr.Create(
            state: 0xD15CUL,
            stream: 8UL
        );

        for (var index = 0; (index < 2048); ++index) {
            var i = ((index < 8)
                ? ((ulong)index)
                : (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32()
            );
            var wide = ((BigInteger)i);

            if (LowDiscrepancy.R1(index: i).Value != ((uint)(((wide * golden) % modulus) >> 32))) { return $"the golden sequence disagrees with the wide-integer recurrence at index {i}"; }

            var point = LowDiscrepancy.R2(index: i);

            if (point.X.Value != ((uint)(((wide * plastic) % modulus) >> 32))) { return $"the plastic sequence's first coordinate disagrees at index {i}"; }
            if (point.Y.Value != ((uint)(((wide * plasticSquared) % modulus) >> 32))) { return $"the plastic sequence's second coordinate disagrees at index {i}"; }
        }

        // The property the name claims. 4096 golden points into 256 equal bins land 16 to a bin, give or take four; a
        // sequence that merely looked random would fail this band roughly as often as it met it.
        var bins = new int[256];

        for (var index = 0UL; (index < 4096UL); ++index) {
            ++bins[(LowDiscrepancy.R1(index: index).Value >> 24)];
        }

        for (var bin = 0; (bin < bins.Length); ++bin) {
            if (
                (bins[bin] < 12) ||
                (bins[bin] > 20)
            ) { return $"golden bin {bin} took {bins[bin]} of an expected sixteen points"; }
        }

        var cells = new int[256];

        for (var index = 0UL; (index < 4096UL); ++index) {
            var point = LowDiscrepancy.R2(index: index);

            ++cells[(point.X.Value >> 28) | ((point.Y.Value >> 28) << 4)];
        }

        for (var cell = 0; (cell < cells.Length); ++cell) {
            if (
                (cells[cell] < 8) ||
                (cells[cell] > 24)
            ) { return $"plastic cell {cell} took {cells[cell]} of an expected sixteen points"; }
        }

        return null;
    }
    /// <summary>The cryptographic draw's contracts: the inverted-interval refusal, the singleton and full-range
    /// intervals, and range membership at five carrier widths.</summary>
    public static string? SecureRandomContracts() {
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = SecureRandom.NextUInt<uint>(
                maximum: 5U,
                minimum: 10U
            ),
            paramName: "maximum"
        )) { return "an inverted interval was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = SecureRandom.NextUInt<byte>(
                maximum: 0,
                minimum: 1
            ),
            paramName: "maximum"
        )) { return "an inverted byte interval was accepted"; }

        var failure = (SecureRandomInterval<byte>(width: 8) ??
                       (SecureRandomInterval<ushort>(width: 16) ??
                       (SecureRandomInterval<uint>(width: 32) ??
                       (SecureRandomInterval<ulong>(width: 64) ??
                       SecureRandomInterval<UInt128>(width: 128)))));

        return failure;
    }

    // The interval contracts at one carrier width. There is deliberately NO value oracle: the draws are cryptographic
    // and non-reproducible, so what can be stated is the interval, the singleton and the liveness.
    private static string? SecureRandomInterval<T>(int width)
        where T : struct, IBinaryInteger<T>, IUnsignedNumber<T> {
        var singleton = ((T.One + T.One) + T.One);

        for (var index = 0; (index < 32); ++index) {
            if (SecureRandom.NextUInt(
                maximum: singleton,
                minimum: singleton
            ) != singleton) { return $"the width-{width} singleton interval returned something other than its only value"; }

            _ = SecureRandom.NextUInt(
                maximum: T.AllBitsSet,
                minimum: T.Zero
            );
        }

        var minimum = (T.One + T.One);
        var maximum = (T.One << 5);
        var distinct = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < 256); ++index) {
            var drawn = SecureRandom.NextUInt(
                maximum: maximum,
                minimum: minimum
            );

            if (
                (drawn < minimum) ||
                (drawn > maximum)
            ) { return $"a width-{width} bounded draw left its interval"; }

            _ = distinct.Add(item: drawn.ToString(
                format: null,
                formatProvider: CultureInfo.InvariantCulture
            ));
        }

        // Liveness, not distribution: a generator that returned one constant would satisfy every row above.
        if (distinct.Count < 4) { return $"256 width-{width} draws over a thirty-value interval produced {distinct.Count} distinct values"; }

        var unbounded = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < 64); ++index) {
            _ = unbounded.Add(item: SecureRandom.NextUInt<T>().ToString(
                format: null,
                formatProvider: CultureInfo.InvariantCulture
            ));
        }

        if (unbounded.Count < 2) { return $"64 unbounded width-{width} draws produced one value"; }

        return null;
    }
    // A binary32's square, exactly, as an integer at the common scale 2^400. The value is significand·2^scale with a
    // 24-bit significand, so the square is an integer shift of an integer square and no rounding enters anywhere.
    private static BigInteger ConeScaledSquare(uint bits) {
        var exponent = ((int)((bits >> 23) & 0xFFU));
        var mantissa = ((int)(bits & 0x7F_FFFFU));
        var significand = ((exponent == 0)
            ? mantissa
            : mantissa | (1 << 23)
        );
        var scale = ((exponent == 0)
            ? -149
            : (exponent - 150)
        );

        return ((((BigInteger)significand) * significand) << (ConeNormScale + (2 * scale)));
    }
    // The actual value an ArgumentOutOfRangeException carried, or null where the call did not refuse that way.
    private static object? ActualValueOf(Action action) {
        try {
            action();
        } catch (ArgumentOutOfRangeException exception) {
            return exception.ActualValue;
        }

        return null;
    }
    // The two fraction adapters over one generator are the raw draw itself and the raw draw's top sixteen bits: 256
    // interleaved pairs against a twin generator from the same state, so the adapters are carriage and nothing more.
    private static string? FractionAdaptersCarryRawDraws(Func<UnitFraction32> wide, Func<UnitFraction16> narrow, Func<uint> raw) {
        for (var index = 0; (index < 256); ++index) {
            var drawnWide = wide();
            var expectedWide = raw();

            if (drawnWide.Value != expectedWide) { return $"NextUnitFraction32 at draw {index} is {drawnWide.Value}, not the raw draw {expectedWide}"; }

            var drawnNarrow = narrow();
            var expectedNarrow = ((ushort)(raw() >> 16));

            if (drawnNarrow.Value != expectedNarrow) { return $"NextUnitFraction16 at draw {index} is {drawnNarrow.Value}, not {expectedNarrow}"; }
        }

        return null;
    }
}
