namespace Puck.World;

/// <summary>
/// Marks an abstract sealed-hierarchy type as a closed union over its own nested sealed case records — the
/// hand-rolled precursor to a native C# discriminated union. Pattern-matching and JSON polymorphism ride the
/// ordinary sealed-record hierarchy and <c>System.Text.Json</c>'s own <c>JsonPolymorphic</c>/<c>JsonDerivedType</c>
/// attributes; this attribute carries no behavior of its own. It exists only to mark the hierarchy CLOSED — every
/// case is a nested sealed record and the base type's constructor is private, so no case can be added outside the
/// declaring type — and to give a mechanical grep target for "what is a union in this codebase", the way
/// <c>[VerifiedCode]</c> gives one for branded members. Shared here (rather than declared per-union) so every union
/// in the document model carries the identical marker and a later real union-type feature has exactly one file to
/// retire.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class UnionAttribute : Attribute;
