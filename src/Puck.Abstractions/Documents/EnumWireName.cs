using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Puck.Abstractions.Documents;

/// <summary>
/// The name an enum member crosses a wire under: the name its <see cref="JsonStringEnumMemberNameAttribute"/> gives
/// it, else its declared name — exactly what <see cref="StrictEnumConverter{TEnum}"/> writes. A writer that spells an
/// enum by hand (a console readout, a hand-built JSON object) reads the name here, so the attribute stays the one
/// spelling.
/// </summary>
/// <typeparam name="TEnum">The enum.</typeparam>
public static class EnumWireName<[DynamicallyAccessedMembers(memberTypes: DynamicallyAccessedMemberTypes.PublicFields)] TEnum> where TEnum : struct, Enum {
    private static readonly Dictionary<TEnum, string> Names = Build();

    /// <summary>Gets the wire name of a defined member.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its wire name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined member.</exception>
    public static string Of(TEnum value) =>
        (Names.TryGetValue(
            key: value,
            value: out var name
        )
            ? name
            : throw new ArgumentOutOfRangeException(
                actualValue: value,
                message: $"{value} is not a defined {typeof(TEnum).Name} member.",
                paramName: nameof(value)
            ));
    /// <summary>Finds the member a wire name spells.</summary>
    /// <param name="name">The wire name, compared ordinally.</param>
    /// <param name="value">The member; the default when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a member's wire name.</returns>
    public static bool TryParse(string name, out TEnum value) {
        foreach (var (member, wire) in Names) {
            if (string.Equals(
                a: wire,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                value = member;

                return true;
            }
        }

        value = default;

        return false;
    }

    private static Dictionary<TEnum, string> Build() {
        var names = new Dictionary<TEnum, string>();

        foreach (var field in typeof(TEnum).GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Static)) {
            names[((TEnum)field.GetValue(obj: null)!)] = (field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name);
        }

        return names;
    }
}
