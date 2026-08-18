using System.Buffers.Binary;

namespace Puck.Shaders;

/// <summary>One bound config value: its type and its little-endian component bytes (4 bytes per component), ready
/// to be copied into a push-constant block.</summary>
/// <param name="Type">The value type.</param>
/// <param name="Bytes">The <see cref="ShaderValueTypes.SizeBytes"/> little-endian component bytes.</param>
public sealed record ShaderConfigValue(ShaderValueType Type, ReadOnlyMemory<byte> Bytes) {
    /// <summary>Reads component <paramref name="index"/> as a <see cref="uint"/> (bit pattern, whatever the kind).</summary>
    /// <param name="index">The component index.</param>
    /// <returns>The component's 32 bits.</returns>
    public uint ComponentBits(int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source: Bytes.Span.Slice(length: ((int)ShaderValueTypes.ComponentBytes), start: (index * ((int)ShaderValueTypes.ComponentBytes))));
}
/// <summary>A document's <c>config</c> for one shader set, validated against the manifest's config schema with
/// every absent field resolved to its default — the output of <see cref="ShaderSetManifest.TryBindConfig"/> and the
/// input a fullscreen pass fills its push constants from.</summary>
public sealed class ShaderConfigValues {
    private readonly Dictionary<string, ShaderConfigValue> m_values;

    internal ShaderConfigValues(Dictionary<string, ShaderConfigValue> values) {
        m_values = values;
    }

    /// <summary>Gets an empty binding — a set whose manifest declares no config.</summary>
    public static ShaderConfigValues Empty { get; } = new ShaderConfigValues(values: new Dictionary<string, ShaderConfigValue>(comparer: StringComparer.Ordinal));

    /// <summary>Gets the bound value of a config field.</summary>
    /// <param name="name">The field's name.</param>
    /// <returns>The value.</returns>
    /// <exception cref="KeyNotFoundException">The manifest declares no such field.</exception>
    public ShaderConfigValue this[string name] => m_values[name];

    /// <summary>Gets the field names, in the manifest's declaration order.</summary>
    public IEnumerable<string> Names => m_values.Keys;

    /// <summary>Gets a bound value, when the field exists.</summary>
    /// <param name="name">The field's name.</param>
    /// <param name="value">The value, when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool TryGet(string name, out ShaderConfigValue value) => m_values.TryGetValue(key: name, value: out value!);
}
