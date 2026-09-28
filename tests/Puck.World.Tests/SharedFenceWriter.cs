using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.World.Tests;

/// <summary>The Direct3D 11 producer the shared-fence laws order against a reader on another API: a device on the
/// default hardware adapter or the software (WARP) renderer that opens a consumer's shared texture and writes a pattern
/// into it with <c>UpdateSubresource</c>, which the immediate context queues on the GPU. It never waits on the CPU; only
/// a <see cref="Platform.Windows.Win32D3D11CompletionSignal"/> over its device orders its writes. It calls each
/// interface through its vtable slot, counted from the Windows SDK headers (<c>d3d11.h</c>, <c>d3d11_1.h</c>,
/// <c>dxgi.h</c>), so the suite carries no interop generator.</summary>
[SupportedOSPlatform("windows10.0.15063")]
internal sealed unsafe class SharedFenceWriter : IDisposable {
    private const int AdapterGetDescSlot = 8;
    private const int ContextUpdateSubresourceSlot = 48;
    private const int Device1OpenSharedResource1Slot = 48;
    private const int DxgiDeviceGetAdapterSlot = 7;
    // DXGI_ADAPTER_DESC: Description (WCHAR[128]), four UINT ids, three SIZE_T sizes, then AdapterLuid.
    private const int AdapterDescriptionBytes = 304;
    private const int AdapterLuidOffset = 296;
    private const uint DriverTypeHardware = 1;
    private const uint DriverTypeWarp = 5;
    private const uint SdkVersion = 7;
    private const int UnknownQueryInterfaceSlot = 0;
    private const int UnknownReleaseSlot = 2;

    private static readonly Guid Device1Iid = new(g: "a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
    private static readonly Guid DxgiDeviceIid = new(g: "54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid Texture2DIid = new(g: "6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly nint m_context;
    private readonly nint m_device;
    private readonly nint m_device1;
    private readonly List<nint> m_textures = [];

    private SharedFenceWriter(nint device, nint device1, nint context, long adapterLuid) {
        m_context = context;
        m_device = device;
        m_device1 = device1;
        AdapterLuid = adapterLuid;
    }

    /// <summary>Gets the LUID of the adapter the device is on, packed <c>(HighPart &lt;&lt; 32) | LowPart</c>.</summary>
    public long AdapterLuid { get; }
    /// <summary>Gets the immediate context, as the completion signal takes it.</summary>
    public nint Context => m_context;
    /// <summary>Gets the device, as the completion signal takes it.</summary>
    public nint Device => m_device;

    /// <summary>Creates a writer on the default hardware adapter, or on WARP; returns <see langword="null"/> when the
    /// host has no such device.</summary>
    /// <param name="warp">Whether the device is the software renderer.</param>
    /// <returns>The writer, owned by the caller, or <see langword="null"/>.</returns>
    public static SharedFenceWriter? TryCreate(bool warp) {
        nint device;
        nint context;
        var levels = stackalloc uint[2] { 0xB100u, 0xB000u };
        uint granted;
        var created = D3D11CreateDevice(
            adapter: 0,
            context: &context,
            device: &device,
            driverType: (warp
                ? DriverTypeWarp
                : DriverTypeHardware),
            featureLevelCount: 2,
            featureLevels: levels,
            flags: 0x20,
            granted: &granted,
            sdkVersion: SdkVersion,
            software: 0
        );

        if (created < 0) {
            return null;
        }

        try {
            var device1 = QueryInterface(
                iid: Device1Iid,
                unknown: device
            );
            var dxgiDevice = QueryInterface(
                iid: DxgiDeviceIid,
                unknown: device
            );
            nint adapter;

            try {
                Check(result: ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(
                    instance: dxgiDevice,
                    slot: DxgiDeviceGetAdapterSlot
                ))(dxgiDevice, &adapter));
            } finally {
                Release(unknown: dxgiDevice);
            }

            var description = stackalloc byte[AdapterDescriptionBytes];

            try {
                Check(result: ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(
                    instance: adapter,
                    slot: AdapterGetDescSlot
                ))(adapter, description));
            } finally {
                Release(unknown: adapter);
            }

            var lowPart = *((uint*)(description + AdapterLuidOffset));
            var highPart = *((int*)((description + AdapterLuidOffset) + 4));

            return new SharedFenceWriter(
                adapterLuid: (((long)highPart) << 32) | lowPart,
                context: context,
                device: device,
                device1: device1
            );
        } catch {
            Release(unknown: context);
            Release(unknown: device);

            throw;
        }
    }
    /// <summary>Opens a consumer's shared texture on this device.</summary>
    /// <param name="sharedHandle">The texture's shared NT handle.</param>
    /// <returns>The opened <c>ID3D11Texture2D*</c>, released with the writer.</returns>
    public nint Open(nint sharedHandle) {
        nint texture;
        var iid = Texture2DIid;

        Check(result: ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(
            instance: m_device1,
            slot: Device1OpenSharedResource1Slot
        ))(m_device1, sharedHandle, &iid, &texture));
        m_textures.Add(item: texture);

        return texture;
    }
    /// <summary>Queues a write of a tightly packed RGBA8 pattern into an opened texture's first subresource; nothing
    /// waits for it.</summary>
    /// <param name="target">The opened texture.</param>
    /// <param name="pixels">The pattern.</param>
    /// <param name="width">The width, in pixels.</param>
    public void Write(nint target, byte[] pixels, int width) {
        fixed (byte* data = pixels) {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)Slot(
                instance: m_context,
                slot: ContextUpdateSubresourceSlot
            ))(m_context, target, 0U, null, data, ((uint)(width * 4)), 0U);
        }
    }
    public void Dispose() {
        foreach (var texture in m_textures) {
            Release(unknown: texture);
        }

        Release(unknown: m_device1);
        Release(unknown: m_context);
        Release(unknown: m_device);
    }

    private static void Check(int result) {
        if (result < 0) {
            Marshal.ThrowExceptionForHR(errorCode: result);
        }
    }
    private static nint QueryInterface(nint unknown, Guid iid) {
        nint result;

        Check(result: ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(
            instance: unknown,
            slot: UnknownQueryInterfaceSlot
        ))(unknown, &iid, &result));

        return result;
    }
    private static void Release(nint unknown) {
        if (0 != unknown) {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(
                instance: unknown,
                slot: UnknownReleaseSlot
            ))(unknown);
        }
    }
    private static void* Slot(nint instance, int slot) => (*((void***)instance))[slot];
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, uint* featureLevels, uint featureLevelCount, uint sdkVersion, nint* device, uint* granted, nint* context);
}
