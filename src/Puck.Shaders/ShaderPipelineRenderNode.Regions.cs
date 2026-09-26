using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// External buffers the host writes as regions: a host buffer port. The node owns a bound region and, on each frame it
// records, flushes the slot's share once that slot's previous submission has retired, then binds the slot's buffer, so
// the host writes the region's contents at any time and a submission in flight never reads a write meant for a later
// frame.
public sealed partial class ShaderPipelineRenderNode {
    private readonly Dictionary<string, GpuRegion> m_externalRegions = new(comparer: StringComparer.Ordinal);

    /// <summary>Binds a region the host writes for a named external buffer, a host buffer port. The node owns the region
    /// from here: each frame it records, after waiting the frame slot's previous submission, it flushes what that slot
    /// owes and binds the slot's buffer, and it disposes the region on device loss and at disposal, the only way a name
    /// takes another region. A region is written in place or through a ring: a staged region's copy is not recorded, so
    /// one is refused.</summary>
    /// <param name="name">The external buffer's version name.</param>
    /// <param name="region">The region, of the node's frames in flight in slots and at least the declared size in
    /// bytes.</param>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or not a declared external buffer of the
    /// candidate graph, a region is already bound to it, or <paramref name="region"/> is staged or has another number of
    /// slots than the node's frames in flight.</exception>
    public void BindRegion(string name, GpuRegion region) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(region);
        ValidateExternalBinding(
            kind: ShaderPipelineResourceKind.Buffer,
            name: name
        );

        if (region.Policy == GpuResidencyPolicy.Staged) {
            throw new ArgumentException(
                message: $"Region '{name}' is staged, and a host buffer port records no staged copy; write it in place or through a ring.",
                paramName: nameof(region)
            );
        }
        if (region.SlotCount != m_inFlight) {
            throw new ArgumentException(
                message: $"Region '{name}' has {region.SlotCount} slot(s), but the node keeps {m_inFlight} frame(s) in flight.",
                paramName: nameof(region)
            );
        }

        if (!m_externalRegions.TryAdd(
            key: name,
            value: region
        )) {
            throw new ArgumentException(
                message: $"Region '{name}' is already bound; a name takes another region only after a device loss.",
                paramName: nameof(name)
            );
        }


        m_externalBuffers[name] = region.Buffer(slot: 0);
    }

    // Flushes each bound region's share of a slot whose previous submission has retired, and binds that slot's buffer.
    private void FlushRegions(int slot) {
        foreach (var (name, region) in m_externalRegions) {
            region.Flush(slot: slot);
            m_externalBuffers[name] = region.Buffer(slot: slot);
        }
    }
    // Disposes every bound region, once no submission can read it: after the node waited its submissions, after a device
    // loss, or when a retired node's host has seen its readers complete.
    private void ReleaseRegions() {
        foreach (var (name, region) in m_externalRegions) {
            _ = m_externalBuffers.Remove(key: name);
            region.Dispose();
        }

        m_externalRegions.Clear();
    }
}
