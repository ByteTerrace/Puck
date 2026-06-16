namespace Puck.Memory;

public unsafe interface IAllocator {
    void* Allocate(nuint size, nuint alignment = 0);
    void Free(void* ptr);
    void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0);
}
