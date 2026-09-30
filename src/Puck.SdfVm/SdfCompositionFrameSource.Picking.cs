namespace Puck.SdfVm;

public sealed partial class SdfCompositionFrameSource {
    private ComposedPickMap? m_pickMap;

    private ISdfPickMap? ComposePickMap() {
        var changed = (m_pickMap is null);

        if (!changed) {
            for (var index = 0; (index < m_emitters.Count); index++) {
                if (!ReferenceEquals(objA: m_pickMap!.Maps[index], objB: m_emitters[index].PickMap) ||
                    (m_pickMap.MeshCounts[index] != m_emitters[index].MeshDraws.Count)) { changed = true; break; }
            }
        }
        if (changed) {
            m_pickMap = new ComposedPickMap(maps: m_emitters.Select(selector: static emitter => emitter.PickMap).ToArray(),
                meshCounts: m_emitters.Select(selector: static emitter => emitter.MeshDraws.Count).ToArray());
        }
        return m_pickMap;
    }

    private sealed class ComposedPickMap(ISdfPickMap?[] maps, int[] meshCounts) : ISdfPickMap {
        public ISdfPickMap?[] Maps { get; } = maps;
        public int[] MeshCounts { get; } = meshCounts;

        public object? Resolve(uint identity) {
            var owner = Owner(identity: identity, local: out var local);

            return owner?.Resolve(identity: local);
        }
        public string? MaterialName(uint identity, int material) {
            var owner = Owner(identity: identity, local: out var local);

            return owner?.MaterialName(identity: local, material: material);
        }

        // Identity and material attribution must reach the same captured emitter. SDF ordinals are already
        // program-relative; mesh draws are rebased into that emitter's local draw table.
        private ISdfPickMap? Owner(uint identity, out uint local) {
            local = identity;
            var kind = SdfVisibility.KindOf(identity: identity);
            var source = SdfVisibility.SourceOf(identity: identity);

            for (var index = 0; (index < Maps.Length); index++) {
                if (kind == SdfVisibilityKind.Mesh) {
                    if (source < MeshCounts[index]) {
                        local = SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: source);
                        return Maps[index];
                    }
                    source -= ((uint)MeshCounts[index]);
                } else if (Maps[index]?.Resolve(identity: identity) is not null) {
                    return Maps[index];
                }
            }
            return null;
        }
    }
}
