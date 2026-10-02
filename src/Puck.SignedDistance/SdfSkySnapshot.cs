using Puck.Abstractions.Documents;
using Puck.Abstractions.Presentation;
using System.Text.Json.Serialization;

namespace Puck.SignedDistance;

/// <summary>The affine operation a sky layer applies to the color beneath it.</summary>
[JsonConverter(typeof(StrictEnumConverter<SdfSkyBlend>))]
public enum SdfSkyBlend : byte {
    /// <summary>Coverage-weighted source over destination.</summary>
    Over,
    /// <summary>Adds the source emission.</summary>
    Add,
    /// <summary>Multiplies the destination by the source.</summary>
    Multiply,
    /// <summary>Adds the source and scales the destination by one minus the source.</summary>
    Screen,
}

/// <summary>The sky consumers that see a layer. This is structural authored data, not an animated predicate.</summary>
[JsonConverter(typeof(StrictEnumConverter<SdfSkyVisibility>))]
public enum SdfSkyVisibility : byte {
    /// <summary>Only the camera composite sees the layer.</summary>
    Camera = 1,
    /// <summary>Only the environment lighting and reflection consumers see the layer.</summary>
    Lighting = 2,
    /// <summary>Both the camera and the environment see the layer.</summary>
    Both = 3,
}

/// <summary>The shared directional admission test applied before a sky kind evaluates.</summary>
public enum SdfSkyMask : uint {
    /// <summary>Every direction is admitted.</summary>
    None,
    /// <summary>A closed band between two elevation angles.</summary>
    Elevation,
    /// <summary>A cone around a unit direction.</summary>
    Cone,
}

/// <summary>The common resolved facts of one sky layer. Names and kind labels are retained at preparation time;
/// editor readers display these values rather than resolving the authored fields again.</summary>
/// <param name="Name">The authored layer identity.</param>
/// <param name="Kind">The registered kind's author-facing name.</param>
/// <param name="Class">Where this kind evaluates.</param>
/// <param name="Blend">The ordered composition operation.</param>
/// <param name="Visibility">Which consumers may see the layer.</param>
/// <param name="MinimumTier">The cheapest tier that admits the layer.</param>
/// <param name="Opacity">The resolved, domain-checked layer opacity.</param>
/// <param name="Enabled">Whether the layer has any admitted work after common and kind-specific gates.</param>
/// <param name="Clock">The optional authored layer clock.</param>
/// <param name="Phase">That clock's resolved unit phase, or zero when no clock is named.</param>
public readonly record struct SdfSkyLayerInfo(string Name, string Kind, SdfSkyLayerClass Class,
    SdfSkyBlend Blend, SdfSkyVisibility Visibility, QualityTier MinimumTier, float Opacity, bool Enabled,
    string? Clock, double Phase);

/// <summary>The common sky state attached to the frame that consumes it. Its run storage follows authored
/// structure; animated gates update layer facts in place without changing that structure.</summary>
public sealed class SdfSkySnapshot {
    private readonly SdfSkyLayerInfo[] m_layers;
    private readonly SdfSkyRun[] m_runs;
    private readonly ISdfSkyParameterTable[] m_tables;

    /// <summary>Prepares one stack's fixed run storage.</summary>
    /// <param name="layers">All authored layers in stack order, including inactive layers.</param>
    /// <param name="quality">The resolved sky quality.</param>
    /// <param name="tables">The prepared native parameter tables, one for each present kind.</param>
    /// <param name="common">The native common admission rows in the same order as the layers.</param>
    /// <param name="stops">The native gradient stop rows, shared by their prepared ranges.</param>
    public SdfSkySnapshot(ReadOnlySpan<SdfSkyLayerInfo> layers, QualityTier quality,
        ReadOnlySpan<ISdfSkyParameterTable> tables = default, ISdfSkyParameterTable? common = null,
        ISdfSkyParameterTable? stops = null) {
        m_layers = layers.ToArray();
        m_runs = new SdfSkyRun[layers.Length];
        m_tables = tables.ToArray();
        Common = common;
        Stops = stops;
        var classes = new SdfSkyLayerClass[layers.Length];

        for (var index = 0; index < layers.Length; index++) { classes[index] = layers[index].Class; }
        RunCount = SdfSkyRuns.Write(classes, m_runs);
        Quality = quality;
    }

    private SdfSkySnapshot(SdfSkySnapshot source) {
        m_layers = source.m_layers.ToArray();
        m_runs = source.m_runs;
        m_tables = new ISdfSkyParameterTable[source.m_tables.Length];
        for (var index = 0; index < m_tables.Length; index++) { m_tables[index] = source.m_tables[index].Clone(); }
        RunCount = source.RunCount;
        Quality = source.Quality;
        AdmissionRevision = source.AdmissionRevision;
        Common = source.Common?.Clone();
        Stops = source.Stops?.Clone();
    }

    /// <summary>Gets or sets the already resolved sky quality.</summary>
    public QualityTier Quality { get; set; }
    /// <summary>Gets the structural layer count, including layers whose evaluations are currently gated off.</summary>
    public int LayerCount => m_layers.Length;
    /// <summary>Gets the number of fixed authored-order runs.</summary>
    public int RunCount { get; }
    /// <summary>Gets the number of native tables. An absent kind has no table.</summary>
    public int TableCount => m_tables.Length;
    /// <summary>Gets the revision of the static admission fields. Live opacity and enabled values are checked
    /// from the common native row and do not invalidate per-view name selection.</summary>
    public ulong AdmissionRevision { get; private set; }
    /// <summary>Tests whether two snapshots retain the same prepared layer and run identities.</summary>
    /// <param name="other">Another independently copied presentation snapshot.</param>
    /// <returns>Whether existing ordinal maps and per-view name selections remain applicable.</returns>
    public bool SharesLayout(SdfSkySnapshot? other) => other is not null && ReferenceEquals(m_runs, other.m_runs);
    /// <summary>Gets the native common admission rows, or null for a facts-only snapshot.</summary>
    public ISdfSkyParameterTable? Common { get; }
    /// <summary>Gets the native gradient stops, or null when this stack has no gradient.</summary>
    public ISdfSkyParameterTable? Stops { get; }
    /// <summary>Gets this snapshot's owned native-array payload, excluding managed headers and shared run metadata.
    /// Each independently published copy owns and reports its own payload.</summary>
    public ulong NativeBytes {
        get {
            var bytes = checked((ulong)((Common?.Bytes.Length ?? 0) + (Stops?.Bytes.Length ?? 0)));
            for (var index = 0; index < m_tables.Length; index++) { bytes = checked(bytes + (ulong)m_tables[index].Bytes.Length); }
            return bytes;
        }
    }
    /// <summary>Reads a present kind's native records.</summary>
    /// <param name="index">The prepared table index.</param>
    /// <returns>The table containing the renderer's exact parameter bytes.</returns>
    public ISdfSkyParameterTable Table(int index) => m_tables[index];
    /// <summary>Reads one layer's common resolved state.</summary>
    /// <param name="index">The authored stack index.</param>
    /// <returns>The resolved layer facts.</returns>
    public SdfSkyLayerInfo Layer(int index) => m_layers[index];
    /// <summary>Reads one fixed run.</summary>
    /// <param name="index">The run index below <see cref="RunCount"/>.</param>
    /// <returns>The run's authored range and evaluation class.</returns>
    public SdfSkyRun Run(int index) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RunCount);
        return m_runs[index];
    }
    /// <summary>Updates resolved fields without changing layer identity, kind or evaluation class.</summary>
    /// <param name="index">The authored stack index.</param>
    /// <param name="layer">The newly resolved facts.</param>
    /// <exception cref="ArgumentException">The update would change the prepared stack's structure.</exception>
    public void SetLayer(int index, in SdfSkyLayerInfo layer) {
        var current = m_layers[index];

        if ((current.Name != layer.Name) || (current.Kind != layer.Kind) || (current.Class != layer.Class)) {
            throw new ArgumentException("A sky layer's identity, kind and class change only when its stack is prepared again.", nameof(layer));
        }
        if (current.Visibility != layer.Visibility || current.MinimumTier != layer.MinimumTier) { AdmissionRevision++; }
        m_layers[index] = layer;
    }
    /// <summary>Copies a snapshot's resolved facts, reusing destination storage when the prepared layout matches.</summary>
    /// <param name="source">The current resolved snapshot, or null for no prepared stack.</param>
    /// <param name="destination">The previous destination snapshot.</param>
    /// <returns>The updated destination, allocating only for a new prepared layout.</returns>
    public static SdfSkySnapshot? Copy(SdfSkySnapshot? source, SdfSkySnapshot? destination) {
        if (source is null) { return null; }
        if ((destination is null) || !ReferenceEquals(source.m_runs, destination.m_runs)) { return new(source); }
        source.m_layers.CopyTo(destination.m_layers, 0);
        for (var index = 0; index < source.m_tables.Length; index++) {
            destination.m_tables[index].CopyFrom(source.m_tables[index]);
        }
        if (source.Common is { } common) { destination.Common!.CopyFrom(common); }
        if (source.Stops is { } stops) { destination.Stops!.CopyFrom(stops); }
        destination.Quality = source.Quality;
        destination.AdmissionRevision = source.AdmissionRevision;
        return destination;
    }
}
