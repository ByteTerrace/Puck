using Puck.Testing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Xunit;
using Xunit.Sdk;

namespace Puck.World.Tests;

/// <summary>
/// The Vulkan device laws' devices run under the Khronos validation layer as <see cref="HeadlessVulkanDevice.Validation"/>
/// says, read back from the device, and a validation message fails the law that owns the device. A device asked for
/// validation reports the layer live through its own writer; one deliberate violation reaches that writer, and disposing
/// the device then fails, naming the violation; a device asked for none reports no layer. The violation is
/// <c>vkEnumeratePhysicalDevices</c> with a null count pointer, which the layer's stateless parameter validation reports
/// as <c>VUID-vkEnumeratePhysicalDevices-pPhysicalDeviceCount-parameter</c> and skips, so neither the loader nor the
/// driver reads the pointer; a device without the layer is never given it.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class HeadlessVulkanDeviceValidationLawTests {
    private const string Violation = "VUID-vkEnumeratePhysicalDevices-pPhysicalDeviceCount-parameter";

    [Fact]
    public void TheDeviceLawsFollowTheOneSwitch() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(HeadlessVulkanDeviceValidationLawTests));

        Assert.Equal(actual: device.IsValidated, expected: HeadlessVulkanDevice.Validation);
    }
    [Fact]
    public void ADeliberateViolationFailsTheLawThatOwnsTheDevice() {
        var device = HeadlessVulkanDevice.Create(
            applicationName: nameof(HeadlessVulkanDeviceValidationLawTests),
            validation: true
        );
        VkResult result;

        try {
            Assert.True(condition: device.IsValidated);
            Assert.Empty(collection: device.ValidationMessages);

            result = EnumerateWithoutACount(instance: device.Instance);
        } catch {
            device.Dispose();

            throw;
        }

        var failure = Assert.Throws<FailException>(testCode: device.Dispose);

        Assert.NotEqual(actual: result, expected: VkResult.Success);
        Assert.Contains(actualString: failure.Message, expectedSubstring: $"the first {Violation}");
    }
    [Fact]
    public void ADeviceAskedForNoValidationRunsWithoutTheLayer() {
        using var device = HeadlessVulkanDevice.Create(
            applicationName: nameof(HeadlessVulkanDeviceValidationLawTests),
            validation: false
        );

        Assert.False(condition: device.IsValidated);
    }

    // The violation, given only to an instance under the layer, which skips the call it reports.
    private static unsafe VkResult EnumerateWithoutACount(VulkanInstance instance) {
        var enumerate = ((delegate* unmanaged[Cdecl]<nint, uint*, nint, VkResult>)new VulkanProcResolver().ResolveInstanceProc(
            functionName: "vkEnumeratePhysicalDevices"u8,
            instanceHandle: instance.Commands.Handle
        ));

        return enumerate(instance.Commands.Handle, null, 0);
    }
}
