using Puck.Shaders;

namespace Puck.SdfVm;

// The history resources an sdf.world pass reads or writes through members: the resolve's history surface, the probe
// lookup's gathers and the glass pass's, and the history colour the reflection pass samples. A history's two allocations
// alternate across submissions independently of a descriptor set's slot, so each is bound from the resource its
// recording hands the port, writing the slot's set only where that resource moved, never from the ports a slot caches for
// the recorder's life.
internal sealed class SdfHistoryPorts {
    // Each history port: its position, whether it is an input, the member it binds at, and whether that member is a
    // sampled image rather than a buffer.
    private readonly (int Port, bool Input, string Member, bool Image)[] m_ports;
    // Per frame slot, the buffer or image view handle each port's member last bound in the slot's set.
    private readonly nint[][] m_bound;

    // Collects the history ports among a pass's inputs and outputs, named as the fragment declares them.
    internal SdfHistoryPorts(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, int slots) {
        m_ports = [
            .. inputs.Select(selector: static (name, port) => (Port: port, Input: true, Member: ReadMemberOf(version: name), Image: IsImage(version: name)))
                .Where(predicate: static entry => (entry.Member is not null)).Select(selector: static entry => (entry.Port, entry.Input, entry.Member!, entry.Image)),
            .. outputs.Select(selector: static (name, port) => (Port: port, Input: false, Member: WrittenMemberOf(version: name), Image: false))
                .Where(predicate: static entry => (entry.Member is not null)).Select(selector: static entry => (entry.Port, entry.Input, entry.Member!, entry.Image)),
        ];
        m_bound = new nint[slots][];
        for (var slot = 0; (slot < slots); slot++) {
            m_bound[slot] = new nint[m_ports.Length];
        }
    }

    // Whether a fragment version is history this binds, whose allocations alternate across submissions.
    internal static bool IsHistory(string version) => (ReadMemberOf(version: version) is not null);

    // Binds each history port's resource as the recording hands it into the slot's set.
    internal void Bind(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables, ShaderInterfaceLayout layout, string pass) {
        var bound = m_bound[recording.Slot];

        for (var index = 0; (index < m_ports.Length); index++) {
            var (port, input, member, image) = m_ports[index];
            var resource = (input ? recording.Inputs[port] : recording.Outputs[port]);

            if (image) {
                var view = resource.ReadImage(latch: recording.Latch).ImageViewHandle;

                if (view != bound[index]) {
                    tables.Bindings.WriteSampledImage(arrayElement: 0, binding: SdfKernelInterfaces.BindingOf(layout: layout, member: member), descriptorSetHandle: set, imageViewHandle: view);
                    bound[index] = view;
                }
                continue;
            }

            var buffer = (resource.Buffer ?? throw new InvalidOperationException(message: $"Pass '{pass}' binds no buffer at its history port '{member}'."));

            if (buffer.BufferHandle != bound[index]) {
                tables.WriteInterfaceBuffer(buffer: buffer, layout: layout, member: member, set: set);
                bound[index] = buffer.BufferHandle;
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
        SdfWorldPackage.Parts.HistoryColor => SdfWorldPackage.ReflectionHistory,
        _ => null,
    };
    // Whether a history version a pass reads is an image, sampled at its member.
    private static bool IsImage(string version) => (version == SdfWorldPackage.Parts.HistoryColor);
    // The member a pass writes a history through, or null for a version that is no history it writes.
    private static string? WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.ProbeHistory => SdfWorldPackage.ViewsProbeHistoryWritten,
        SdfWorldPackage.Parts.GlassProbeHistory => SdfWorldPackage.GlassProbeHistoryWritten,
        _ => null,
    };
}
