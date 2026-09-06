using System.Text.Json;

namespace Puck.World.Server;

/// <summary>An optional installed-provider capability for bounded, read-only collection snapshots.</summary>
public interface IWorldConfiguredObservationProvider {
    /// <summary>Binds a read-only source without performing I/O. The returned source is owned by the composition.</summary>
    /// <param name="settings">Provider-specific query and disclosure policy.</param>
    /// <param name="maximumItems">Maximum complete snapshot size; exceeding it must refuse, never truncate.</param>
    /// <returns>The owned source.</returns>
    IWorldExtensionObservationSource BindObservation(JsonElement settings, int maximumItems);
}

/// <summary>A read-only source. It must cooperate with cancellation and never return a partial collection as complete.</summary>
public interface IWorldExtensionObservationSource : IDisposable {
    /// <summary>Gets the provider-declared read specialization, for host diagnostics only (never interpreted by the
    /// engine, which learns no cloud noun for it).</summary>
    string Kind { get; }
    /// <summary>Reads a complete detached snapshot outside the simulation. Failures retain the previous world projection.</summary>
    /// <param name="cancellationToken">Host lifetime and request deadline.</param>
    /// <returns>Stable keyed items, with only fields approved for world disclosure.</returns>
    ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>One external collection item. Fields are bounded textual values; this projection makes no universal SDK ontology.</summary>
/// <param name="Key">Stable, case-sensitive Puck cell key.</param>
/// <param name="Fields">Provider-approved fields. Credentials and arbitrary response bodies must never enter this map.</param>
public sealed record WorldExtensionObservationItem(string Key, IReadOnlyDictionary<string, string> Fields);

/// <summary>Maps a provider collection into existing authored state rows of any cell kind, and nothing else.</summary>
/// <param name="Name">Unique observation name.</param>
/// <param name="Provider">Configured provider instance.</param>
/// <param name="Client">Configured acting principal.</param>
/// <param name="Settings">Provider-specific source settings.</param>
/// <param name="Fields">Provider field name to existing state-row name; each row is exclusively owned by this
/// projection and its cells are parsed by the row's own <see cref="CellKind"/> (Int: an invariant integer; Fixed:
/// an invariant decimal to Q48.16; Bool: true/false or 1/0; Text: as authored).</param>
/// <param name="RefreshTicks">Minimum simulation ticks between read attempts, including failures.</param>
/// <param name="MaximumItems">Complete collection ceiling, 1–128. Rendering admission can impose a smaller practical limit.</param>
public sealed record WorldExtensionObservationSettings(string Name, string Provider, string Client, JsonElement Settings,
    IReadOnlyDictionary<string, string> Fields, int RefreshTicks = 14400, int MaximumItems = 64);

/// <summary>Host read-back for collection freshness and the amount of projected work. Counts describe observed items, not telemetry.</summary>
/// <param name="Name">Configured observation name.</param>
/// <param name="Kind">The bound source's own <see cref="IWorldExtensionObservationSource.Kind"/>.</param>
/// <param name="Items">Size of the last complete collection.</param>
/// <param name="ObservedTick">Tick when the last complete collection reached the pump, or null before the first read succeeds.</param>
/// <param name="Reading">Whether a source call is still running.</param>
/// <param name="Submissions">Batches submitted to ordinary authority admission, including refused attempts.</param>
/// <param name="Failure">Last source or projection exception type; never an arbitrary SDK message.</param>
/// <param name="LastRefusedField">The provider field name whose value last failed to parse into its row's cell kind,
/// or null once a complete projection has since parsed every field.</param>
/// <param name="Applied">Whether read-back has confirmed the last collection's projection.</param>
public sealed record WorldExtensionObservationStatus(string Name, string Kind, int Items, ulong? ObservedTick,
    bool Reading, long Submissions, string? Failure, string? LastRefusedField, bool Applied);
