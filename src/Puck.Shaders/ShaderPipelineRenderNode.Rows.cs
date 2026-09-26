using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// The World group's regions: one region per bound row and element type, which every array reading that row as that type
// binds, so two passes reading one row the same way read one copy of it. An array no row binds reads the one region of
// its element type that no row writes, all zeros. Each region holds the longest length of the arrays reading it, element
// i at byte 4i, and takes the policy GpuResidency.Select picks for its size with a reader in flight: a ring, or a staged
// copy through the node's region copies (ShaderPipelineRenderNode.Regions.cs), its copy sets reserved in the graph's
// copy pool after the package regions' and before the host buffer ports'. The graph's leading pass owns the regions, so
// they retire with the graph. The node keeps each row's values as the host last wrote them and writes them into every
// graph it installs, so a replacement reads the rows the one it replaces read.
public sealed partial class ShaderPipelineRenderNode {
    private readonly Dictionary<string, double[]> m_rowValues = new(comparer: StringComparer.Ordinal);
    // The rows the host binds, which the next graph built is allocated with; the rows the installed graph was allocated
    // with; and whether the installed graph must be rebuilt because they differ for its arrays.
    private RowBindings m_rows = RowBindings.Empty;
    private RowBindings m_installedRows = RowBindings.Empty;

    private bool m_rebindPending;

    // The installed graph's row regions, and, while a graph allocates, the regions no pass has taken yet.
    private RowRegion[] m_rowRegions = [];

    private RowRegion[]? m_rowRegionsOwner;
    // The installing graph's row plan and the copy-pool share each staged row region takes (-1 for a ring).
    private RowPlan? m_installingRows;

    private int[] m_rowShares = [];

    /// <summary>Gets the number of row regions the installed graph binds: one per bound row and element type its arrays
    /// read, and one per element type its unbound arrays read.</summary>
    public int RowRegionCount => m_rowRegions.Length;

    /// <summary>Binds arrays to the rows they read. The binding holds for every graph the node installs after it; when it
    /// changes what an installed graph's arrays read, the installed pipeline is rebuilt beside it as a resize is, and
    /// installs at a frame boundary with its regions regrouped. A binding naming a pass or an array a graph does not
    /// declare binds nothing in it. Binding the rows already bound does nothing.</summary>
    /// <param name="rows">The bindings; an array bound twice takes its last.</param>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
    public void BindRows(IReadOnlyList<ShaderPipelineRowBinding> rows) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: rows);

        var next = RowBindings.Of(rows: rows);

        if (next.SameAs(other: m_rows)) {
            return;
        }

        m_rows = next;

        foreach (var row in m_rowValues.Keys.ToArray()) {
            if (!next.Binds(row: row)) {
                _ = m_rowValues.Remove(key: row);
            }
        }

        m_rebindPending = (
            m_ready &&
            (m_pipeline is { } installed) &&
            !next.SameFor(
                other: m_installedRows,
                plan: installed.Plan
            )
        );
    }
    /// <summary>Returns whether an installed pass declares an array in its World group.</summary>
    /// <param name="passName">The pass.</param>
    /// <param name="array">The array's name.</param>
    /// <returns><see langword="true"/> when an installed pass declares the array.</returns>
    public bool DeclaresArray(string passName, string array) {
        if (
            (m_pipeline is null) ||
            !m_ready
        ) {
            return false;
        }

        foreach (var pass in m_passes) {
            if (!string.Equals(
                a: pass.Name,
                b: passName,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            foreach (var slot in pass.ParametersLayout.Arrays) {
                if (string.Equals(
                    a: slot.Name,
                    b: array,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return true;
                }
            }

            return false;
        }

        return false;
    }
    /// <summary>Writes a row's values into every region reading it, each as its element type
    /// (<see cref="ShaderPipelineParameterLayout.WriteArray"/>): value <c>i</c> is element <c>i</c>, and elements past
    /// the values read zero. Each pass reads the write the next time it binds, and a region owes each slot only the words
    /// that moved. The node keeps the values and writes them into every graph it installs later.</summary>
    /// <param name="row">The row, as <see cref="BindRows"/> names it.</param>
    /// <param name="values">The row's values.</param>
    /// <returns><see langword="true"/> when an array is bound to the row and the values were kept;
    /// <see langword="false"/> for a row no array is bound to.</returns>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public bool TryWriteRow(string row, ReadOnlySpan<double> values) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: row);

        if (!m_rows.Binds(row: row)) {
            return false;
        }
        if (
            !m_rowValues.TryGetValue(
                key: row,
                value: out var kept
            ) ||
            (kept.Length != values.Length)
        ) {
            kept = new double[values.Length];
            m_rowValues[row] = kept;
        }

        values.CopyTo(destination: kept);

        foreach (var region in m_rowRegions) {
            if (string.Equals(
                a: region.Row,
                b: row,
                comparisonType: StringComparison.Ordinal
            )) {
                region.Write(values: kept);
            }
        }

        return true;
    }

    // The bytes of a graph's row regions, each under the policy the device picks for its size with a reader in flight.
    private ulong RowRegionBytesOf(ShaderPipelinePlan plan, RowBindings rows) {
        var bytes = 0UL;

        foreach (var region in RowPlan.Of(plan: plan, rows: rows).Regions) {
            bytes = checked((bytes + RegionBytesOf(byteCount: region.ByteCount)));
        }

        return bytes;
    }
    // Creates the installing graph's row regions, each staged one taking the copy-pool share CreateRegionCopies reserved
    // for it, and writes each bound row's kept values into its regions. The regions belong to m_rowRegionsOwner until the
    // graph's leading pass takes them.
    private void CreateRowRegions(IGpuComputePipeline? copyPipeline) {
        var plan = m_installingRows!;
        var regions = new RowRegion[plan.Regions.Length];

        m_rowRegionsOwner = regions;

        for (var index = 0; (index < regions.Length); index++) {
            var spec = plan.Regions[index];
            var staged = (m_rowShares[index] >= 0);

            regions[index] = new RowRegion(
                region: CreateRegion(
                    byteCount: ((int)spec.ByteCount),
                    copyPipeline: copyPipeline,
                    copySets: (staged
                        ? m_regionCopies!.Region(index: m_rowShares[index])
                        : null),
                    name: new GpuObjectName(
                        detail: spec.Name,
                        owner: m_descriptor.Name,
                        part: "rows"
                    ),
                    staged: staged
                ),
                row: spec.Row,
                type: spec.Type
            );
        }

        m_rowRegions = regions;
    }
    // Gives an installing pass its arrays' regions, and the graph's regions to own when no pass has taken them yet.
    private void TakeRowRegions(RuntimePass pass) {
        if (m_rowRegionsOwner is { } owned) {
            pass.RowRegions = owned;
            m_rowRegionsOwner = null;
        }
        if (m_installingRows!.PassArrays.TryGetValue(
            key: pass.Name,
            value: out var indices
        )) {
            pass.ArrayRegions = [.. indices.Select(selector: index => m_rowRegions[index].Region)];
            pass.WorldSets = new nint[m_inFlight];
        }
    }
    // Writes every row region's kept values, or zeros for a row not written yet, and owes each slot the whole region, so
    // what the first frame after an install or a reset uploads never depends on how many frames ran before it.
    private void SeedRowRegions() {
        foreach (var region in m_rowRegions) {
            region.Write(values: (((region.Row is { } row) && m_rowValues.TryGetValue(
                key: row,
                value: out var kept
            ))
                ? kept
                : []));

            for (var slot = 0; (slot < m_inFlight); slot++) {
                region.Region.OweAll(slot: slot);
            }
        }
    }

    // One region a graph's arrays read: the row it holds (null for the unbound arrays of its type), its element type, and
    // the region with the host bytes each write is staged in.
    private sealed class RowRegion(GpuRegion region, string? row, ShaderValueType type) {
        private readonly byte[] m_bytes = new byte[region.ByteCount];

        public readonly GpuRegion Region = region;
        public readonly string? Row = row;
        public readonly ShaderValueType Type = type;

        public void Write(ReadOnlySpan<double> values) {
            ShaderPipelineParameterLayout.WriteArray(
                elements: m_bytes,
                type: Type,
                values: values
            );
            _ = Region.Write(
                bytes: m_bytes,
                offset: 0
            );
        }
    }
    // The rows a host binds, ordered by pass and then array so two statements of the same bindings compare equal.
    private sealed class RowBindings {
        public static readonly RowBindings Empty = new(bindings: new Dictionary<(string Pass, string Array), string>());

        private readonly Dictionary<(string Pass, string Array), string> m_bindings;
        private readonly HashSet<string> m_rows;

        private RowBindings(Dictionary<(string Pass, string Array), string> bindings) {
            m_bindings = bindings;
            m_rows = new HashSet<string>(
                collection: bindings.Values,
                comparer: StringComparer.Ordinal
            );
        }

        public static RowBindings Of(IReadOnlyList<ShaderPipelineRowBinding> rows) {
            var bindings = new Dictionary<(string Pass, string Array), string>();

            foreach (var binding in rows) {
                ArgumentException.ThrowIfNullOrWhiteSpace(argument: binding.Pass);
                ArgumentException.ThrowIfNullOrWhiteSpace(argument: binding.Array);
                ArgumentException.ThrowIfNullOrWhiteSpace(argument: binding.Row);
                bindings[(binding.Pass, binding.Array)] = binding.Row;
            }

            return new RowBindings(bindings: bindings);
        }
        public bool Binds(string row) => m_rows.Contains(item: row);
        public string? RowOf(string pass, string array) => m_bindings.GetValueOrDefault(key: (pass, array));
        public bool SameAs(RowBindings other) =>
            (
                (m_bindings.Count == other.m_bindings.Count) &&
                m_bindings.All(predicate: pair => (
                    other.m_bindings.TryGetValue(
                        key: pair.Key,
                        value: out var row
                    ) &&
                    string.Equals(
                        a: row,
                        b: pair.Value,
                        comparisonType: StringComparison.Ordinal
                    )
                ))
            );
        // Whether both bind every array a plan declares to the same row.
        public bool SameFor(RowBindings other, ShaderPipelinePlan plan) =>
            plan.Passes.All(predicate: pass => pass.Parameters.Arrays.All(predicate: array => string.Equals(
                a: RowOf(array: array.Name, pass: pass.Name),
                b: other.RowOf(array: array.Name, pass: pass.Name),
                comparisonType: StringComparison.Ordinal
            )));
    }
    // The row regions a graph's arrays read under a binding, one per row and element type, ordered by the passes and
    // arrays reading them and each as long as the longest array reading it; and, per pass, the region each of its arrays
    // reads.
    private sealed class RowPlan {
        private RowPlan(RowRegionSpec[] regions, Dictionary<string, int[]> passArrays) {
            Regions = regions;
            PassArrays = passArrays;
        }

        public Dictionary<string, int[]> PassArrays { get; }
        public RowRegionSpec[] Regions { get; }

        public static RowPlan Of(ShaderPipelinePlan plan, RowBindings rows) {
            var regions = new List<RowRegionSpec>();
            var indexOf = new Dictionary<(string? Row, ShaderValueType Type), int>();
            var passArrays = new Dictionary<string, int[]>(comparer: StringComparer.Ordinal);

            foreach (var pass in plan.Passes) {
                var arrays = pass.Parameters.Arrays;

                if (arrays.Count == 0) {
                    continue;
                }

                var indices = new int[arrays.Count];

                for (var index = 0; (index < arrays.Count); index++) {
                    var array = arrays[index];
                    var key = (Row: rows.RowOf(array: array.Name, pass: pass.Name), array.Type);

                    if (!indexOf.TryGetValue(
                        key: key,
                        value: out var region
                    )) {
                        region = regions.Count;
                        indexOf.Add(
                            key: key,
                            value: region
                        );
                        regions.Add(item: new RowRegionSpec(
                            Length: array.Length,
                            Row: key.Row,
                            Type: key.Type
                        ));
                    } else if (regions[region].Length < array.Length) {
                        regions[region] = (regions[region] with { Length = array.Length });
                    }

                    indices[index] = region;
                }

                passArrays.Add(
                    key: pass.Name,
                    value: indices
                );
            }

            return new RowPlan(
                passArrays: passArrays,
                regions: [.. regions]
            );
        }
    }
    // One row region a plan states: the row it holds (null for unbound arrays), its element type and its element count.
    private readonly record struct RowRegionSpec(string? Row, ShaderValueType Type, uint Length) {
        public ulong ByteCount => (((ulong)Length) * ShaderValueTypes.ComponentBytes);
        public string Name => $"{(Row ?? "unbound")} {Type.Spelling()}";
    }
}
