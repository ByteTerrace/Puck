using System.Buffers;
using System.Globalization;
using Puck.Maths;
using System.Numerics;
using System.Text;

namespace Puck.Text;

/// <summary>
/// The enrichment markup grammar and its single-pass stack parser. The wire format uses four ASCII <b>C0 control
/// chars</b> as delimiters (collision-proof against ordinary text and trivially strippable) plus a binding sigil; a
/// human authoring front-end (<see cref="BbCodeTextMarkup"/>) compiles a friendlier <c>[wave]…[/wave]</c> syntax down
/// to this stream, so agents and JSON never embed raw control chars.
/// <para>
/// <see cref="EnumerateRichTextRunes"/> is a left-to-right rune scan over a <see cref="Stack{TextEffect}"/>: a start
/// tag pushes, a matching end tag pops, <c>reset</c> clears, and each visible rune pairs with the stack top. Nesting is
/// stack-based and the innermost effect <b>shadows</b> (there is no composition of two effects on one rune); malformed
/// or unknown tags are dropped, mismatched end tags are ignored, and malformed UTF-16 is replaced with
/// <see cref="Rune.ReplacementChar"/>, so arbitrary text is safe to pass through.
/// </para>
/// </summary>
public static class TextEnrichmentTags {
    /// <summary>The late-binding sigil: a value of <c>base⟨SUB⟩variableName</c> binds a content-time channel (ASCII SUB, <c>U+001A</c>).</summary>
    public const char BindingSigil = '\u001A';
    /// <summary>The tag closing delimiter (ASCII US, <c>U+001F</c>).</summary>
    public const char TagEnd = '\u001F';
    /// <summary>The delimiter between the tag command and each parameter field (ASCII GS, <c>U+001D</c>).</summary>
    public const char TagFieldSeparator = '\u001D';
    /// <summary>The tag opening delimiter (ASCII RS, <c>U+001E</c>).</summary>
    public const char TagStart = '\u001E';
    /// <summary>The delimiter between a parameter name and its value (ASCII FS, <c>U+001C</c>).</summary>
    public const char TagValueSeparator = '\u001C';

    // The one recognized-name table for effect kinds; internal so the BBCode front-end resolves names identically.
    internal static TextEffectKind ParseEffectKind(string name) =>
        name switch {
            "shake" => TextEffectKind.Shake,
            "wave" => TextEffectKind.Wave,
            "pulse" => TextEffectKind.Pulse,
            "jitter" => TextEffectKind.Jitter,
            "dissolve" => TextEffectKind.Dissolve,
            "color" or "colour" => TextEffectKind.Color,
            "weight" or "bold" or "emphasis" => TextEffectKind.Weight,
            "reveal" or "typewriter" => TextEffectKind.Reveal,
            _ => TextEffectKind.None
        };

    private static void ApplyTag(TextEnrichmentTagRecord tagRecord, Stack<TextEffect> effectStack) {
        if (!tagRecord.IsRecognized) {
            return;
        }

        if (tagRecord.IsReset) {
            effectStack.Clear();

            return;
        }

        if (tagRecord.IsEnd) {
            if (
                effectStack.TryPeek(result: out var currentEffect) &&
                (currentEffect.Kind == tagRecord.Kind)
            ) {
                _ = effectStack.Pop();
            }

            return;
        }

        effectStack.Push(item: CreateEffect(tagRecord: tagRecord));
    }
    private static TextEffect CreateEffect(TextEnrichmentTagRecord tagRecord) {
        var effect = tagRecord.Kind switch {
            TextEffectKind.Shake => new TextEffect(
            TextEffectKind.Shake,
            new(2.5f),
            new(10.0f),
            new(0.0f),
            new(1.0f)
        ),
            TextEffectKind.Wave => new TextEffect(
            TextEffectKind.Wave,
            new(3.0f),
            new(2.0f),
            new(0.0f),
            new(1.0f)
        ),
            TextEffectKind.Pulse => new TextEffect(
            TextEffectKind.Pulse,
            new(0.08f),
            new(2.0f),
            new(0.0f),
            new(1.0f)
        ),
            TextEffectKind.Jitter => new TextEffect(
            TextEffectKind.Jitter,
            new(1.5f),
            new(14.0f),
            new(0.0f),
            new(1.0f)
        ),
            TextEffectKind.Dissolve => new TextEffect(
            TextEffectKind.Dissolve,
            new(1.0f),
            new(1.0f),
            new(0.0f),
            new(1.5f),
            TextEffectDissolveStyle.Devilish
        ),
            TextEffectKind.Weight => new TextEffect(
            TextEffectKind.Weight,
            new(0.35f),
            new(0.0f),
            new(0.0f),
            new(1.0f)
        ),
            TextEffectKind.Reveal => new TextEffect(
            TextEffectKind.Reveal,
            new(0.0f),
            new(0.0f),
            new(0.06f),
            new(0.25f)
        ),
            TextEffectKind.Color => new TextEffect(
            TextEffectKind.Color,
            new(0.0f),
            new(0.0f),
            new(0.0f),
            new(1.0f),
            TextEffectDissolveStyle.None,
            Vector4.One
        ),
            _ => TextEffect.None
        };

        foreach (var parameter in tagRecord.Parameters) {
            var name = parameter.Name.ToLowerInvariant();

            if (
                (name is "style" or "flavor" or "flavour") &&
                TryParseDissolveStyle(
                value: parameter.Value,
                style: out var dissolveStyle
            )
            ) {
                effect = effect with { DissolveStyle = dissolveStyle };

                continue;
            }

            if (
                (name is "color" or "colour" or "hex" or "tint") &&
                TryParseHexColor(
                value: parameter.Value,
                color: out var tint
            )
            ) {
                effect = effect with { TintColor = tint };

                continue;
            }

            if (!TryParseEffectParameter(
                value: parameter.Value,
                parameter: out var parsedParameter
            )) {
                continue;
            }

            effect = name switch {
                "amplitude" or "amplitudepixels" or "intensity" or "amount" or "weight" => effect with { AmplitudePixels = parsedParameter },
                "frequency" or "frequencyhz" or "speed" or "rate" => effect with { FrequencyHz = parsedParameter },
                "phase" or "stagger" => effect with { Phase = parsedParameter },
                "duration" or "durationseconds" => effect with { DurationSeconds = parsedParameter },
                _ => effect
            };
        }

        return effect;
    }
    private static ulong HashStringFnv1A(string value) {
        var hash = Fnv1aHash.Create();

        foreach (var character in value) {
            hash.Add(value: character);
        }

        return hash.Value;
    }
    private static TextEnrichmentTagRecord ParseTagRecord(string payload) {
        var fields = payload.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: TagFieldSeparator
        );

        if (fields.Length == 0) {
            return default;
        }

        var command = fields[0];
        var isEnd = command.StartsWith(value: '/');
        var normalizedName = (isEnd
            ? command[1..]
            : command).Trim().ToLowerInvariant();
        var isReset = (normalizedName == "reset");
        var kind = (isReset
            ? TextEffectKind.None
            : ParseEffectKind(name: normalizedName)
        );
        var parameters = new List<TextEnrichmentTagParameter>();

        for (var index = 1; (index < fields.Length); index++) {
            var field = fields[index];
            var separatorIndex = field.IndexOf(value: TagValueSeparator);

            if (
                (separatorIndex <= 0) ||
                (separatorIndex == (field.Length - 1))
            ) {
                continue;
            }

            parameters.Add(item: new TextEnrichmentTagParameter(
                Name: field[..separatorIndex],
                Value: field[(separatorIndex + 1)..]
            ));
        }

        return new TextEnrichmentTagRecord(
            IsEnd: isEnd,
            IsRecognized: (isReset || (kind != TextEffectKind.None)),
            IsReset: isReset,
            Kind: kind,
            Parameters: parameters
        );
    }
    private static string RemoveDelimiters(string value) {
        var builder = new StringBuilder(capacity: value.Length);

        foreach (var rune in value.EnumerateRunes()) {
            if (!IsDelimiter(unicodeScalar: rune.Value)) {
                _ = builder.Append(value: rune);
            }
        }

        return builder.ToString();
    }
    // The one scan both enumerators consume: each token is either a parsed tag (with the raw span it occupied) or a
    // non-delimiter rune. An unterminated TagStart and stray delimiter runes are dropped, never yielded.
    private static IEnumerable<TextEnrichmentScanToken> Scan(string text) {
        var index = 0;

        while (index < text.Length) {
            if (text[index] == TagStart) {
                if (TryReadTag(
                    nextIndex: out var nextIndex,
                    startIndex: index,
                    tagRecord: out var tagRecord,
                    text: text
                )) {
                    yield return new TextEnrichmentScanToken(
                        Rune: default,
                        Tag: tagRecord,
                        TagEndIndex: nextIndex,
                        TagStartIndex: index
                    );
                    index = nextIndex;
                } else {
                    index++;
                }

                continue;
            }

            var status = Rune.DecodeFromUtf16(
                source: text.AsSpan(start: index),
                result: out var rune,
                charsConsumed: out var charsConsumed
            );

            if (status != OperationStatus.Done) {
                rune = Rune.ReplacementChar;
                charsConsumed = 1;
            }

            index += charsConsumed;

            if (!IsDelimiter(unicodeScalar: rune.Value)) {
                yield return new TextEnrichmentScanToken(
                    Rune: rune,
                    Tag: null,
                    TagEndIndex: 0,
                    TagStartIndex: 0
                );
            }
        }
    }
    private static string ToTagName(TextEffectKind kind) =>
        kind switch {
            TextEffectKind.Shake => "shake",
            TextEffectKind.Wave => "wave",
            TextEffectKind.Pulse => "pulse",
            TextEffectKind.Jitter => "jitter",
            TextEffectKind.Dissolve => "dissolve",
            TextEffectKind.Color => "color",
            TextEffectKind.Weight => "weight",
            TextEffectKind.Reveal => "reveal",
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(kind),
            actualValue: kind,
            message: "Unknown enrichment effect kind."
        )
        };
    private static bool TryParseDissolveStyle(string value, out TextEffectDissolveStyle style) {
        style = value.Trim().ToLowerInvariant() switch {
            "devil" or "devilish" or "flame" or "fire" or "infernal" => TextEffectDissolveStyle.Devilish,
            "sick" or "sickly" or "melt" or "melting" or "toxic" or "slime" => TextEffectDissolveStyle.Sickly,
            _ => TextEffectDissolveStyle.None
        };

        return (style != TextEffectDissolveStyle.None);
    }
    private static bool TryParseEffectParameter(string value, out TextEffectParameter parameter) {
        parameter = default;

        var mode = TextEffectParameterBindingMode.Multiplicative;
        var subIndex = value.IndexOf(value: BindingSigil);

        if (subIndex < 0) {
            if (
                float.TryParse(
                s: value,
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out var literal
            ) &&
                float.IsFinite(f: literal)
            ) {
                parameter = new TextEffectParameter(BaseValue: literal);

                return true;
            }

            return false;
        }

        var baseValue = 1.0f;
        var variableName = value[(subIndex + 1)..].Trim();

        if (variableName.Length == 0) {
            return false;
        }

        var prefix = value[..subIndex].Trim();

        if (prefix.EndsWith(value: '+')) {
            mode = TextEffectParameterBindingMode.Additive;
            prefix = prefix[..^1].Trim();
            baseValue = 0.0f;
        } else if (prefix.EndsWith(value: '*')) {
            mode = TextEffectParameterBindingMode.Multiplicative;
            prefix = prefix[..^1].Trim();
            baseValue = 1.0f;
        } else if (prefix.Length == 0) {
            mode = TextEffectParameterBindingMode.Replacement;
            baseValue = 0.0f;
        }

        if (
            (prefix.Length > 0) &&
            (!float.TryParse(
            s: prefix,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out baseValue
        ) || !float.IsFinite(f: baseValue))
        ) {
            return false;
        }

        parameter = new TextEffectParameter(
            BaseValue: baseValue,
            VariableHash: HashStringFnv1A(value: variableName),
            BindingMode: mode
        );

        return true;
    }
    // Parses RGB/RGBA hex in three-, four-, six-, or eight-digit form into a linear-agnostic 0..1 RGBA vector.
    private static bool TryParseHexColor(string value, out Vector4 color) {
        color = Vector4.One;

        var text = value.Trim();

        if (text.StartsWith(value: '#')) {
            text = text[1..];
        }

        if (text.Length is not (3 or 4 or 6 or 8)) {
            return false;
        }

        if (!uint.TryParse(
            s: text,
            style: NumberStyles.HexNumber,
            provider: CultureInfo.InvariantCulture,
            result: out var packed
        )) {
            return false;
        }

        if (text.Length is 3 or 4) {
            var red = (packed >> ((text.Length - 1) * 4)) & 0xFu;
            var green = (packed >> ((text.Length - 2) * 4)) & 0xFu;
            var blue = (packed >> ((text.Length - 3) * 4)) & 0xFu;
            var alpha = ((text.Length == 4)
                ? packed & 0xFu
                : 0xFu
            );

            packed =
                ((red * 0x11u) << 24) |
                ((green * 0x11u) << 16) |
                ((blue * 0x11u) << 8) |
                (alpha * 0x11u);
        } else if (text.Length == 6) {
            packed = (packed << 8) | 0xFFu;
        }

        color = new Vector4(
            w: ((packed & 0xFFu) / 255.0f),
            x: (((packed >> 24) & 0xFFu) / 255.0f),
            y: (((packed >> 16) & 0xFFu) / 255.0f),
            z: (((packed >> 8) & 0xFFu) / 255.0f)
        );

        return true;
    }
    private static bool TryReadTag(string text, int startIndex, out TextEnrichmentTagRecord tagRecord, out int nextIndex) {
        tagRecord = default;
        nextIndex = (startIndex + 1);

        var endIndex = text.IndexOf(
            startIndex: (startIndex + 1),
            value: TagEnd
        );

        if (endIndex < 0) {
            return false;
        }

        tagRecord = ParseTagRecord(payload: text[(startIndex + 1)..endIndex]);
        nextIndex = (endIndex + 1);

        return true;
    }

    /// <summary>Builds an end tag that pops the given effect kind.</summary>
    /// <param name="kind">The effect kind to close; must not be <see cref="TextEffectKind.None"/>.</param>
    /// <returns>The delimited end-tag string.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="TextEffectKind.None"/>.</exception>
    public static string CreateEndTag(TextEffectKind kind) {
        if (kind == TextEffectKind.None) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(kind),
                actualValue: kind,
                message: "An enrichment end tag requires an effect kind."
            );
        }

        return $"{TagStart}/{ToTagName(kind: kind)}{TagEnd}";
    }
    /// <summary>Builds a <c>reset</c> tag that clears the whole effect stack.</summary>
    /// <returns>The delimited reset-tag string.</returns>
    public static string CreateResetTag() =>
        $"{TagStart}reset{TagEnd}";
    /// <summary>Builds a start tag that pushes the given effect kind with optional parameters.</summary>
    /// <param name="kind">The effect kind to open; must not be <see cref="TextEffectKind.None"/>.</param>
    /// <param name="parameters">The parameters to encode (blank name/value pairs are skipped).</param>
    /// <returns>The delimited start-tag string.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="TextEffectKind.None"/>.</exception>
    public static string CreateStartTag(TextEffectKind kind, params TextEnrichmentTagParameter[] parameters) {
        ArgumentNullException.ThrowIfNull(argument: parameters);

        if (kind == TextEffectKind.None) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(kind),
                actualValue: kind,
                message: "An enrichment start tag requires an effect kind."
            );
        }

        var builder = new StringBuilder();

        _ = builder.Append(value: TagStart);
        _ = builder.Append(value: ToTagName(kind: kind));

        foreach (var parameter in parameters) {
            if (
                string.IsNullOrWhiteSpace(value: parameter.Name) ||
                string.IsNullOrWhiteSpace(value: parameter.Value)
            ) {
                continue;
            }

            _ = builder.Append(value: TagFieldSeparator);
            _ = builder.Append(value: RemoveDelimiters(value: parameter.Name));
            _ = builder.Append(value: TagValueSeparator);
            _ = builder.Append(value: RemoveDelimiters(value: parameter.Value));
        }

        _ = builder.Append(value: TagEnd);

        return builder.ToString();
    }
    /// <summary>Enumerates the visible runes of a marked-up string, each paired with the effect in force at its
    /// position (the innermost open tag, shadowing).</summary>
    /// <param name="text">The marked-up text (a control-char stream, e.g. from <see cref="BbCodeTextMarkup.Compile"/>).</param>
    /// <returns>The visible runes with their resolved effects, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static IEnumerable<TextEffectRune> EnumerateRichTextRunes(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        var effectStack = new Stack<TextEffect>();

        foreach (var token in Scan(text: text)) {
            if (token.Tag is TextEnrichmentTagRecord tagRecord) {
                ApplyTag(
                    effectStack: effectStack,
                    tagRecord: tagRecord
                );
            } else {
                yield return new TextEffectRune(
                    Rune: token.Rune,
                    Effect: ((effectStack.Count == 0)
                    ? TextEffect.None
                    : effectStack.Peek())
                );
            }
        }
    }
    /// <summary>Enumerates a marked-up string as sanitizable segments — visible runes and recognized tags — so a caller
    /// can strip enrichment while preserving the plain text.</summary>
    /// <param name="text">The marked-up text.</param>
    /// <returns>The ordered segments.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static IEnumerable<TextEnrichmentSegment> EnumerateSanitizableSegments(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        foreach (var token in Scan(text: text)) {
            if (token.Tag is TextEnrichmentTagRecord tagRecord) {
                if (tagRecord.IsRecognized) {
                    yield return TextEnrichmentSegment.Tag(text: text[token.TagStartIndex..token.TagEndIndex]);
                }
            } else {
                yield return TextEnrichmentSegment.VisibleRune(rune: token.Rune);
            }
        }
    }
    /// <summary>Enumerates just the visible runes of a marked-up string, dropping tags and delimiters.</summary>
    /// <param name="text">The marked-up text.</param>
    /// <returns>The visible runes, in order.</returns>
    public static IEnumerable<Rune> EnumerateVisibleRunes(string text) {
        foreach (var richTextRune in EnumerateRichTextRunes(text: text)) {
            yield return richTextRune.Rune;
        }
    }
    /// <summary>Determines whether a Unicode scalar is one of the grammar's delimiter control chars.</summary>
    /// <param name="unicodeScalar">The scalar to test.</param>
    /// <returns><see langword="true"/> when the scalar is a tag delimiter.</returns>
    public static bool IsDelimiter(int unicodeScalar) =>
        (unicodeScalar is TagValueSeparator or TagFieldSeparator or TagStart or TagEnd);

    private readonly record struct TextEnrichmentScanToken(TextEnrichmentTagRecord? Tag, int TagStartIndex, int TagEndIndex, Rune Rune);
}
