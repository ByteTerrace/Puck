using System.Numerics;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldConfiguredExtensions {
    private sealed class Observation(WorldExtensionObservationSettings settings, WorldExtensionClient client,
        IWorldExtensionObservationSource source) {
        public WorldExtensionObservationSettings Settings { get; } = settings;
        public WorldExtensionClient Client { get; } = client;
        public IWorldExtensionObservationSource Source { get; } = source;
        public Task<IReadOnlyList<WorldExtensionObservationItem>>? Pending;
        public IReadOnlyList<WorldExtensionObservationItem>? Items;
        public ulong? Started;
        public ulong? Observed;
        public string? Failure;
        public long Submissions;
        public bool NeedsProjection;
    }
    private readonly List<Observation> m_observations = [];

    /// <summary>Gets detached host-only source diagnostics, including last successful observation tick and failures.</summary>
    public IReadOnlyList<WorldExtensionObservationStatus> Observations => m_observations.Select(source =>
        new WorldExtensionObservationStatus(source.Settings.Name, source.Items?.Count ?? 0, source.Observed,
            source.Pending is { IsCompleted: false }, source.Submissions, source.Failure, source.Items is not null && !source.NeedsProjection)).ToArray();

    private void ConfigureObservations(Dictionary<string, IWorldConfiguredProvider> providers, HashSet<string> outputs) {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new List<string>();
        if (m_configuration.Observations?.Count > 16) { throw new ArgumentException("At most sixteen observation sources are allowed."); }
        foreach (var settings in m_configuration.Observations ?? []) {
            ValidateName(settings.Name);
            if (!names.Add(settings.Name) || settings.MaximumItems is < 1 or > 128 || settings.RefreshTicks <= 0 ||
                settings.Fields.Count is < 1 or > 16) { throw new ArgumentException("Invalid observation name or budget."); }
            var client = Client(ParsePrincipal(settings.Client));
            foreach (var field in settings.Fields) {
                ValidateName(field.Key);
                RequireTable(field.Value, CellKind.Text);
                _ = ReadTable(client, field.Value, CellKind.Text);
                var row = m_server.Definition.State.First(row => row.Name.Value == field.Value);
                if (!outputs.Add(field.Value) || row.Capacity < settings.MaximumItems) { throw new ArgumentException("Observation tables need exclusive ownership and sufficient capacity."); }
            }
            if (settings.Placements is { } placement) {
                ValidateName(placement.Prefix);
                if (placement.VariantField is { } variantField) { ValidateName(variantField); }
                if (placement.Variants is { } variants && (variants.Count > 128 || placement.VariantField is null)) {
                    throw new ArgumentException("Observation variants need a selector field and at most 128 choices.");
                }
                if (prefixes.Any(prefix => prefix.StartsWith(placement.Prefix, StringComparison.Ordinal) || placement.Prefix.StartsWith(prefix, StringComparison.Ordinal)) ||
                    placement.Columns is < 1 or > 128 || !float.IsFinite(placement.SpacingX) || !float.IsFinite(placement.SpacingZ) ||
                    placement.SpacingX <= 0 || placement.SpacingZ <= 0) { throw new ArgumentException("Invalid observation placement layout or overlapping prefix."); }
                prefixes.Add(placement.Prefix);
                _ = ObservationTemplate(placement);
                foreach (var prototype in (placement.Variants?.Values ?? []).Append(placement.Prototype).Where(id => id is not null)) {
                    if (!m_server.Definition.Creations.Any(row => row.Id.Value == prototype)) { throw new ArgumentException("Observation variant must name an authored prototype."); }
                }
                // Prefix ownership is explicit deployment authority. Never adopt unrelated authored placements.
                if (m_server.Definition.Placements.Any(row => row.Id.StartsWith(placement.Prefix, StringComparison.Ordinal))) {
                    var keys = ReadTable(client, settings.Fields.First().Value, CellKind.Text).Select(cell => placement.Prefix + cell.Key).ToHashSet(StringComparer.Ordinal);
                    if (m_server.Definition.Placements.Any(row => row.Id.StartsWith(placement.Prefix, StringComparison.Ordinal) && !keys.Contains(row.Id))) {
                        throw new ArgumentException("Observation prefix collides with an existing placement.");
                    }
                }
            }
            if (!providers.TryGetValue(settings.Provider, out var provider) || provider is not IWorldConfiguredObservationProvider factory) {
                throw new ArgumentException("Selected provider does not support collection observations.");
            }
            m_observations.Add(new(settings, client, factory.BindObservation(settings.Settings, settings.MaximumItems)));
        }
    }

    private WorldPlacement ObservationTemplate(WorldExtensionObservationPlacements settings) {
        var template = m_server.Definition.Placements.SingleOrDefault(row => row.Id == settings.Template)
            ?? throw new InvalidOperationException("Observation placement template is missing.");
        if (template.Scale != 1 || template.Attach is not null || template.Inhabit is not null ||
            template.Distribution is not null || template.Mirror is not null || template.Board is not null || template.FaceSources is not null ||
            template.Contribution is not null || template.Respond is not null || template.Emission is not null) {
            throw new InvalidOperationException("Observation template must be an ordinary static placement at unit scale.");
        }
        return template;
    }

    private void PumpObservations(ulong tick) {
        foreach (var observation in m_observations) {
            if (!observation.Client.Runtime.IsActive) { continue; }
            if (observation.Pending is { IsCompleted: true } pending) {
                observation.Pending = null;
                try {
                    observation.Items = pending.GetAwaiter().GetResult();
                    observation.Observed = tick;
                    observation.Failure = null;
                    observation.NeedsProjection = true;
                } catch (Exception exception) { observation.Failure = exception.GetType().Name; }
            }
            if (observation.NeedsProjection && observation.Items is { } items) {
                try { ProjectObservation(observation, items); }
                catch (Exception exception) { observation.Failure = exception.GetType().Name; }
            }
            if (observation.Pending is null && (observation.Started is null || tick - observation.Started.Value >= (ulong)observation.Settings.RefreshTicks)) {
                observation.Started = tick;
                observation.Pending = Task.Run(async () => {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(m_stop.Token);
                    deadline.CancelAfter((m_configuration.Worker ?? WorldExtensionHostOptions.Default).OperationTimeout);
                    var items = await observation.Source.ReadAsync(deadline.Token).ConfigureAwait(false);
                    if (items.Count > observation.Settings.MaximumItems) { throw new InvalidOperationException("Observation exceeds its complete snapshot budget."); }
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    var copy = new List<WorldExtensionObservationItem>(items.Count);
                    foreach (var item in items) {
                        ValidateName(item.Key);
                        if (!keys.Add(item.Key) || item.Fields.Count > 32 || item.Fields.Any(field => field.Value is null || field.Value.Length > 4096) ||
                            observation.Settings.Fields.Keys.Any(field => !item.Fields.ContainsKey(field))) {
                            throw new InvalidOperationException("Invalid or oversized observation item.");
                        }
                        copy.Add(new(item.Key, new Dictionary<string, string>(item.Fields, StringComparer.Ordinal)));
                    }
                    return (IReadOnlyList<WorldExtensionObservationItem>)copy.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
                });
            }
        }
    }

    private void ProjectObservation(Observation observation, IReadOnlyList<WorldExtensionObservationItem> items) {
        var principal = observation.Client.Principal;
        var mutations = new List<WorldMutation>();
        foreach (var field in observation.Settings.Fields) {
            RequireTable(field.Value, CellKind.Text);
            var actual = ReadTable(observation.Client, field.Value, CellKind.Text);
            var cells = items.Select(item => new StateCell(CellName.Parse(item.Key), Text: item.Fields.TryGetValue(field.Key, out var text)
                ? text : throw new InvalidOperationException("Observation omitted a required field."))).ToArray();
            if (actual.Count == cells.Length && actual.Zip(cells).All(pair => pair.First.Key == pair.Second.Key.Value && pair.First.Text == pair.Second.Text)) { continue; }
            var row = m_server.Definition.State.First(row => row.Name.Value == field.Value);
            mutations.Add(new WorldMutation.UpsertStateRow(principal, row with { Cells = cells }));
        }
        if (observation.Settings.Placements is { } settings) {
            var template = ObservationTemplate(settings);
            var desired = new HashSet<string>(StringComparer.Ordinal);
            var actual = m_server.Definition.Placements.ToDictionary(row => row.Id, StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++) {
                var item = items[index];
                var prototype = settings.Prototype ?? template.PrototypeId;
                if (settings.VariantField is { } variant && item.Fields.TryGetValue(variant, out var value) && settings.Variants?.TryGetValue(value, out var selected) == true) { prototype = selected; }
                var id = settings.Prefix + item.Key;
                desired.Add(id);
                var placement = template with { Id = id, Parent = template.Id, PrototypeId = prototype,
                    Position = new Vector3((index % settings.Columns) * settings.SpacingX, 0, (index / settings.Columns + 1) * settings.SpacingZ), YawDegrees = 0 };
                if (!actual.TryGetValue(id, out var existing) || existing != placement) { mutations.Add(new WorldMutation.UpsertPlacement(principal, placement)); }
            }
            foreach (var existing in actual.Values) {
                if (existing.Id.StartsWith(settings.Prefix, StringComparison.Ordinal) && !desired.Contains(existing.Id)) {
                    mutations.Add(new WorldMutation.RemovePlacement(principal, existing.Id));
                }
            }
        }
        if (mutations.Count == 0) { observation.NeedsProjection = false; return; }
        observation.Client.Submit(new WorldMutation.Batch(principal, mutations));
        observation.Submissions++;
        // Read-back on the next scan proves admission. Refused contributions must not poison the change cache.
    }

    /// <summary>Waits for current read-only source calls. Call Pump afterwards to admit their results.</summary>
    /// <param name="cancellationToken">Cancels this wait only.</param>
    /// <returns>Completion of the current calls; faults propagate to this explicit host wait.</returns>
    public Task FlushObservationsAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(m_observations.Where(source => source.Pending is not null).Select(source => source.Pending!)).WaitAsync(cancellationToken);
}
