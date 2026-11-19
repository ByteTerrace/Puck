using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// One pass pipeline an <see cref="SdfWorldTables"/> records a view's passes with (the mesh pass, a cascade's, the
/// glass pass, the occlusion kernel), which may still be building when the tables are made: the tables exist once the
/// bake's kernels are built (<see cref="SdfWorldPipelines.PollRequired"/>), so the bake samples while the driver compiles
/// the rest, and a frame waits for every slot itself (<see cref="SdfWorldTables.FrameWaiting"/>).
/// <para>Invariant: a pass reads a slot's pipeline only once the slot is built. <see cref="Built"/> is the one read and
/// refuses an unbuilt slot, and the views record only frames the tables have found waiting on nothing.</para>
/// <para>The slot never owns its lease: the holder that took it (<see cref="SdfWorldPipelineSource"/>) releases it after
/// every table built from it.</para>
/// </summary>
public sealed class SdfPassPipelineSlot {
    private readonly GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_lease;

    private GpuPassPipeline? m_built;
    private Exception? m_refusal;

    /// <summary>Initializes a new instance of the <see cref="SdfPassPipelineSlot"/> class over a pipeline already
    /// built.</summary>
    /// <param name="name">The pipeline's name, which a frame waiting on it reports.</param>
    /// <param name="built">The pipeline.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="built"/> is
    /// <see langword="null"/>.</exception>
    public SdfPassPipelineSlot(string name, GpuPassPipeline built) {
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: built);

        Name = name;
        m_built = built;
    }
    // A slot over a lease its holder keeps, named by the pipeline's description.
    internal SdfPassPipelineSlot(GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> lease) {
        Name = lease.Key.Name;
        m_lease = lease;
    }

    /// <summary>Gets the pipeline's name.</summary>
    public string Name { get; }
    /// <summary>Gets why the pipeline's build was refused, or <see langword="null"/> while it was not.</summary>
    public Exception? Refusal => m_refusal;
    /// <summary>Gets the built pipeline.</summary>
    /// <exception cref="InvalidOperationException">The pipeline is not built (<see cref="PollRefusing"/>).</exception>
    public GpuPassPipeline Built => (m_built ?? throw new InvalidOperationException(message: $"The '{Name}' pass pipeline is not built."));

    /// <summary>Returns whether the pipeline is built, taking a build that has just finished. A build that failed, other
    /// than by a device loss, is refused and kept (<see cref="Refusal"/>), never thrown, and is not polled again, since a
    /// poll after a failure starts a fresh build; a device loss rebuilds every slot.</summary>
    /// <returns><see langword="true"/> once the pipeline is built.</returns>
    /// <exception cref="DeviceLostException">The device was lost during the creation.</exception>
    public bool PollRefusing() {
        if (m_built is not null) { return true; }
        if ((m_refusal is not null) || (m_lease is null)) { return false; }
        (m_built, m_refusal) = SdfWorldPipelines.PollRefusing(lease: m_lease, name: Name);
        return (m_built is not null);
    }
}
