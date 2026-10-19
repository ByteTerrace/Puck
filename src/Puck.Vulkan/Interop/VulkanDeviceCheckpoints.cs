using System.Text;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Per-pass breadcrumbs for a device loss (<c>VK_NV_device_diagnostic_checkpoints</c>), the peer of Direct3D 12's DRED
/// breadcrumbs: every debug label a command buffer opens (<see cref="Mark"/>) also records a checkpoint whose marker
/// names the label, and after <c>VK_ERROR_DEVICE_LOST</c> the watched queue's last checkpoints (<see cref="Read"/>) say
/// which pass its work had reached at the top of the pipe and which pass every earlier pass had finished before. A device
/// without the extension marks nothing and reads why. Thread-safe.
/// </summary>
public sealed unsafe class VulkanDeviceCheckpoints {
    /// <summary>The optional device extension.</summary>
    public const string ExtensionName = "VK_NV_device_diagnostic_checkpoints";

    private const uint StageBottomOfPipe = 0x2000;
    private const uint StageTopOfPipe = 0x1;
    private const uint StructureTypeCheckpointData = 1000206000;

    private readonly delegate* unmanaged[Cdecl]<nint, nint, void> m_mark;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, VkCheckpointDataNv*, void> m_read;

    private readonly Lock m_lock = new();
    // Every label marked, by its marker less one; a label keeps its first marker.
    private readonly List<string> m_labels = [];
    private readonly Dictionary<string, nint> m_markers = new(comparer: StringComparer.Ordinal);

    private nint m_queue;

    /// <summary>Resolves the extension's entry points only when device creation enabled it.</summary>
    /// <param name="device">The owning device handle.</param>
    /// <param name="procedures">The device's procedure resolver.</param>
    /// <param name="enabled">Whether device creation enabled the extension.</param>
    public VulkanDeviceCheckpoints(nint device, VulkanProcResolver procedures, bool enabled) {
        ArgumentNullException.ThrowIfNull(argument: procedures);

        if (enabled) {
            m_mark = ((delegate* unmanaged[Cdecl]<nint, nint, void>)procedures.ResolveOptionalDeviceProc(deviceHandle: device, functionName: "vkCmdSetCheckpointNV"u8));
            m_read = ((delegate* unmanaged[Cdecl]<nint, uint*, VkCheckpointDataNv*, void>)procedures.ResolveOptionalDeviceProc(deviceHandle: device, functionName: "vkGetQueueCheckpointDataNV"u8));
        }
        IsEnabled = ((m_mark != null) && (m_read != null));
    }

    /// <summary>Gets whether checkpoints are recorded and read.</summary>
    public bool IsEnabled { get; }

    /// <summary>Names the queue whose checkpoints a device loss reads: the device's graphics queue.</summary>
    /// <param name="queue">The queue handle.</param>
    public void Watch(nint queue) => m_queue = queue;
    /// <summary>Records a checkpoint naming a label as a command buffer opens it.</summary>
    /// <param name="commandBuffer">The command buffer.</param>
    /// <param name="label">The label.</param>
    public void Mark(nint commandBuffer, string label) {
        if (!IsEnabled) {
            return;
        }

        nint marker;

        lock (m_lock) {
            if (!m_markers.TryGetValue(key: label, value: out marker)) {
                m_labels.Add(item: label);
                marker = m_labels.Count;
                m_markers.Add(key: label, value: marker);
            }
        }
        m_mark(commandBuffer, marker);
    }
    /// <summary>Reads the watched queue's last checkpoints after a device loss.</summary>
    /// <returns>Each checkpoint's stage and label, or why there are none.</returns>
    public string Read() {
        if (!IsEnabled) {
            return $"{ExtensionName} unavailable.";
        }

        var count = 0u;

        m_read(m_queue, &count, null);
        if (count == 0) {
            return $"{ExtensionName}: the queue reached no checkpoint.";
        }

        var data = new VkCheckpointDataNv[count];

        for (var index = 0; (index < data.Length); index++) {
            data[index].SType = StructureTypeCheckpointData;
        }
        fixed (VkCheckpointDataNv* first = data) {
            m_read(m_queue, &count, first);
        }

        var report = new StringBuilder(value: $"{ExtensionName}: the queue's last checkpoints");

        lock (m_lock) {
            for (var index = 0; (index < Math.Min(val1: count, val2: ((uint)data.Length))); index++) {
                var stage = data[index].Stage switch {
                    StageTopOfPipe => "started",
                    StageBottomOfPipe => "finished every pass before",
                    var other => $"reached stage 0x{other:x} at",
                };
                var marker = data[index].PCheckpointMarker;
                var label = (((marker > 0) && (marker <= m_labels.Count)) ? m_labels[((int)(marker - 1))] : $"unknown marker 0x{marker:x}");

                report.Append(handler: $"\n  {stage} '{label}'");
            }
        }

        return report.ToString();
    }
}
