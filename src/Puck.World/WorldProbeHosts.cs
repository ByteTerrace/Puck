using System.Text;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The probe hosts the presentation runs, one per world whose probes it shows: the boot world's
/// (<see cref="Boot"/>), and one for every other world the presentation shows through a screen (a portal's destination, a
/// painting's world, a world a seat is presented in), built from the definition the screens that show it last reconciled,
/// so a probe of that world reads and writes its own world's cameras and rings
/// (<see cref="WorldScreenBinder.ProbeKey"/>). A presented world's probes are presentation only, as every probe is, so its
/// host runs whichever authority steps the world. A world's host is built again when its <c>probes</c> section changes and
/// retired once no screen shows the world. A host that cannot be built (a row that fails its deep check, a track that does
/// not load) is recorded on the binder by name (<see cref="WorldScreenBinder.HostProbeWorld"/>), so each screen showing one
/// of that world's probes stands its capture fill in and names the refusal rather than waiting for a host that never comes.
/// </summary>
/// <remarks>Serviced once per host frame, on the thread that produces frames, like every
/// <see cref="ISnapshotInputCapture"/>. A host with no presentation (headless) shows no other world and runs the boot
/// world's host alone.</remarks>
internal sealed class WorldProbeHosts : ISnapshotInputCapture, IDisposable {
    private readonly IInputClock m_clock;
    private readonly IInputFocus m_focus;
    private readonly InputRouter m_router;
    private readonly PlayerRoster m_roster;
    private readonly WorldScreenBinder m_screens;

    // The presented worlds' hosts by world instance name, with the probes section each was built from; a refused world
    // holds no host.
    private readonly Dictionary<string, (IReadOnlyList<WorldProbe> Probes, WorldProbes? Host)> m_presented = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, WorldDefinition> m_shown = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_retired = [];

    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="WorldProbeHosts"/> class over the boot world's host.</summary>
    /// <param name="boot">The boot world's probe host, which this owner services but does not own.</param>
    /// <param name="clock">The shared input capture clock.</param>
    /// <param name="focus">The terminal input focus.</param>
    /// <param name="roster">The seat roster.</param>
    /// <param name="router">The command router.</param>
    /// <param name="screens">The screen binder the hosts publish through and read the presented worlds from.</param>
    /// <exception cref="ArgumentNullException">A dependency is <see langword="null"/>.</exception>
    public WorldProbeHosts(WorldProbes boot, IInputClock clock, IInputFocus focus, PlayerRoster roster, InputRouter router, WorldScreenBinder screens) {
        ArgumentNullException.ThrowIfNull(argument: boot);
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: focus);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: screens);

        Boot = boot;
        m_clock = clock;
        m_focus = focus;
        m_roster = roster;
        m_router = router;
        m_screens = screens;
    }

    /// <summary>Gets the boot world's probe host, which <c>probe.record</c> and <c>probe.set</c> address.</summary>
    public WorldProbes Boot { get; }

    /// <inheritdoc/>
    public void CaptureFrame(ulong frameKey) {
        if (m_disposed) {
            return;
        }

        Boot.CaptureFrame(frameKey: frameKey);
        m_screens.CollectPresentedWorlds(worlds: m_shown);

        m_retired.Clear();

        foreach (var world in m_presented.Keys) {
            if (!m_shown.ContainsKey(key: world)) {
                m_retired.Add(item: world);
            }
        }
        foreach (var world in m_retired) {
            Retire(world: world);
        }
        foreach (var (world, definition) in m_shown) {
            if (
                !m_presented.TryGetValue(
                    key: world,
                    value: out var hosted
                ) ||
                !ReferenceEquals(
                    objA: hosted.Probes,
                    objB: definition.Probes
                )
            ) {
                Retire(world: world);
                m_presented[world] = (definition.Probes, Build(
                    definition: definition,
                    world: world
                ));
            }

            m_presented[world].Host?.CaptureFrame(frameKey: frameKey);
        }
    }
    /// <summary>Appends the <c>probe.status</c> read-back of every host: the boot world's, then each presented world's,
    /// prefixed <c>world &lt;name&gt;:</c>, with a refused world's reason.</summary>
    /// <param name="builder">The builder to append into.</param>
    /// <returns><see langword="false"/> when no host has a <c>probes</c> row and no presented world was refused,
    /// leaving <paramref name="builder"/> untouched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public bool Describe(StringBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        var wrote = Boot.Describe(builder: builder);

        foreach (var (world, (_, host)) in m_presented) {
            var start = builder.Length;

            if (wrote) {
                _ = builder.Append(value: " | ");
            }

            _ = builder.Append(value: "world ").Append(value: world).Append(value: ": ");

            if (host is null) {
                _ = builder.Append(value: "refused");
            } else if (!host.Describe(builder: builder)) {
                builder.Length = start;

                continue;
            }

            wrote = true;
        }

        return wrote;
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var (_, host) in m_presented.Values) {
            host?.Dispose();
        }

        m_presented.Clear();
    }

    // Builds a presented world's host, or records why it cannot run on the binder and holds none.
    private WorldProbes? Build(string world, WorldDefinition definition) {
        try {
            var host = new WorldProbes(
                clock: m_clock,
                definition: definition,
                documentDirectory: definition.DocumentDirectory,
                focus: m_focus,
                passes: null,
                roster: m_roster,
                router: m_router,
                screens: m_screens,
                world: world
            );

            m_screens.HostProbeWorld(
                refusal: null,
                world: world
            );

            return host;
        } catch (InvalidOperationException exception) {
            m_screens.HostProbeWorld(
                refusal: exception.Message,
                world: world
            );
            Console.Error.WriteLine(value: $"[probe: no host runs for world '{world}': {exception.Message}]");

            return null;
        }
    }
    // Retires a presented world's host, ending its runs and releasing its rings and exports.
    private void Retire(string world) {
        if (!m_presented.Remove(
            key: world,
            value: out var hosted
        )) {
            return;
        }

        hosted.Host?.Dispose();
        m_screens.ForgetProbeWorld(world: world);
    }
}
