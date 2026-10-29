using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private readonly Dictionary<string, WorldAuthorityCheckpoint> m_scenarioBoot = [];
    private readonly Dictionary<string, (byte[] Source, WorldAuthorityCheckpoint Image)> m_scenarioImages = [];

    private Dictionary<string, int> m_scenarioLifetimes = [];
    private string[] m_scenarioAuthorities = [];
    private Action? m_restoreScenarioResolver;

    private sealed record ObservationImage(string Owner, WorldObservationSite Site, WorldScreenSource.Session Source,
        ulong Generation, string? Instance, bool IsNew, string? Refusal, Puck.Commands.Principal? Principal,
        WorldDisclosureTier Tier, string SourceAuthority);

    private ObservationImage[] m_scenarioObservations = [];

    /// <summary>Retains the authority cohort admitted by boot, including screen observers.</summary>
    public void CaptureScenarioBoot() {
        m_scenarioBoot.Clear();
        m_scenarioImages.Clear();
        foreach (var row in m_instances.Values) {
            if (!row.Server.TryCaptureCheckpoint(CaptureRow(row: row), out var checkpoint, out var reason)) {
                throw new InvalidOperationException(message: reason);
            }
            m_scenarioBoot.Add(key: row.Name, value: checkpoint!);
            m_scenarioImages.Add(key: row.Name, value: (File.ReadAllBytes(path: row.SourcePath), checkpoint!));
        }
        m_restoreScenarioResolver = m_resolver.CaptureRestorePoint();
        m_scenarioLifetimes = new(m_startedLifetimes, StringComparer.Ordinal);
        m_scenarioAuthorities = m_issuedAuthorities.ToArray();
        m_scenarioObservations = m_screenSessions.SelectMany(selector: pair => pair.Value.Rows.Values.Select(selector: session =>
            new ObservationImage(session.Owner, session.Site, session.Source, session.GenerationId, session.InstanceName,
                session.IsNewGeneration, session.Refusal, session.Observation?.Session,
                (session.Observation?.Tier ?? default), (session.Observation?.SourceAuthority ?? string.Empty)))).ToArray();
    }

    /// <summary>Reports an authority after admission and before its first step.</summary>
    public event Action<WorldInstance>? Admitted;

    /// <summary>Returns an isolated local host to its captured boot, retiring the scenario's other authorities.</summary>
    public void RestoreScenario(WorldAuthorityCheckpoint checkpoint) {
        var boot = (Boot ?? throw new InvalidOperationException(message: "This host has no boot authority."));

        foreach (var row in m_instances.Values) {
            if (row.Tape is { Mode: WorldReplayMode.Recording } tape) { tape.CancelRecording(); }
            if (row.Tape is { Mode: WorldReplayMode.Replaying } drive) { drive.CancelDrive(); }
            row.Server.ClearSessionObservers();
        }
        m_screenSessions.Clear();
        PublishScreenSessions();
        m_pendingTransfers.Clear();
        m_inDoubtTransfers.Clear();
        foreach (var forwarded in m_forwardedBodies.Values) { (forwarded.Authority as IDisposable)?.Dispose(); }
        m_forwardedBodies.Clear();
        m_deferredReaps.Clear();
        m_announcedCrossingHolds.Clear();
        m_seededArrivals.Clear();
        m_freshCounters.Clear();
        m_retainedInstances.Clear();
        boot.Server.Persistence.RestoreScenario(checkpoint: checkpoint);
        var endpoint = EndpointFor(instance: boot);

        for (var slot = 0; (slot < Math.Min(val1: m_seats.SeatCount, val2: boot.Server.Population.Capacity)); slot++) {
            m_seats.PublishRoute(slot, endpoint, new WorldEntityAddress(boot.Server.AuthorityIdentity, slot, boot.Server.Population.Generation(index: slot)));
        }
        foreach (var name in Names.ToArray()) {
            if (!m_scenarioBoot.ContainsKey(key: name) && !TryStop(name: name, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
        }
        m_startedLifetimes.Clear();
        foreach (var (name, lifetime) in m_scenarioLifetimes) { m_startedLifetimes.Add(key: name, value: lifetime); }
        m_issuedAuthorities.Clear();
        m_issuedAuthorities.UnionWith(other: m_scenarioAuthorities);
        m_restoreScenarioResolver?.Invoke();
        foreach (var (name, initial) in m_scenarioBoot) {
            if (name == BootInstanceName) { continue; }
            if (!m_instances.TryGetValue(key: name, value: out var row)) { throw new InvalidOperationException(message: $"Scenario retired boot authority '{name}'."); }
            var source = File.ReadAllBytes(path: row.SourcePath);
            var cached = m_scenarioImages[name];

            if (!source.AsSpan().SequenceEqual(other: cached.Source)) {
                if (!WorldDefinitionLoader.TryLoadFileForAdmission(row.SourcePath, out var admission, out _, out var reason,
                    instanceIdentity: name, catalog: row.Server.Machines.ValidationCatalog)) { throw new InvalidOperationException(message: reason); }
                cached = (source, row.Server.Persistence.ScenarioCheckpoint(admission: admission!, boot: initial));
                m_scenarioImages[name] = cached;
            }
            var image = cached.Image;

            row.Server.Persistence.RestoreScenario(checkpoint: image);
            RestoreRow(row: row, slice: image.HostRow);
        }
        ForgetResolvedDocuments();
        RestoreRow(row: boot, slice: checkpoint.HostRow);
        foreach (var saved in m_scenarioObservations) {
            var session = new WorldObservationSession(owner: saved.Owner, site: saved.Site, source: saved.Source) {
                GenerationId = saved.Generation,
                InstanceName = saved.Instance,
                IsNewGeneration = saved.IsNew,
                Refusal = saved.Refusal,
            };

            if ((saved.Principal is { } principal) && (saved.Instance is { } target)) {
                session.Observation = m_instances[target].Server.RestoreSessionObservation(principal, saved.Tier,
                    saved.SourceAuthority, session.Mirror);
            }
            if (!m_screenSessions.TryGetValue(key: saved.Owner, value: out var owned)) { owned = (null, new()); }
            owned.Rows.Add(key: saved.Site, value: session);
            m_screenSessions[saved.Owner] = owned;
        }
        PublishScreenSessions();
    }
}
