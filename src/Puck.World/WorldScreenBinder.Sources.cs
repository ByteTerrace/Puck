using Puck.Commands;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

// The screens as the world's views bind them, and the instances they read. A row's producer, machine or probe source is a
// render-graph source instance: the runtime opens it through the registered producers (and the machine upload and probe
// producer below, of the reserved ids), renders it at its cadence, and hands each sdf.world instance that reads it its latest image, which
// the view's passes bind to every screen whose row reads it. A camera view or a session is a view instance the runtime
// hands a view the same way.
public sealed partial class WorldScreenBinder : ISdfScreenSources {
    /// <summary>Gets or sets the render-graph runtime the world renders through, whose source instances this binder reads
    /// a screen's feed, fault and light from; <see langword="null"/> in a presentation with no render graph, which runs no
    /// source instance.</summary>
    public RenderGraphRuntime? Runtime { get; set; }
    /// <inheritdoc/>
    public IReadOnlyList<int> Screens => m_screenIndices;

    /// <inheritdoc/>
    public bool Emits(int screen) => (m_slots.TryGetValue(key: screen, value: out var slot) && (ShownOf(screen: screen) switch {
        WorldScreenSource.Machine or WorldScreenSource.Producer or WorldScreenSource.Probe => true,
        WorldScreenSource.Session => ((slot.Session is { } feed) && (feed.InstanceName != WorldInstanceHost.BootInstanceName)),
        _ => false,
    }));
    /// <inheritdoc/>
    /// <remarks>A screen is drawn from the mapping <see cref="Mappings"/> last published for it.</remarks>
    public SourceMapping? MappingOf(int screen) => (Mappings.TryGet(
        mapping: out var mapping,
        screen: screen
    )
        ? mapping
        : null
    );
    /// <summary>Returns the machine host of a world instance this process runs: the boot world's own, or the host of a
    /// local instance a screen shows or a seat is presented in, which steps that world's machines on its own ticks.</summary>
    /// <param name="world">The world instance's name.</param>
    /// <returns>The host, or <see langword="null"/> for a world this process does not run, whose machines run on its own
    /// authority and reach no screen here.</returns>
    public IWorldMachineHost? MachinesOf(string world) {
        if (string.Equals(
            a: world,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            return m_machines;
        }

        return ((m_instanceHost.TryGet(
            instance: out var instance,
            name: world
        ) && (instance is not null))
            ? instance.Server.Machines
            : null);
    }
    /// <inheritdoc/>
    /// <remarks>A seat's or a camera's view of the world's residency shows the world's screens (<see cref="InstanceOf"/>); a
    /// session of the boot world, or a camera it shows, filmed into the world's frame shows its own level's.</remarks>
    public string? ReadOf(int view, int screen) {
        var filmed = (view - m_hostViewCount);

        return ((((uint)filmed) < ((uint)m_hostLevels.Count)) && (m_hostLevels[filmed] is { } level))
            ? level.InstanceOf(screen: screen)
            : InstanceOf(screen: screen);
    }
    /// <summary>Returns the name of the instance a screen of the boot world reads: the source it shows, its row's or the
    /// one a live presentation verb bound over the row; the source instance of a producer, machine or probe source, and
    /// the view instance of a camera view or a session.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows nothing or text.</returns>
    public string? InstanceOf(int screen) => (Mappings.InstanceOf(screen: screen) ?? (m_slots.TryGetValue(
        key: screen,
        value: out var slot
    )
        ? slot.ViewInstance
        : null
    ));

    // The feed a running source instance opened: an imported producer's (a probe's included) through its adapter, an
    // uploaded producer's through its upload; null for a machine source, one the set does not run, or no runtime.
    private IWorldImageFeed? FeedOf(string instance) => ((Runtime is { } runtime)
        ? RunningFeedOf(
            instance: instance,
            runtime: runtime
        )
        : null);
    // Why a screen reading a source instance shows nothing: its producer's fault (a feed that did not open or has no
    // signal, a probe not yet live, an upload refused), or null while it shows its image. A machine's fault is
    // Machines.State's concern.
    private string? SourceFault(string instance) => ((Runtime is { } runtime)
        ? RunningSourceFault(
            instance: instance,
            runtime: runtime
        )
        : null);
}