using System.Runtime.InteropServices;

namespace Puck.Memory;

public unsafe sealed class NativeMemoryAllocator : IAllocator {
    public void* Allocate(nuint size, nuint alignment = 0) {
        var align = ((alignment == 0)
            ? 16
            : alignment);
        var headerSize = align;

        while (headerSize < (nuint)sizeof(Header)) {
            headerSize *= 2;
        }

        var raw = NativeMemory.AlignedAlloc(
            alignment: headerSize,
            byteCount: (size + headerSize)
        );

        if (raw is null) {
            return null;
        }

        var ptr = ((byte*)raw + headerSize);
        var header = ((Header*)ptr - 1);

        header->Raw = raw;
        header->Size = size;

        return ptr;
    }
    public void Free(void* ptr) {
        if (ptr is not null) {
            var header = ((Header*)ptr - 1);
            var raw = header->Raw;

            NativeMemory.AlignedFree(ptr: raw);
        }
    }
    public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) {
        if (ptr is null) {
            return Allocate(
                alignment: alignment,
                size: newSize
            );
        }

        var header = ((Header*)ptr - 1);
        var oldSize = header->Size;

        var newPtr = Allocate(
            alignment: alignment,
            size: newSize
        );

        if (newPtr is null) {
            return null;
        }

        var copySize = ((oldSize < newSize)
            ? oldSize
            : newSize);

        if (copySize > 0) {
            Buffer.MemoryCopy(
                destination: newPtr,
                destinationSizeInBytes: newSize,
                source: ptr,
                sourceBytesToCopy: copySize
            );
        }

        Free(ptr: ptr);
        return newPtr;
    }

    private struct Header {
        public void* Raw;
        public nuint Size;
    }
}
