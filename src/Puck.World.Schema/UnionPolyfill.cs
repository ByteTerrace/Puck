namespace Puck.World;

// The hand-written C# 15 basic union pattern (docs/campaign.md, "Compiled rule operands are a closed union"),
// polyfilled internally until .NET 11 supplies the real attribute/interface pair. The day the toolchain moves, the
// flip is deleting these two markers and switching on the carrier's Value directly wherever a case-type dispatch
// exists today; nothing else moves.
/// <summary>
/// Marks a closed discriminated union — either an abstract class/record whose only cases are its own nested sealed
/// classes/records (<see cref="WorldStateDomain"/>), or a hand-written carrier struct over a sealed class hierarchy
/// declared alongside it (<see cref="CompiledWorldOperand"/>, <see cref="CompiledWorldEffect"/>). Pattern-matching and,
/// for the authored-document cases, JSON polymorphism ride the ordinary sealed hierarchy and
/// <c>System.Text.Json</c>'s own <c>JsonPolymorphic</c>/<c>JsonDerivedType</c> attributes; this attribute carries no
/// behavior of its own. It exists only to mark the hierarchy CLOSED — every case is declared in the same file/assembly
/// and the base type's constructor is private or private protected, so no case can be added from outside — and to
/// give a mechanical grep target for "what is a union in this codebase", the way <c>[VerifiedCode]</c> gives one for
/// branded members. Shared here (rather than declared per-union) so every union in the codebase carries the identical
/// marker and a later real union-type feature has exactly one file to retire.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class UnionAttribute : Attribute;

/// <summary>The polyfilled stand-in for the future C# union pattern's own marker interface: a union carrier STRUCT
/// (never the class/record form of <see cref="UnionAttribute"/>, which needs no boxed indirection) exposes its one
/// boxed case through <see cref="Value"/>, untyped, for reflection or generic tooling that does not know the concrete
/// case type.</summary>
internal interface IUnion {
    /// <summary>The one live case, or <see langword="null"/> for a default-initialized carrier.</summary>
    object? Value { get; }
}
