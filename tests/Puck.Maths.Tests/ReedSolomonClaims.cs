using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims for <see cref="ReedSolomon"/> — the systematic coding surface over <see cref="BinaryField{T}"/>.
/// Every statement here is decided against <see cref="Oracles"/>' shared-nothing <see cref="BigInteger"/> arithmetic or
/// against a constant published outside this tree, never against a second spelling of the subject: a generator is
/// checked by EVALUATING it at the roots it claims rather than by rebuilding it, and a codeword is checked by
/// evaluating it at those same roots by the definition rather than by the subject's own Horner schedule.
/// <see cref="LawRegistry"/> invokes each claim below as a Default-tier law.
/// </summary>
internal static class ReedSolomonClaims {
    /// <summary>The fields the sweep runs over: the standard's degree-8 modulus, the catalog's other degree-8 modulus,
    /// and a narrow degree-4 modulus whose degree sits below its carrier's width.</summary>
    private static readonly (int Degree, byte Tail)[] ByteFields = [(8, 0x1D), (8, 0x1B), (4, 0x3)];
    /// <summary>The check-symbol counts the sweep builds generators for, spanning the one-symbol edge, the standard's
    /// smallest and largest block counts, and a count past every vector width the region ladder has.</summary>
    private static readonly int[] CheckCounts = [1, 2, 7, 10, 26, 30, 68];
    /// <summary>The message lengths the encode sweep runs at. The last is past
    /// <c>ReedSolomon</c>'s stack threshold, so the pooled working buffer is exercised rather than only the stack one.</summary>
    private static readonly int[] MessageLengths = [0, 1, 2, 9, 16, 55, 116, 600];
    /// <summary>The root runs the sweep starts at — zero is the convention the QR standard uses and one is the other
    /// common choice, and an off-by-one between them is exactly what the root law is built to see.</summary>
    private static readonly int[] FirstRootExponents = [0, 1];

    /// <summary>ISO/IEC 18004 Annex I's worked example: the sixteen data codewords of the numeric-mode string
    /// <c>01234567</c> at version 1, error-correction level M.</summary>
    private static readonly byte[] PublishedMessage = [0x10, 0x20, 0x0C, 0x56, 0x61, 0x80, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11];
    /// <summary>The ten error-correction codewords ISO/IEC 18004 Annex I publishes for that message.</summary>
    private static readonly byte[] PublishedCheckSymbols = [0xA5, 0x24, 0xD4, 0xC1, 0xED, 0x36, 0xC7, 0x87, 0x2C, 0x55];

    /// <summary>Deterministic message content — an affine walk folded into the field's legal element space.</summary>
    /// <param name="length">The message length.</param>
    /// <param name="salt">A per-case offset so two sweeps at one length do not share content.</param>
    /// <param name="degree">The field's degree, which bounds the elements.</param>
    /// <returns>The message symbols.</returns>
    private static byte[] Message(int length, int salt, int degree) {
        var mask = ((byte)((1 << degree) - 1));
        var message = new byte[length];

        for (var index = 0; (index < length); ++index) {
            message[index] = ((byte)(((index * 61) + (salt * 29) + 7) & mask));
        }

        return message;
    }

    /// <summary>Widens a span of byte-carried field elements into the oracle's arbitrary-width coefficients.</summary>
    /// <param name="values">The elements.</param>
    /// <returns>The same elements as <see cref="BigInteger"/> coefficients.</returns>
    private static BigInteger[] Widen(ReadOnlySpan<byte> values) {
        var widened = new BigInteger[values.Length];

        for (var index = 0; (index < values.Length); ++index) {
            widened[index] = values[index];
        }

        return widened;
    }

    /// <summary>Proves every generator <see cref="ReedSolomon.BuildGenerator{T}(BinaryField{T}, T, int, Span{T})"/>
    /// writes is monic, has the requested degree, and VANISHES at each of the consecutive powers it claims as roots —
    /// decided by evaluating the produced coefficients in <see cref="Oracles.BinaryFieldPolynomialValue"/>, whose powers
    /// come from <see cref="Oracles.BinaryFieldRepeatedProduct"/>, so no part of the subject's construction is
    /// consulted. The next power past the run must NOT vanish wherever it is a fresh element, which is what makes an
    /// off-by-one in the root run visible rather than silently absorbed.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? GeneratorRootsSurface() {
        foreach (var (degree, tail) in ByteFields) {
            var field = BinaryField<byte>.Create(degree: degree, reductionTail: tail);
            var rootBase = ((byte)2);

            foreach (var first in FirstRootExponents) {
                foreach (var count in CheckCounts) {
                    var generator = new byte[(count + 1)];

                    ReedSolomon.BuildGenerator(field: field, rootBase: rootBase, firstRootExponent: first, generator: generator);

                    if (1 != generator[0]) {
                        return $"generator of degree {count} at degree {degree}/tail 0x{tail:X2} is not monic; its leading coefficient is 0x{generator[0]:X2}";
                    }

                    var coefficients = Widen(values: generator);
                    var roots = new HashSet<BigInteger>();

                    for (var index = 0; (index < count); ++index) {
                        var root = Oracles.BinaryFieldRepeatedProduct(value: rootBase, exponent: (first + index), degree: degree, reductionTail: tail);
                        var value = Oracles.BinaryFieldPolynomialValue(coefficients: coefficients, point: root, degree: degree, reductionTail: tail);

                        _ = roots.Add(item: root);

                        if (!value.IsZero) {
                            return string.Create(
                                provider: CultureInfo.InvariantCulture,
                                handler: $"degree-{count} generator at degree {degree}/tail 0x{tail:X2}, first root exponent {first}, does not vanish at root index {index} (the element {root}); it evaluates to {value}"
                            );
                        }
                    }

                    // The power one past the run. Where it is a fresh element the generator must NOT vanish there, which
                    // is the assertion an off-by-one root run fails. Where the field is small enough that the power has
                    // already appeared as a root, the generator legitimately vanishes and the check is skipped rather
                    // than turned into a false counterexample.
                    var beyond = Oracles.BinaryFieldRepeatedProduct(value: rootBase, exponent: (first + count), degree: degree, reductionTail: tail);

                    if (!roots.Contains(item: beyond)) {
                        var beyondValue = Oracles.BinaryFieldPolynomialValue(coefficients: coefficients, point: beyond, degree: degree, reductionTail: tail);

                        if (beyondValue.IsZero) {
                            return string.Create(
                                provider: CultureInfo.InvariantCulture,
                                handler: $"degree-{count} generator at degree {degree}/tail 0x{tail:X2}, first root exponent {first}, vanishes at the element {beyond}, one power PAST its declared root run"
                            );
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Proves <see cref="ReedSolomon.ComputeCheckSymbols{T}(BinaryField{T}, ReadOnlySpan{T}, ReadOnlySpan{T}, Span{T})"/>
    /// reproduces the ten error-correction codewords ISO/IEC 18004 Annex I publishes for its worked example, over the
    /// field and root convention that standard specifies. The constant is authored outside this tree and outside this
    /// repository, so agreement pins the generator construction, the division schedule, the field's modulus and the
    /// symbol order together — a transposed generator or the neighbouring degree-8 modulus produces a different
    /// remainder while still looking like a working code.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PublishedRemainderSurface() {
        var field = BinaryField<byte>.Create(degree: 8, reductionTail: 0x1D);
        var generator = new byte[(PublishedCheckSymbols.Length + 1)];
        var produced = new byte[PublishedCheckSymbols.Length];

        ReedSolomon.BuildGenerator(field: field, rootBase: ((byte)2), firstRootExponent: 0, generator: generator);
        ReedSolomon.ComputeCheckSymbols(field: field, generator: generator, message: PublishedMessage, checkSymbols: produced);

        for (var index = 0; (index < PublishedCheckSymbols.Length); ++index) {
            if (produced[index] != PublishedCheckSymbols[index]) {
                return $"check symbol {index} of the published worked example is 0x{produced[index]:X2}; the standard publishes 0x{PublishedCheckSymbols[index]:X2}";
            }
        }

        // The neighbouring degree-8 modulus is a legal field and a legal code, and it must NOT reproduce the standard's
        // remainder. Asserting that is what stops this claim from passing on a subject that ignored the modulus.
        var neighbour = BinaryField<byte>.Create(degree: 8, reductionTail: 0x1B);
        var neighbourGenerator = new byte[(PublishedCheckSymbols.Length + 1)];
        var neighbourProduced = new byte[PublishedCheckSymbols.Length];

        ReedSolomon.BuildGenerator(field: neighbour, rootBase: ((byte)2), firstRootExponent: 0, generator: neighbourGenerator);
        ReedSolomon.ComputeCheckSymbols(field: neighbour, generator: neighbourGenerator, message: PublishedMessage, checkSymbols: neighbourProduced);

        if (neighbourProduced.AsSpan().SequenceEqual(other: PublishedCheckSymbols)) {
            return "the degree-8 modulus 0x11B reproduces the remainder ISO/IEC 18004 publishes for 0x11D, so this claim cannot be reading the modulus at all";
        }

        return null;
    }

    /// <summary>Proves the systematic codeword a message and its check symbols form is divisible by the generator —
    /// every syndrome vanishes — that the remainder occupies exactly the generator's degree, and that
    /// <see cref="ReedSolomon.ComputeSyndromes{T}(BinaryField{T}, T, int, ReadOnlySpan{T}, Span{T})"/> agrees with the
    /// definition-form evaluation at every root, on intact codewords AND on corrupted ones. Corrupting one symbol must
    /// disturb some syndrome; without that the vanishing statement would be satisfied by a subject that answered zero
    /// unconditionally.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CodewordSyndromesSurface() {
        foreach (var (degree, tail) in ByteFields) {
            var field = BinaryField<byte>.Create(degree: degree, reductionTail: tail);
            var rootBase = ((byte)2);

            foreach (var first in FirstRootExponents) {
                foreach (var count in CheckCounts) {
                    var generator = new byte[(count + 1)];

                    ReedSolomon.BuildGenerator(field: field, rootBase: rootBase, firstRootExponent: first, generator: generator);

                    foreach (var length in MessageLengths) {
                        var message = Message(length: length, salt: (count + first), degree: degree);
                        var check = new byte[count];

                        ReedSolomon.ComputeCheckSymbols(field: field, generator: generator, message: message, checkSymbols: check);

                        var codeword = new byte[(length + count)];

                        message.CopyTo(array: codeword, index: 0);
                        check.CopyTo(array: codeword, index: length);

                        var coefficients = Widen(values: codeword);
                        var syndromes = new byte[count];

                        ReedSolomon.ComputeSyndromes(field: field, rootBase: rootBase, firstRootExponent: first, codeword: codeword, syndromes: syndromes);

                        for (var index = 0; (index < count); ++index) {
                            var root = Oracles.BinaryFieldRepeatedProduct(value: rootBase, exponent: (first + index), degree: degree, reductionTail: tail);
                            var expected = Oracles.BinaryFieldPolynomialValue(coefficients: coefficients, point: root, degree: degree, reductionTail: tail);

                            if (!expected.IsZero) {
                                return string.Create(
                                    provider: CultureInfo.InvariantCulture,
                                    handler: $"the codeword of a {length}-symbol message with {count} check symbols at degree {degree}/tail 0x{tail:X2}, first root exponent {first}, does not vanish at root index {index}; it evaluates to {expected}"
                                );
                            }

                            if (expected != syndromes[index]) {
                                return $"ComputeSyndromes gave 0x{syndromes[index]:X2} at root index {index} for an intact codeword; the definition-form evaluation gives {expected}";
                            }
                        }

                        if (0 == length) { continue; }

                        // One symbol flipped by one bit. Some syndrome must move, and ComputeSyndromes must still agree
                        // with the definition-form evaluation once the values are no longer all zero.
                        var damaged = ((byte[])codeword.Clone());

                        damaged[(length / 2)] ^= 1;

                        var damagedCoefficients = Widen(values: damaged);
                        var damagedSyndromes = new byte[count];
                        var disturbed = false;

                        ReedSolomon.ComputeSyndromes(field: field, rootBase: rootBase, firstRootExponent: first, codeword: damaged, syndromes: damagedSyndromes);

                        for (var index = 0; (index < count); ++index) {
                            var root = Oracles.BinaryFieldRepeatedProduct(value: rootBase, exponent: (first + index), degree: degree, reductionTail: tail);
                            var expected = Oracles.BinaryFieldPolynomialValue(coefficients: damagedCoefficients, point: root, degree: degree, reductionTail: tail);

                            if (expected != damagedSyndromes[index]) {
                                return $"ComputeSyndromes gave 0x{damagedSyndromes[index]:X2} at root index {index} for a corrupted codeword; the definition-form evaluation gives {expected}";
                            }

                            disturbed |= !expected.IsZero;
                        }

                        if (!disturbed) {
                            return string.Create(
                                provider: CultureInfo.InvariantCulture,
                                handler: $"flipping one bit of a {length}-symbol codeword with {count} check symbols at degree {degree}/tail 0x{tail:X2} left every syndrome zero, so the vanishing statement above proves nothing here"
                            );
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Proves the surface's refusals and its two carrier-width statements: every argument the three members
    /// reject is rejected by the documented exception type AND parameter name, a default-initialized descriptor refuses
    /// all three rather than answering, and the whole surface runs at the sixteen-bit carrier as well as the byte one —
    /// where a codeword is checked against the same definition-form evaluation.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SurfaceRefusalsAndWideCarrierSurface() {
        var field = BinaryField<byte>.Create(degree: 8, reductionTail: 0x1D);
        var generator = new byte[11];

        ReedSolomon.BuildGenerator(field: field, rootBase: ((byte)2), firstRootExponent: 0, generator: generator);

        var refusal = (Refuses(action: () => ReedSolomon.BuildGenerator(field: field, rootBase: ((byte)2), firstRootExponent: 0, generator: new byte[1]), type: typeof(ArgumentOutOfRangeException), parameterName: "generator", what: "BuildGenerator with a one-coefficient generator") ??
                       Refuses(action: () => ReedSolomon.BuildGenerator(field: field, rootBase: ((byte)2), firstRootExponent: 0, generator: []), type: typeof(ArgumentOutOfRangeException), parameterName: "generator", what: "BuildGenerator with an empty generator") ??
                       Refuses(action: () => ReedSolomon.BuildGenerator(field: field, rootBase: ((byte)2), firstRootExponent: -1, generator: new byte[3]), type: typeof(ArgumentOutOfRangeException), parameterName: "firstRootExponent", what: "BuildGenerator with a negative first root exponent") ??
                       Refuses(action: () => ReedSolomon.ComputeCheckSymbols(field: field, generator: new byte[1], message: [], checkSymbols: []), type: typeof(ArgumentOutOfRangeException), parameterName: "generator", what: "ComputeCheckSymbols with a one-coefficient generator") ??
                       Refuses(action: () => ReedSolomon.ComputeCheckSymbols(field: field, generator: generator, message: [], checkSymbols: new byte[9]), type: typeof(ArgumentOutOfRangeException), parameterName: "checkSymbols", what: "ComputeCheckSymbols with a check span shorter than the generator's degree") ??
                       Refuses(action: () => ReedSolomon.ComputeCheckSymbols(field: field, generator: new byte[] { 2, 1, 1 }, message: [], checkSymbols: new byte[2]), type: typeof(ArgumentException), parameterName: "generator", what: "ComputeCheckSymbols with a non-monic generator") ??
                       Refuses(action: () => ReedSolomon.ComputeSyndromes(field: field, rootBase: ((byte)2), firstRootExponent: -1, codeword: [], syndromes: new byte[1]), type: typeof(ArgumentOutOfRangeException), parameterName: "firstRootExponent", what: "ComputeSyndromes with a negative first root exponent") ??
                       Refuses(action: () => ReedSolomon.BuildGenerator(field: default, rootBase: ((byte)2), firstRootExponent: 0, generator: new byte[3]), type: typeof(InvalidOperationException), parameterName: null, what: "BuildGenerator on a default-initialized field") ??
                       Refuses(action: () => ReedSolomon.ComputeCheckSymbols(field: default, generator: generator, message: [], checkSymbols: new byte[10]), type: typeof(InvalidOperationException), parameterName: null, what: "ComputeCheckSymbols on a default-initialized field") ??
                       Refuses(action: () => ReedSolomon.ComputeSyndromes(field: default, rootBase: ((byte)2), firstRootExponent: 0, codeword: [], syndromes: new byte[2]), type: typeof(InvalidOperationException), parameterName: null, what: "ComputeSyndromes on a default-initialized field") ??
                       Refuses(action: () => ReedSolomon.ComputeSyndromes(field: default, rootBase: ((byte)2), firstRootExponent: 0, codeword: [], syndromes: []), type: typeof(InvalidOperationException), parameterName: null, what: "ComputeSyndromes on a default-initialized field with no syndromes requested"));

        if (refusal is not null) { return refusal; }

        // An empty message divides to a zero remainder, and the check span must be written rather than left alone.
        var untouched = new byte[10];

        Array.Fill(array: untouched, value: ((byte)0xAB));
        ReedSolomon.ComputeCheckSymbols(field: field, generator: generator, message: [], checkSymbols: untouched);

        foreach (var symbol in untouched) {
            if (0 != symbol) { return $"the check symbols of an empty message are not all zero; one is 0x{symbol:X2}"; }
        }

        // The sixteen-bit carrier, end to end, against the same definition-form evaluation.
        const int WideDegree = 16;
        const ushort WideTail = 0x2B;

        var wideField = BinaryField<ushort>.Create(degree: WideDegree, reductionTail: WideTail);
        var wideGenerator = new ushort[9];
        var wideMessage = new ushort[40];
        var wideCheck = new ushort[8];

        for (var index = 0; (index < wideMessage.Length); ++index) {
            wideMessage[index] = ((ushort)((index * 4093) + 11));
        }

        ReedSolomon.BuildGenerator(field: wideField, rootBase: ((ushort)2), firstRootExponent: 0, generator: wideGenerator);
        ReedSolomon.ComputeCheckSymbols(field: wideField, generator: wideGenerator, message: wideMessage, checkSymbols: wideCheck);

        var wideCodeword = new ushort[(wideMessage.Length + wideCheck.Length)];

        wideMessage.CopyTo(array: wideCodeword, index: 0);
        wideCheck.CopyTo(array: wideCodeword, index: wideMessage.Length);

        var wideCoefficients = new BigInteger[wideCodeword.Length];

        for (var index = 0; (index < wideCodeword.Length); ++index) {
            wideCoefficients[index] = wideCodeword[index];
        }

        var wideSyndromes = new ushort[wideCheck.Length];

        ReedSolomon.ComputeSyndromes(field: wideField, rootBase: ((ushort)2), firstRootExponent: 0, codeword: wideCodeword, syndromes: wideSyndromes);

        for (var index = 0; (index < wideCheck.Length); ++index) {
            var root = Oracles.BinaryFieldRepeatedProduct(value: 2, exponent: index, degree: WideDegree, reductionTail: WideTail);
            var expected = Oracles.BinaryFieldPolynomialValue(coefficients: wideCoefficients, point: root, degree: WideDegree, reductionTail: WideTail);

            if (!expected.IsZero) {
                return string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"the sixteen-bit codeword does not vanish at root index {index}; it evaluates to {expected}"
                );
            }

            if (expected != wideSyndromes[index]) {
                return $"the sixteen-bit ComputeSyndromes gave 0x{wideSyndromes[index]:X4} at root index {index}; the definition-form evaluation gives {expected}";
            }
        }

        return null;
    }

    /// <summary>Runs an action that must throw, and reports what it did instead.</summary>
    /// <param name="action">The call under test.</param>
    /// <param name="type">The exception type the call must throw.</param>
    /// <param name="parameterName">The parameter the refusal must name, or <see langword="null"/> when the refusal names none.</param>
    /// <param name="what">The call, for the counterexample text.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the refusal is the declared one.</returns>
    private static string? Refuses(Action action, Type type, string? parameterName, string what) {
        try {
            action();
        }
        catch (Exception thrown) when (type.IsInstanceOfType(o: thrown)) {
            if (parameterName is null) { return null; }

            return ((thrown is ArgumentException argument) && (argument.ParamName == parameterName))
                ? null
                : $"{what} threw {thrown.GetType().Name} naming '{(thrown as ArgumentException)?.ParamName}' rather than '{parameterName}'";
        }
        catch (Exception thrown) {
            return $"{what} threw {thrown.GetType().Name} rather than {type.Name}";
        }

        return $"{what} did not throw at all";
    }
}
