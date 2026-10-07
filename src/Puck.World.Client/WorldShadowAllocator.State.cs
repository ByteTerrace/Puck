using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldShadowAllocator {
    /// <summary>Remaps a pure authored reorder by name, preserving crossing ticks and both delivered intervals.</summary>
    /// <param name="lights">The same authored lights in their new table order.</param>
    public void RemapLights(IReadOnlyList<WorldRenderLight> lights) {
        m_current.Remap(lights: lights);
        m_previous.Remap(lights: lights);
    }

    private sealed class State {
        // Current candidates plus the held and incoming names that can outlive a delivery.
        private const int LightCapacity = ((SdfLights.MaxLights + MaxSlots) + MaxFadeSlots);

        public readonly WorldShadowSlot?[] Held = new WorldShadowSlot?[MaxSlots];
        public readonly WorldShadowCandidate?[] Lights = new WorldShadowCandidate?[LightCapacity];
        public readonly WorldShadowFade[] Fades = new WorldShadowFade[MaxFadeSlots];
        public readonly WorldShadowQueued[] Queued = new WorldShadowQueued[MaxSlots];
        public readonly WorldShadowQueued[] PriorQueue = new WorldShadowQueued[MaxSlots];

        private readonly WorldShadowCandidate?[] m_scratch = new WorldShadowCandidate?[LightCapacity];

        public int QueuedCount;

        public WorldShadowCandidate Light(int index) => Lights[index]!.Value;
        public int Index(in WorldShadowCandidate candidate) {
            for (var index = 0; (index < Lights.Length); index++) {
                if (Same(left: Lights[index], right: candidate)) { return index; }
            }
            return -1;
        }
        public int HeldSlot(in WorldShadowCandidate candidate) {
            for (var slot = 0; (slot < MaxSlots); slot++) {
                if (Same(left: Held[slot]?.Candidate, right: candidate)) { return slot; }
            }
            return -1;
        }
        public int FadeFor(int slot) {
            for (var index = 0; (index < MaxFadeSlots); index++) {
                if (Fades[index].Active && (Fades[index].Slot == slot)) { return index; }
            }
            return -1;
        }
        public int FreeFade(int count) {
            for (var index = 0; (index < count); index++) { if (!Fades[index].Active) { return index; } }
            return -1;
        }
        public WorldShadowCandidate? Future(int slot) {
            var fade = FadeFor(slot: slot);

            return ((fade >= 0) ? Light(index: Fades[fade].IncomingLight) : Held[slot]?.Candidate);
        }
        public void Clear() {
            Array.Clear(array: Held);
            Array.Clear(array: Lights);
            Array.Clear(array: Fades);
            Array.Clear(array: Queued);
            Array.Clear(array: PriorQueue);
            QueuedCount = 0;
        }
        public void CopyFrom(State source) {
            source.Held.CopyTo(array: Held, index: 0);
            source.Lights.CopyTo(array: Lights, index: 0);
            source.Fades.CopyTo(array: Fades, index: 0);
            source.Queued.CopyTo(array: Queued, index: 0);
            source.PriorQueue.CopyTo(array: PriorQueue, index: 0);
            QueuedCount = source.QueuedCount;
        }
        public void UpdateLights(ReadOnlySpan<WorldShadowCandidate> candidates) {
            Lights.CopyTo(array: m_scratch, index: 0);
            Array.Clear(array: Lights);
            foreach (var candidate in candidates) { Lights[candidate.LightIndex] = candidate; }
            var retained = SdfLights.MaxLights;

            for (var index = 0; (index < m_scratch.Length); index++) {
                if ((m_scratch[index] is not { } old) || (Index(candidate: old) >= 0) || !Referenced(candidate: old, index: index)) { continue; }
                Lights[retained++] = old with { LightIndex = -1 };
            }
            Span<int> positions = stackalloc int[LightCapacity];

            for (var index = 0; (index < positions.Length); index++) {
                positions[index] = ((m_scratch[index] is { } old) ? Index(candidate: old) : -1);
            }
            RemapEntries(positions: positions);
        }

        private bool Referenced(int index, in WorldShadowCandidate candidate) {
            if (HeldSlot(candidate: candidate) >= 0) { return true; }
            foreach (var fade in Fades) {
                if (fade.Active && ((fade.OutgoingLight == index) || (fade.IncomingLight == index))) { return true; }
            }
            return false;
        }

        public void Remap(IReadOnlyList<WorldRenderLight> lights) {
            Span<int> positions = stackalloc int[LightCapacity];

            for (var old = 0; (old < Lights.Length); old++) {
                positions[old] = old;
                if (Lights[old] is not { } candidate) { continue; }
                var updated = candidate with { LightIndex = -1 };

                for (var index = 0; (index < lights.Count); index++) {
                    if (!StringComparer.Ordinal.Equals(x: candidate.Name, y: lights[index].LightName)) { continue; }
                    updated = candidate with { LightIndex = index, ListOrdinal = index };
                    break;
                }
                Lights[old] = updated;
            }
            RemapEntries(positions: positions);
        }

        private WorldShadowCandidate Resolve(in WorldShadowCandidate candidate) {
            var index = Index(candidate: candidate);

            return ((index >= 0) ? Light(index: index) : candidate with { LightIndex = -1 });
        }
        private void RemapEntries(ReadOnlySpan<int> positions) {
            for (var slot = 0; (slot < MaxSlots); slot++) {
                if (Held[slot] is not { } held) { continue; }
                var candidate = Resolve(candidate: held.Candidate);

                Held[slot] = held with { Candidate = candidate, Rank = Rank(candidate: candidate, source: this) };
            }
            for (var index = 0; (index < MaxFadeSlots); index++) {
                if (Fades[index] is not { Active: true } fade) { continue; }
                Fades[index] = fade with { OutgoingLight = positions[fade.OutgoingLight], IncomingLight = positions[fade.IncomingLight] };
            }
            for (var index = 0; (index < QueuedCount); index++) {
                var item = Queued[index];

                Queued[index] = item with { Incoming = Resolve(candidate: item.Incoming) };
                PriorQueue[index] = Queued[index];
            }
        }
    }
}
