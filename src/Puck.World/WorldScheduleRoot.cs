namespace Puck.World;

/// <summary>The boot-time override for <c>schedule.directory</c> — the <c>--state-dir</c>/<c>--capture-dir</c>
/// pattern applied to schedule output, so two runs of the same document can write sibling directories without two
/// document copies.</summary>
/// <remarks>The override is also what arms the section: <see cref="WorldScheduleRunner"/> submits a row only when
/// <see cref="IsArmed"/>, so a boot that did not ask for schedule output runs no scheduled command at all.</remarks>
internal static class WorldScheduleRoot {
    private static string? OverridePath;

    /// <summary>Gets a value indicating whether this boot armed the <c>schedule</c> section.</summary>
    public static bool IsArmed => (OverridePath is not null);

    /// <summary>Applies the boot-time override. Call at most once, before <see cref="WorldScheduleRunner"/> is
    /// constructed.</summary>
    /// <param name="path">The schedule output directory (created on first use).</param>
    /// <exception cref="InvalidOperationException">An override was already applied.</exception>
    public static void Override(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        if (OverridePath is not null) {
            throw new InvalidOperationException(message: "the schedule directory was already overridden this boot");
        }

        OverridePath = Path.GetFullPath(path: path);
    }
    /// <summary>Returns the refusal a verb that writes, re-reads or rewinds the running document takes inside an
    /// armed scheduled run, or <see langword="null"/> when this boot runs no schedule.</summary>
    /// <param name="definition">The running document.</param>
    /// <param name="verb">The verb's own name, for the refusal line.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    /// <remarks>A schedule has no cursor: which rows have been submitted is this process's own state, never the
    /// document's, so a restored, re-read or rewound world runs every row again from tick 1 and the export the run
    /// was asked for measures a different trajectory. Refused by name rather than half-supported.</remarks>
    public static string? RefuseInsideArmedRun(WorldDefinition definition, string verb) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ((IsArmed && (definition.Schedule is not null))
            ? $"[{verb}: refused — this boot armed the document's schedule, and a scheduled run carries no cursor: which rows have been submitted is process state, not document state, so a restored or re-read world would submit every row again from tick 1. A test run is not resumable; nothing done]"
            : null
        );
    }
    /// <summary>Resolves the effective schedule directory: the boot override when present, else
    /// <paramref name="authored"/> resolved against the current directory.</summary>
    /// <param name="authored">The document's <c>schedule.directory</c>.</param>
    /// <returns>The rooted directory path.</returns>
    public static string Resolve(string authored) =>
        (OverridePath ?? Path.GetFullPath(path: authored));
}
