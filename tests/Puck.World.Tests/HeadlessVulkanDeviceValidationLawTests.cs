using Puck.Testing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The Vulkan device laws' devices run under the Khronos validation layer as <see cref="HeadlessVulkanDevice.Validation"/>
/// says, read back from the device: a device asked for validation reports the layer among its instance's layers, and
/// one deliberate violation reaches the layer, which reports it on standard error; a device asked for none reports no
/// layer. The violation is <c>vkEnumeratePhysicalDevices</c> with a null count pointer, which the layer's stateless
/// parameter validation reports as <c>VUID-vkEnumeratePhysicalDevices-pPhysicalDeviceCount-parameter</c> and skips,
/// so neither the loader nor the driver reads the pointer; a device without the layer is never given it. The laws swap
/// the process's error writer, so they run in <see cref="ConsoleRedirectionCollection"/>.
/// </summary>
[Collection(ConsoleRedirectionCollection.Name)]
public sealed class HeadlessVulkanDeviceValidationLawTests {
    [Fact]
    public void TheDeviceLawsFollowTheOneSwitch() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(HeadlessVulkanDeviceValidationLawTests));

        Assert.Equal(actual: device.IsValidated, expected: HeadlessVulkanDevice.Validation);
    }
    [Fact]
    public void ADeviceAskedForValidationReportsADeliberateViolation() {
        var captured = new StringWriter();
        var error = Console.Error;
        VkResult result;
        bool validated;

        Console.SetError(newError: captured);

        try {
            using var device = HeadlessVulkanDevice.Create(
                applicationName: nameof(HeadlessVulkanDeviceValidationLawTests),
                validation: true
            );

            validated = device.IsValidated;
            result = EnumerateWithoutACount(instance: device.Instance);
        } finally {
            Console.SetError(newError: error);
        }

        var lines = captured.ToString();

        // The layer's own words, for the record of a run.
        Console.Error.Write(value: lines);

        Assert.True(condition: validated);
        Assert.NotEqual(actual: result, expected: VkResult.Success);
        Assert.Contains(actualString: lines, expectedSubstring: "[vulkan-debug] validation ERROR");
        Assert.Contains(actualString: lines, expectedSubstring: "VUID-vkEnumeratePhysicalDevices-pPhysicalDeviceCount-parameter");
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
