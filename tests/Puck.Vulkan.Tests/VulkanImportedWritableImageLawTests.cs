using System.Reflection;
using Puck.Vulkan.Interfaces;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, the ownership handoff an imported writable image records: the release hands the image
/// from the writing queue family to <see cref="VulkanQueueFamily.External"/> after every write, the acquire takes it back
/// before every later access, and both hold it in <c>VK_IMAGE_LAYOUT_GENERAL</c>, the layout the writer leaves it
/// in.</summary>
public sealed class VulkanImportedWritableImageLawTests {
    private const nint CommandBuffer = 0x10;
    private const nint Image = 0x20;
    private const uint QueueFamily = 3;

    [Fact]
    public void TheReleaseHandsTheImageToTheExternalFamilyAfterEveryWrite() {
        var barrier = Record(release: true);

        Assert.Equal(
            actual: (barrier.SourceQueueFamily, barrier.DestinationQueueFamily),
            expected: (QueueFamily, VulkanQueueFamily.External)
        );
        Assert.Equal(
            actual: (barrier.OldLayout, barrier.NewLayout),
            expected: (VulkanImageLayout.General, VulkanImageLayout.General)
        );
        Assert.Equal(
            actual: (barrier.SourceStageMask, barrier.SourceAccessMask),
            expected: (VulkanPipelineStageFlags.AllCommands, VulkanAccessFlags.ShaderWrite | VulkanAccessFlags.TransferWrite)
        );
    }
    [Fact]
    public void TheAcquireTakesTheImageBackFromTheExternalFamilyBeforeEveryLaterAccess() {
        var barrier = Record(release: false);

        Assert.Equal(
            actual: (barrier.SourceQueueFamily, barrier.DestinationQueueFamily),
            expected: (VulkanQueueFamily.External, QueueFamily)
        );
        Assert.Equal(
            actual: (barrier.OldLayout, barrier.NewLayout),
            expected: (VulkanImageLayout.General, VulkanImageLayout.General)
        );
        Assert.Equal(
            actual: (barrier.DestinationStageMask, barrier.DestinationAccessMask),
            expected: (VulkanPipelineStageFlags.AllCommands, VulkanAccessFlags.ShaderRead | VulkanAccessFlags.ShaderWrite | VulkanAccessFlags.TransferWrite)
        );
    }

    // Records one handoff through a recording API that keeps the image barrier it is handed and refuses every other call.
    private static Barrier Record(bool release) {
        var recording = DispatchProxy.Create<IVulkanCommandBufferRecordingApi, BarrierRecorder>();

        VulkanImportedWritableImage.RecordHandoff(
            commandBufferHandle: CommandBuffer,
            device: null!,
            imageHandle: Image,
            queueFamily: QueueFamily,
            recording: recording,
            release: release
        );

        var barriers = ((BarrierRecorder)((object)recording)).Barriers;

        return Assert.Single(collection: barriers);
    }

    public sealed record Barrier(uint OldLayout, uint NewLayout, uint SourceAccessMask, uint DestinationAccessMask, uint SourceStageMask, uint DestinationStageMask, uint SourceQueueFamily, uint DestinationQueueFamily);
    public class BarrierRecorder : DispatchProxy {
        public List<Barrier> Barriers { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (
                (targetMethod?.Name != nameof(IVulkanCommandBufferRecordingApi.TransitionImageLayout)) ||
                (args is null)
            ) {
                throw new NotSupportedException(message: $"the handoff recorded {targetMethod?.Name}");
            }

            Assert.Equal(
                actual: (((nint)args[1]!), ((nint)args[2]!)),
                expected: (CommandBuffer, Image)
            );
            Barriers.Add(item: new Barrier(
                DestinationAccessMask: ((uint)args[9]!),
                DestinationQueueFamily: ((uint)args[13]!),
                DestinationStageMask: ((uint)args[11]!),
                NewLayout: ((uint)args[7]!),
                OldLayout: ((uint)args[6]!),
                SourceAccessMask: ((uint)args[8]!),
                SourceQueueFamily: ((uint)args[12]!),
                SourceStageMask: ((uint)args[10]!)
            ));

            return null;
        }
    }
}
