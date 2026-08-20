using System.Globalization;
using Puck.Assets;

namespace Puck.Cli.Canary;

internal sealed record CanaryTranscript(string RunDirectory, IReadOnlyList<string> Stderr, IReadOnlyList<string> Stdout);
internal sealed record CanaryAssertionResult(string Detail, bool Passed);
internal sealed record CanaryEvaluation(IReadOnlyList<CanaryAssertionResult> Results) {
    public bool Passed => Results.All(predicate: static result => result.Passed);
}
/// <summary>The live-capture noise floor a <c>framesAgree</c> assertion measures against.</summary>
/// <remarks>
/// <para>Two windowed captures of identical simulation state are never bit-equal: composed silhouette shading
/// carries ±1-LSB variance across a boot-time transition, so a byte comparison of two live frames reports a
/// difference on roughly one run in three.</para>
/// <para>Deliberately NOT <see cref="Parity.ParityEnvelope"/>: that envelope guards a whole-frame MEAN, which fits
/// diffuse cross-backend codegen noise but not a frame proof, where the change is one object's worth of pixels. A
/// body relocation covering 0.06% of the frame measures ~0.03 LSB mean — inside the envelope, though every changed
/// pixel moved by up to 209 LSB.</para>
/// <para>Measured against the real World: the ±1-LSB shading yields 0 pixels at or above
/// <see cref="MinChangedDelta"/>; the weakest true body relocation in the travel canaries yields 551.</para>
/// </remarks>
internal static class CanaryFrameNoise {
    /// <summary>The per-pixel channel delta, in LSB, at or above which a pixel counts as changed.</summary>
    public const int MinChangedDelta = 2;
    /// <summary>The largest changed-pixel count two captures may carry and still count as the same frame.</summary>
    public const long MaxChangedPixels = 64;
}
internal static class CanaryAssertions {
    /// <summary>The token a manifest writes where the runner's per-leg companion-authority endpoint belongs.</summary>
    public const string AuthorityToken = "{authority}";

    // authorityTranscripts resolves an assertion's optional authority id to that authority's own transcript; a null
    // authority (the ordinary, non-federated shape) always reads primaryTranscript. filesDiffer and framesAgree always
    // read primaryTranscript.RunDirectory regardless of authority, since capture paths are leg-scoped, not per-process.
    // authorityEndpoint substitutes {authority} in a line assertion's text: the runner binds a companion authority to
    // a free loopback port per leg, so a manifest names the endpoint by token rather than pinning a port the runner
    // owns. An empty value leaves the token unsubstituted, which fails a "present" check rather than matching.
    public static CanaryEvaluation Evaluate(CanaryLeg leg, CanaryTranscript primaryTranscript, IReadOnlyDictionary<string, CanaryTranscript>? authorityTranscripts = null, string authorityEndpoint = "") {
        var results = new List<CanaryAssertionResult>(capacity: leg.Assertions.Count);
        var values = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        // An authority id absent from the map (replaying a federated positive leg's assertions against a
        // non-federated or differently-shaped discriminating leg) resolves to an empty transcript rather than
        // throwing — the observation is simply absent, which fails "present" checks the same way a genuine miss does.
        CanaryTranscript Resolve(string? authority) => ((authority is null)
            ? primaryTranscript
            : (authorityTranscripts?.GetValueOrDefault(key: authority) ?? new CanaryTranscript(RunDirectory: primaryTranscript.RunDirectory, Stderr: [], Stdout: [])));

        foreach (var assertion in leg.Assertions) {
            if (assertion is CanaryResponseAssertion response) {
                results.Add(item: EvaluateResponse(assertion: response, transcript: Resolve(authority: response.Authority), values: values));
            }
        }

        foreach (var assertion in leg.Assertions) {
            switch (assertion) {
                case CanaryResponseAssertion:
                    break;
                case CanaryLineAssertion line:
                    results.Add(item: EvaluateLine(assertion: line, authorityEndpoint: authorityEndpoint, transcript: Resolve(authority: line.Authority)));
                    break;
                case CanarySequenceAssertion sequence:
                    results.Add(item: EvaluateSequence(assertion: sequence, transcript: Resolve(authority: sequence.Authority)));
                    break;
                case CanaryRelationAssertion relation:
                    results.Add(item: EvaluateRelation(assertion: relation, values: values));
                    break;
                case CanaryFileDifferenceAssertion files:
                    results.Add(item: EvaluateFileDifference(assertion: files, transcript: primaryTranscript));
                    break;
                case CanaryFrameAgreementAssertion frames:
                    results.Add(item: EvaluateFrameAgreement(assertion: frames, transcript: primaryTranscript));
                    break;
            }
        }

        return new CanaryEvaluation(Results: results);
    }

    private static CanaryAssertionResult EvaluateFileDifference(CanaryFileDifferenceAssertion assertion, CanaryTranscript transcript) {
        var beforePath = Path.Combine(path1: transcript.RunDirectory, path2: assertion.Before);
        var afterPath = Path.Combine(path1: transcript.RunDirectory, path2: assertion.After);

        if (!File.Exists(path: beforePath) || !File.Exists(path: afterPath)) {
            var missing = new[] { beforePath, afterPath }.Where(predicate: static path => !File.Exists(path: path)).Select(selector: Path.GetFileName);

            return new CanaryAssertionResult(Detail: $"{assertion.Name}: missing capture(s) {string.Join(separator: ", ", values: missing)}", Passed: false);
        }

        var equal = File.ReadAllBytes(path: beforePath).AsSpan().SequenceEqual(other: File.ReadAllBytes(path: afterPath));
        var passed = (assertion.Different ? !equal : equal);

        return new CanaryAssertionResult(
            Detail: $"{assertion.Name}: {assertion.Before} and {assertion.After} are {(equal ? "byte-identical" : "different")}",
            Passed: passed
        );
    }

    private static CanaryAssertionResult EvaluateFrameAgreement(CanaryFrameAgreementAssertion assertion, CanaryTranscript transcript) {
        var beforePath = Path.Combine(path1: transcript.RunDirectory, path2: assertion.Before);
        var afterPath = Path.Combine(path1: transcript.RunDirectory, path2: assertion.After);

        if (!File.Exists(path: beforePath) || !File.Exists(path: afterPath)) {
            var missing = new[] { beforePath, afterPath }.Where(predicate: static path => !File.Exists(path: path)).Select(selector: Path.GetFileName);

            return new CanaryAssertionResult(Detail: $"{assertion.Name}: missing capture(s) {string.Join(separator: ", ", values: missing)}", Passed: false);
        }

        PngImage before;
        PngImage after;

        try {
            before = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: beforePath));
            after = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: afterPath));
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or UnauthorizedAccessException)) {
            return new CanaryAssertionResult(Detail: $"{assertion.Name}: a capture could not be decoded ({exception.Message.ReplaceLineEndings(replacementText: " ")})", Passed: false);
        }

        // Mismatched extents are a broken capture pair, not a measurable divergence — neither direction can be
        // honestly decided from them, so both fail here rather than one of them passing by accident.
        if ((before.Width != after.Width) || (before.Height != after.Height)) {
            return new CanaryAssertionResult(
                Detail: $"{assertion.Name}: capture extents disagree ({before.Width}x{before.Height} vs {after.Width}x{after.Height})",
                Passed: false
            );
        }

        var (changedPixels, maxDelta) = MeasureChange(after: after, before: before);
        var agree = (changedPixels <= CanaryFrameNoise.MaxChangedPixels);

        return new CanaryAssertionResult(
            Detail: $"{assertion.Name}: {changedPixels} pixel(s) changed by >= {CanaryFrameNoise.MinChangedDelta} LSB (max {maxDelta}), budget {CanaryFrameNoise.MaxChangedPixels} — the frames {(agree ? "agree" : "diverge")}",
            Passed: (agree == assertion.Agree)
        );
    }
    // Counts the pixels a reader would call changed: max |dR|,|dG|,|dB| at or above the noise delta. Alpha is ignored
    // — a composed screenshot is opaque and the channel carries no scene content.
    private static (long ChangedPixels, int MaxDelta) MeasureChange(PngImage before, PngImage after) {
        var beforePixels = before.RgbaPixels;
        var afterPixels = after.RgbaPixels;
        var changedPixels = 0L;
        var maxDelta = 0;

        for (var index = 0; (index < beforePixels.Length); index += 4) {
            var deltaR = Math.Abs(value: (beforePixels[(index + 0)] - afterPixels[(index + 0)]));
            var deltaG = Math.Abs(value: (beforePixels[(index + 1)] - afterPixels[(index + 1)]));
            var deltaB = Math.Abs(value: (beforePixels[(index + 2)] - afterPixels[(index + 2)]));
            var pixelDelta = Math.Max(val1: deltaR, val2: Math.Max(val1: deltaG, val2: deltaB));

            if (pixelDelta >= CanaryFrameNoise.MinChangedDelta) {
                changedPixels++;
            }
            if (pixelDelta > maxDelta) {
                maxDelta = pixelDelta;
            }
        }

        return (changedPixels, maxDelta);
    }

    public static IReadOnlyList<string> ResponseLines(CanaryTranscript transcript, CanaryStream stream, string verb) {
        var prefix = $"[{verb}:";

        return Lines(stream: stream, transcript: transcript)
            .Where(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix))
            .ToArray();
    }

    private static CanaryAssertionResult EvaluateLine(CanaryLineAssertion assertion, CanaryTranscript transcript, string authorityEndpoint) {
        var text = assertion.Text.Replace(oldValue: AuthorityToken, newValue: authorityEndpoint, comparisonType: StringComparison.Ordinal);
        var matched = Lines(transcript: transcript, stream: assertion.Stream).Any(predicate: line => assertion.Match switch {
            CanaryLineMatch.Exact => string.Equals(a: line, b: text, comparisonType: StringComparison.Ordinal),
            CanaryLineMatch.Contains => line.Contains(value: text, comparisonType: StringComparison.Ordinal),
            _ => false,
        });
        var passed = (matched == assertion.Present);
        var expectation = (assertion.Present ? "present" : "absent");

        return new CanaryAssertionResult(Detail: $"{assertion.Name}: {expectation} on {StreamName(stream: assertion.Stream)}", Passed: passed);
    }
    private static CanaryAssertionResult EvaluateResponse(CanaryResponseAssertion assertion, CanaryTranscript transcript, Dictionary<string, string> values) {
        var responses = ResponseLines(transcript: transcript, stream: assertion.Stream, verb: assertion.Verb);

        if (responses.Count != assertion.Count) {
            return new CanaryAssertionResult(
                Detail: $"{assertion.Name}: {assertion.Verb} cardinality {responses.Count}, expected exactly {assertion.Count} on {StreamName(stream: assertion.Stream)}",
                Passed: false
            );
        }

        var selected = responses[(assertion.Occurrence - 1)];

        foreach (var extraction in assertion.Extractions) {
            if (!TryExtract(error: out var error, extraction: extraction, line: selected, value: out var value)) {
                return new CanaryAssertionResult(Detail: $"{assertion.Name}: {error}", Passed: false);
            }

            values[extraction.Name] = value;
        }

        return new CanaryAssertionResult(
            Detail: $"{assertion.Name}: selected {assertion.Verb} occurrence {assertion.Occurrence} of exactly {assertion.Count} on {StreamName(stream: assertion.Stream)}",
            Passed: true
        );
    }
    private static CanaryAssertionResult EvaluateSequence(CanarySequenceAssertion assertion, CanaryTranscript transcript) {
        var lines = Lines(transcript: transcript, stream: assertion.Stream);
        var priorIndex = -1;

        foreach (var selector in assertion.Responses) {
            var indices = lines
                .Select(selector: static (line, index) => (line, index))
                .Where(predicate: pair => pair.line.StartsWith(value: $"[{selector.Verb}:", comparisonType: StringComparison.Ordinal))
                .Select(selector: static pair => pair.index)
                .ToArray();

            if (indices.Length != selector.Count) {
                return new CanaryAssertionResult(
                    Detail: $"{assertion.Name}: {selector.Verb} cardinality {indices.Length}, expected exactly {selector.Count} on {StreamName(stream: assertion.Stream)}",
                    Passed: false
                );
            }

            var selectedIndex = indices[(selector.Occurrence - 1)];

            if (selectedIndex <= priorIndex) {
                return new CanaryAssertionResult(Detail: $"{assertion.Name}: {selector.Verb} occurrence {selector.Occurrence} arrived out of order", Passed: false);
            }

            priorIndex = selectedIndex;
        }

        return new CanaryAssertionResult(Detail: $"{assertion.Name}: {assertion.Responses.Count} selected responses arrived in order on {StreamName(stream: assertion.Stream)}", Passed: true);
    }
    private static CanaryAssertionResult EvaluateRelation(CanaryRelationAssertion assertion, Dictionary<string, string> values) {
        if (!TryResolveOperand(operand: assertion.Left, values: values, value: out var left, error: out var leftError)) {
            return new CanaryAssertionResult(Detail: $"{assertion.Name}: {leftError}", Passed: false);
        }

        switch (assertion.Operator) {
            case CanaryRelationOperator.Equal:
            case CanaryRelationOperator.NotEqual: {
                    if (!TryResolveOperand(operand: assertion.Right!, values: values, value: out var right, error: out var rightError)) {
                        return new CanaryAssertionResult(Detail: $"{assertion.Name}: {rightError}", Passed: false);
                    }

                    var equal = ValuesEqual(left: left, right: right);
                    var passed = ((assertion.Operator == CanaryRelationOperator.Equal) ? equal : !equal);

                    return new CanaryAssertionResult(Detail: $"{assertion.Name}: '{left}' {OperatorName(relationOperator: assertion.Operator)} '{right}'", Passed: passed);
                }
            case CanaryRelationOperator.BetweenInclusive:
            case CanaryRelationOperator.AtLeast:
            case CanaryRelationOperator.AtMost: {
                    if (!TryNumber(number: out var number, value: left)) {
                        return new CanaryAssertionResult(Detail: $"{assertion.Name}: '{left}' is not a finite number", Passed: false);
                    }

                    var passed = assertion.Operator switch {
                        CanaryRelationOperator.BetweenInclusive => ((number >= assertion.Minimum) && (number <= assertion.Maximum)),
                        CanaryRelationOperator.AtLeast => (number >= assertion.Minimum),
                        CanaryRelationOperator.AtMost => (number <= assertion.Maximum),
                        _ => false,
                    };
                    var bounds = assertion.Operator switch {
                        CanaryRelationOperator.BetweenInclusive => $"in [{assertion.Minimum}, {assertion.Maximum}]",
                        CanaryRelationOperator.AtLeast => $">= {assertion.Minimum}",
                        CanaryRelationOperator.AtMost => $"<= {assertion.Maximum}",
                        _ => string.Empty,
                    };

                    return new CanaryAssertionResult(Detail: $"{assertion.Name}: {number.ToString(provider: CultureInfo.InvariantCulture)} {bounds}", Passed: passed);
                }
            case CanaryRelationOperator.MinimumMargin: {
                    if (!TryResolveOperand(operand: assertion.Right!, values: values, value: out var right, error: out var rightError)) {
                        return new CanaryAssertionResult(Detail: $"{assertion.Name}: {rightError}", Passed: false);
                    }
                    if (!TryNumber(number: out var leftNumber, value: left) || !TryNumber(number: out var rightNumber, value: right)) {
                        return new CanaryAssertionResult(Detail: $"{assertion.Name}: minimumMargin needs two finite numbers, got '{left}' and '{right}'", Passed: false);
                    }

                    var actual = Math.Abs(value: (leftNumber - rightNumber));

                    return new CanaryAssertionResult(
                        Detail: $"{assertion.Name}: margin {actual.ToString(provider: CultureInfo.InvariantCulture)} >= {assertion.Margin}",
                        Passed: (actual >= assertion.Margin)
                    );
                }
            default:
                return new CanaryAssertionResult(Detail: $"{assertion.Name}: unsupported relation", Passed: false);
        }
    }
    private static bool TryExtract(string line, CanaryValueExtraction extraction, out string value, out string error) {
        value = string.Empty;
        error = string.Empty;

        if (!TryReadField(line: line, field: extraction.Field, value: out var fieldValue)) {
            error = $"field '{extraction.Field}' was absent from the selected response";

            return false;
        }

        if (extraction.Component is not { } component) {
            value = fieldValue;

            return true;
        }

        var components = fieldValue.Split(options: StringSplitOptions.TrimEntries, separator: ',');

        if (component >= components.Length) {
            error = $"field '{extraction.Field}' has {components.Length} component(s), so component {component} does not exist";

            return false;
        }

        value = components[component];

        return true;
    }
    private static bool TryReadField(string line, string field, out string value) {
        value = string.Empty;

        var colon = line.IndexOf(value: ':');
        var end = (line.EndsWith(value: ']') ? (line.Length - 1) : line.Length);

        if ((colon < 0) || (colon >= end)) {
            return false;
        }

        var index = (colon + 1);

        while (index < end) {
            while ((index < end) && char.IsWhiteSpace(c: line[index])) {
                index++;
            }

            var nameStart = index;

            while ((index < end) && !char.IsWhiteSpace(c: line[index]) && (line[index] != '=')) {
                index++;
            }

            if ((index >= end) || (line[index] != '=')) {
                while ((index < end) && !char.IsWhiteSpace(c: line[index])) {
                    index++;
                }
                continue;
            }

            var name = line[nameStart..index];

            index++;
            var parenthesized = ((index < end) && (line[index] == '('));

            if (parenthesized) {
                index++;
            }
            var valueStart = index;

            if (parenthesized) {
                while ((index < end) && (line[index] != ')')) {
                    index++;
                }
            } else {
                while ((index < end) && !char.IsWhiteSpace(c: line[index])) {
                    index++;
                }
            }

            if (string.Equals(a: name, b: field, comparisonType: StringComparison.Ordinal)) {
                value = line[valueStart..index];

                return true;
            }

            if (parenthesized && (index < end)) {
                index++;
            }
        }

        return false;
    }
    private static bool TryResolveOperand(CanaryOperand operand, Dictionary<string, string> values, out string value, out string error) {
        error = string.Empty;

        if (operand.ValueName is { } valueName) {
            if (values.TryGetValue(key: valueName, value: out value!)) {
                return true;
            }

            value = string.Empty;
            error = $"extracted value '{valueName}' is unavailable because its response assertion failed";

            return false;
        }

        if (operand.StringLiteral is { } literal) {
            value = literal;

            return true;
        }

        value = operand.NumberLiteral!.Value.ToString(format: "R", provider: CultureInfo.InvariantCulture);

        return true;
    }
    private static bool ValuesEqual(string left, string right) =>
        ((TryNumber(number: out var leftNumber, value: left) && TryNumber(number: out var rightNumber, value: right))
            ? (leftNumber == rightNumber)
            : string.Equals(a: left, b: right, comparisonType: StringComparison.Ordinal));
    private static bool TryNumber(string value, out double number) =>
        (double.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out number) && double.IsFinite(d: number));
    private static IReadOnlyList<string> Lines(CanaryTranscript transcript, CanaryStream stream) =>
        ((stream == CanaryStream.Stdout) ? transcript.Stdout : transcript.Stderr);
    private static string StreamName(CanaryStream stream) => ((stream == CanaryStream.Stdout) ? "stdout" : "stderr");
    private static string OperatorName(CanaryRelationOperator relationOperator) => relationOperator switch {
        CanaryRelationOperator.Equal => "==",
        CanaryRelationOperator.NotEqual => "!=",
        _ => relationOperator.ToString(),
    };
}
