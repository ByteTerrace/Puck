using System.Globalization;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldConfiguredExtensions {
    private sealed class Observation(WorldExtensionObservationSettings settings, WorldExtensionClient client,
        IWorldExtensionObservationSource source) {
        public WorldExtensionObservationSettings Settings { get; } = settings;
        public WorldExtensionClient Client { get; } = client;
        public IWorldExtensionObservationSource Source { get; } = source;

        public string? Failure;
        public IReadOnlyList<WorldExtensionObservationItem>? Items;
        public string? LastRefusedField;
        public bool NeedsProjection;
        public ulong? Observed;
        public Task<IReadOnlyList<WorldExtensionObservationItem>>? Pending;
        public ulong? Started;
        public long Submissions;
    }

    private readonly List<Observation> m_observations = [];

    /// <summary>Gets detached host-only source diagnostics, including last successful observation tick and failures.</summary>
    public IReadOnlyList<WorldExtensionObservationStatus> Observations => m_observations.Select(selector: source =>
        new WorldExtensionObservationStatus(
        source.Settings.Name,
        source.Source.Kind,
        (source.Items?.Count ?? 0),
        source.Observed,
        (source.Pending is { IsCompleted: false }),
        source.Submissions,
        source.Failure,
        source.LastRefusedField,
        ((source.Items is not null) && !source.NeedsProjection)
    )).ToArray();

    private void ConfigureObservations(Dictionary<string, IWorldConfiguredProvider> providers, HashSet<string> outputs) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (m_configuration.Observations?.Count > 16) { throw new ArgumentException(message: "At most sixteen observation sources are allowed."); }
        foreach (var settings in (m_configuration.Observations ?? [])) {
            ValidateName(name: settings.Name);
            if (
                !names.Add(item: settings.Name) ||
                (settings.MaximumItems is < 1 or > 128) ||
                (settings.RefreshTicks <= 0) ||
                (settings.Fields.Count is < 1 or > 16)
            ) { throw new ArgumentException(message: "Invalid observation name or budget."); }
            var client = Client(principal: ParsePrincipal(text: settings.Client));

            foreach (var field in settings.Fields) {
                ValidateName(name: field.Key);
                var kind = RequireTable(name: field.Value);

                _ = ReadTable(
                    client,
                    field.Value,
                    kind
                );
                var row = m_server.Definition.State.First(predicate: row => (row.Name.Value == field.Value));

                if (
                    !outputs.Add(item: field.Value) ||
                    (row.Capacity < settings.MaximumItems)
                ) { throw new ArgumentException(message: "Observation tables need exclusive ownership and sufficient capacity."); }
            }
            if (
                !providers.TryGetValue(
                key: settings.Provider,
                value: out var provider
            ) ||
                (provider is not IWorldConfiguredObservationProvider factory)
            ) {
                throw new ArgumentException(message: "Selected provider does not support collection observations.");
            }
            m_observations.Add(item: new(
                settings,
                client,
                factory.BindObservation(
                    settings.Settings,
                    settings.MaximumItems
                )
            ));
        }
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
            if (
                observation.NeedsProjection &&
                (observation.Items is { } items)
            ) {
                try { ProjectObservation(
                    items: items,
                    observation: observation
                ); } catch (Exception exception) { observation.Failure = exception.GetType().Name; }
            }
            if (
                (observation.Pending is null) &&
                ((observation.Started is null) || ((tick - observation.Started.Value) >= ((ulong)observation.Settings.RefreshTicks)))
            ) {
                observation.Started = tick;
                observation.Pending = Task.Run(function: async () => {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: m_stop.Token);

                    deadline.CancelAfter(delay: (m_configuration.Worker ?? WorldExtensionHostOptions.Default).OperationTimeout);
                    var items = await observation.Source.ReadAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);

                    if (items.Count > observation.Settings.MaximumItems) { throw new InvalidOperationException(message: "Observation exceeds its complete snapshot budget."); }
                    var keys = new HashSet<string>(comparer: StringComparer.Ordinal);
                    var copy = new List<WorldExtensionObservationItem>(capacity: items.Count);

                    foreach (var item in items) {
                        ValidateName(name: item.Key);
                        if (
                            !keys.Add(item: item.Key) ||
                            (item.Fields.Count > 32) ||
                            item.Fields.Any(predicate: field => ((field.Value is null) || (field.Value.Length > 4096))) ||
                            observation.Settings.Fields.Keys.Any(predicate: field => !item.Fields.ContainsKey(key: field))
                        ) {
                            throw new InvalidOperationException(message: "Invalid or oversized observation item.");
                        }
                        copy.Add(item: new(
                            item.Key,
                            new Dictionary<string, string>(
                                collection: item.Fields,
                                comparer: StringComparer.Ordinal
                            )
                        ));
                    }
                    return ((IReadOnlyList<WorldExtensionObservationItem>)copy.OrderBy(
                        item => item.Key,
                        StringComparer.Ordinal
                    ).ToArray());
                });
            }
        }
    }
    private void ProjectObservation(Observation observation, IReadOnlyList<WorldExtensionObservationItem> items) {
        var principal = observation.Client.Principal;
        var mutations = new List<WorldMutation>();

        foreach (var field in observation.Settings.Fields) {
            var row = m_server.Definition.State.First(predicate: row => (row.Name.Value == field.Value));
            var actual = ReadTable(
                observation.Client,
                field.Value,
                row.Kind
            );
            StateCell[] cells;

            try {
                cells = items.Select(selector: item => ParseObservedCell(
                    row.Kind,
                    CellName.Parse(candidate: item.Key),
                    (item.Fields.TryGetValue(
                        key: field.Key,
                        value: out var text
                    )
                    ? text
                    : throw new InvalidOperationException(message: $"Observation '{observation.Settings.Name}' item '{item.Key}' omitted field '{field.Key}'."))
                )).ToArray();
            } catch {
                observation.LastRefusedField = field.Key;
                throw;
            }
            if (
                (actual.Count == cells.Length) &&
                actual.Zip(second: cells).All(predicate: pair => ((pair.First.Key == pair.Second.Key.Value) && ObservedCellMatches(
                row.Kind,
                pair.First,
                pair.Second
            )))
            ) { continue; }
            mutations.Add(item: new WorldMutation.UpsertStateRow(
                Principal: principal,
                Row: row with { Cells = cells }
            ));
        }
        observation.LastRefusedField = null;
        if (mutations.Count == 0) { observation.NeedsProjection = false; return; }
        observation.Client.Submit(mutation: new WorldMutation.Batch(
            principal,
            mutations
        ));
        observation.Submissions++;
        // Read-back on the next scan proves admission. Refused contributions must not poison the change cache.
    }
    /// <summary>Parses one observed field value by its target row's cell kind, refusing rather than truncating or
    /// silently defaulting on a value the kind cannot represent.</summary>
    private static StateCell ParseObservedCell(CellKind kind, CellName key, string text) => kind switch {
        CellKind.Int => new(
        key,
        Value: long.Parse(
            text,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture
        )
    ),
        CellKind.Fixed => new(
        key,
        Value: NumericLiteral.ToFixed(value: decimal.Parse(
            text,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture
        )).Value
    ),
        CellKind.Bool => new(
        key,
        Value: text switch {
            "true" or "1" => 1L,
            "false" or "0" => 0L,
            _ => throw new FormatException(message: $"'{text}' is not a valid Bool observation value."),
        }
    ),
        _ => new(
        key,
        Text: text
    ),
    };
    private static bool ObservedCellMatches(CellKind kind, WorldObservedCell actual, StateCell desired) =>
        ((kind == CellKind.Text)
            ? (actual.Text == desired.Text)
            : (actual.Value == desired.Value)
        );

    /// <summary>Waits for current read-only source calls. Call Pump afterwards to admit their results.</summary>
    /// <param name="cancellationToken">Cancels this wait only.</param>
    /// <returns>Completion of the current calls; faults propagate to this explicit host wait.</returns>
    public Task FlushObservationsAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(tasks: m_observations.Where(predicate: source => (source.Pending is not null)).Select(selector: source => source.Pending!)).WaitAsync(cancellationToken: cancellationToken);
}
