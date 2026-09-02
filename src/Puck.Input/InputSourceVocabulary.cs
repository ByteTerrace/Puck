using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Resolves a provider-neutral source id string (an <see cref="InputSignal.Source"/> value) against the engine's
/// canonical input-source vocabulary (<see cref="InputSources"/>) and reports the FULL <see cref="CommandValueKind"/>
/// each one declares. This is <see cref="InputSources"/>' own vocabulary reflected once, not a downstream ABI's
/// narrowing of it — a caller that can only carry a subset of <see cref="CommandValueKind"/> (for example, an addon
/// input record's two-component payload) applies that narrowing itself over <see cref="TryResolveDeclaredKind"/>'s
/// full range.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is derived from attributes, not hand-transcribed. <see cref="InputSources"/> members carry
/// <see cref="InputSourceValueAttribute"/> (declaring their <see cref="CommandValueKind"/>) and, for a control no
/// caller can address for a reason beyond its declared kind, <see cref="InputSourceUnaddressableAttribute"/> too —
/// that is the single declaration site. <see cref="BuildTables"/> reads every attribute once, by reflection, and
/// caches the result; a new physical control needs only the one-line attribute added at its declaration in
/// <see cref="InputSources"/>, never a second edit here. A field carrying neither attribute resolves as neither
/// declared nor explicitly unaddressable.
/// </para>
/// <para>
/// Resolution is CASE-INSENSITIVE, in the declared tables and in the parametric families alike. A source id's case
/// is authored-document noise, never identity: <c>Puck.Commands.BindingProfile</c> compiles a page's sources into an
/// <see cref="StringComparer.OrdinalIgnoreCase"/> table and dispatches incoming signals through that same table, so
/// a row authored <c>"Gamepad.ButtonSouth"</c> presses and releases exactly as the canonical spelling does. A
/// case-sensitive catalog beside a case-insensitive compiler refuses working rows as unknown controls, which is the
/// one answer this type must never give. Case-insensitivity widens nothing: an id no member and no family declares
/// stays unknown in every casing, and two <see cref="InputSources"/> members whose ids differ only by case are the
/// same authoring defect as two identical ones (<see cref="BuildTables"/> throws on either).
/// </para>
/// <para>
/// <see cref="InputSources.Keyboard.Text"/> carries both attributes at once: its declared kind
/// (<see cref="CommandValueKind.Digital"/>) is perfectly representable, but <see cref="IsExplicitlyUnaddressable"/>
/// is still <see langword="true"/> for it because the text payload riding beside that kind is not — a caller that
/// only reads the declared kind would wrongly treat it as an ordinary digital control.
/// </para>
/// </remarks>
public static class InputSourceVocabulary {
    private static readonly Dictionary<string, CommandValueKind> KindsBySourceId;
    private static readonly HashSet<string> ExplicitlyUnaddressableSourceIds;
    private static readonly HashSet<string> RelativeSourceIds;

    static InputSourceVocabulary() {
        (KindsBySourceId, ExplicitlyUnaddressableSourceIds, RelativeSourceIds) = BuildTables();
    }

    /// <summary>Indicates whether <paramref name="sourceId"/> declares <see cref="InputSourceValueAttribute.Relative"/>
    /// — each sample a delta, not a deflection.</summary>
    /// <param name="sourceId">The provider-neutral source id text.</param>
    public static bool IsRelative(string sourceId) {
        return RelativeSourceIds.Contains(item: sourceId);
    }
    /// <summary>Attempts to resolve <paramref name="sourceId"/> against the engine's canonical source-id surface
    /// and report the full <see cref="CommandValueKind"/> it declares.</summary>
    /// <param name="sourceId">The provider-neutral source id text (e.g. <c>"gamepad.buttonSouth"</c>).</param>
    /// <param name="kind">When this returns <see langword="true"/>, the declared value kind.</param>
    /// <returns><see langword="true"/> if <paramref name="sourceId"/> names a recognized source, whether from an
    /// exact declared field or an open-ended parametric family (keyboard letters/digits, numbered mouse buttons);
    /// otherwise <see langword="false"/>.</returns>
    public static bool TryResolveDeclaredKind(string sourceId, out CommandValueKind kind) {
        if (KindsBySourceId.TryGetValue(
            key: sourceId,
            value: out kind
        )) {
            return true;
        }

        return TryResolveParametricKind(
            kind: out kind,
            sourceId: sourceId
        );
    }
    /// <summary>Indicates whether <paramref name="sourceId"/> names a recognized physical control — the name half of
    /// <see cref="TryResolveDeclaredKind"/>, for a caller that admits every declared value kind.</summary>
    /// <param name="sourceId">The provider-neutral source id text (e.g. <c>"gamepad.buttonSouth"</c>).</param>
    public static bool IsKnownSourceId(string sourceId) {
        return TryResolveDeclaredKind(
            kind: out _,
            sourceId: sourceId
        );
    }
    /// <summary>Indicates whether <paramref name="sourceId"/> names a control explicitly marked
    /// <see cref="InputSourceUnaddressableAttribute"/> — a control unaddressable for a reason its declared
    /// <see cref="CommandValueKind"/> alone does not say (today only a text payload). A caller narrowing
    /// <see cref="CommandValueKind"/> to its own addressable subset still owes that narrowing separately; this
    /// answers only the explicit marker, never the declared-kind test.</summary>
    /// <param name="sourceId">The provider-neutral source id text.</param>
    public static bool IsExplicitlyUnaddressable(string sourceId) {
        return ExplicitlyUnaddressableSourceIds.Contains(item: sourceId);
    }

    // Reads InputSourceValueAttribute/InputSourceUnaddressableAttribute off every const string field declared on
    // InputSources' physical-control groups (Keyboard, Mouse, Gamepad — named directly rather than discovered via
    // GetNestedTypes, so each typeof() flows straight into ClassifyGroup's DynamicallyAccessedMembers annotation).
    // A future third group must add a line here, or it is silently never classified.
    //
    // Parametric families (keyboard letters/functions and numbered mouse buttons) are recognized below without
    // trying to reflect an unbounded constant set. A source id declared by two different members is an InputSources
    // authoring defect, not caller-supplied data — it cannot vary by caller — so it belongs here, at table-build
    // time, thrown loud and unconditional the same way CommandRegistry's constructor throws on a duplicate command
    // name. declaredBy is the one map both destination tables are gated through, so the guard is symmetric by
    // construction instead of needing a matching check duplicated at each Add call.
    private static (Dictionary<string, CommandValueKind> Kinds, HashSet<string> ExplicitlyUnaddressable, HashSet<string> Relative) BuildTables() {
        var declaredBy = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);
        var kinds = new Dictionary<string, CommandValueKind>(comparer: StringComparer.OrdinalIgnoreCase);
        var explicitlyUnaddressable = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var relative = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        ClassifyGroup(
            declaredBy: declaredBy,
            explicitlyUnaddressable: explicitlyUnaddressable,
            relative: relative,
            kinds: kinds,
            type: typeof(InputSources.Keyboard)
        );
        ClassifyGroup(
            declaredBy: declaredBy,
            explicitlyUnaddressable: explicitlyUnaddressable,
            relative: relative,
            kinds: kinds,
            type: typeof(InputSources.Mouse)
        );
        ClassifyGroup(
            declaredBy: declaredBy,
            explicitlyUnaddressable: explicitlyUnaddressable,
            relative: relative,
            kinds: kinds,
            type: typeof(InputSources.Gamepad)
        );

        return (kinds, explicitlyUnaddressable, relative);
    }
    private static void ClassifyGroup(Dictionary<string, string> declaredBy, HashSet<string> explicitlyUnaddressable, Dictionary<string, CommandValueKind> kinds, HashSet<string> relative, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type type) {
        foreach (var field in type.GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
            if (
                !field.IsLiteral ||
                (field.FieldType != typeof(string))
            ) {
                continue;
            }

            var sourceId = ((string)field.GetRawConstantValue()!);
            var memberName = $"InputSources.{type.Name}.{field.Name}";

            if (declaredBy.TryGetValue(
                key: sourceId,
                value: out var existingMember
            )) {
                throw new InvalidOperationException(message: $"InputSources id \"{sourceId}\" is declared by both {existingMember} and {memberName}.");
            }

            declaredBy.Add(
                key: sourceId,
                value: memberName
            );

            if (field.GetCustomAttribute<InputSourceUnaddressableAttribute>() is not null) {
                _ = explicitlyUnaddressable.Add(item: sourceId);
            }

            if (field.GetCustomAttribute<InputSourceValueAttribute>() is { } declaredValue) {
                kinds.Add(
                    key: sourceId,
                    value: declaredValue.Kind
                );

                if (declaredValue.Relative) {
                    _ = relative.Add(item: sourceId);
                }
            }
        }
    }
    // Open-ended families mint source ids rather than declaring one constant each: keyboard letters/functions and
    // numbered mouse buttons. Recognize exactly the same canonical range each public factory accepts. Every id
    // resolved here is CommandValueKind.Digital — none of the open-ended families mint an analog control, except the
    // probe family's Axis1D.
    //
    // Every prefix and letter test is case-insensitive, matching the declared tables: the compiler that consumes
    // this answer dispatches "Keyboard.A" and "keyboard.a" through one table entry, so both name the same control
    // here. The DIGIT walks stay exact — a canonical number has no case, and "keyboard.f01" is still malformed.
    private static bool TryResolveParametricKind(string sourceId, out CommandValueKind kind) {
        kind = default;

        const string ProbePrefix = "probe.";

        if (sourceId.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ProbePrefix)) {
            var name = sourceId.AsSpan(start: ProbePrefix.Length);

            if (
                name.IsEmpty ||
                (name.Length > 64)
            ) {
                return false;
            }

            foreach (var character in name) {
                if (
                    !char.IsAsciiLetter(c: character) &&
                    !char.IsAsciiDigit(c: character) &&
                    (character != '-')
                ) {
                    return false;
                }
            }

            kind = CommandValueKind.Axis1D;
            return true;
        }

        const string MouseButtonPrefix = "mouse.button";

        if (sourceId.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: MouseButtonPrefix)) {
            if (TryParseCanonicalPositiveNumber(digits: sourceId.AsSpan(start: MouseButtonPrefix.Length), maximum: ushort.MaxValue, number: out _)) {
                kind = CommandValueKind.Digital;
                return true;
            }

            return false;
        }

        const string KeyboardPrefix = "keyboard.";

        if (!sourceId.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: KeyboardPrefix
        )) {
            return false;
        }

        var suffix = sourceId.AsSpan(start: KeyboardPrefix.Length);

        if (
            (suffix.Length == 1) &&
            char.IsAsciiLetter(c: suffix[0])
        ) {
            kind = CommandValueKind.Digital;
            return true;
        }

        if (
            (suffix.Length == 1) &&
            char.IsAsciiDigit(c: suffix[0])
        ) {
            kind = CommandValueKind.Digital;
            return true;
        }

        const string NumpadPrefix = "numpad";

        if (
            suffix.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: NumpadPrefix) &&
            (suffix.Length == (NumpadPrefix.Length + 1)) &&
            char.IsAsciiDigit(c: suffix[^1])
        ) {
            kind = CommandValueKind.Digital;
            return true;
        }

        if (
            (suffix.Length >= 2) &&
            char.IsAsciiLetter(c: suffix[0]) &&
            ((suffix[0] | 0x20) == 'f') &&
            TryParseCanonicalPositiveNumber(
            digits: suffix[1..],
            maximum: 12,
            number: out var number
        )
        ) {
            kind = CommandValueKind.Digital;
            return true;
        }

        return false;
    }
    // Accepts exactly the canonical digit forms "1".."12" (or "1".."65535" for mouse buttons) — digits only, no
    // sign, no leading/trailing whitespace, no leading zero, culture-invariant. int.TryParse's default
    // NumberStyles.Integer permits AllowLeadingSign (so "keyboard.f+1" resolved) and is
    // CultureInfo.CurrentCulture-dependent; a hand-rolled digit walk is the only way to accept the canonical range
    // and nothing else regardless of the host's culture.
    private static bool TryParseCanonicalPositiveNumber(ReadOnlySpan<char> digits, int maximum, out int number) {
        number = 0;

        if (
            digits.IsEmpty ||
            (digits[0] == '0')
        ) {
            return false;
        }

        foreach (var digit in digits) {
            if (!char.IsAsciiDigit(c: digit)) {
                return false;
            }

            var next = ((number * 10) + (digit - '0'));

            if (next > maximum) {
                return false;
            }

            number = next;
        }

        return (number > 0);
    }
}
