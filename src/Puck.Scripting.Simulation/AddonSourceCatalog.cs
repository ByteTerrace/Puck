using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Puck.Commands;
using Puck.Input;

namespace Puck.Scripting.Simulation;

/// <summary>
/// Resolves a provider-neutral source id string against the engine's canonical input-source vocabulary
/// (<see cref="Puck.Input.InputSources"/>) and reports the record value shape it carries. The vocabulary itself
/// is open — <see cref="Puck.Input.InputSources"/> is the engine's single home for physical-control names, not
/// something this catalog forks. It lives in the Simulation adapter rather than the scripting core because the
/// source-id vocabulary is Simulation-lane knowledge, and vocabulary is scoped per lane by assembly. The addon
/// wasm ABI's own input channel no longer routes
/// through this catalog — it speaks a small, closed, host-owned channel-name vocabulary instead (see
/// <c>Puck.World.Server.WorldAddonChannelResolver</c>) — so today's one consumer is the binding-vocabulary check
/// (<c>Puck.World.WorldAffordances</c>), called directly by static method rather than through an injected seam.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is derived from attributes, not hand-transcribed. <see cref="Puck.Input.InputSources"/> members carry
/// <see cref="Puck.Input.InputSourceValueAttribute"/> (declaring their <see cref="CommandValueKind"/>) and,
/// where the ABI cannot carry them for a reason beyond that kind, <see cref="Puck.Input.InputSourceUnaddressableAttribute"/>
/// too — that is the single declaration site. <see cref="BuildTables"/> reads every attribute once, by
/// reflection, and caches the result; a new physical control needs only the one-line attribute added at its
/// declaration in <see cref="InputSources"/>, never a second edit here. A field carrying neither attribute
/// resolves as neither addressable nor unaddressable, silently.
/// </para>
/// <para>
/// Not every <see cref="InputSources"/> entry is declarable here. <see cref="InputSources.Keyboard.Text"/>
/// carries a text payload no fixed-point <c>(valueX, valueY)</c> record can hold, and the three-/four-component
/// motion sources (<see cref="InputSources.Gamepad.Gyro"/>, <see cref="InputSources.Gamepad.Accelerometer"/>,
/// <see cref="InputSources.Gamepad.Orientation"/>) do not fit the record's two-component shape. Those ids name
/// real, recognized controls that this ABI cannot carry, which is a different mistake from a typo and is
/// reported differently: <see cref="TryResolve"/> refuses both, but <see cref="IsUnaddressable"/> separates
/// them so a declaration naming a real control is told why it cannot be carried rather than that it does not
/// exist. Only one of those four — <see cref="InputSources.Keyboard.Text"/> — is marked directly with
/// <see cref="Puck.Input.InputSourceUnaddressableAttribute"/> alongside its declared kind; the three motion
/// sources need no separate marker because their declared <see cref="CommandValueKind"/> already falls outside
/// the addressable subset (see <see cref="ClassifySource"/>).
/// </para>
/// </remarks>
public static class AddonSourceCatalog {
    private static readonly Dictionary<string, AddonSourceShape> ShapesBySourceId;
    private static readonly HashSet<string> UnaddressableSourceIds;

    static AddonSourceCatalog() {
        (ShapesBySourceId, UnaddressableSourceIds) = BuildTables();
    }

    /// <summary>Attempts to resolve <paramref name="sourceId"/> against the engine's canonical source-id
    /// surface and report the record value shape it carries.</summary>
    /// <param name="sourceId">The provider-neutral source id text (e.g. <c>"gamepad.buttonSouth"</c>).</param>
    /// <param name="shape">When this returns <see langword="true"/>, the record value shape the source carries.</param>
    /// <returns><see langword="true"/> if <paramref name="sourceId"/> names a source this ABI can carry; otherwise <see langword="false"/>.</returns>
    public static bool TryResolve(string sourceId, out AddonSourceShape shape) {
        if (ShapesBySourceId.TryGetValue(
            key: sourceId,
            value: out shape
        )) {
            return true;
        }

        return TryResolveParametric(
            shape: out shape,
            sourceId: sourceId
        );
    }

    /// <summary>Indicates whether <paramref name="sourceId"/> names a control the engine genuinely has but this
    /// ABI cannot carry — a text-bearing key, or a motion source with more components than a record's
    /// <c>(valueX, valueY)</c> pair holds. <see cref="TryResolve"/> refuses these
    /// alongside unknown ids; this separates them so the refusal can say which mistake was made.</summary>
    /// <param name="sourceId">The provider-neutral source id text.</param>
    /// <returns><see langword="true"/> when the id names a real control this ABI cannot express.</returns>
    public static bool IsUnaddressable(string sourceId) {
        return UnaddressableSourceIds.Contains(item: sourceId);
    }

    // Reads InputSourceValueAttribute/InputSourceUnaddressableAttribute off every const string field declared
    // on InputSources' two physical-control groups (Keyboard, Gamepad — named directly rather than
    // discovered via GetNestedTypes, so each typeof() flows straight into ClassifySource's DynamicallyAccessedMembers
    // annotation the way AddonAbiRustPort's AppendAbiConstants does for AddonAbi's nested offset classes). A
    // future third group must add a line here, or it is silently never classified.
    //
    // A source id declared by two different members is an InputSources authoring defect, not addon-supplied
    // data — it cannot vary by which addon mounts — so it belongs here, at catalog-build time, thrown loud and
    // unconditional the same way CommandRegistry's constructor throws on a duplicate command name, rather than
    // softened into a per-addon fault a well-formed module could trigger. declaredBy is the one map both
    // destination tables are gated through, so the guard is symmetric by construction instead of needing a
    // matching check duplicated at each Add call.
    private static (Dictionary<string, AddonSourceShape> Shapes, HashSet<string> Unaddressable) BuildTables() {
        var declaredBy = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var shapes = new Dictionary<string, AddonSourceShape>(comparer: StringComparer.Ordinal);
        var unaddressable = new HashSet<string>(comparer: StringComparer.Ordinal);

        ClassifyGroup(
            declaredBy: declaredBy,
            shapes: shapes,
            unaddressable: unaddressable,
            type: typeof(InputSources.Keyboard)
        );
        ClassifyGroup(
            declaredBy: declaredBy,
            shapes: shapes,
            unaddressable: unaddressable,
            type: typeof(InputSources.Gamepad)
        );

        return (shapes, unaddressable);
    }
    private static void ClassifyGroup(Dictionary<string, string> declaredBy, Dictionary<string, AddonSourceShape> shapes, HashSet<string> unaddressable, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type type) {
        foreach (var field in type.GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
            if (
                !field.IsLiteral ||
                (field.FieldType != typeof(string))
            ) {
                continue;
            }

            var sourceId = (string)field.GetRawConstantValue()!;
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

            ClassifySource(
                field: field,
                shapes: shapes,
                sourceId: sourceId,
                unaddressable: unaddressable
            );
        }
    }

    // The derivation rule: InputSourceUnaddressableAttribute always wins, regardless of any declared value kind
    // (InputSources.Keyboard.Text carries it for the reason its own remarks give, not because its kind is
    // unrepresentable). Otherwise, only Digital/Axis1D/Axis2D — the shapes a record's
    // (valueX, valueY) pair can hold — resolve; any other declared kind (Axis3D, Orientation — the three-/
    // four-component motion sources) is unaddressable by construction, with no separate marker needed. A field
    // carrying neither attribute resolves as neither addressable nor unaddressable, uncaught. ClassifyGroup
    // has already proven sourceId unique across both tables before calling this, so neither Add below can
    // observe a duplicate.
    private static void ClassifySource(Dictionary<string, AddonSourceShape> shapes, HashSet<string> unaddressable, FieldInfo field, string sourceId) {
        if (field.GetCustomAttribute<InputSourceUnaddressableAttribute>() is not null) {
            _ = unaddressable.Add(item: sourceId);
            return;
        }

        var declaredValue = field.GetCustomAttribute<InputSourceValueAttribute>();

        if (declaredValue is null) {
            return;
        }

        var shape = declaredValue.Kind switch {
            CommandValueKind.Digital => AddonSourceShape.Digital,
            CommandValueKind.Axis1D => AddonSourceShape.Axis1D,
            CommandValueKind.Axis2D => AddonSourceShape.Axis2D,
            _ => (AddonSourceShape?)null,
        };

        if (shape is null) {
            _ = unaddressable.Add(item: sourceId);
        } else {
            shapes.Add(
                key: sourceId,
                value: shape.Value
            );
        }
    }

    // The two open-ended keyboard families (InputSources.Keyboard.Letter/Function) mint a source id per
    // character/number rather than declaring one constant each; recognizing them means mirroring that shape
    // (a single lowercase letter, or "f" + the same 1..12 range WindowInputMapper accepts) rather than
    // declaring an unbounded number of constants in the reflected tables above.
    private static bool TryResolveParametric(string sourceId, out AddonSourceShape shape) {
        shape = default;

        const string KeyboardPrefix = "keyboard.";

        if (!sourceId.StartsWith(
            value: KeyboardPrefix,
            comparisonType: StringComparison.Ordinal
        )) {
            return false;
        }

        var suffix = sourceId.AsSpan(start: KeyboardPrefix.Length);

        if (
            (suffix.Length == 1) &&
            char.IsAsciiLetterLower(c: suffix[0])
        ) {
            shape = AddonSourceShape.Digital;
            return true;
        }

        if (
            (suffix.Length >= 2) &&
            (suffix[0] == 'f') &&
            TryParseFunctionKeyNumber(
            digits: suffix[1..],
            number: out var number
        ) &&
            (number >= 1) &&
            (number <= 12)
        ) {
            shape = AddonSourceShape.Digital;
            return true;
        }

        return false;
    }

    // Accepts exactly the canonical digit forms "1".."12" — digits only, no sign, no leading/trailing
    // whitespace, no leading zero, culture-invariant. int.TryParse's default NumberStyles.Integer permits
    // AllowLeadingSign (so "keyboard.f+1" resolved) and is CultureInfo.CurrentCulture-dependent; a hand-rolled
    // digit walk is the only way to accept "1".."12" and nothing else regardless of the host's culture.
    private static bool TryParseFunctionKeyNumber(ReadOnlySpan<char> digits, out int number) {
        number = 0;

        if (
            (digits.Length is not (1 or 2)) ||
            !char.IsAsciiDigit(c: digits[0]) ||
            ((digits.Length == 2) && !char.IsAsciiDigit(c: digits[1]))
        ) {
            return false;
        }

        if (
            (digits.Length == 2) &&
            (digits[0] == '0')
        ) {
            return false;
        }

        number = ((digits.Length == 1)
            ? (digits[0] - '0')
            : (((digits[0] - '0') * 10) + (digits[1] - '0')));
        return true;
    }
}
