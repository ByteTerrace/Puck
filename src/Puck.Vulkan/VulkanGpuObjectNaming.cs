using System.Text;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Names a Vulkan device's objects with <c>vkSetDebugUtilsObjectNameEXT</c>, so the validation layer's messages
/// (<c>[vulkan-debug]</c> lines) print each object's name beside its handle. It is on only when the device is created
/// with validation; it names nothing when the device was created without <c>VK_EXT_debug_utils</c>.
/// </summary>
/// <param name="deviceContext">The device context whose logical device the objects belong to; read when an object is
/// named, after its creation brought the device up.</param>
/// <param name="isEnabled">Whether the device is created with validation.</param>
public sealed unsafe class VulkanGpuObjectNaming(IVulkanDeviceContext deviceContext, bool isEnabled) : GpuObjectNaming {
    private const uint StructureTypeDebugUtilsObjectNameInfo = 1000128000;

    /// <inheritdoc/>
    public override bool IsEnabled => isEnabled;

    /// <summary>Returns the <c>VkObjectType</c> of a neutral object kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The <c>VkObjectType</c> value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a declared kind.</exception>
    public static uint ToVkObjectType(GpuObjectKind kind) =>
        kind switch {
            GpuObjectKind.Buffer => 9U,
            GpuObjectKind.CommandBuffer => 6U,
            GpuObjectKind.CommandPool => 25U,
            GpuObjectKind.DescriptorPool => 22U,
            GpuObjectKind.DescriptorSet => 23U,
            GpuObjectKind.Image => 10U,
            GpuObjectKind.ImageView => 14U,
            GpuObjectKind.Pipeline => 19U,
            GpuObjectKind.RenderPass => 18U,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "The object kind is not declared.",
                paramName: nameof(kind)
            ),
        };

    /// <inheritdoc/>
    protected override void Apply(GpuObjectKind kind, nint handle, string name) {
        var device = deviceContext.LogicalDevice.Commands;
        var setName = device.SetDebugUtilsObjectNameExt;

        if (setName is null) {
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(s: name);
        var nameBytes = ((byteCount < 256)
            ? stackalloc byte[(byteCount + 1)]
            : new byte[(byteCount + 1)]
        );

        Encoding.UTF8.GetBytes(
            bytes: nameBytes,
            chars: name
        );
        nameBytes[byteCount] = 0;

        fixed (byte* objectName = nameBytes) {
            var info = new VkDebugUtilsObjectNameInfoExt {
                ObjectHandle = unchecked((ulong)handle),
                ObjectName = objectName,
                ObjectType = ToVkObjectType(kind: kind),
                StructureType = StructureTypeDebugUtilsObjectNameInfo,
            };

            // A name is a diagnostic: a refusal leaves the object unnamed and the creation standing.
            _ = setName(
                device.Handle,
                in info
            );
        }
    }
}
