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
    /// <summary>Reads a complete detached snapshot outside the simulation. Failures retain the previous world projection.</summary>
    /// <param name="cancellationToken">Host lifetime and request deadline.</param>
    /// <returns>Stable keyed items, with only fields approved for world disclosure.</returns>
    ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>One external collection item. Fields are bounded textual values; this projection makes no universal SDK ontology.</summary>
/// <param name="Key">Stable, case-sensitive Puck cell key.</param>
/// <param name="Fields">Provider-approved fields. Credentials and arbitrary response bodies must never enter this map.</param>
public sealed record WorldExtensionObservationItem(string Key, IReadOnlyDictionary<string, string> Fields);

/// <summary>Maps a provider collection into existing authored text tables and optional static placements.</summary>
/// <param name="Name">Unique observation name.</param>
/// <param name="Provider">Configured provider instance.</param>
/// <param name="Client">Configured acting principal.</param>
/// <param name="Settings">Provider-specific source settings.</param>
/// <param name="Fields">Provider field name to existing text-table name; each table is exclusively owned by this projection.</param>
/// <param name="Placements">Optional author-selected static representation.</param>
/// <param name="RefreshTicks">Minimum simulation ticks between read attempts, including failures.</param>
/// <param name="MaximumItems">Complete collection ceiling, 1–128. Rendering admission can impose a smaller practical limit.</param>
public sealed record WorldExtensionObservationSettings(string Name, string Provider, string Client, JsonElement Settings,
    IReadOnlyDictionary<string, string> Fields, WorldExtensionObservationPlacements? Placements = null,
    int RefreshTicks = 14400, int MaximumItems = 64);

/// <summary>Instances of an existing authored placement template, laid out relative to its frame. The template itself remains visible.</summary>
/// <param name="Template">Existing static placement whose prototype and facets are copied.</param>
/// <param name="Prefix">Exclusively reserved prefix for generated placement IDs.</param>
/// <param name="Columns">Number of grid columns.</param>
/// <param name="SpacingX">Positive local X distance between columns.</param>
/// <param name="SpacingZ">Positive local Z distance between rows; the first row starts one spacing beyond the template.</param>
/// <param name="VariantField">Optional item field selecting a prototype from Variants.</param>
/// <param name="Variants">Exact field values to existing authored prototype IDs; unmatched values use Prototype or the template prototype.</param>
/// <param name="Prototype">Optional default prototype, allowing the template to be a distinct courtyard or landmark.</param>
public sealed record WorldExtensionObservationPlacements(string Template, string Prefix, int Columns,
    float SpacingX, float SpacingZ, string? VariantField = null, IReadOnlyDictionary<string, string>? Variants = null, string? Prototype = null);

/// <summary>Host read-back for collection freshness and the amount of projected work. Counts describe observed items, not telemetry.</summary>
/// <param name="Name">Configured observation name.</param>
/// <param name="Items">Size of the last complete collection.</param>
/// <param name="ObservedTick">Tick when the last complete collection reached the pump, or null before the first read succeeds.</param>
/// <param name="Reading">Whether a source call is still running.</param>
/// <param name="Submissions">Batches submitted to ordinary authority admission, including refused attempts.</param>
/// <param name="Failure">Last source or projection exception type; never an arbitrary SDK message.</param>
/// <param name="Applied">Whether read-back has confirmed the last collection's projection.</param>
public sealed record WorldExtensionObservationStatus(string Name, int Items, ulong? ObservedTick,
    bool Reading, long Submissions, string? Failure, bool Applied);
