namespace Puck.World.Protocol;

/// <summary>A guest-facing reference into one <c>Server.WorldHandleTable</c> slot — an index plus the generation its
/// slot carried when this value was minted, stamped with the identity of the table that minted it. Only the index
/// (and, once channels carry one, the generation) is meant to ever cross to a guest; the generation is how a
/// resolve tells a handle minted before a rebuild apart from a fresh one that happens to reuse the same index — see
/// <c>Server.WorldHandleTable</c>'s own remarks.
/// <para><b>A handle is bound to the table that minted it.</b> <see cref="TablePrincipal"/> and
/// <see cref="TableCapability"/> never cross to a guest and are never guest-supplied — the host alone stamps them at
/// mint time and checks them at resolve time. Without them, a bare <c>(Index, Generation)</c> pair is, by
/// construction, interchangeable across every principal's and every capability's table: every table's generation
/// counter starts at 0 and climbs slowly, so two different tables' same-index slots collide on generation far more
/// often than not, and a resolve would silently answer whatever the wrong table's matching slot holds. Stamping the
/// table's own identity into the value turns a mismatched resolve into a verification failure instead of a silent
/// hit.</para>
/// <para>Declared in Protocol (not Server) so a query-answer payload can carry one to a caller that never sees a
/// live <c>Server.WorldHandleTable</c> reference — <c>Client.PlayerRoster.DriveTarget</c> caches this value across
/// ticks and re-resolves it through <see cref="Puck.World.Protocol.WorldQuery.GrantHandleResolve"/> rather than
/// holding the table itself.</para></summary>
/// <param name="Index">The 0-based slot index.</param>
/// <param name="Generation">The slot's generation at mint time.</param>
/// <param name="TablePrincipal">The principal of the handle table that minted this handle.</param>
/// <param name="TableCapability">The capability of the handle table that minted this handle.</param>
public readonly record struct WorldHandle(int Index, int Generation, WorldPrincipal TablePrincipal, WorldCapability TableCapability);
