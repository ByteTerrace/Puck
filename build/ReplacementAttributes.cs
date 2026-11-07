#nullable enable

namespace Puck;

/// <summary>
/// Marks a type whose live instance a host can swap for a new one while code holds the old: the state arena a document
/// install or checkpoint restore replaces, and what is replaced with it. With no argument the type is its own root;
/// <c>[Replaceable(typeof(Root))]</c> says the instance is replaced whenever <c>Root</c>'s is (an arena's scratch goes
/// with its arena). <c>Puck.Analyzers</c> refuses a local or parameter of a marked type read after a call that can
/// replace its root (STALE001), so a reference captured before a replacement is never used after it.
/// <para>
/// The type is <c>internal</c> and linked into every project as source (see <c>Directory.Build.props</c>): the analyzer
/// matches it by name, so a production assembly's copy marks its types for every assembly that references them.
/// </para>
/// </summary>
/// <param name="root">The type whose replacement replaces this one; <see langword="null"/> when the type is its own root.</param>
[AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
internal sealed class ReplaceableAttribute(Type? root = null) : Attribute {
    /// <summary>The type whose replacement replaces this one, or <see langword="null"/> when the type is its own root.</summary>
    public Type? Root { get; } = root;
}

/// <summary>
/// Marks a member that can swap the live instance of a <c>[Replaceable]</c> root, directly or through what it calls:
/// after it returns, a reference to the root (or to anything replaced with it) taken before the call may name the old
/// instance. <c>Puck.Analyzers</c> follows calls inside an assembly on its own; the mark carries the fact across an
/// assembly boundary and through virtual dispatch, so it is required on a member visible outside its assembly, on a type
/// that hands out the root, that reaches a replacement, and on the member an override or implementation that reaches one
/// overrides or implements (STALE002), and on a method that assigns the field a property serves outside a constructor (STALE003). A member of an
/// assembly that cannot see the root (a hosting or actor contract below the state projects) names
/// <c>typeof(object)</c>, which covers every root.
/// </summary>
/// <param name="root">The <c>[Replaceable]</c> root type the member can replace, or a type every root it can replace
/// derives from.</param>
[AttributeUsage(validOn: AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor, Inherited = false, AllowMultiple = true)]
internal sealed class ReplacesAttribute(Type root) : Attribute {
    /// <summary>The root type the member can replace.</summary>
    public Type Root { get; } = root;
}

/// <summary>
/// Marks a static method that builds a new object and restores state into it before returning it, so the replacement it
/// reaches belongs to the new object, which no one holds yet: construction, the same as a constructor, where
/// <c>Puck.Analyzers</c> already ignores what the constructor swaps. A call to it replaces nothing a caller captured, so
/// the analyzer does not record it. Never put it on a method that restores into an object its caller passed in or
/// already holds.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
internal sealed class ConstructsAttribute : Attribute;
