using System.Text;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>Retrieves and retains one lost device's driver diagnostics without submitting more GPU work.</summary>
public sealed unsafe class VulkanDeviceFault {
    /// <summary>The optional device extension.</summary>
    public const string ExtensionName = "VK_EXT_device_fault";
    /// <summary>The feature structure whose first flag enables fault reporting; binary dumps remain disabled.</summary>
    public const uint FeatureStructureType = 1000341000;

    private readonly nint m_device;
    private readonly delegate* unmanaged[Cdecl]<nint, VkDeviceFaultCountsExt*, VkDeviceFaultInfoExt*, VkResult> m_query;
    private readonly Lock m_lock = new();
    private readonly string? m_unavailable;

    private string? m_report;

    /// <summary>Gets the retained diagnostic after a device loss was reported, or null before one. Reading this
    /// property never queries a healthy device, including when only its presentation surface was lost.</summary>
    public string? Report => m_report;

    /// <summary>Resolves the query only when its feature was enabled on this device.</summary>
    /// <param name="device">The owning device handle.</param>
    /// <param name="procedures">The device's procedure resolver.</param>
    /// <param name="enabled">Whether device creation enabled the extension and feature.</param>
    public VulkanDeviceFault(nint device, VulkanProcResolver procedures, bool enabled) {
        m_device = device;
        if (enabled) {
            m_query = ((delegate* unmanaged[Cdecl]<nint, VkDeviceFaultCountsExt*, VkDeviceFaultInfoExt*, VkResult>)procedures.ResolveOptionalDeviceProc(
                deviceHandle: device, functionName: "vkGetDeviceFaultInfoEXT"u8));
        }
        if (!enabled) { m_unavailable = "VK_EXT_device_fault unavailable (extension or deviceFault feature not enabled)."; } else if (m_query == null) { m_unavailable = "VK_EXT_device_fault enabled, but vkGetDeviceFaultInfoEXT unavailable."; }
    }

    /// <summary>Returns the driver's report after <c>VK_ERROR_DEVICE_LOST</c>. Query failures remain diagnostic text,
    /// so they cannot replace the original device-loss exception. The first report is retained for later failures.</summary>
    /// <returns>The fault description, typed addresses and vendor records, or why they could not be read.</returns>
    public string ReadAfterDeviceLoss() {
        lock (m_lock) {
            if (m_report is not null) { return m_report; }
            try { m_report = Query(); } catch (Exception exception) { m_report = $"VK_EXT_device_fault query failed: {exception.GetType().Name}: {exception.Message}"; }
            return m_report;
        }
    }

    private string Query() {
        if (m_unavailable is not null) { return m_unavailable; }
        var counts = new VkDeviceFaultCountsExt { SType = 1000341001 };
        var result = m_query(m_device, &counts, null);

        if (!result.IsSuccess()) { return $"vkGetDeviceFaultInfoEXT counts failed: {result}."; }

        var addresses = new VkDeviceFaultAddressInfoExt[checked((int)counts.AddressInfoCount)];
        var vendors = new VkDeviceFaultVendorInfoExt[checked((int)counts.VendorInfoCount)];
        // Only deviceFault was enabled: the driver must report zero binary bytes.
        counts.VendorBinarySize = 0;
        fixed (VkDeviceFaultAddressInfoExt* address = addresses)
        fixed (VkDeviceFaultVendorInfoExt* vendor = vendors) {
            var info = new VkDeviceFaultInfoExt { PAddressInfos = address, PVendorInfos = vendor, SType = 1000341002 };

            result = m_query(m_device, &counts, &info);
            if (!result.IsSuccess()) { return $"vkGetDeviceFaultInfoEXT details failed: {result}."; }
            var report = new StringBuilder(value: $"VK_EXT_device_fault: {Description(value: info.Description)} (query: {result}; addresses: {counts.AddressInfoCount}; vendor records: {counts.VendorInfoCount})");

            for (var index = 0; (index < Math.Min(val1: counts.AddressInfoCount, val2: ((uint)addresses.Length))); index++) {
                var entry = addresses[index];
                var kind = entry.AddressType switch {
                    0 => "none",
                    1 => "read invalid (page fault)",
                    2 => "write invalid (page fault)",
                    3 => "execute invalid (page fault)",
                    4 => "instruction pointer unknown",
                    5 => "instruction pointer invalid",
                    6 => "instruction pointer fault",
                    _ => "unknown",
                };

                report.Append(handler: $"\n  address[{index}]: {kind} (type {entry.AddressType}), address=0x{entry.ReportedAddress:x16}, precision=0x{entry.AddressPrecision:x}");
            }
            for (var index = 0; (index < Math.Min(val1: counts.VendorInfoCount, val2: ((uint)vendors.Length))); index++) {
                report.Append(handler: $"\n  vendor[{index}]: {Description(value: vendor[index].Description)}, code=0x{vendor[index].VendorFaultCode:x16}, data=0x{vendor[index].VendorFaultData:x16}");
            }
            return report.ToString();
        }
    }
    private static string Description(byte* value) {
        var bytes = new ReadOnlySpan<byte>(length: 256, pointer: value);
        var end = bytes.IndexOf(value: ((byte)0));

        return Encoding.UTF8.GetString(bytes: ((end < 0) ? bytes : bytes[..end]));
    }
}
