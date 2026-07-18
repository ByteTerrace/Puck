// Puck.BareMetal — running a real Linux process. Our self-hosted UEFI kernel decodes an embedded,
// genuinely toolchain-compiled musl Linux x86-64 binary (built from a guest/*.c source; see
// GuestElf.cs), parses it like Linux would (ELF64 header + program headers), maps its PT_LOAD
// segments (zeroing .bss), builds a System V initial process stack (argc/argv/envp/auxv), and drops
// into ring 3. A static-pie guest enters musl's _start directly; a dynamic guest (PT_INTERP) enters
// ld-musl, which links it in-guest (Route A). Guest syscalls (arch_prctl, set_tid_address, ioctl,
// writev, write, exit_group) trap to our SYSCALL trampoline and are serviced over COM1. Position-
// independent binaries self-relocate at startup wherever we load them.
//
// Part of Puck.BareMetal.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class Serial {
    [DllImport("puckefi"), SuppressGCTransition]
    private static extern void PuckSerialWriteByte(int b);

    public static void Write(string s) {
        for (int i = 0; (i < s.Length); i++)
            PuckSerialWriteByte(b: (byte)s[i]);
    }
    public static void WriteLine(string s) {
        Write(s: s);
        PuckSerialWriteByte(b: '\r');
        PuckSerialWriteByte(b: '\n');
    }
    public static void WriteUInt(int value) {
        if (value >= 10)
            WriteUInt((value / 10));
        PuckSerialWriteByte(('0' + (value % 10)));
    }
    public static void WriteHex(ulong value) {
        Write(s: "0x");
        bool any = false;

        for (int shift = 60; (shift >= 0); shift -= 4) {
            int nibble = (int)((value >> shift) & 0xF);

            if ((nibble != 0) || any || (shift == 0)) {
                PuckSerialWriteByte(((nibble < 10) ? ('0' + nibble) : (('a' + nibble) - 10)));
                any = true;
            }
        }
    }
}
internal static unsafe class Program {
    private const uint PT_INTERP = 3;
    private const uint PT_LOAD = 1;

    // An ELF mapped into memory: its load bias (address of vaddr 0), in-memory entry + program
    // headers, and whether it requests an interpreter (PT_INTERP -> a dynamic executable).
    private struct MappedElf {
        public ulong Bias;
        public byte* Entry;
        public byte* Phdr;
        public int PhEnt;
        public int PhNum;
        public bool HasInterp;
    }

    // Kernel services (compat/native/puck-efi.c + puck-efi-x64.asm), direct-bound at link time.
    [DllImport("puckefi"), SuppressGCTransition]
    private static extern void PuckSetUserAccessible(ulong addr, ulong size);
    [DllImport("puckefi"), SuppressGCTransition]
    private static extern void PuckEnterUserMode(void* entry, void* userStackTop);
    [DllImport("puckefi"), SuppressGCTransition]
    private static extern void PuckVfsAddFile(byte* path, byte* content, ulong len);

    // A NUL-terminated ASCII copy of `s` on the (never-freed) bump heap; returns its address.
    private static byte* CString(string s) {
        byte[] b = new byte[(s.Length + 1)];

        for (int i = 0; (i < s.Length); i++) b[i] = (byte)s[i];
        b[s.Length] = 0;
        return (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(b));
    }
    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[(o + 1)] << 8));
    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | (b[(o + 1)] << 8) | (b[(o + 2)] << 16) | (b[(o + 3)] << 24));
    private static ulong ReadU64(byte[] b, int o) {
        ulong lo = ReadU32(b: b, o: o);
        ulong hi = ReadU32(b, (o + 4));

        return lo | (hi << 32);
    }

    // Base64 -> bytes (puck has no Convert.FromBase64String). The embedded blob is a multiple of
    // 3 bytes, so there is no padding, but '=' is handled defensively.
    private static int B64(char c) {
        if ((c >= 'A') && (c <= 'Z')) return (c - 'A');
        if ((c >= 'a') && (c <= 'z')) return ((c - 'a') + 26);
        if ((c >= '0') && (c <= '9')) return ((c - '0') + 52);
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    }
    private static byte[] FromBase64(string s) {
        int outLen = ((s.Length / 4) * 3);

        if ((s.Length >= 1) && (s[(s.Length - 1)] == '=')) outLen--;
        if ((s.Length >= 2) && (s[(s.Length - 2)] == '=')) outLen--;
        byte[] r = new byte[outLen];
        int oi = 0;

        for (int i = 0; ((i + 3) < s.Length); i += 4) {
            int v = (B64(s[i]) << 18) | (B64(s[(i + 1)]) << 12)
                  | ((B64(s[(i + 2)]) & 0x3F) << 6) | (B64(s[(i + 3)]) & 0x3F);

            if (oi < outLen) r[oi++] = (byte)(v >> 16);
            if (oi < outLen) r[oi++] = (byte)(v >> 8);
            if (oi < outLen) r[oi++] = (byte)v;
        }
        return r;
    }

    // Push a NUL-terminated string just below *p; returns the new (lower) pointer = the string start.
    private static byte* PushString(byte* p, string s) {
        p -= (s.Length + 1);
        for (int i = 0; (i < s.Length); i++) p[i] = (byte)s[i];
        p[s.Length] = 0;
        return p;
    }

    // Lay out a System V AMD64 initial process stack in [stackBase, stackBase+stackLen) and return
    // the initial rsp (which points at argc). From the top down: the argv[0] string and 16 AT_RANDOM
    // bytes, then the 16-byte-aligned argc / argv / envp / auxv arrays. For a dynamic guest we add
    // AT_BASE (the ld-musl load base) so the interpreter can relocate itself; AT_PHDR/PHENT/PHNUM/
    // ENTRY always describe the MAIN executable (ld-musl reads them to link it).
    // Environment handed to the guest. Empty by default (clean serial); add "RADV_DEBUG=startup,info"
    // here to make RADV dump its full device-info banner + per-stage init logs over the serial.
    private static readonly string[] Env = { };

    private static byte* BuildUserStack(byte* stackBase, int stackLen, MappedElf main, bool dynamic, ulong interpBase) {
        byte* p = (stackBase + stackLen);

        byte* arg0 = PushString(p: p, s: "puck-guest");

        p = arg0;

        // Push the environment strings; collect their addresses for the envp array. (ulong[], not a
        // pointer stackalloc -- stackalloc would pull in __security_cookie, absent in puck.)
        ulong[] envPtrs = new ulong[Env.Length];

        for (int i = 0; (i < Env.Length); i++) { p = PushString(p: p, s: Env[i]); envPtrs[i] = (ulong)p; }

        p -= 16;
        byte* random = p;

        for (int i = 0; (i < 16); i++) random[i] = (byte)(0x9E * (i + 1));

        // argc/argv0/argvNull (3) + envp (Env.Length) + envpNull (1) + auxv pairs (6, +AT_BASE when
        // dynamic) + AT_NULL (1), sized so the block is a multiple of 16 bytes; aligning the top keeps
        // argc 16-aligned (ABI).
        int auxPairs = (6 + (dynamic ? 1 : 0));
        int blockBytes = (((((3 + Env.Length) + 1) + (auxPairs * 2)) + 2) * 8);

        if ((blockBytes & 0xF) != 0) blockBytes += 8;
        byte* argcAddr = (byte*)((((ulong)p) & ~(ulong)0xF) - (ulong)blockBytes);
        ulong* w = (ulong*)argcAddr;
        int j = 0;

        w[j++] = 1;                       // argc
        w[j++] = (ulong)arg0;             // argv[0]
        w[j++] = 0;                       // argv terminator
        for (int i = 0; (i < Env.Length); i++) w[j++] = envPtrs[i]; // envp[]
        w[j++] = 0;                       // envp terminator
        w[j++] = 3; w[j++] = (ulong)main.Phdr;   // AT_PHDR  (main executable's program headers)
        w[j++] = 4; w[j++] = (ulong)main.PhEnt;  // AT_PHENT
        w[j++] = 5; w[j++] = (ulong)main.PhNum;  // AT_PHNUM
        w[j++] = 6; w[j++] = 4096;               // AT_PAGESZ
        w[j++] = 9; w[j++] = (ulong)main.Entry;  // AT_ENTRY (main executable's entry)
        if (dynamic) { w[j++] = 7; w[j++] = interpBase; } // AT_BASE (ld-musl load base)
        w[j++] = 25; w[j++] = (ulong)random;     // AT_RANDOM (16 bytes for stack/TLS canary)
        w[j++] = 0; w[j++] = 0;                   // AT_NULL
        return argcAddr;
    }

    // Map one ELF (main exe or interpreter) into a fresh image: compute the load extent, copy PT_LOAD
    // segments (zero-init handles .bss), make it ring-3 reachable, and report its bias/entry/phdrs.
    // The freestanding kernel never frees the bump heap, so the image stays valid after this returns.
    private static MappedElf MapElf(byte[] elf, string tag) {
        ulong entry = ReadU64(b: elf, o: 24);
        ulong phoff = ReadU64(b: elf, o: 32);
        ushort phentsize = ReadU16(b: elf, o: 54);
        ushort phnum = ReadU16(b: elf, o: 56);

        ulong minVaddr = 0xFFFFFFFFFFFFFFFFUL, maxEnd = 0, phdrVaddr = phoff;
        bool hasInterp = false;

        for (int i = 0; (i < phnum); i++) {
            int ph = ((int)phoff + (i * phentsize));
            uint type = ReadU32(b: elf, o: ph);

            if (type == PT_INTERP) hasInterp = true;
            if (type != PT_LOAD) continue;
            ulong off = ReadU64(elf, (ph + 8));
            ulong vaddr = ReadU64(elf, (ph + 16));
            ulong filesz = ReadU64(elf, (ph + 32));
            ulong memsz = ReadU64(elf, (ph + 40));

            if (vaddr < minVaddr) minVaddr = vaddr;
            if ((vaddr + memsz) > maxEnd) maxEnd = (vaddr + memsz);
            if ((phoff >= off) && (phoff < (off + filesz))) phdrVaddr = (vaddr + (phoff - off));
        }

        int imageSize = (int)(maxEnd - minVaddr);
        byte[] image = new byte[imageSize]; // zero-init => .bss (memsz > filesz) zeroed for free

        for (int i = 0; (i < phnum); i++) {
            int ph = ((int)phoff + (i * phentsize));

            if (ReadU32(b: elf, o: ph) != PT_LOAD) continue;
            ulong off = ReadU64(elf, (ph + 8));
            ulong vaddr = ReadU64(elf, (ph + 16));
            ulong filesz = ReadU64(elf, (ph + 32));
            int dst = (int)(vaddr - minVaddr);

            for (ulong k = 0; (k < filesz); k++) image[(dst + (int)k)] = elf[((int)off + (int)k)];
        }

        byte* baseP = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(image));
        ulong bias = ((ulong)baseP - minVaddr);

        PuckSetUserAccessible(addr: (ulong)baseP, size: (ulong)imageSize);

        Serial.Write(s: "[loader] "); Serial.Write(s: tag); Serial.Write(s: " mapped, bias=");
        Serial.WriteHex(value: bias); Serial.Write(s: " entry="); Serial.WriteHex((bias + entry)); Serial.WriteLine(s: "");

        return new MappedElf {
            Bias = bias,
            Entry = (byte*)(bias + entry),
            HasInterp = hasInterp,
            PhEnt = phentsize,
            PhNum = phnum,
            Phdr = (byte*)(bias + phdrVaddr),
        };
    }
    private static int Main() {
        Serial.WriteLine(s: "[loader] decoding the embedded musl Linux x86-64 guest...");
        byte[] elf = FromBase64(s: GuestElf.Base64);

        if ((elf[0] != 0x7F) || (elf[1] != (byte)'E') || (elf[2] != (byte)'L') || (elf[3] != (byte)'F')) {
            Serial.WriteLine(s: "[loader] FAIL: bad ELF magic");
            return 1;
        }
        if ((elf[4] != 2) || (ReadU16(b: elf, o: 18) != 0x3E)) {
            Serial.WriteLine(s: "[loader] FAIL: not an x86-64 ELF64");
            return 2;
        }

        MappedElf main = MapElf(elf: elf, tag: "guest");
        bool dynamic = (main.HasInterp && (GuestElf.InterpBase64.Length > 0));

        byte[] userStack = new byte[131072];
        byte* userStackBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(userStack));

        PuckSetUserAccessible(addr: (ulong)userStackBase, size: (ulong)userStack.Length);

        byte* enterEntry;
        byte* sp;

        if (dynamic) {
            // Route A: the guest is a dynamic PIE (PT_INTERP). Load ld-musl, hand it AT_BASE/AT_PHDR/
            // AT_ENTRY for the main exe, and iretq into the interpreter -- ld-musl links the guest
            // (processes its relocations, binds DT_NEEDED) and then jumps to the guest's entry itself.
            Serial.WriteLine(s: "[loader] dynamic guest (PT_INTERP); registering .so closure + ld-musl...");
            for (int i = 0; (i < GuestElf.LibPaths.Length); i++) {
                byte[] lib = FromBase64(s: GuestElf.LibBlobs[i]);
                byte* libPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(lib));

                PuckVfsAddFile(CString(s: GuestElf.LibPaths[i]), libPtr, (ulong)lib.Length);
                Serial.Write(s: "[loader]   registered ");
                Serial.Write(s: GuestElf.LibPaths[i]);
                Serial.Write(s: " (");
                Serial.WriteUInt(lib.Length);
                Serial.WriteLine(s: " bytes)");
            }
            MappedElf interp = MapElf(elf: FromBase64(s: GuestElf.InterpBase64), tag: "ld-musl");

            sp = BuildUserStack(userStackBase, userStack.Length, main, true, interp.Bias);
            enterEntry = interp.Entry;
            Serial.WriteLine(s: "[loader] entering ld-musl at ring 3 (it links the guest, then runs it)...");
        } else {
            sp = BuildUserStack(userStackBase, userStack.Length, main, false, 0);
            enterEntry = main.Entry;
            Serial.WriteLine(s: "[loader] dropping into static musl _start at ring 3...");
        }

        PuckEnterUserMode(entry: enterEntry, userStackTop: sp);
        Serial.WriteLine(s: "[loader] (unreachable: the guest exited)");
        return 0;
    }
}
