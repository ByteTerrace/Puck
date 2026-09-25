namespace Puck.World;

/// <summary>The schedule output directory a boot's <c>--schedule-dir</c> names. A document carries no output path of
/// its own, so two runs of the same document write sibling directories without two document copies. Immutable; the
/// boot registers one and every consumer takes it from the service collection.</summary>
/// <remarks>The directory is also what arms the section: <see cref="WorldScheduleRunner"/> submits a row only when
/// <see cref="IsArmed"/>, so a boot that did not ask for schedule output runs no scheduled command at all.</remarks>
public sealed class WorldScheduleRoot {
    private readonly string? m_directory;

    /// <summary>Initializes a new instance of the <see cref="WorldScheduleRoot"/> class.</summary>
    /// <param name="path">The schedule output directory the boot names (created on first use), made absolute here, or
    /// <see langword="null"/> for a boot that arms no schedule.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    public WorldScheduleRoot(string? path) {
        if (path is not null) {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        }

        m_directory = ((path is null)
            ? null
            : Path.GetFullPath(path: path)
        );
    }

    /// <summary>Gets the rooted schedule output directory this boot armed.</summary>
    /// <exception cref="InvalidOperationException">This boot armed no schedule.</exception>
    public string Directory => (m_directory ?? throw new InvalidOperationException(message: "this boot armed no schedule directory"));
    /// <summary>Gets a value indicating whether this boot armed the <c>schedule</c> section.</summary>
    public bool IsArmed => (m_directory is not null);

    /// <summary>Returns the refusal a verb that writes, re-reads or rewinds the running document takes inside an
    /// armed scheduled run, or <see langword="null"/> when this boot runs no schedule.</summary>
    /// <param name="definition">The running document.</param>
    /// <param name="verb">The verb's own name, for the refusal line.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    /// <remarks>A schedule has no cursor: which rows have been submitted is this process's own state, never the
    /// document's, so a restored, re-read or rewound world runs every row again from tick 1 and the export the run
    /// was asked for measures a different trajectory. Refused by name rather than half-supported.</remarks>
    public string? RefuseInsideArmedRun(WorldDefinition definition, string verb) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ((IsArmed && (definition.Schedule is not null))
            ? $"[{verb}: refused — this boot armed the document's schedule, and a scheduled run carries no cursor: which rows have been submitted is process state, not document state, so a restored or re-read world would submit every row again from tick 1. A test run is not resumable; nothing done]"
            : null
        );
    }
}
