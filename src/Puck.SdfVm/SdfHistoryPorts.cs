using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// The history buffers an sdf.world pass reads or writes through members: the resolve's history surface, the probe
// lookup's gathers and the glass pass's. A history's two allocations alternate across submissions independently of a
// descriptor set's slot, so each is bound from the buffer its recording hands the port, writing the slot's set only where
// that buffer moved, never from the ports a slot caches for the recorder's life.
internal sealed class SdfHistoryPorts {
    // Each history port: its position, whether it is an input, and the member it binds at.
    private readonly (int Port, bool Input, string Member)[] m_ports;
    // Per frame slot, the buffer each port's member last bound in the slot's set.
    private readonly IGpuBuffer?[][] m_bound;

    // Collects the history ports among a pass's inputs and outputs, named as the fragment declares them.
    internal SdfHistoryPorts(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, int slots) {
        m_ports = [
            .. inputs.Select(selector: static (name, port) => (Port: port, Input: true, Member: ReadMemberOf(version: name)))
                .Where(predicate: static entry => (entry.Member is not null)).Select(selector: static entry => (entry.Port, entry.Input, entry.Member!)),
            .. outputs.Select(selector: static (name, port) => (Port: port, Input: false, Member: WrittenMemberOf(version: name)))
                .Where(predicate: static entry => (entry.Member is not null)).Select(selector: static entry => (entry.Port, entry.Input, entry.Member!)),
        ];
        m_bound = new IGpuBuffer?[slots][];
        for (var slot = 0; (slot < slots); slot++) {
            m_bound[slot] = new IGpuBuffer?[m_ports.Length];
        }
    }

    // Whether a fragment version is history this binds, whose allocations alternate across submissions.
    internal static bool IsHistory(string version) => (ReadMemberOf(version: version) is not null);

    // Binds each history port's buffer as the recording hands it into the slot's set.
    internal void Bind(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables, ShaderInterfaceLayout layout, string pass) {
        var bound = m_bound[recording.Slot];

        for (var index = 0; (index < m_ports.Length); index++) {
            var (port, input, member) = m_ports[index];
            var buffer = ((input ? recording.Inputs[port].Buffer : recording.Outputs[port].Buffer)
                ?? throw new InvalidOperationException(message: $"Pass '{pass}' binds no buffer at its history port '{member}'."));

            if (!ReferenceEquals(objA: buffer, objB: bound[index])) {
                tables.WriteInterfaceBuffer(buffer: buffer, layout: layout, member: member, set: set);
                bound[index] = buffer;
            }
        }
    }
    // Forgets what a slot's set binds, after its ports were written again for other tables.
    internal void Forget(int slot) => Array.Clear(array: m_bound[slot]);

    // The member a pass reads a history through, or null for a version that is no history.
    private static string? ReadMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.HistorySurface => SdfWorldPackage.HistorySurface,
        SdfWorldPackage.Parts.ProbeHistory => SdfWorldPackage.ViewsProbeHistory,
        SdfWorldPackage.Parts.GlassProbeHistory => SdfWorldPackage.GlassProbeHistory,
        _ => null,
    };
    // The member a pass writes a history through, or null for a version that is no history it writes.
    private static string? WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.ProbeHistory => SdfWorldPackage.ViewsProbeHistoryWritten,
        SdfWorldPackage.Parts.GlassProbeHistory => SdfWorldPackage.GlassProbeHistoryWritten,
        _ => null,
    };
}
