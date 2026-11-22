using Puck.Abstractions.Machines;
using Puck.Platform;
using Puck.SdfVm;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    /// <summary>Raised on the frame when the boot world's machine instance map moved (a boot, replacement, removal or
    /// restore), as the published machine table shows it, so the audio director rebinds its machine streams.</summary>
    public event Action? MachinesChanged;

    /// <summary>Resolves one of the boot world's machine audio streams from the published machine table.</summary>
    /// <param name="instance">The authored instance name.</param>
    /// <param name="output">The provider's stream name.</param>
    /// <returns>The stream, or <see langword="null"/> when no instance of that name has it.</returns>
    public IAudioMachine? AudioOutput(string instance, string output) => MachinesOf(world: WorldDefinitionLoader.BootInstanceName)?.AudioOutput(
        instance: instance,
        output: output
    );
    /// <summary>Returns the same-device image-view handle the world's views bound to a screen index in its latest frame, or 0
    /// when the index is unbound, not declared, or nothing has been produced yet — the live state <c>world.screens</c>
    /// reports.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>The bound handle, or 0.</returns>
    public nint CurrentHandle(int index) => ((
        m_slots.ContainsKey(key: index) &&
        (index >= 0) &&
        (index < SdfWorldTables.MaxScreenSurfaces) &&
        (ViewHost is { } residency)
    )
        ? residency.BoundScreenSource(screen: index)
        : 0
    );
    /// <summary>Returns how the image a screen shows crosses from its producer's device to the render device: through the
    /// shared fence the producer signals and the render device's submission waits for, or by the producer's CPU wait
    /// and why. Only a camera on its GPU tier, a capture on its GPU route and a probe output cross devices.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>The order, or <see langword="null"/> when the screen's source crosses no devices.</returns>
    public SharedFenceOrder? FenceOrderAt(int index) => (((InstanceOf(screen: index) is { } instance) && (FeedOf(instance: instance) is { } feed))
        ? FenceOrderOf(feed: feed)
        : null);
    /// <summary>Reads the presentation's half of a screen's state for <c>screen.state</c>: the image the engine binds for
    /// it and why its source shows nothing.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="handle">The bound image handle, or zero.</param>
    /// <param name="fault">Why the screen's source shows nothing, or <see langword="null"/> while it shows its image.</param>
    /// <returns>Whether the index is a declared screen.</returns>
    public bool TryPresentedState(int index, out nint handle, out string? fault) {
        if (!m_slots.TryGetValue(key: index, value: out var slot)) {
            handle = 0;
            fault = null;

            return false;
        }

        handle = CurrentHandle(index: index);
        fault = ((InstanceOf(screen: index) is { } instance)
            ? SourceFault(instance: instance)
            : slot.DeclaredFault);

        return true;
    }
    /// <summary>Gets the live decal-text source at a screen index, or <see langword="null"/> when the slot's current
    /// source has neither text nor a machine fault — what the frame source's per-frame decal providers consult, so a live source change
    /// clears the decal the same frame it re-binds the image path.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public WorldScreenSource.Text? TextSourceAt(int index) {
        if (!m_slots.TryGetValue(index, out var slot)) { return null; }
        if (slot.Text is { } text) { return text; }
        if ((slot.DeclaredSource is not WorldScreenSource.Machine machine) ||
            (MachinesOf(world: WorldDefinitionLoader.BootInstanceName)?.Faults.GetValueOrDefault(key: machine.Instance) is not { } fault)) {
            slot.FaultText = null;
            return null;
        }
        if (slot.FaultText?.Fault != fault) {
            // Use the text screen's ordinary font/grid path, escaping diagnostics as literal text.
            const int Columns = 64;
            var lines = new List<string>();
            foreach (var row in fault.Replace('\r', ' ').Split('\n')) {
                for (var offset = 0; offset < row.Length; offset += Columns) {
                    lines.Add(row.Substring(offset, Math.Min(Columns, row.Length - offset)).Replace("{", "{{").Replace("}", "}}"));
                }
            }
            // A verbose provider error must fit the same bounded decal as authored text; status verbs retain it whole.
            slot.FaultText = (fault, new WorldScreenSource.Text(lines, Columns: Columns,
                Rows: Math.Min(lines.Count, SdfWorldTables.MaxScreenDecalCells / Columns)));
        }
        return slot.FaultText.Value.Text;
    }
}
