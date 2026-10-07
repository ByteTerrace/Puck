namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>The arrays a capture writes into when it is scratch rather than a record to keep (the sweep refusal's
    /// snapshot, <see cref="StepScratch"/>). They are kept by element type and length, and each is handed out at most
    /// once between resets, so one set serves every body a population steps, whatever its kit's register shapes or
    /// its tape's length, and allocates only for a shape it has not met before.</summary>
    internal sealed class CaptureBuffers {
        private readonly Dictionary<(Type Element, int Length), Shelf> m_shelves = [];
        private readonly List<Shelf> m_taken = [];

        /// <summary>Hands out an array of the given length that no other take since the last reset holds.</summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="length">The array's length.</param>
        /// <returns>The array, holding whatever its last capture left in it.</returns>
        public T[] Take<T>(int length) {
            if (length == 0) {
                return [];
            }

            if (!m_shelves.TryGetValue(key: (typeof(T), length), value: out var shelf)) {
                shelf = new Shelf();
                m_shelves.Add(key: (typeof(T), length), value: shelf);
            }

            if (shelf.Taken == 0) {
                m_taken.Add(item: shelf);
            }

            if (shelf.Taken == shelf.Arrays.Count) {
                shelf.Arrays.Add(item: new T[length]);
            }

            return ((T[])shelf.Arrays[shelf.Taken++]);
        }
        /// <summary>Returns every array taken since the last reset, for the next capture to write into.</summary>
        public void Reset() {
            foreach (var shelf in m_taken) {
                shelf.Taken = 0;
            }

            m_taken.Clear();
        }

        // The arrays of one element type and length, and how many of them the current capture holds.
        private sealed class Shelf {
            public List<Array> Arrays { get; } = [];
            public int Taken { get; set; }
        }
    }
}
