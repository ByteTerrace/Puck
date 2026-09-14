namespace Puck.GamingBricks.Forge;

/// <summary>Conservative effects shared by cartridge capacity analysis and frame costing.</summary>
public static class CartridgeEffects {
    // One conservative traversal serves both a single-name query and the guard analysis's bulk write collection.
    // True means the visitor stopped the walk, or an implicit writer without a document can affect any variable.
    internal static bool VisitWrites(CartridgeStatement[]? statements, Func<string, bool> visit, CartridgeDocument? document) {
        bool Visit(string? name) => ((name is not null) && visit(name));

        foreach (var statement in (statements ?? [])) {
            if (statement is null) {
                continue;
            }
            if (
                (statement.Kind is "load" or "clock") &&
                (document is null)
            ) {
                return true;
            }
            if (
                (statement.Kind == "load") &&
                (document?.Save?.Variables is { } saved)
            ) {
                foreach (var name in saved) {
                    if (Visit(name: name)) { return true; }
                }
            }
            if (
                (statement.Kind == "clock") &&
                (document?.Clock is { } clock) &&
                (Visit(name: clock.Seconds) || Visit(name: clock.Minutes) || Visit(name: clock.Hours) ||
                    Visit(name: clock.Day) || Visit(name: clock.Month) || Visit(name: clock.Year))
            ) {
                return true;
            }
            if (
                Visit(name: statement.Index) ||
                ((statement.Target is { Key: null } target) && Visit(name: target.State)) ||
                VisitWrites(
                statement.Then,
                visit,
                document
            ) ||
                VisitWrites(
                statement.Else,
                visit,
                document
            ) ||
                VisitWrites(
                statement.Body,
                visit,
                document
            )
            ) {
                return true;
            }
        }
        return false;
    }

    /// <summary>Adds queue bounds, saturating at the first impossible capacity.</summary>
    /// <param name="left">The first nonnegative bound.</param>
    /// <param name="right">The second nonnegative bound.</param>
    /// <returns>The sum, capped at queue capacity plus one.</returns>
    public static int AddMapWrites(int left, int right) => ((int)Math.Min(
        val1: (CartridgeLimits.MapWriteCount + 1L),
        val2: (((long)left) + right)
    ));
    /// <summary>Bounds the map writes a validated statement tree can execute.</summary>
    /// <param name="statements">The statement tree.</param>
    /// <returns>The bound, saturating at queue capacity plus one.</returns>
    public static int MapWrites(CartridgeStatement[]? statements) {
        var total = 0;

        foreach (var statement in (statements ?? [])) {
            var count = statement?.Kind switch {
                "map" => 1,
                "if" => Math.Max(
                val1: MapWrites(statements: statement.Then),
                val2: MapWrites(statements: statement.Else)
            ),
                "repeat" => Math.Min(
                val1: (CartridgeLimits.MapWriteCount + 1),
                val2: (Math.Clamp(
                    (statement.Count ?? 0),
                    0,
                    CartridgeLimits.RepeatCount
                ) * MapWrites(statements: statement.Body))
            ),
                _ => 0,
            };

            total = AddMapWrites(
                left: total,
                right: count
            );
        }
        return total;
    }
    /// <summary>Determines whether a statement tree may change a variable.</summary>
    /// <param name="statements">The statement tree.</param>
    /// <param name="name">The guard variable.</param>
    /// <param name="document">The document describing implicit destinations, or null for conservative analysis.</param>
    /// <returns>True for a direct assignment, loop-index update, or implicit state writer.</returns>
    /// <remarks>Load writes its saved variables and clock writes its declared destinations. A declared scene uses a
    /// snapshot and does not depend on this inference.</remarks>
    public static bool Writes(CartridgeStatement[]? statements, string name, CartridgeDocument? document = null) =>
        VisitWrites(
            document: document,
            statements: statements,
            visit: written => (written == name)
        );
}
