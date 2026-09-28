using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>Deterministic cost attributed to one placement in the currently rendered program.</summary>
/// <param name="Placement">The authored placement id.</param>
/// <param name="Prototype">Its captured prototype id.</param>
/// <param name="OwnedWords">Packed words exclusively owned by its live SDF instances.</param>
/// <param name="Shapes">Its live ShapeBlend instructions.</param>
/// <param name="Instances">Its live SDF instance rows.</param>
/// <param name="ScopeClamps">Its non-unit scope clamps.</param>
/// <param name="BoundRadius">Largest packed instance bound, including safety padding.</param>
/// <param name="Halo">Largest blend/scoped-field outward reach before safety padding.</param>
/// <param name="Unmaskable">Whether any instance cannot be finitely culled.</param>
/// <param name="StampPoolSlots">Reserved transform slots for this placement's live stamp registration.</param>
/// <param name="MeshDraws">Live mesh draws belonging to the placement.</param>
/// <param name="HiddenFieldInstances">SDF instances hidden from the camera while another representation draws.</param>
public sealed record WorldPlacementCost(string Placement, string? Prototype, int OwnedWords, int Shapes, int Instances,
    int ScopeClamps, float BoundRadius, float Halo, bool Unmaskable, int StampPoolSlots, int MeshDraws, int HiddenFieldInstances) {
    /// <summary>Gets the representation captured by this frame's emission, independently of another world's bake cache.</summary>
    public bool DrawsBake { get; init; }
}
/// <summary>Partitions a live program's words into placement ownership, other instance ownership and shared tables.
/// Reading this report performs no emission, GPU work or simulation mutation.</summary>
/// <param name="ProgramWords">The live packed program's total words.</param>
/// <param name="SharedWords">Headers, palettes, grid, shared part assets and world-stream words.</param>
/// <param name="OtherInstanceWords">Words owned by instances without a placement identity, including bodies and parked capacity.</param>
/// <param name="Placements">Placement rows, sorted by descending owned words then ordinal id.</param>
public sealed record WorldPlacementCostReport(int ProgramWords, int SharedWords, int OtherInstanceWords, IReadOnlyList<WorldPlacementCost> Placements) {
    /// <summary>Reads the same program and immutable ordinal map published with one render frame.</summary>
    /// <param name="frame">The live frame.</param>
    /// <returns>The complete word partition and placement rows.</returns>
    public static WorldPlacementCostReport Read(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        var rows = new Dictionary<string, WorldPlacementCost>(comparer: StringComparer.Ordinal);
        var owned = 0;
        var other = 0;
        var program = frame.Program;

        for (var index = 0; (index < program.Instances.Count); index++) {
            var cost = program.InspectInstance(index: index);

            owned = checked((owned + cost.OwnedWords));
            if (frame.PickMap?.Resolve(identity: 0x40000000u | (((uint)index) + 1)) is not WorldPickTarget { Placement: { } placement } target) {
                other = checked((other + cost.OwnedWords));
                continue;
            }
            var row = Row(placement: placement, target: target);

            rows[placement] = row with {
                OwnedWords = checked((row.OwnedWords + cost.OwnedWords)),
                Shapes = (row.Shapes + cost.Shapes),
                Instances = (row.Instances + 1),
                ScopeClamps = (row.ScopeClamps + cost.ScopeClamps),
                BoundRadius = MathF.Max(x: row.BoundRadius, y: cost.BoundRadius),
                Halo = MathF.Max(x: row.Halo, y: cost.Halo),
                Unmaskable = (row.Unmaskable || cost.Unmaskable),
                HiddenFieldInstances = (row.HiddenFieldInstances + (program.Instances[index].CameraHidden ? 1 : 0)),
            };
        }
        for (var index = 0; (index < frame.MeshDraws.Count); index++) {
            if (frame.PickMap?.Resolve(identity: 0x80000000u | ((uint)index)) is not WorldPickTarget { Placement: { } placement } target) { continue; }
            var row = Row(placement: placement, target: target);

            rows[placement] = row with { MeshDraws = (row.MeshDraws + 1) };
        }
        return new WorldPlacementCostReport(ProgramWords: program.Words.Length, SharedWords: checked((program.Words.Length - owned)),
            OtherInstanceWords: other, Placements: [.. rows.Values.OrderByDescending(keySelector: static row => row.OwnedWords)
                .ThenBy(keySelector: static row => row.Placement, comparer: StringComparer.Ordinal)]);

        WorldPlacementCost Row(WorldPickTarget target, string placement) => (rows.TryGetValue(key: placement, value: out var row)
            ? row : new WorldPlacementCost(Placement: placement, Prototype: target.Prototype, OwnedWords: 0, Shapes: 0,
                Instances: 0, ScopeClamps: 0, BoundRadius: 0, Halo: 0, Unmaskable: false, StampPoolSlots: target.StampPoolSlots,
                MeshDraws: 0, HiddenFieldInstances: 0) { DrawsBake = target.DrawsBake });
    }
}
