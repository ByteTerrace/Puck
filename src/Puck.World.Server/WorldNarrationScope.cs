namespace Puck.World.Server;

/// <summary>
/// The row label ambient for engine narration written to <see cref="Console.Out"/>/<see cref="Console.Error"/>
/// while it is set — the desktop leaves it unset for every write, and installs no reader; a host running several
/// rows sets it around each row's own work and installs a writer that tags every line by the scope active when it
/// was written.
/// </summary>
public static class WorldNarrationScope {
    private static readonly AsyncLocal<string?> s_current = new();

    /// <summary>Gets or sets the row label ambient on the current logical call context, or <see langword="null"/>
    /// outside any row's own work.</summary>
    public static string? Current {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}
