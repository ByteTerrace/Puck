using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The screen slot door. A screen's identity is the authored index its material id carries, with no count limit; a slot is
// one of MaxScreenSurfaces rows every per-screen table holds (surfaces, mappings, emission, decal descriptors, source
// images), a cache entry a resident screen claims here and gives back when it stops being resident. The identity-to-slot
// map the kernels read (sdfScreenSlot) follows the slots' records in the mapping region, one word an identity holding the
// slot plus one, so a released screen's word reads zero and its material shades as one with no source.
//
// Invariant: a slot names at most one resident screen, and no table is read through a released slot. SettleScreenSlots is
// the one writer of the map, the slot-of-screen mirror and the resident mask: every per-screen write resolves its slot
// through SlotOfScreen first (TryScreenSlot), so a write for a screen holding none has nowhere to land, and a release
// zeroes the screen's map word, clears the slot's bound flag and decal, and forgets the slot's cached mapping before the
// slot can be claimed again. One residency packs its tables on one thread at a time, so the door takes no lock.
public sealed partial class SdfWorldTables {
    // The byte offset of the identity-to-slot map in the mapping region: past every slot's record. KEEP IN SYNC with
    // sdfScreenSlot in frame/sdf-screen-mapping.hlsli.
    private const int ScreenSlotMapOffset = (MaxScreenSurfaces * ScreenMappingStride);
    // The identities the map holds before it first grows: a row of four words is the region's unit.
    private const int InitialScreenMapIdentities = 256;

    private readonly int[] m_screenOfSlot = new int[MaxScreenSurfaces];
    // Each identity's slot plus one, zero for none, the CPU twin of the map's words.
    private int[] m_slotOfScreen = new int[InitialScreenMapIdentities];
    // The frame stamp each identity was last wanted at, so a release test reads one word.
    private long[] m_screenWanted = new long[InitialScreenMapIdentities];
    private long m_screenSettles;
    private UInt128 m_screenResident;

    /// <summary>Gets the slots resident screens hold, a bit a slot (<see cref="BitWords"/>): every per-frame walk over
    /// screens visits these members.</summary>
    public UInt128 ResidentScreens => m_screenResident;
    /// <summary>Gets the screens wanted resident at the last settle that held no slot, because more were wanted than the
    /// slots hold: each shades as a screen with no source until a nearer one leaves.</summary>
    public int DegradedScreens { get; private set; }

    /// <summary>Returns the slot a screen holds this frame.</summary>
    /// <param name="screen">The screen's identity.</param>
    /// <returns>The slot, or -1 when the screen holds none.</returns>
    public int SlotOfScreen(int screen) => ((((uint)screen) < ((uint)m_slotOfScreen.Length)) ? (m_slotOfScreen[screen] - 1) : -1);
    /// <summary>Returns the screen a slot holds this frame.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The screen's identity, or -1 for a free slot.</returns>
    public int ScreenOfSlot(int slot) => (BitWords.Contains(mask: m_screenResident, index: slot) ? m_screenOfSlot[slot] : -1);
    /// <summary>Returns the slots the screens a sky's layers sample hold this frame (<see cref="SdfSky.ScreenAt"/>), a bit a
    /// slot.</summary>
    /// <param name="sky">The sky.</param>
    /// <returns>The slots.</returns>
    public UInt128 SlotsOfSky(SdfSky sky) {
        ArgumentNullException.ThrowIfNull(argument: sky);

        var slots = UInt128.Zero;

        for (var index = 0; (index < sky.LayerCount); index++) {
            if (SlotOfScreen(screen: sky.ScreenAt(index: index)) is var slot and >= 0) { slots |= BitWords.Bit(index: slot); }
        }
        return slots;
    }
    /// <summary>Returns the slots a set of screens holds this frame, a bit a slot.</summary>
    /// <param name="screens">The screens' identities; one holding no slot, or negative, adds nothing.</param>
    /// <returns>The slots.</returns>
    public UInt128 SlotsOf(ReadOnlySpan<int> screens) {
        var slots = UInt128.Zero;

        foreach (var screen in screens) {
            if (SlotOfScreen(screen: screen) is var slot and >= 0) { slots |= BitWords.Bit(index: slot); }
        }
        return slots;
    }

    /// <summary>Settles which screens hold slots this frame: the first <see cref="MaxScreenSurfaces"/> of
    /// <paramref name="wanted"/>, which the caller orders most wanted first (the nearest). A screen that keeps a slot
    /// keeps the one it holds, so a steady scene rewrites nothing; a screen no longer wanted gives its slot back; and a
    /// newly wanted one claims the lowest free slot, its declared surface written into the slot's row. A wanted screen
    /// past the slots holds none and shades as a screen with no source (<see cref="DegradedScreens"/>): no screen is ever
    /// refused for capacity.</summary>
    /// <param name="wanted">The resident screens' identities, most wanted first, each once.</param>
    /// <exception cref="ArgumentOutOfRangeException">An identity is negative or past
    /// <see cref="SdfProgramBuilder.MaxScreenIdentities"/>.</exception>
    public void SettleScreenSlots(ReadOnlySpan<int> wanted) {
        var held = wanted[..Math.Min(val1: wanted.Length, val2: MaxScreenSurfaces)];
        var stamp = ++m_screenSettles;
        var highest = -1;

        foreach (var screen in held) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: screen, paramName: nameof(wanted));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: screen, other: SdfProgramBuilder.MaxScreenIdentities, paramName: nameof(wanted));
            highest = Math.Max(val1: highest, val2: screen);
        }
        EnsureScreenMap(identities: (highest + 1));
        foreach (var screen in held) { m_screenWanted[screen] = stamp; }
        foreach (var slot in BitWords.Members(mask: m_screenResident)) {
            if (m_screenWanted[m_screenOfSlot[slot]] != stamp) { ReleaseScreenSlot(slot: slot); }
        }
        foreach (var screen in held) {
            if (m_slotOfScreen[screen] == 0) { ClaimScreenSlot(screen: screen); }
        }
        DegradedScreens = (wanted.Length - held.Length);
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScreensResident, amount: held.Length);
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScreensDegraded, amount: DegradedScreens);
    }

    private void ClaimScreenSlot(int screen) {
        var slot = (int)UInt128.TrailingZeroCount(value: ~m_screenResident);

        m_screenResident |= BitWords.Bit(index: slot);
        m_screenOfSlot[slot] = screen;
        m_slotOfScreen[screen] = (slot + 1);
        WriteScreenMapWord(screen: screen, word: ((uint)(slot + 1)));
        WriteDeclaredSurface(slot: slot, screen: screen);
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScreenSlotClaims);
    }
    private void ReleaseScreenSlot(int slot) {
        var screen = m_screenOfSlot[slot];

        // The slot's state is the screen's, so it is cleared through the slot, before the screen gives it up.
        ClearDecal(descriptor: slot);
        if (m_decalRuns[slot].Length > 0) {
            _ = m_decalPool.Free(start: (ulong)m_decalRuns[slot].Start);
            m_decalRuns[slot] = default;
        }
        SetScreenBound(bound: false, screenIndex: screen);
        m_screenMappings[slot] = null;
        m_screenFaces[slot] = default;
        m_screenTreatments[slot] = default;
        m_screenRates[slot] = 0f;
        m_screenResident &= ~BitWords.Bit(index: slot);
        m_slotOfScreen[screen] = 0;
        WriteScreenMapWord(screen: screen, word: 0u);
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScreenSlotReleases);
    }
    // Writes a slot's surface row from the live program's declaration of its screen; a screen the program does not
    // declare leaves the row as it stands, and a surface transform patches it each frame it moves (SetScreenSurface).
    private void WriteDeclaredSurface(int slot, int screen) {
        if ((m_liveProgram is not null) && m_liveProgram.Appearance.TryGetScreenSurface(screenIndex: screen, surface: out var surface)) {
            WriteSurfaceRow(halfHeight: surface.HalfHeight, halfWidth: surface.HalfWidth, origin: surface.Origin, right: surface.Right, slot: slot, up: surface.Up);
        }
    }
    // Rewrites every resident slot's surface row from a newly uploaded program.
    private void WriteResidentSurfaces() {
        foreach (var slot in BitWords.Members(mask: m_screenResident)) {
            WriteDeclaredSurface(slot: slot, screen: m_screenOfSlot[slot]);
        }
    }
    private void WriteScreenMapWord(int screen, uint word) {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: word);
        _ = m_screenMappingRegion.Write(bytes: bytes, offset: (ScreenSlotMapOffset + (screen * sizeof(uint))));
    }
    // Grows the map, its CPU twin and the wanted stamps to hold `identities`, a row of four words at a time, by half again
    // past what is asked, as the program region grows; what the region replaces retires after the next upload.
    private void EnsureScreenMap(int identities) {
        if (identities <= m_slotOfScreen.Length) {
            return;
        }

        var grown = (int)GpuCapacity.Grown(current: m_slotOfScreen.Length, required: identities, ceiling: SdfProgramBuilder.MaxScreenIdentities, divisor: 2);

        grown = grown.AlignUp(alignment: 4);
        Array.Resize(array: ref m_slotOfScreen, newSize: grown);
        Array.Resize(array: ref m_screenWanted, newSize: grown);
        m_retired.Reserve();
        Retire(replaced: m_screenMappingRegion.Grow(byteCount: ScreenMappingRegionBytes(identities: grown)), carryingRegion: m_screenMappingRegion);
        m_bindingRevision++;
    }
    // The mapping region's bytes for a map of `identities` words: every slot's record, then the map.
    private static int ScreenMappingRegionBytes(int identities) => checked(ScreenSlotMapOffset + (identities * sizeof(uint)));
}
