using System.Reflection;

namespace Puck.Maths.Tests;

/// <summary>
/// Structural claims over the shape of <c>Puck.Maths</c>' public surface. The declarations in <see cref="LawRegistry"/>
/// invoke these methods as Default-tier laws.
/// </summary>
internal static class PublicTableClaims {
    /// <summary>
    /// Finds every publicly reachable static field or property that hands a caller an array, and every publicly
    /// reachable static field that can be reassigned.
    /// </summary>
    /// <returns>The offenders by name, or null when none exists.</returns>
    /// <remarks>
    /// Any caller can write an element of a shared array, and any caller can replace a static field that is not
    /// read-only, so either one lets one consumer change what every other consumer reads from a deterministic table. A
    /// table is exposed as a <see cref="ReadOnlySpan{T}"/> over constant data, or as a method that returns a read-only
    /// view. A static method that returns a fresh array each call hands out no shared state and is not examined.
    /// </remarks>
    public static string? PublicStaticTablesAreNotMutable() {
        const BindingFlags Statics = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Static;
        var offenders = new List<string>();

        foreach (var type in typeof(FixedQ4816).Assembly.GetExportedTypes()) {
            foreach (var field in type.GetFields(bindingAttr: Statics)) {
                if (field.IsLiteral) { continue; }
                if (field.FieldType.IsArray) { offenders.Add(item: $"{type.FullName}.{field.Name} is a public static array field"); } else if (!field.IsInitOnly) { offenders.Add(item: $"{type.FullName}.{field.Name} is a public static field that is not read-only"); }
            }
            foreach (var property in type.GetProperties(bindingAttr: Statics)) {
                if (property.PropertyType.IsArray && (property.GetMethod is not null)) {
                    offenders.Add(item: $"{type.FullName}.{property.Name} is a public static array property");
                }
            }
        }
        offenders.Sort(comparison: StringComparer.Ordinal.Compare);
        return ((offenders.Count == 0) ? null : string.Join(separator: "; ", values: offenders));
    }
}
