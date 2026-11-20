#nullable enable

namespace Puck;

/// <summary>
/// Marks a field or property that holds a raw <c>System.Runtime.CompilerServices.ConditionalWeakTable</c> as a side
/// table: its values are not derived by code from their keys, so a code update (<c>Puck.Abstractions.CodeRevision</c>)
/// leaves them standing. A side table holds an attachment a writer records (a source origin, a refusal noted while
/// lowering), per-object state a code update must not drop (a pool, a guard's last valid values), or a value whose
/// identity live state was built from. A product of code goes through <c>Puck.Abstractions.CodeDerivedTable</c>
/// instead; <c>Puck.Analyzers</c> refuses a raw weak table field without this mark (CACHE001).
/// <para>
/// The type is <c>internal</c> and linked into every project as source (see <c>Directory.Build.props</c>): the analyzer
/// matches it by name.
/// </para>
/// </summary>
/// <param name="reason">The reason the table's values are not products of code.</param>
[AttributeUsage(validOn: AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
internal sealed class SideTableAttribute(string reason) : Attribute {
    /// <summary>Gets the reason the table's values are not products of code.</summary>
    public string Reason { get; } = reason;
}
