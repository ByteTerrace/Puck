using System.Buffers.Binary;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// One logical device's <c>VkPipelineCache</c>, seeded from the device's <see cref="GpuPipelineCacheFile"/>, passed to
/// every compute and graphics pipeline creation on the device, and written back by <see cref="Persist"/> and on
/// disposal. Created without the externally-synchronized flag, so the driver synchronizes it internally and pipelines
/// may be created on several threads at once. The file owns where the cache lives and when it is written; this type
/// owns only the native cache and the header check.
/// <para>
/// A file is loaded only when its header names this device: header version one, the device's vendor and device IDs
/// and its pipeline-cache UUID (<see cref="GpuDeviceIdentity.PipelineCacheUuid"/>). A file that fails that check, or
/// that the driver refuses, is reported by name and the cache starts empty. A cache the driver cannot create at all
/// leaves <see cref="Handle"/> zero, and pipelines are then created uncached: the cache never stops a device.
/// </para>
/// </summary>
public sealed unsafe class VulkanPipelineCache : IDisposable {
    // VK_UUID_SIZE: the length of pipelineCacheUUID.
    private const int UuidLength = 16;
    // VkPipelineCacheHeaderVersionOne: headerSize, headerVersion, vendorID, deviceID (four uint32) + pipelineCacheUUID.
    private const int HeaderLength = ((sizeof(uint) * 4) + UuidLength);
    private const uint HeaderVersionOne = 1u;
    private const uint StructureTypePipelineCacheCreateInfo = 17u;

    private readonly VulkanDeviceCommands m_device;
    private readonly GpuPipelineCacheFile m_file;
    private readonly Lock m_gate = new();

    private bool m_disposed;

    private VulkanPipelineCache(VulkanDeviceCommands device, nint handle, GpuPipelineCacheFile file) {
        Handle = handle;
        m_device = device;
        m_file = file;
    }

    /// <summary>Gets the native <c>VkPipelineCache</c> handle, or zero when the driver could not create one.</summary>
    public nint Handle { get; }

    private static nint CreateHandle(VulkanDeviceCommands device, byte[]? data, out VkResult result) {
        fixed (byte* initialData = data) {
            var createInfo = new VkPipelineCacheCreateInfo {
                InitialDataSize = ((nuint)(data?.Length ?? 0)),
                PInitialData = ((nint)initialData),
                SType = StructureTypePipelineCacheCreateInfo,
            };

            result = device.CreatePipelineCache(
                device.Handle,
                in createInfo,
                0,
                out var handle
            );

            return (result.IsSuccess()
                ? handle
                : 0
            );
        }
    }
    // Why a cache file's header does not describe this device, or null when it does.
    private static string? Refuse(ReadOnlySpan<byte> data, GpuDeviceIdentity identity) {
        if (data.Length < HeaderLength) {
            return $"{data.Length} bytes is shorter than the {HeaderLength}-byte cache header";
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(source: data);

        if ((headerSize < HeaderLength) || (headerSize > ((uint)data.Length))) {
            return $"its header claims {headerSize} bytes";
        }

        var headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(source: data[sizeof(uint)..]);

        if (headerVersion != HeaderVersionOne) {
            return $"its header version is {headerVersion}, not {HeaderVersionOne}";
        }

        var vendorId = BinaryPrimitives.ReadUInt32LittleEndian(source: data[(sizeof(uint) * 2)..]);
        var deviceId = BinaryPrimitives.ReadUInt32LittleEndian(source: data[(sizeof(uint) * 3)..]);

        if ((vendorId != identity.VendorId) || (deviceId != identity.DeviceId)) {
            return $"it was written for device {vendorId:x4}:{deviceId:x4}, not {identity.VendorId:x4}:{identity.DeviceId:x4}";
        }

        if (!string.Equals(
            a: Convert.ToHexStringLower(bytes: data.Slice(
                length: UuidLength,
                start: (sizeof(uint) * 4)
            )),
            b: identity.PipelineCacheUuid,
            comparisonType: StringComparison.Ordinal
        )) {
            return "its pipeline-cache UUID differs from the driver's";
        }

        return null;
    }
    // The cache's current data, or empty when the driver could not return it (reported).
    private static ReadOnlyMemory<byte> Serialize(VulkanPipelineCache cache) {
        var device = cache.m_device;
        nuint size = 0;
        var result = device.GetPipelineCacheData(
            device.Handle,
            cache.Handle,
            &size,
            null
        );

        if (result.IsSuccess() && (size > 0)) {
            var data = new byte[size];

            fixed (byte* pointer = data) {
                result = device.GetPipelineCacheData(
                    device.Handle,
                    cache.Handle,
                    &size,
                    pointer
                );
            }

            // VK_INCOMPLETE, a success code, means the cache grew between the two calls; the data written is still a
            // whole, valid cache.
            if (result.IsSuccess()) {
                return data.AsMemory(
                    length: ((int)size),
                    start: 0
                );
            }
        }

        Console.Error.WriteLine(value: $"[pipeline-cache] not written {cache.m_file.Path}: vkGetPipelineCacheData returned {result}");

        return ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Creates a device's pipeline cache, seeded from its file when the file describes this device.</summary>
    /// <param name="device">The logical device's command table.</param>
    /// <param name="identity">The physical device's identity, whose vendor, device and pipeline-cache UUID a file's
    /// header must match.</param>
    /// <param name="file">The device's cache file.</param>
    /// <returns>The cache; its <see cref="Handle"/> is zero when the driver could not create one.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static VulkanPipelineCache Create(VulkanDeviceCommands device, GpuDeviceIdentity identity, GpuPipelineCacheFile file) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: identity);
        ArgumentNullException.ThrowIfNull(argument: file);

        var data = file.Read();

        if ((data is not null) && (Refuse(
            data: data,
            identity: identity
        ) is { } reason)) {
            file.Refuse(reason: reason);
            data = null;
        }

        var handle = CreateHandle(
            data: data,
            device: device,
            result: out var result
        );

        if ((0 == handle) && (data is not null)) {
            file.Refuse(reason: $"vkCreatePipelineCache refused it ({result})");
            handle = CreateHandle(
                data: null,
                device: device,
                result: out result
            );
        }

        if (0 == handle) {
            Console.Error.WriteLine(value: $"[pipeline-cache] vulkan: vkCreatePipelineCache failed ({result}); pipelines are created uncached");
        }

        return new VulkanPipelineCache(
            device: device,
            file: file,
            handle: handle
        );
    }
    /// <summary>Counts one pipeline created through this cache.</summary>
    /// <param name="feedback">The driver's report on the creation; a report without the valid bit counts as a miss.</param>
    public void Count(in VkPipelineCreationFeedback feedback) =>
        m_file.Count(cacheHit: feedback.IsCacheHit);
    /// <summary>Writes the cache to its file, then destroys it. Call before the device is destroyed.</summary>
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            PersistLocked();
            m_disposed = true;
            m_device.Destroy(
                destroy: m_device.DestroyPipelineCache,
                handle: Handle
            );
        }
    }
    /// <summary>Writes the cache to its file when a creation missed it since the last write. Safe on any thread; a
    /// failed read of the cache data or a failed write is reported and leaves the previous file whole.</summary>
    public void Persist() {
        lock (m_gate) {
            if (!m_disposed) {
                PersistLocked();
            }
        }
    }

    private void PersistLocked() {
        if (0 == Handle) {
            return;
        }

        m_file.Persist(
            serialize: static cache => Serialize(cache: cache),
            state: this
        );
    }
}
