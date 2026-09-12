namespace Puck.GamingBricks.Forge;

/// <summary>Conservative effects shared by cartridge capacity analysis and frame costing.</summary>
public static class CartridgeEffects {
    /// <summary>Adds queue bounds, saturating at the first impossible capacity.</summary>
    /// <param name="left">The first nonnegative bound.</param>
    /// <param name="right">The second nonnegative bound.</param>
    /// <returns>The sum, capped at queue capacity plus one.</returns>
    public static int AddMapWrites(int left, int right) => (int)Math.Min(CartridgeLimits.MapWriteCount + 1L, (long)left + right);

    /// <summary>Bounds the map writes a validated statement tree can execute.</summary>
    /// <param name="statements">The statement tree.</param>
    /// <returns>The bound, saturating at queue capacity plus one.</returns>
    public static int MapWrites(CartridgeStatement[]? statements) {
        var total = 0;
        foreach (var statement in statements ?? []) {
            var count = statement?.Kind switch {
                "map" => 1,
                "if" => Math.Max(MapWrites(statement.Then), MapWrites(statement.Else)),
                "repeat" => Math.Min(CartridgeLimits.MapWriteCount + 1,
                    Math.Clamp(statement.Count ?? 0, 0, CartridgeLimits.RepeatCount) * MapWrites(statement.Body)),
                _ => 0,
            };
            total = AddMapWrites(total, count);
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
    public static bool Writes(CartridgeStatement[]? statements, string name, CartridgeDocument? document = null) {
        foreach (var statement in statements ?? []) {
            if (statement is null) {
                continue;
            }
            var implicitWrite = statement.Kind switch {
                "load" => document is null || (document.Save?.Variables?.Contains(name, StringComparer.Ordinal) ?? false),
                "clock" => document is null || document.Clock is { } clock &&
                    new[] { clock.Seconds, clock.Minutes, clock.Hours, clock.Day, clock.Month, clock.Year }.Contains(name, StringComparer.Ordinal),
                _ => false,
            };
            if (implicitWrite || statement.Index == name ||
                statement.Target is { Key: null } target && target.State == name ||
                Writes(statement.Then, name, document) || Writes(statement.Else, name, document) || Writes(statement.Body, name, document)) {
                return true;
            }
        }
        return false;
    }
}
