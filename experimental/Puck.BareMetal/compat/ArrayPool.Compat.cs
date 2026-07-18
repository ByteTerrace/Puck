// Part of Puck.BareMetal. System.Buffers.ArrayPool<T> — rent/return reusable arrays. Matters more
// than in the BCL: with no GC, every `new T[]` is a permanent allocation (mimalloc heap on the
// hosted build, firmware pool then bump allocator on UEFI, neither reclaims), so pooling transient buffers is what
// bounds memory growth for a long-running host.
//
// Arrays are bucketed by power-of-two capacity (16 .. 2^30). Rent rounds the request up to a
// bucket and reuses a cached array if free, else allocates exactly the bucket size. Return caches
// the array in its bucket (capped), optionally clearing it. NOT thread-safe (single-threaded use).

namespace System.Buffers {
    public sealed class ArrayPool<T> {
        private const int MinBucketSizeLog2 = 4;  // smallest pooled array is 16 elements
        private const int BucketCount = 27;       // up to 2^(4+26) = 2^30 elements
        private const int MaxArraysPerBucket = 8; // cap on cached arrays per bucket

        // The process-wide pool. First access runs this type's static constructor, which on the
        // bare-metal build requires GC-static init + lazy-cctor support in the kernel entry.
        public static readonly ArrayPool<T> Shared = new ArrayPool<T>();

        // Flattened cache: _free[bucket * MaxArraysPerBucket + slot]; _counts[bucket] live slots.
        private readonly T[][] _free;
        private readonly int[] _counts;

        public ArrayPool() {
            _free = new T[(BucketCount * MaxArraysPerBucket)][];
            _counts = new int[BucketCount];
        }

        public static ArrayPool<T> Create() => new ArrayPool<T>();
        public T[] Rent(int minimumLength) {
            if (minimumLength < 0)
                Environment.FailFast(null);

            if (minimumLength == 0)
                return new T[0];

            int bucket = SelectBucketIndex(minimumLength);

            if (bucket < BucketCount) {
                int count = _counts[bucket];

                if (count > 0) {
                    count--;
                    int slot = ((bucket * MaxArraysPerBucket) + count);
                    T[] array = _free[slot];

                    _free[slot] = null;
                    _counts[bucket] = count;
                    return array;
                }

                return new T[GetBucketCapacity(bucket)];
            }

            // Larger than the biggest bucket: hand back an exact-fit array (not pooled on return).
            return new T[minimumLength];
        }
        public void Return(T[] array, bool clearArray = false) {
            if ((array is null) || (array.Length == 0))
                return;

            int bucket = SelectBucketIndex(array.Length);

            // Only pool arrays whose length is exactly a bucket capacity (i.e. ones Rent produced).
            if ((bucket >= BucketCount) || (array.Length != GetBucketCapacity(bucket)))
                return;

            if (clearArray) {
                for (int i = 0; (i < array.Length); i++)
                    array[i] = default;
            }

            int count = _counts[bucket];

            if (count < MaxArraysPerBucket) {
                _free[((bucket * MaxArraysPerBucket) + count)] = array;
                _counts[bucket] = (count + 1);
            }
            // Bucket full: drop the array (it is simply not cached).
        }

        // Index of the smallest bucket whose capacity is >= size. Capacities are 16, 32, 64, ...
        private static int SelectBucketIndex(int size) {
            int capacity = (1 << MinBucketSizeLog2);
            int index = 0;

            while (capacity < size) {
                capacity <<= 1;
                index++;
            }
            return index;
        }
        private static int GetBucketCapacity(int bucketIndex) => (1 << (MinBucketSizeLog2 + bucketIndex));
    }
}
