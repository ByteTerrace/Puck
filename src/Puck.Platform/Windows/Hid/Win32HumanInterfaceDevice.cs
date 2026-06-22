using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Input.Hid;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.HumanInterfaceDevice;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Puck.Platform.Windows.Hid;

/// <summary>
/// The Windows HID transport: an opened device interface exposing the report lengths, identity, and async
/// read/write/feature operations of <see cref="IHidDevice"/> over CsWin32 (SetupAPI enumeration, overlapped
/// <c>CreateFile</c> I/O, <c>HidP</c> caps). Created and enumerated through <see cref="Win32HidDeviceSource"/>.
/// </summary>
[SupportedOSPlatform(platformName: "windows5.1.2600")]
internal sealed class Win32HumanInterfaceDevice : IHidDevice, IEquatable<Win32HumanInterfaceDevice> {
    private const uint DISPOSED_FALSE = uint.MinValue;
    private const uint DISPOSED_TRUE = 1U;

    /// <summary>
    /// Opens a specific device interface by path and reads its capabilities. Returns <see langword="null"/> on
    /// any failure (unopenable, access denied, malformed cap set) rather than a half-constructed device, so a
    /// flaky peripheral can never become a black-hole instance. Button/value capability tables are not read —
    /// the report parsers work from raw reports — but the report lengths and usage are.
    /// </summary>
    /// <param name="devicePath">The device interface path (from <see cref="EnumerateInterfaces"/>).</param>
    /// <returns>An opened device, or <see langword="null"/> if it could not be opened.</returns>
    public static Win32HumanInterfaceDevice? Open(string devicePath) {
        ArgumentNullException.ThrowIfNull(devicePath);

        var fileStream = GetFileStream(
            devicePath: devicePath,
            errorCode: out var errorCode
        );

        if ((errorCode != WIN32_ERROR.NO_ERROR) || (fileStream is null)) {
            fileStream?.Dispose();

            return null;
        }

        var preparsedData = ((PHIDP_PREPARSED_DATA)nint.Zero);

        try {
            if (!PInvoke.HidD_GetAttributes(
                Attributes: out var attributes,
                HidDeviceObject: fileStream.SafeFileHandle
            )) {
                fileStream.Dispose();

                return null;
            }

            if (!PInvoke.HidD_GetPreparsedData(
                HidDeviceObject: fileStream.SafeFileHandle,
                PreparsedData: out preparsedData
            )) {
                fileStream.Dispose();

                return null;
            }

            if (NTSTATUS.HIDP_STATUS_SUCCESS != PInvoke.HidP_GetCaps(
                Capabilities: out var capabilities,
                PreparsedData: preparsedData
            )) {
                _ = PInvoke.HidD_FreePreparsedData(PreparsedData: preparsedData);
                fileStream.Dispose();

                return null;
            }

            return new Win32HumanInterfaceDevice(
                attributes: attributes,
                capabilities: capabilities,
                devicePath: devicePath,
                fileStream: fileStream,
                preparsedData: preparsedData
            );
        } catch {
            if (nint.Zero != preparsedData) {
                _ = PInvoke.HidD_FreePreparsedData(PreparsedData: preparsedData);
            }

            fileStream.Dispose();

            return null;
        }
    }

    [SupportedOSPlatform(platformName: "windows5.1.2600")]
    private static FileStream? GetFileStream(
        string devicePath,
        out WIN32_ERROR errorCode
    ) {
        var accessMode = FileAccess.ReadWrite;
        var deviceHandle = PInvoke.CreateFile(
            dwCreationDisposition: FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            dwDesiredAccess: ((uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE)),
            dwFlagsAndAttributes: FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
            dwShareMode: (FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE),
            hTemplateFile: default,
            lpFileName: devicePath,
            lpSecurityAttributes: null
        );
        var fileStream = default(FileStream?);

        errorCode = (deviceHandle.IsInvalid ? (WIN32_ERROR)Marshal.GetLastPInvokeError() : WIN32_ERROR.NO_ERROR);

        if (WIN32_ERROR.ERROR_ACCESS_DENIED == errorCode) { // try read-only
            deviceHandle = PInvoke.CreateFile(
                dwCreationDisposition: FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                dwDesiredAccess: ((uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ),
                dwFlagsAndAttributes: FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                dwShareMode: (FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE),
                hTemplateFile: default,
                lpFileName: devicePath,
                lpSecurityAttributes: null
            );
            errorCode = (deviceHandle.IsInvalid ? (WIN32_ERROR)Marshal.GetLastPInvokeError() : WIN32_ERROR.NO_ERROR);
            accessMode = FileAccess.Read;
        }

        if (WIN32_ERROR.ERROR_ACCESS_DENIED == errorCode) { // try write-only
            deviceHandle = PInvoke.CreateFile(
                dwCreationDisposition: FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                dwDesiredAccess: ((uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE),
                dwFlagsAndAttributes: FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                dwShareMode: (FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE),
                hTemplateFile: default,
                lpFileName: devicePath,
                lpSecurityAttributes: null
            );
            errorCode = (deviceHandle.IsInvalid ? (WIN32_ERROR)Marshal.GetLastPInvokeError() : WIN32_ERROR.NO_ERROR);
            accessMode = FileAccess.Write;
        }

        if (WIN32_ERROR.ERROR_SHARING_VIOLATION == errorCode) { // try read-only, read share
            deviceHandle = PInvoke.CreateFile(
                dwCreationDisposition: FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                dwDesiredAccess: ((uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ),
                dwFlagsAndAttributes: FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                dwShareMode: FILE_SHARE_MODE.FILE_SHARE_READ,
                hTemplateFile: default,
                lpFileName: devicePath,
                lpSecurityAttributes: null
            );
            errorCode = (deviceHandle.IsInvalid ? (WIN32_ERROR)Marshal.GetLastPInvokeError() : WIN32_ERROR.NO_ERROR);
            accessMode = FileAccess.Read;
        }

        if (WIN32_ERROR.ERROR_SHARING_VIOLATION == errorCode) { // try read-only, none share
            deviceHandle = PInvoke.CreateFile(
                dwCreationDisposition: FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                dwDesiredAccess: ((uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ),
                dwFlagsAndAttributes: FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                dwShareMode: FILE_SHARE_MODE.FILE_SHARE_NONE,
                hTemplateFile: default,
                lpFileName: devicePath,
                lpSecurityAttributes: null
            );
            errorCode = (deviceHandle.IsInvalid ? (WIN32_ERROR)Marshal.GetLastPInvokeError() : WIN32_ERROR.NO_ERROR);
            accessMode = FileAccess.Read;
        }

        if (WIN32_ERROR.NO_ERROR == errorCode) {
            fileStream = new FileStream(
                access: accessMode,
                bufferSize: 0,
                handle: deviceHandle,
                isAsync: true
            );
        }

        return fileStream;
    }
    [SupportedOSPlatform(platformName: "windows5.0")]
    private unsafe static string GetPath(
        SafeHandle deviceInfoSet,
        in SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        out WIN32_ERROR errorCode
    ) {
        var deviceInterfaceDetailDataSize = uint.MinValue;
        var devicePath = string.Empty;

        _ = PInvoke.SetupDiGetDeviceInterfaceDetail(
            DeviceInterfaceData: in deviceInterfaceData,
            DeviceInterfaceDetailData: null,
            DeviceInterfaceDetailDataSize: uint.MinValue,
            DeviceInfoData: null,
            DeviceInfoSet: deviceInfoSet,
            RequiredSize: &deviceInterfaceDetailDataSize
        );
        errorCode = ((WIN32_ERROR)Marshal.GetLastPInvokeError());

        if (WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER == errorCode) {
            var deviceInterfaceDetailDataBuffer = ArrayPool<byte>.Shared.Rent(minimumLength: ((int)deviceInterfaceDetailDataSize));

            try {
                fixed (byte* pDeviceInterfaceDetailDataBuffer = deviceInterfaceDetailDataBuffer) {
                    var deviceInterfaceDetail = ((SP_DEVICE_INTERFACE_DETAIL_DATA_W*)pDeviceInterfaceDetailDataBuffer);

                    deviceInterfaceDetail->cbSize = ((uint)sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W));

                    if (!PInvoke.SetupDiGetDeviceInterfaceDetail(
                        DeviceInterfaceData: in deviceInterfaceData,
                        DeviceInterfaceDetailData: deviceInterfaceDetail,
                        DeviceInterfaceDetailDataSize: deviceInterfaceDetailDataSize,
                        DeviceInfoData: null,
                        DeviceInfoSet: deviceInfoSet,
                        RequiredSize: &deviceInterfaceDetailDataSize
                    )) {
                        errorCode = ((WIN32_ERROR)Marshal.GetLastPInvokeError());
                    } else {
                        devicePath = new string(value: MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value: ((char*)Unsafe.AsPointer(ref deviceInterfaceDetail->DevicePath.e0))));
                        errorCode = WIN32_ERROR.NO_ERROR;
                    }
                }
            } finally {
                ArrayPool<byte>.Shared.Return(array: deviceInterfaceDetailDataBuffer);
            }
        }

        return devicePath;
    }

    /// <summary>
    /// Enumerates present HID device interfaces as lightweight <see cref="HidDeviceInfo"/> records (path +
    /// VID/PID parsed from the path) <em>without opening any device</em>. Callers filter on VID/PID and open
    /// only the handful they care about via <see cref="Open"/>, instead of opening every keyboard and mouse on
    /// the system.
    /// </summary>
    /// <returns>The present HID device interfaces.</returns>
    public static IEnumerable<HidDeviceInfo> EnumerateInterfaces() {
        using var deviceInfoSet = PInvoke.SetupDiGetClassDevs(
            ClassGuid: PInvoke.GUID_DEVINTERFACE_HID,
            Enumerator: null,
            Flags: (SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT),
            hwndParent: HWND.Null
        );

        if (deviceInfoSet.IsInvalid) {
            HidThrowHelper.ThrowLastWin32Exception(
                errorCode: ((WIN32_ERROR)Marshal.GetLastPInvokeError()),
                source: nameof(PInvoke.SetupDiGetClassDevs)
            );
        }

        var memberIndex = uint.MinValue;

        while (true) {
            var deviceInterfaceData = new SP_DEVICE_INTERFACE_DATA() {
                cbSize = ((uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()),
            };

            if (!PInvoke.SetupDiEnumDeviceInterfaces(
                DeviceInfoData: null,
                DeviceInfoSet: deviceInfoSet,
                DeviceInterfaceData: ref deviceInterfaceData,
                InterfaceClassGuid: in PInvoke.GUID_DEVINTERFACE_HID,
                MemberIndex: memberIndex++
            )) {
                var errorCode = ((WIN32_ERROR)Marshal.GetLastPInvokeError());

                if (WIN32_ERROR.ERROR_NO_MORE_ITEMS != errorCode) {
                    HidThrowHelper.ThrowLastWin32Exception(
                        errorCode: errorCode,
                        source: nameof(PInvoke.SetupDiEnumDeviceInterfaces)
                    );
                }

                break;
            }

            var devicePath = GetPath(
                deviceInfoSet: deviceInfoSet,
                deviceInterfaceData: in deviceInterfaceData,
                errorCode: out var pathErrorCode
            );

            if ((pathErrorCode == WIN32_ERROR.NO_ERROR) && (devicePath.Length > 0)) {
                _ = TryGetVidPid(
                    path: devicePath,
                    productId: out var productId,
                    vendorId: out var vendorId
                );

                yield return new HidDeviceInfo(
                    Path: devicePath,
                    ProductId: productId,
                    VendorId: vendorId
                );
            }
        }
    }

    private static bool TryGetVidPid(string path, out ushort vendorId, out ushort productId) {
        var hasVendor = TryParseHexField(path: path, token: "vid_", value: out vendorId);
        var hasProduct = TryParseHexField(path: path, token: "pid_", value: out productId);

        return (hasVendor && hasProduct);
    }
    private static bool TryParseHexField(string path, string token, out ushort value) {
        value = 0;

        var index = path.IndexOf(value: token, comparisonType: StringComparison.OrdinalIgnoreCase);

        if (index < 0) {
            return false;
        }

        var start = (index + token.Length);

        if ((start + 4) > path.Length) {
            return false;
        }

        return ushort.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out value,
            s: path.AsSpan(start: start, length: 4),
            style: NumberStyles.HexNumber
        );
    }

    private readonly HIDD_ATTRIBUTES m_attributes;
    private readonly HIDP_CAPS m_capabilities;
    private readonly string m_devicePath;
    private readonly FileStream? m_fileStream;
    private uint m_isDisposed = DISPOSED_FALSE;
    private PHIDP_PREPARSED_DATA m_preparsedData = ((PHIDP_PREPARSED_DATA)nint.Zero);

    /// <inheritdoc />
    public string DevicePath { get => m_devicePath; }
    /// <inheritdoc />
    public ushort VendorId { get => m_attributes.VendorID; }
    /// <inheritdoc />
    public ushort ProductId { get => m_attributes.ProductID; }
    /// <inheritdoc />
    public ushort UsagePage { get => m_capabilities.UsagePage; }
    /// <inheritdoc />
    public ushort Usage { get => m_capabilities.Usage; }
    /// <inheritdoc />
    public int InputReportByteLength { get => m_capabilities.InputReportByteLength; }
    /// <inheritdoc />
    public int OutputReportByteLength { get => m_capabilities.OutputReportByteLength; }

    private Win32HumanInterfaceDevice(
        HIDD_ATTRIBUTES attributes,
        HIDP_CAPS capabilities,
        string devicePath,
        FileStream? fileStream,
        PHIDP_PREPARSED_DATA preparsedData
    ) {
        m_attributes = attributes;
        m_capabilities = capabilities;
        m_devicePath = devicePath;
        m_fileStream = fileStream;
        m_preparsedData = preparsedData;
    }

    ~Win32HumanInterfaceDevice() => Dispose(disposing: false);

    private void Dispose(bool disposing) {
        if (DISPOSED_FALSE == Interlocked.CompareExchange(
            comparand: DISPOSED_FALSE,
            location1: ref m_isDisposed,
            value: DISPOSED_TRUE
        )) {
            if (disposing) {
                m_fileStream?.Dispose();
            }

            if (nint.Zero != m_preparsedData) {
                _ = PInvoke.HidD_FreePreparsedData(PreparsedData: m_preparsedData);
                m_preparsedData = ((PHIDP_PREPARSED_DATA)nint.Zero);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(obj: this);
    }
    /// <summary>Determines whether the other device wraps the same device interface path.</summary>
    /// <param name="other">The device to compare with.</param>
    /// <returns><see langword="true"/> if both wrap the same device path; otherwise <see langword="false"/>.</returns>
    public bool Equals(Win32HumanInterfaceDevice? other) => ((other is not null) && string.Equals(
        a: m_devicePath,
        b: other.m_devicePath,
        comparisonType: StringComparison.Ordinal
    ));
    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(other: (obj as Win32HumanInterfaceDevice));
    /// <inheritdoc />
    public override int GetHashCode() => (m_devicePath?.GetHashCode(comparisonType: StringComparison.Ordinal) ?? 0);
    /// <inheritdoc />
    public ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    ) {
        var fileStream = m_fileStream;

        return ((fileStream is null)
            ? ValueTask.FromResult(result: 0)
            : fileStream.ReadAsync(
                buffer: buffer,
                cancellationToken: cancellationToken
            ));
    }
    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        bool throwOnTimeout = false,
        int timeoutInMilliseconds = 120,
        CancellationToken cancellationToken = default
    ) {
        var fileStream = m_fileStream;
        var numberOfBytesRead = 0;

        if (fileStream is not null) {
            using var limitCancellationTokenSource = new CancellationTokenSource(millisecondsDelay: timeoutInMilliseconds);

            try {
                using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    token1: cancellationToken,
                    token2: limitCancellationTokenSource.Token
                );

                numberOfBytesRead = await fileStream.ReadAsync(
                    buffer: buffer,
                    cancellationToken: linkedCancellationTokenSource.Token
                );
            } catch (OperationCanceledException) when (limitCancellationTokenSource.IsCancellationRequested) {
                if (throwOnTimeout) {
                    throw;
                }
            }
        }

        return numberOfBytesRead;
    }
    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    ) {
        var fileStream = m_fileStream;

        if (fileStream is not null) {
            await fileStream.WriteAsync(
                buffer: buffer,
                cancellationToken: cancellationToken
            );
        }
    }

    /// <inheritdoc />
    [SupportedOSPlatform(platformName: "windows5.1.2600")]
    public unsafe bool TryGetFeatureReport(Span<byte> buffer) {
        var fileStream = m_fileStream;

        if ((fileStream is null) || buffer.IsEmpty) {
            return false;
        }

        fixed (byte* reportBuffer = buffer) {
            return PInvoke.HidD_GetFeature(
                HidDeviceObject: fileStream.SafeFileHandle,
                ReportBuffer: reportBuffer,
                ReportBufferLength: ((uint)buffer.Length)
            );
        }
    }
}
