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
            var kind = (identity >> 30);
            var source = identity & 0x3FFFFFFF;

            for (var index = 0; (index < Maps.Length); index++) {
                if (kind == 2) {
                    if (source < MeshCounts[index]) {
                        return Maps[index]?.Resolve(identity: 0x80000000 | source);
                    }
                    source -= ((uint)MeshCounts[index]);
                } else if (Maps[index]?.Resolve(identity: identity) is { } target) {
                    return target;
                }
            }
            return null;
        }
    }
}
