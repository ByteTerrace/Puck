using Puck.Maths;
using System.Runtime.CompilerServices;

namespace Puck.World.Server;

public static partial class WorldStateHashComposition {
    private static readonly ConditionalWeakTable<WorldStateSection, IdentityRecordHash> IdentityRecordHashes = new();

    /// <summary>Folds explicit identity ownership and logical-to-physical attachment declarations.</summary>
    public static void AppendPoolBindings(ref Fnv1aHash hash, WorldDefinition definition) {
        var carriers = (definition.Properties?.Carriers ?? []);
        var records = (definition.Identity?.Records ?? []);

        if ((carriers.Count == 0) && (records.Count == 0)) {
            return;
        }
        AppendString(hash: ref hash, value: "pool-bindings");
        hash.Add(value: carriers.Count);
        foreach (var carrier in carriers) {
            AppendString(hash: ref hash, value: carrier.Pool.Value);
            AppendString(hash: ref hash, value: carrier.Field.Value);
            hash.Add(value: carrier.Bindings.Count);
            foreach (var binding in carrier.Bindings) {
                AppendString(hash: ref hash, value: binding.Member.Value);
                AppendString(hash: ref hash, value: binding.Placement);
                hash.Add(value: (binding.Seat ?? -1));
            }
        }
        hash.Add(value: records.Count);
        foreach (var record in records) {
            AppendString(hash: ref hash, value: record.Value);
        }
    }
    /// <summary>Folds an identity's owned records, caching the immutable snapshot's digest between writes.</summary>
    public static void AppendIdentityRecords(ref Fnv1aHash hash, WorldIdentity? identity) {
        if (identity?.RecordState is not { } records) {
            return;
        }
        AppendString(hash: ref hash, value: "identity-records");
        hash.Add(value: IdentityRecordHashes.GetValue(records, static section => new IdentityRecordHash(section: section)).Value);
    }

    private sealed class IdentityRecordHash {
        public IdentityRecordHash(WorldStateSection section) {
            var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
            var hash = Fnv1aHash.Create();

            hash.Add(value: arena.ComputeHash());
            AppendDeclaration(hash: ref hash, state: section);
            Value = hash.Value;
        }

        public ulong Value { get; }
    }

    private static void AppendPoolDeclarations(ref Fnv1aHash hash, WorldStateSection? state) {
        if (state is not { Records.Count: > 0 } and not { Pools.Count: > 0 } and not { PairPools.Count: > 0 }) {
            return;
        }
        AppendString(hash: ref hash, value: "records-and-pools");
        var records = (state.Records ?? []);

        hash.Add(value: ((uint)records.Count));
        foreach (var record in records) {
            AppendString(hash: ref hash, value: record.Name.Value);
            var fields = (record.Fields ?? []);

            hash.Add(value: ((uint)fields.Count));
            foreach (var field in fields) {
                AppendString(hash: ref hash, value: field.Name.Value);
                hash.Add(value: ((byte)field.Kind));
                hash.Add(value: ((byte)field.Overflow));
                hash.Add(value: ((byte)(field.Min.HasValue ? 1 : 0)));
                hash.Add(value: (field.Min ?? 0));
                hash.Add(value: ((byte)(field.Max.HasValue ? 1 : 0)));
                hash.Add(value: (field.Max ?? 0));
                AppendString(hash: ref hash, value: field.Enum?.Value);
                AppendString(hash: ref hash, value: field.Space?.Value);
                hash.Add(value: (field.Dimensions ?? -1));
                AppendAdvance(hash: ref hash, advance: field.Advance);
                AppendPoolDefault(hash: ref hash, value: field.Default);
            }
        }
        var pools = (state.Pools ?? []);

        hash.Add(value: ((uint)pools.Count));
        foreach (var pool in pools) {
            AppendString(hash: ref hash, value: pool.Name.Value);
            AppendString(hash: ref hash, value: pool.Record.Value);
            hash.Add(value: pool.Capacity);
        }
        var pairs = (state.PairPools ?? []);

        hash.Add(value: pairs.Count);
        foreach (var pair in pairs) {
            AppendString(hash: ref hash, value: pair.Name.Value);
            AppendString(hash: ref hash, value: pair.Record.Value);
            AppendString(hash: ref hash, value: pair.LeftPool.Value);
            AppendString(hash: ref hash, value: pair.RightPool.Value);
            hash.Add(value: pair.MaxLive);
            hash.Add(value: ((byte)(pair.Directed ? 1 : 0)));
            hash.Add(value: ((byte)(pair.AllowSelf ? 1 : 0)));
        }
        // Initial populations and snapshots are arena values. Folding them here as well would make an export
        // differ from its live arena solely because publication changed the document representation.
    }
    private static void AppendPoolDefault(ref Fnv1aHash hash, CellValue value) {
        hash.Add(value: ((byte)(value.HasValue ? 1 : 0)));
        if (!value.HasValue) {
            return;
        }
        hash.Add(value: ((byte)value.Kind));
        switch (value.Kind) {
            case CellKind.Text:
                AppendString(hash: ref hash, value: value.AsText);
                break;
            case CellKind.Vector:
                var components = value.AsVector.Span;
                hash.Add(value: components.Length);
                foreach (var component in components) {
                    hash.Add(value: unchecked((byte)component));
                }
                break;
            default:
                hash.Add(value: value.Raw);
                break;
        }
    }
}
