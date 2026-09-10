using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>
/// The numeric/boolean/enum DELTA walker <c>world.row.step</c> addresses a leaf through — one level deeper than
/// <see cref="WorldRowCommandModule"/>'s own section table, which resolves a whole ROW; this walks INTO it, to one
/// member, and applies a DELTA rather than assigning a literal. Mutates <c>root</c> (a row's live JSON node, from
/// <see cref="WorldRowCommandModule"/>'s per-section reader) IN PLACE through <see cref="WorldRowFieldPath"/>, which
/// owns the path GRAMMAR and tree walk (including its <c>[field=value]</c> selector arm) shared with
/// <see cref="WorldRowCommandModule"/>'s literal set/add/remove/read forms.
/// </summary>
/// <remarks>
/// <para><b>Field-type semantics</b>: a number ADDS <c>delta</c>, typed by the leaf's declared CLR type (walked by
/// plain reflection off the row's own <c>Type</c>, never by how JSON spelled the value) — an integer field steps in
/// exact integer arithmetic, a float/double/decimal field in floating point, so a fractional step on a whole-numbered
/// float lands and an out-of-range integer step refuses by name rather than throwing; a JSON boolean TOGGLES on any
/// nonzero delta; a JSON string whose declared CLR type (walked
/// by plain reflection off the row's own <c>Type</c> — a row's own field types, never a JsonTypeInfo, since the leaf
/// needs an <see cref="Type.IsEnum"/> answer no JSON metadata carries) is an enum CYCLES forward/backward by
/// <c>delta</c>'s sign, wrapping, spelled exactly as the row's own C# member (the wire's one enum spelling — see
/// <see cref="Puck.Assets.Documents.DocumentJsonOptions.Shared"/>'s remarks). Anything else (an array/object leaf —
/// every <c>Vector2</c>/<c>Vector3</c>/<c>Quaternion</c> included — or a non-enum string) refuses by name.</para>
/// </remarks>
public static class WorldRowFieldStepper {
    // The CLR floating-point families a numeric leaf steps in floating point (a fractional delta lands, and a whole
    // value stays a float rather than snapping to an integer). Nullable<T> is already unwrapped by TryResolveClrType.
    private static bool IsFloatingClrType(Type type) =>
        ((type == typeof(float)) ||
        (type == typeof(double)) ||
        (type == typeof(decimal)) ||
        (type == typeof(Half)));
    // The CLR integer families a numeric leaf steps in integer arithmetic — the current value stays EXACT (never
    // routed through a float that loses precision past 2^24) and the delta is rounded to whole steps.
    private static bool IsIntegralClrType(Type type) =>
        ((type == typeof(byte)) ||
        (type == typeof(sbyte)) ||
        (type == typeof(short)) ||
        (type == typeof(ushort)) ||
        (type == typeof(int)) ||
        (type == typeof(uint)) ||
        (type == typeof(long)) ||
        (type == typeof(ulong)) ||
        (type == typeof(nint)) ||
        (type == typeof(nuint)));

    /// <summary>Steps the field at <paramref name="fieldPath"/> inside <paramref name="root"/> (a row's own live
    /// JSON node) by <paramref name="delta"/>, mutating the node in place.</summary>
    /// <param name="root">The row's JSON node (the whole row, for a keyed section; the whole section, for a keyless
    /// one) — mutated in place on success.</param>
    /// <param name="rowType">The row's own CLR type — the reflection root an enum leaf's vocabulary resolves
    /// through.</param>
    /// <param name="fieldPath">The dotted/indexed path to the field, relative to <paramref name="root"/>.</param>
    /// <param name="delta">The step. Added to a number; toggles a boolean on any nonzero value; cycles an enum by its
    /// sign.</param>
    /// <param name="oldText">The field's value before the step, on success.</param>
    /// <param name="newText">The field's value after the step, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryStep(JsonNode root, Type rowType, string fieldPath, float delta, out string oldText, out string newText, out string? error) {
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: rowType);

        oldText = string.Empty;
        newText = string.Empty;

        if (!float.IsFinite(f: delta)) {
            error = "delta must be a finite number";

            return false;
        }

        if (!WorldRowFieldPath.TryParse(
            error: out error,
            path: fieldPath,
            segments: out var segments
        )) {
            return false;
        }

        var last = segments[^1];

        if (!WorldRowFieldPath.TryNavigate(
            container: out var container,
            error: out error,
            path: fieldPath,
            root: root,
            segments: segments.AsSpan(start: 0, length: (segments.Length - 1))
        )) {
            return false;
        }

        if (!WorldRowFieldPath.TryGetLeaf(
            container: container!,
            error: out error,
            last: last,
            leaf: out var leaf,
            path: fieldPath
        )) {
            return false;
        }

        JsonNode replacement;

        switch (leaf!.GetValueKind()) {
            case JsonValueKind.True or JsonValueKind.False: {
                    var current = leaf.GetValue<bool>();
                    var applied = ((delta != 0f) ? !current : current);

                    oldText = (current ? "true" : "false");
                    newText = (applied ? "true" : "false");
                    replacement = JsonValue.Create(value: applied)!;

                    break;
                }
            case JsonValueKind.Number: {
                    // Type the numeric step by the field's REAL CLR type, never by how JSON SPELLED the value:
                    // SerializeToNode renders a float 8f as the integer literal `8`, so keying on the JSON kind would step
                    // a float field in integer arithmetic (8 - 0.4 rounds back to 8, a silent no-op). The CLR type is
                    // authoritative — an integer field steps exactly in integer space, a float field in floating point.
                    _ = WorldRowFieldPath.TryResolveClrType(
                        error: out _,
                        leafType: out var numericType,
                        rowType: rowType,
                        segments: segments
                    );

                    // When reflection cannot reach the leaf's CLR type (a member no property walk resolves), fall back to
                    // the JSON spelling — best effort, the only signal left.
                    var stepInteger = ((numericType is { } resolved)
                        ? IsIntegralClrType(type: resolved)
                        : leaf.AsValue().TryGetValue<long>(value: out _)
                    );

                    if (stepInteger) {
                        // Round the delta to whole steps and add in integer space so the current value stays exact past
                        // 2^24. A delta or sum outside long's range refuses by name rather than throwing OverflowException
                        // up through the dispatcher (which catches nothing) — a malformed step submits nothing.
                        long currentLong;
                        long appliedLong;

                        try {
                            currentLong = leaf.GetValue<long>();
                            appliedLong = checked((currentLong + ((long)Math.Round(mode: MidpointRounding.AwayFromZero, value: delta))));
                        } catch (Exception exception) when ((exception is OverflowException or FormatException or InvalidOperationException)) {
                            error = $"'{fieldPath}': integer step out of range (delta {delta.ToString(format: "0.####", provider: CultureInfo.InvariantCulture)})";

                            return false;
                        }

                        oldText = currentLong.ToString(provider: CultureInfo.InvariantCulture);
                        newText = appliedLong.ToString(provider: CultureInfo.InvariantCulture);
                        replacement = JsonValue.Create(value: appliedLong)!;
                    } else {
                        var currentDouble = leaf.GetValue<double>();
                        var appliedDouble = (currentDouble + delta);

                        if (!double.IsFinite(d: appliedDouble)) {
                            error = $"'{fieldPath}': step result is not finite (current {currentDouble.ToString(format: "0.####", provider: CultureInfo.InvariantCulture)}, delta {delta.ToString(format: "0.####", provider: CultureInfo.InvariantCulture)})";

                            return false;
                        }

                        oldText = currentDouble.ToString(format: "0.####", provider: CultureInfo.InvariantCulture);
                        newText = appliedDouble.ToString(format: "0.####", provider: CultureInfo.InvariantCulture);
                        replacement = JsonValue.Create(value: appliedDouble)!;
                    }

                    break;
                }
            case JsonValueKind.String: {
                    if (
                        !WorldRowFieldPath.TryResolveClrType(
                            error: out error,
                            leafType: out var leafType,
                            rowType: rowType,
                            segments: segments
                        ) ||
                        (leafType is not { IsEnum: true })
                    ) {
                        error ??= $"'{fieldPath}': not a steppable field (number, boolean, or named enum only)";

                        return false;
                    }

                    var names = Enum.GetNames(enumType: leafType);
                    var current = leaf.GetValue<string>();
                    var index = Array.IndexOf(
                        array: names,
                        value: current
                    );

                    if (index < 0) {
                        error = $"'{fieldPath}': current value '{current}' is not a recognized {leafType.Name} member";

                        return false;
                    }

                    var direction = MathF.Sign(x: delta);

                    if (direction == 0) {
                        error = $"'{fieldPath}': delta must be nonzero to cycle an enum";

                        return false;
                    }

                    var appliedIndex = ((((index + direction) % names.Length) + names.Length) % names.Length);

                    oldText = current;
                    newText = names[appliedIndex];
                    replacement = JsonValue.Create(value: names[appliedIndex])!;

                    break;
                }
            default:
                error = $"'{fieldPath}': not a steppable field (number, boolean, or named enum only)";

                return false;
        }

        if (!WorldRowFieldPath.TrySetLeaf(
            container: container!,
            error: out error,
            last: last,
            path: fieldPath,
            replacement: replacement
        )) {
            return false;
        }

        error = null;

        return true;
    }
}
