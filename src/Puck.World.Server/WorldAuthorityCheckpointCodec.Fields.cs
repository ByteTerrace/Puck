using Puck.Networking;
using Puck.Physics.Fields;
namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeFields(IReadOnlyList<WorldFieldRegionCheckpoint>? section) {
        var writer = new WireWriter(); writer.WriteInt32(value: (section?.Count ?? 0));
        foreach (var region in (section ?? [])) {
            writer.WriteString(value: region.Region); writer.WriteInt32(value: region.FieldNames.Count);
            for (var field = 0; (field < region.FieldNames.Count); field++) {
                writer.WriteString(value: region.FieldNames[field]); WriteLongArray(writer: writer, values: region.Cells.Raw[field]);
            }
        }
        return writer.ToArray();
    }
    private static bool TryDecodeFields(byte[] bytes, out string reason, out IReadOnlyList<WorldFieldRegionCheckpoint>? section) {
        var reader = new WireReader(bytes: bytes); var count = reader.ReadCount(field: "physical regions", maximum: StateCapacity.MaxRows, minimum: 0);
        var regions = new List<WorldFieldRegionCheckpoint>();

        for (var index = 0; ((index < count) && !reader.Failed); index++) {
            var region = reader.ReadRequiredString(field: "physical region");
            var fields = reader.ReadCount(field: "region fields", maximum: WorldFieldCapacity.MaxFields, minimum: 1);
            var names = new string[fields]; var raw = new long[fields][];

            for (var field = 0; ((field < fields) && !reader.Failed); field++) {
                names[field] = reader.ReadRequiredString(field: "field identity");
                raw[field] = ReadLongArray(field: "region cells", reader: ref reader);
            }
            regions.Add(item: new WorldFieldRegionCheckpoint(region, names, new FieldLattice.Checkpoint(Raw: raw)));
        }
        if (!reader.TryFinish(failure: out var failure)) { section = null; reason = $"fields section: {failure}"; return false; }
        section = regions; reason = string.Empty; return true;
    }
}
