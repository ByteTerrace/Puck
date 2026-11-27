namespace Puck.Commands;

/// <summary>
/// Resolves host-owned bindings that remain active independently of a game's current page or chord state. This is
/// the small terminal/navigation plane beside, rather than inside, authored gameplay bindings. The router asks it for
/// a source the seat's active page leaves unbound, and for every source while the seat's focus is released (a host
/// console open), so an authored context that binds one of its sources takes that source only while it holds.
/// </summary>
public interface IAlwaysActiveInputBindings {
    /// <summary>Returns the host bindings for a slot and provider-neutral source, or <see langword="null"/>. A returned
    /// list is immutable runtime data; return a different list to change the mapping.</summary>
    IReadOnlyList<CommandBinding>? Resolve(int slot, string source);
}
