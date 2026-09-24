using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// What a GPU device is, as its backend reported it when the device was created: the backend, the adapter, and the
/// driver. It is recorded beside counted work so two readings can be told apart by the machine that made them, and it
/// names the device's persistent pipeline-cache file (<see cref="CacheKey"/>). It is never branched on: no selection,
/// fallback, or workaround reads it. What selection reads is the device's <see cref="GpuMemoryProfile"/>.
/// </summary>
/// <param name="Backend">The backend's name: <c>vulkan</c> or <c>directx</c>.</param>
/// <param name="AdapterName">The adapter's name as the driver reports it.</param>
/// <param name="VendorId">The adapter's PCI vendor identifier.</param>
/// <param name="DeviceId">The adapter's PCI device identifier.</param>
/// <param name="DriverVersionRaw">The driver version exactly as the API reports it: Vulkan's packed
/// <c>driverVersion</c>, or Direct3D's user-mode driver version (<c>LARGE_INTEGER</c>).</param>
/// <param name="DriverVersion">The driver version as its vendor displays it: Vulkan's <c>driverInfo</c> when the
/// device reports driver properties, Direct3D's four 16-bit parts of the user-mode driver version; empty when the
/// backend has no display form.</param>
/// <param name="ApiVersion">The API version the device supports: Vulkan's <c>major.minor.patch</c>, or the highest
/// Direct3D feature level the device supports (<c>12_1</c>).</param>
/// <param name="DriverName">The driver's name (Vulkan's <c>driverName</c>); empty when the backend reports none.</param>
/// <param name="DriverId">The driver's identifier (Vulkan's <c>VkDriverId</c>); zero when the backend reports none.</param>
/// <param name="ConformanceVersion">The conformance-test version the driver passed (Vulkan's
/// <c>conformanceVersion</c>, <c>major.minor.subminor.patch</c>); empty when the backend reports none.</param>
/// <param name="PipelineCacheUuid">The driver's pipeline-cache UUID (Vulkan's <c>pipelineCacheUUID</c>) as 32 lowercase
/// hexadecimal digits: a pipeline cache written under another UUID is not valid on this driver. Empty when the backend
/// reports none.</param>
public sealed record GpuDeviceIdentity(
    [property: JsonPropertyName(name: GpuDeviceIdentity.BackendField)] string Backend,
    [property: JsonPropertyName(name: GpuDeviceIdentity.AdapterField)] string AdapterName,
    [property: JsonPropertyName(name: GpuDeviceIdentity.VendorField)] uint VendorId,
    [property: JsonPropertyName(name: GpuDeviceIdentity.DeviceField)] uint DeviceId,
    [property: JsonPropertyName(name: GpuDeviceIdentity.DriverRawField)] ulong DriverVersionRaw,
    [property: JsonPropertyName(name: GpuDeviceIdentity.DriverField)] string DriverVersion,
    [property: JsonPropertyName(name: GpuDeviceIdentity.ApiField)] string ApiVersion,
    [property: JsonPropertyName(name: GpuDeviceIdentity.DriverNameField)] string DriverName = "",
    [property: JsonPropertyName(name: GpuDeviceIdentity.DriverIdField)] uint DriverId = 0U,
    [property: JsonPropertyName(name: GpuDeviceIdentity.ConformanceField)] string ConformanceVersion = "",
    [property: JsonPropertyName(name: GpuDeviceIdentity.PipelineCacheUuidField)] string PipelineCacheUuid = ""
) {
    // Each field's one spelling, shared by the text form, the JSON form, and a serializer reading either.
    private const string AdapterField = "adapter";
    private const string ApiField = "api";
    private const string BackendField = "backend";
    private const string ConformanceField = "conformance";
    private const string DeviceField = "device";
    private const string DriverField = "driver";
    private const string DriverIdField = "driver.id";
    private const string DriverNameField = "driver.name";
    private const string DriverRawField = "driver.raw";
    private const string PipelineCacheUuidField = "pipeline-cache.uuid";
    private const string VendorField = "vendor";

    /// <summary>Gets the device and driver as one path segment, the same on every backend:
    /// <c>&lt;vendor&gt;-&lt;device&gt;-&lt;driver&gt;</c>, the PCI vendor and device identifiers in at least four lowercase
    /// hexadecimal digits and <see cref="DriverVersionRaw"/> in exactly sixteen, the width of the widest version a
    /// backend reports (Direct3D's 64-bit user-mode driver version; Vulkan's 32-bit <c>driverVersion</c> is
    /// zero-padded). A driver version the backend could not read is zero and still names a file.</summary>
    [JsonIgnore]
    public string CacheKey => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{VendorId:x4}-{DeviceId:x4}-{DriverVersionRaw:x16}"
    );

    /// <summary>Formats a Vulkan packed version (<c>VK_MAKE_API_VERSION</c>) as <c>major.minor.patch</c>, dropping the
    /// variant.</summary>
    /// <param name="packed">The packed version.</param>
    /// <returns>The display version.</returns>
    public static string FormatVulkanVersion(uint packed) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{((packed >> 22) & 0x7FU)}.{((packed >> 12) & 0x3FFU)}.{(packed & 0xFFFU)}"
        );
    /// <summary>Formats a Direct3D user-mode driver version as its four 16-bit parts, most significant first.</summary>
    /// <param name="version">The <c>LARGE_INTEGER</c> version <c>CheckInterfaceSupport</c> reports.</param>
    /// <returns>The display version (<c>32.0.15.6636</c>).</returns>
    public static string FormatDirectXDriverVersion(ulong version) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(version >> 48)}.{((version >> 32) & 0xFFFFUL)}.{((version >> 16) & 0xFFFFUL)}.{(version & 0xFFFFUL)}"
        );
    /// <summary>Appends the identity as <c>key=value</c> fields on one line, a value holding a space or a quote
    /// quoted, and an empty or zero backend-specific field left out:
    /// <c>backend=vulkan adapter="…" vendor=0x10de device=0x2786 driver=… driver.raw=0x… api=1.4.303 driver.name="…" driver.id=4 conformance=1.4.1.0 pipeline-cache.uuid=…</c>.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public StringBuilder AppendFields(StringBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        AppendField(
            builder: builder,
            name: BackendField,
            value: Backend
        );
        AppendField(
            builder: builder,
            name: AdapterField,
            value: AdapterName
        );
        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" {VendorField}=0x{VendorId:x4} {DeviceField}=0x{DeviceId:x4}"
        );
        AppendField(
            builder: builder,
            name: DriverField,
            value: DriverVersion
        );
        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" {DriverRawField}=0x{DriverVersionRaw:x}"
        );
        AppendField(
            builder: builder,
            name: ApiField,
            value: ApiVersion
        );
        AppendField(
            builder: builder,
            name: DriverNameField,
            value: DriverName
        );

        if (DriverId != 0U) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" {DriverIdField}={DriverId}"
            );
        }

        AppendField(
            builder: builder,
            name: ConformanceField,
            value: ConformanceVersion
        );
        AppendField(
            builder: builder,
            name: PipelineCacheUuidField,
            value: PipelineCacheUuid
        );

        return builder;
    }
    /// <summary>Writes the identity as one JSON object with every field, under the names
    /// <see cref="AppendFields"/> prints.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public void WriteJson(Utf8JsonWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: BackendField,
            value: Backend
        );
        writer.WriteString(
            propertyName: AdapterField,
            value: AdapterName
        );
        writer.WriteNumber(
            propertyName: VendorField,
            value: VendorId
        );
        writer.WriteNumber(
            propertyName: DeviceField,
            value: DeviceId
        );
        writer.WriteString(
            propertyName: DriverField,
            value: DriverVersion
        );
        writer.WriteNumber(
            propertyName: DriverRawField,
            value: DriverVersionRaw
        );
        writer.WriteString(
            propertyName: ApiField,
            value: ApiVersion
        );
        writer.WriteString(
            propertyName: DriverNameField,
            value: DriverName
        );
        writer.WriteNumber(
            propertyName: DriverIdField,
            value: DriverId
        );
        writer.WriteString(
            propertyName: ConformanceField,
            value: ConformanceVersion
        );
        writer.WriteString(
            propertyName: PipelineCacheUuidField,
            value: PipelineCacheUuid
        );
        writer.WriteEndObject();
    }

    // Appends " name=value", quoting a value that holds whitespace or a quote; an empty value appends nothing.
    private static void AppendField(StringBuilder builder, string name, string value) {
        if (string.IsNullOrEmpty(value: value)) {
            return;
        }

        _ = builder.Append(value: ' ').Append(value: name).Append(value: '=');

        if (!value.Any(predicate: static character => (char.IsWhiteSpace(c: character) || (character == '"')))) {
            _ = builder.Append(value: value);

            return;
        }

        _ = builder.Append(value: '"').Append(value: value.Replace(
            newValue: "\\\"",
            oldValue: "\""
        )).Append(value: '"');
    }
}
