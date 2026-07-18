using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Puck.Abstractions.Lighting;
using Windows.Win32;
using Windows.Win32.Devices.HumanInterfaceDevice;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Puck.Platform.Windows.Lighting;

/// <summary>
/// A from-scratch Win32 HID LampArray device (HID Usage Tables 1.4, "Lighting And Illumination" page
/// <c>0x59</c>): an opened lamp array whose feature reports are read and written over CsWin32 (SetupAPI-enumerated
/// interface, <c>CreateFile</c>, <c>HidP</c> preparsed-data caps, <c>HidD_GetFeature</c>/<c>HidD_SetFeature</c>).
/// Report ids are discovered from the device's own value caps (never hardcoded per model), so the code drives any
/// conformant LampArray — the G915 keyboard, a mouse, a light strip — through the same path. The per-report byte
/// layout it packs is the standard HID LampArray reference report structure, which every conformant device shares.
/// </summary>
/// <remarks>
/// The read path uses <c>HidP_GetUsageValue</c> to pull each attribute by its usage, so field offsets never need
/// to be assumed. The update reports (multi-update, range-update, control) are packed to the reference layout at
/// their discovered report ids. Opened and enumerated through <see cref="Win32LampArrayDeviceSource"/>.
/// </remarks>
[SupportedOSPlatform(platformName: "windows5.1.2600")]
internal sealed class Win32LampArrayDevice : ILampArrayDevice {
    // "Lighting And Illumination" usage page and its usages (HID Usage Tables 1.4, chapter 26).
    private const ushort LightingPage = 0x59;
    private const ushort UsageAutonomousMode = 0x71;
    private const ushort UsageBlueLevelCount = 0x2A;
    private const ushort UsageBoundingBoxDepth = 0x06;
    private const ushort UsageBoundingBoxHeight = 0x05;
    private const ushort UsageBoundingBoxWidth = 0x04;
    private const ushort UsageGreenLevelCount = 0x29;
    private const ushort UsageInputBinding = 0x2D;
    private const ushort UsageIntensityLevelCount = 0x2B;
    private const ushort UsageIsProgrammable = 0x2C;
    private const ushort UsageLampArray = 0x01;
    private const ushort UsageLampArrayKind = 0x07;
    private const ushort UsageLampCount = 0x03;
    private const ushort UsageLampId = 0x21;
    private const ushort UsageLampIdStart = 0x61;
    private const ushort UsageLampPurposes = 0x26;
    private const ushort UsageMinUpdateInterval = 0x08;
    private const ushort UsagePositionX = 0x23;
    private const ushort UsagePositionY = 0x24;
    private const ushort UsagePositionZ = 0x25;
    private const ushort UsageRedLevelCount = 0x28;
    private const ushort UsageUpdateLatency = 0x27;

    // The keyboard/keypad usage page; a keyboard lamp's InputBinding usage rides this page (the HID report carries
    // only the usage, so the page is inferred from the device kind).
    private const ushort KeyboardUsagePage = 0x07;
    // A mouse lamp's InputBinding usage rides the generic Button page.
    private const ushort ButtonUsagePage = 0x09;

    // LampMultiUpdateReport addresses up to eight lamps per feature write (HID LampArray fixed batch size).
    private const int MultiUpdateBatch = 8;
    // LampUpdateFlags: bit 0 (LampUpdateComplete) tells the device to apply the accumulated update immediately.
    private const byte LampUpdateComplete = 0x01;
    private const uint DISPOSED_FALSE = uint.MinValue;
    private const uint DISPOSED_TRUE = 1U;

    /// <summary>
    /// Opens a HID interface by path, verifies it is a LampArray top-level collection, reads its attributes and
    /// per-lamp table, and returns a ready device. Returns <see langword="null"/> for a non-LampArray interface or
    /// any open/parse failure, so a caller can probe every HID path and keep only the lamp arrays.
    /// </summary>
    /// <param name="devicePath">The device interface path (from a HID interface enumeration).</param>
    /// <returns>An opened lamp array, or <see langword="null"/> if the interface is not a usable LampArray.</returns>
    public unsafe static Win32LampArrayDevice? TryOpen(string devicePath) {
        ArgumentNullException.ThrowIfNull(devicePath);

        var deviceHandle = OpenHandle(devicePath: devicePath);

        if (deviceHandle is null) {
            return null;
        }

        var preparsedData = ((PHIDP_PREPARSED_DATA)nint.Zero);

        try {
            if (!PInvoke.HidD_GetPreparsedData(
                HidDeviceObject: deviceHandle,
                PreparsedData: out preparsedData
            )) {
                deviceHandle.Dispose();

                return null;
            }

            if (NTSTATUS.HIDP_STATUS_SUCCESS != PInvoke.HidP_GetCaps(
                Capabilities: out var capabilities,
                PreparsedData: preparsedData
            )) {
                _ = PInvoke.HidD_FreePreparsedData(PreparsedData: preparsedData);
                deviceHandle.Dispose();

                return null;
            }

            if ((capabilities.UsagePage != LightingPage) || (capabilities.Usage != UsageLampArray)) {
                _ = PInvoke.HidD_FreePreparsedData(PreparsedData: preparsedData);
                deviceHandle.Dispose();

                return null;
            }

            var device = new Win32LampArrayDevice(
                capabilities: capabilities,
                deviceHandle: deviceHandle,
                devicePath: devicePath,
                preparsedData: preparsedData
            );

            if (!device.Initialize()) {
                device.Dispose();

                return null;
            }

            return device;
        } catch {
            if (nint.Zero != preparsedData) {
                _ = PInvoke.HidD_FreePreparsedData(PreparsedData: preparsedData);
            }

            deviceHandle.Dispose();

            return null;
        }
    }

    // Opens the HID interface for feature-report I/O, trying progressively looser access. A LampArray is usually
    // held by the OS Dynamic Lighting stack (or a vendor service), so a plain read/write open fails with a sharing
    // violation; a zero-access open still permits HidD_GetFeature/SetFeature and preparsed-data reads, which is all
    // this device needs. Returns null when no access combination opens it.
    [SupportedOSPlatform(platformName: "windows5.1.2600")]
    private static SafeFileHandle? OpenHandle(string devicePath) {
        ReadOnlySpan<uint> accessModes = [
            0U,
            ((uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE)),
            ((uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE),
            ((uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ),
        ];

        foreach (var access in accessModes) {
            var handle = PInvoke.CreateFile(
                dwCreationDisposition: FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                dwDesiredAccess: access,
                dwFlagsAndAttributes: default,
                dwShareMode: FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                hTemplateFile: default,
                lpFileName: devicePath,
                lpSecurityAttributes: null
            );

            if (!handle.IsInvalid) {
                return handle;
            }

            handle.Dispose();
        }

        return null;
    }

    private readonly HIDP_CAPS m_capabilities;
    private readonly SafeFileHandle m_deviceHandle;
    private readonly string m_devicePath;
    private readonly byte[] m_scratch;
    private LampInfo[] m_lamps = [];
    private LampArrayKind m_kind = LampArrayKind.Undefined;
    private int m_boundingBoxWidth;
    private int m_boundingBoxHeight;
    private int m_boundingBoxDepth;
    private int m_minUpdateIntervalMs;
    // Feature report ids discovered from the value caps (0 = not present on this device).
    private byte m_attributesReportId;
    private byte m_requestReportId;
    private byte m_responseReportId;
    private byte m_multiUpdateReportId;
    private byte m_rangeUpdateReportId;
    private byte m_controlReportId;
    // Per-report byte length (including the leading report id byte), keyed by report id.
    private readonly Dictionary<byte, int> m_reportLengthById = [];
    private uint m_isDisposed = DISPOSED_FALSE;
    private PHIDP_PREPARSED_DATA m_preparsedData;

    private Win32LampArrayDevice(
        HIDP_CAPS capabilities,
        SafeFileHandle deviceHandle,
        string devicePath,
        PHIDP_PREPARSED_DATA preparsedData
    ) {
        m_capabilities = capabilities;
        m_deviceHandle = deviceHandle;
        m_devicePath = devicePath;
        m_preparsedData = preparsedData;
        m_scratch = new byte[Math.Max(val1: (int)capabilities.FeatureReportByteLength, val2: 1)];
    }

    /// <inheritdoc />
    public string DeviceId { get => m_devicePath; }
    /// <inheritdoc />
    public LampArrayKind Kind { get => m_kind; }
    /// <inheritdoc />
    public int LampCount { get => m_lamps.Length; }
    /// <inheritdoc />
    public int MinUpdateIntervalInMilliseconds { get => m_minUpdateIntervalMs; }

    /// <inheritdoc />
    public bool TryGetLampInfo(int index, out LampInfo info) {
        if ((index < 0) || (index >= m_lamps.Length)) {
            info = default;

            return false;
        }

        info = m_lamps[index];

        return true;
    }

    // Discovers the feature report ids and lengths, reads the array attributes, then caches every lamp's info.
    private bool Initialize() {
        DiscoverReports();

        if ((m_attributesReportId == 0) || (m_responseReportId == 0) || (m_requestReportId == 0)) {
            return false;
        }

        if (!ReadAttributes(lampCount: out var lampCount)) {
            return false;
        }

        var lamps = new LampInfo[lampCount];

        for (var index = 0; (index < lampCount); index++) {
            if (!ReadLampInfo(lampId: index, info: out lamps[index])) {
                // A gap in the lamp table still yields a usable device; leave the unread lamp at its default.
                lamps[index] = new LampInfo(
                    LampId: index,
                    Position: default,
                    Purposes: LampPurposes.None,
                    InputBindingUsagePage: 0,
                    InputBindingUsage: 0,
                    RedLevelCount: 0,
                    GreenLevelCount: 0,
                    BlueLevelCount: 0,
                    IntensityLevelCount: 0,
                    IsProgrammable: false,
                    UpdateLatencyInMilliseconds: 0
                );
            }
        }

        m_lamps = lamps;

        return true;
    }

    // Walks the feature value caps once: records each report's byte length and picks the report id for each report
    // kind by a usage that is unique to it (LampArrayKind → attributes, PositionX → response, RedUpdateChannel →
    // multi-update, LampIdStart → range-update, AutonomousMode → control). The request report is the remaining one
    // that carries LampId but is neither response nor multi-update.
    private unsafe void DiscoverReports() {
        var capsLength = m_capabilities.NumberFeatureValueCaps;

        if (capsLength == 0) {
            return;
        }

        var valueCaps = new HIDP_VALUE_CAPS[capsLength];
        var lampIdReportIds = new HashSet<byte>();

        fixed (HIDP_VALUE_CAPS* pValueCaps = valueCaps) {
            if (NTSTATUS.HIDP_STATUS_SUCCESS != PInvoke.HidP_GetValueCaps(
                ReportType: HIDP_REPORT_TYPE.HidP_Feature,
                ValueCaps: pValueCaps,
                ValueCapsLength: ref capsLength,
                PreparsedData: m_preparsedData
            )) {
                return;
            }
        }

        for (var index = 0; (index < capsLength); index++) {
            ref var cap = ref valueCaps[index];

            if (cap.UsagePage != LightingPage) {
                continue;
            }

            var usage = (cap.IsRange ? cap.Anonymous.Range.UsageMin : cap.Anonymous.NotRange.Usage);
            var reportCount = ((cap.ReportCount == 0) ? (ushort)1 : cap.ReportCount);

            // Accumulate this field's bit span into its report's running length (the reports are byte-packed with
            // no gaps, so a plain sum of field widths is exact).
            m_reportLengthById.TryGetValue(key: cap.ReportID, value: out var bits);
            m_reportLengthById[cap.ReportID] = (bits + (cap.BitSize * reportCount));

            switch (usage) {
                case UsageLampArrayKind:
                    m_attributesReportId = cap.ReportID;

                    break;
                case UsagePositionX:
                    m_responseReportId = cap.ReportID;

                    break;
                case UsageLampIdStart:
                    m_rangeUpdateReportId = cap.ReportID;

                    break;
                case UsageAutonomousMode:
                    m_controlReportId = cap.ReportID;

                    break;
                case UsageLampId:
                    // The lamp-id ARRAY (report count >= 2) is unique to the multi-update report; the color-channel
                    // usages (0x51..) are NOT a usable signature because the range-update report carries them too.
                    if (reportCount >= 2) {
                        m_multiUpdateReportId = cap.ReportID;
                    }

                    _ = lampIdReportIds.Add(item: cap.ReportID);

                    break;
                default:
                    break;
            }
        }

        foreach (var reportId in lampIdReportIds) {
            if ((reportId != m_responseReportId) && (reportId != m_multiUpdateReportId)) {
                m_requestReportId = reportId;

                break;
            }
        }

        // Convert accumulated bit spans into byte lengths, including the leading report id byte.
        foreach (var reportId in m_reportLengthById.Keys.ToArray()) {
            m_reportLengthById[reportId] = (1 + ((m_reportLengthById[reportId] + 7) / 8));
        }
    }
    private int ReportLength(byte reportId) {
        return (m_reportLengthById.TryGetValue(
            key: reportId,
            value: out var length
        )
            ? length
            : m_scratch.Length);
    }
    private bool ReadAttributes(out int lampCount) {
        lampCount = 0;

        if (!GetFeature(reportId: m_attributesReportId)) {
            return false;
        }

        if (!TryGetUsageValue(usage: UsageLampCount, value: out var count)) {
            return false;
        }

        lampCount = ((int)count);

        _ = TryGetUsageValue(usage: UsageBoundingBoxWidth, value: out var width);
        _ = TryGetUsageValue(usage: UsageBoundingBoxHeight, value: out var height);
        _ = TryGetUsageValue(usage: UsageBoundingBoxDepth, value: out var depth);
        _ = TryGetUsageValue(usage: UsageLampArrayKind, value: out var kind);
        _ = TryGetUsageValue(usage: UsageMinUpdateInterval, value: out var minInterval);

        m_boundingBoxWidth = ((int)width);
        m_boundingBoxHeight = ((int)height);
        m_boundingBoxDepth = ((int)depth);
        m_kind = ToKind(value: kind);
        m_minUpdateIntervalMs = ((int)((minInterval + 999U) / 1000U));

        return (lampCount > 0);
    }
    private unsafe bool ReadLampInfo(int lampId, out LampInfo info) {
        info = default;

        // Ask for the lamp by id (LampAttributesRequestReport), then read back its attributes
        // (LampAttributesResponseReport).
        Array.Clear(array: m_scratch);
        m_scratch[0] = m_requestReportId;
        m_scratch[1] = ((byte)(lampId & 0xFF));
        m_scratch[2] = ((byte)((lampId >> 8) & 0xFF));

        if (!SetFeature(reportId: m_requestReportId)) {
            return false;
        }

        if (!GetFeature(reportId: m_responseReportId)) {
            return false;
        }

        _ = TryGetUsageValue(usage: UsagePositionX, value: out var posX);
        _ = TryGetUsageValue(usage: UsagePositionY, value: out var posY);
        _ = TryGetUsageValue(usage: UsagePositionZ, value: out var posZ);
        _ = TryGetUsageValue(usage: UsageLampPurposes, value: out var purposes);
        _ = TryGetUsageValue(usage: UsageUpdateLatency, value: out var latency);
        _ = TryGetUsageValue(usage: UsageRedLevelCount, value: out var redLevels);
        _ = TryGetUsageValue(usage: UsageGreenLevelCount, value: out var greenLevels);
        _ = TryGetUsageValue(usage: UsageBlueLevelCount, value: out var blueLevels);
        _ = TryGetUsageValue(usage: UsageIntensityLevelCount, value: out var intensityLevels);
        _ = TryGetUsageValue(usage: UsageIsProgrammable, value: out var isProgrammable);
        _ = TryGetUsageValue(usage: UsageInputBinding, value: out var inputBinding);

        var bindingUsage = ((ushort)inputBinding);
        var bindingPage = ((bindingUsage == 0)
            ? (ushort)0
            : ((m_kind == LampArrayKind.Mouse) ? ButtonUsagePage : KeyboardUsagePage));

        info = new LampInfo(
            LampId: lampId,
            Position: new LampPosition(
                X: Normalize(value: posX, extent: m_boundingBoxWidth),
                Y: Normalize(value: posY, extent: m_boundingBoxHeight),
                Z: Normalize(value: posZ, extent: m_boundingBoxDepth)
            ),
            Purposes: ((LampPurposes)purposes),
            InputBindingUsagePage: bindingPage,
            InputBindingUsage: bindingUsage,
            RedLevelCount: ((byte)redLevels),
            GreenLevelCount: ((byte)greenLevels),
            BlueLevelCount: ((byte)blueLevels),
            IntensityLevelCount: ((byte)intensityLevels),
            IsProgrammable: (isProgrammable != 0),
            UpdateLatencyInMilliseconds: ((int)((latency + 999U) / 1000U))
        );

        return true;
    }

    /// <inheritdoc />
    public void UpdateLamps(ReadOnlySpan<int> lampIds, ReadOnlySpan<LampColor> colors) {
        if (lampIds.Length != colors.Length) {
            throw new ArgumentException(message: "lampIds and colors must be the same length.", paramName: nameof(colors));
        }

        if ((m_multiUpdateReportId == 0) || lampIds.IsEmpty) {
            return;
        }

        for (var start = 0; (start < lampIds.Length); start += MultiUpdateBatch) {
            var count = Math.Min(val1: MultiUpdateBatch, val2: (lampIds.Length - start));

            WriteMultiUpdate(
                colors: colors.Slice(start: start, length: count),
                lampIds: lampIds.Slice(start: start, length: count)
            );
        }
    }

    // Packs and writes one LampMultiUpdateReport batch (<= 8 lamps): lampCount, flags, then eight little-endian
    // lamp ids, then eight RGBI color quads — the HID LampArray reference report layout.
    private void WriteMultiUpdate(ReadOnlySpan<int> lampIds, ReadOnlySpan<LampColor> colors) {
        Array.Clear(array: m_scratch);

        m_scratch[0] = m_multiUpdateReportId;
        m_scratch[1] = ((byte)lampIds.Length);
        m_scratch[2] = LampUpdateComplete;

        const int idsOffset = 3;
        var colorsOffset = (idsOffset + (MultiUpdateBatch * 2));

        for (var index = 0; (index < lampIds.Length); index++) {
            var id = lampIds[index];

            m_scratch[(idsOffset + (index * 2))] = ((byte)(id & 0xFF));
            m_scratch[((idsOffset + (index * 2)) + 1)] = ((byte)((id >> 8) & 0xFF));

            var color = colors[index];
            var slot = (colorsOffset + (index * 4));

            m_scratch[slot] = color.Red;
            m_scratch[(slot + 1)] = color.Green;
            m_scratch[(slot + 2)] = color.Blue;
            m_scratch[(slot + 3)] = color.Intensity;
        }

        _ = SetFeature(reportId: m_multiUpdateReportId);
    }

    /// <inheritdoc />
    public void UpdateAllLamps(LampColor color) {
        var lampCount = m_lamps.Length;

        if (lampCount == 0) {
            return;
        }

        // A single LampRangeUpdateReport fills the whole contiguous id range in one feature write.
        if (m_rangeUpdateReportId != 0) {
            Array.Clear(array: m_scratch);

            m_scratch[0] = m_rangeUpdateReportId;
            m_scratch[1] = LampUpdateComplete;
            m_scratch[2] = 0; // LampIdStart low
            m_scratch[3] = 0; // LampIdStart high
            m_scratch[4] = ((byte)((lampCount - 1) & 0xFF)); // LampIdEnd low
            m_scratch[5] = ((byte)(((lampCount - 1) >> 8) & 0xFF)); // LampIdEnd high
            m_scratch[6] = color.Red;
            m_scratch[7] = color.Green;
            m_scratch[8] = color.Blue;
            m_scratch[9] = color.Intensity;

            _ = SetFeature(reportId: m_rangeUpdateReportId);

            return;
        }

        // Fallback for a device without a range report: batch every lamp through multi-update.
        var ids = ((lampCount <= 256) ? stackalloc int[lampCount] : new int[lampCount]);
        var fill = ((lampCount <= 256) ? stackalloc LampColor[lampCount] : new LampColor[lampCount]);

        for (var index = 0; (index < lampCount); index++) {
            ids[index] = index;
            fill[index] = color;
        }

        UpdateLamps(lampIds: ids, colors: fill);
    }

    /// <inheritdoc />
    public bool TrySetAutonomousMode(bool enabled) {
        if (m_controlReportId == 0) {
            return false;
        }

        Array.Clear(array: m_scratch);

        m_scratch[0] = m_controlReportId;
        m_scratch[1] = ((byte)(enabled ? 1 : 0));

        return SetFeature(reportId: m_controlReportId);
    }

    private unsafe bool GetFeature(byte reportId) {
        m_scratch[0] = reportId;

        // GetFeature and HidP reads use the collection's MAX feature report length; the driver selects the actual
        // report from the id byte. Only SetFeature needs the specific report length.
        fixed (byte* pReport = m_scratch) {
            return PInvoke.HidD_GetFeature(
                HidDeviceObject: m_deviceHandle,
                ReportBuffer: pReport,
                ReportBufferLength: m_capabilities.FeatureReportByteLength
            );
        }
    }
    private unsafe bool SetFeature(byte reportId) {
        fixed (byte* pReport = m_scratch) {
            return PInvoke.HidD_SetFeature(
                HidDeviceObject: m_deviceHandle,
                ReportBuffer: pReport,
                ReportBufferLength: ((uint)ReportLength(reportId: reportId))
            );
        }
    }

    // Reads one usage value out of the report currently sitting in the scratch buffer. HidP_GetUsageValue requires
    // the ReportLength to be the collection's MAX feature report length (it selects the report from the id byte).
    private unsafe bool TryGetUsageValue(ushort usage, out uint value) {
        fixed (byte* pReport = m_scratch) {
            return (NTSTATUS.HIDP_STATUS_SUCCESS == PInvoke.HidP_GetUsageValue(
                ReportType: HIDP_REPORT_TYPE.HidP_Feature,
                UsagePage: LightingPage,
                LinkCollection: 0,
                Usage: usage,
                UsageValue: out value,
                PreparsedData: m_preparsedData,
                Report: new PSTR(value: pReport),
                ReportLength: m_capabilities.FeatureReportByteLength
            ));
        }
    }
    private static float Normalize(uint value, int extent) {
        return ((extent <= 0) ? 0.5f : Math.Clamp(value: (value / (float)extent), min: 0f, max: 1f));
    }
    private static LampArrayKind ToKind(uint value) {
        return (Enum.IsDefined(value: ((LampArrayKind)value)) ? ((LampArrayKind)value) : LampArrayKind.Undefined);
    }

    ~Win32LampArrayDevice() => Dispose(disposing: false);

    private void Dispose(bool disposing) {
        if (DISPOSED_FALSE == Interlocked.CompareExchange(
            comparand: DISPOSED_FALSE,
            location1: ref m_isDisposed,
            value: DISPOSED_TRUE
        )) {
            if (disposing) {
                m_deviceHandle.Dispose();
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
}
