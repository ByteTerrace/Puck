namespace Puck.Maths;

// The BFS state-deduplication both output-automaton product constructions share: a component-state tuple gets a
// stable index on first sight (queued for its own transition pass) and reuses that index on every later sighting,
// keyed by the tuple's comma-joined string so structurally equal component-state arrays collapse to one automaton
// state regardless of array identity.
internal static class AutomatonStateDedup {
    public static int AddState(int[] state, List<int[]> states, Dictionary<string, int> indexes, Queue<int> pending) {
        var key = string.Join(
            separator: ',',
            values: state
        );

        if (indexes.TryGetValue(
            key: key,
            value: out var existing
        )) {
            return existing;
        }

        var index = states.Count;

        states.Add(item: state);
        indexes[key] = index;
        pending.Enqueue(item: index);

        return index;
    }
}
