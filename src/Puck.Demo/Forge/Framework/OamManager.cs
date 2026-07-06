namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The shadow-OAM allocator and sprite emission helpers. Games never touch hardware OAM: they write the 40-slot
/// shadow page at <see cref="FrameworkMemoryMap.ShadowOam"/> whenever they like, and the VBlank handler's HRAM
/// trampoline DMA-copies the whole page every frame. Slots are reserved at BUILD time so two features can never
/// fight over a slot; a hidden slot is simply Y = 0 (off the top of the screen).
/// </summary>
internal sealed class OamManager {
    private const int SlotCount = 40;
    private const int BytesPerSlot = 4;

    private readonly Sm83Emitter m_emitter;
    private int m_reservedSlots;

    /// <summary>Creates the manager over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public OamManager(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        m_emitter = emitter;
    }

    /// <summary>Reserves <paramref name="count"/> consecutive sprite slots at build time.</summary>
    /// <param name="count">The number of slots.</param>
    /// <returns>The first reserved slot index.</returns>
    public int Reserve(int count) {
        if ((count < 1) || ((m_reservedSlots + count) > SlotCount)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(count), message: $"Reserving {count} sprite slots exceeds the {SlotCount}-slot table ({m_reservedSlots} already reserved).");
        }

        var baseSlot = m_reservedSlots;

        m_reservedSlots += count;

        return baseSlot;
    }

    /// <summary>Computes the shadow-OAM address of one byte of a slot (0 = Y, 1 = X, 2 = tile, 3 = attributes).</summary>
    /// <param name="slot">The sprite slot.</param>
    /// <param name="byteIndex">The byte within the slot.</param>
    /// <returns>The work-RAM address.</returns>
    public static ushort SpriteAddress(int slot, int byteIndex) {
        if ((slot < 0) || (slot >= SlotCount) || (byteIndex < 0) || (byteIndex >= BytesPerSlot)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot), message: $"Sprite byte ({slot}, {byteIndex}) is outside the shadow OAM.");
        }

        return (ushort)(FrameworkMemoryMap.ShadowOam + (slot * BytesPerSlot) + byteIndex);
    }

    /// <summary>Emits a full slot write from registers: B = Y, C = X, D = tile, E = attributes. Clobbers A.</summary>
    /// <param name="slot">The sprite slot.</param>
    public void EmitSetSprite(int slot) {
        m_emitter.Load(destination: Reg8.A, source: Reg8.B);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 0));
        m_emitter.Load(destination: Reg8.A, source: Reg8.C);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 1));
        m_emitter.Load(destination: Reg8.A, source: Reg8.D);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 2));
        m_emitter.Load(destination: Reg8.A, source: Reg8.E);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 3));
    }

    /// <summary>Emits a full slot write from build-time constants. Clobbers A.</summary>
    /// <param name="slot">The sprite slot.</param>
    /// <param name="y">The hardware Y (screen y + 16).</param>
    /// <param name="x">The hardware X (screen x + 8).</param>
    /// <param name="tile">The tile id.</param>
    /// <param name="attributes">The attribute byte.</param>
    public void EmitSetSpriteImmediate(int slot, byte y, byte x, byte tile, byte attributes) {
        m_emitter.LoadAImmediate(value: y);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 0));
        m_emitter.LoadAImmediate(value: x);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 1));
        m_emitter.LoadAImmediate(value: tile);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 2));
        m_emitter.LoadAImmediate(value: attributes);
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 3));
    }

    /// <summary>Emits a hide for one slot (Y = 0). Clobbers A.</summary>
    /// <param name="slot">The sprite slot.</param>
    public void EmitHideSlot(int slot) {
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 0));
    }

    /// <summary>Emits a hide for a consecutive slot range (one A = 0, many Y stores). Clobbers A.</summary>
    /// <param name="baseSlot">The first slot.</param>
    /// <param name="count">The number of slots.</param>
    public void EmitHideRange(int baseSlot, int count) {
        m_emitter.XorA();

        for (var slot = baseSlot; (slot < (baseSlot + count)); slot++) {
            m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 0));
        }
    }

    /// <summary>Emits an unrolled metasprite draw from a (dy, dx, tile, attributes) row table: HL = the first row
    /// (typically <c>table.Address + index × 4 × spriteCount</c>, computed by the caller), B = the base hardware Y,
    /// C = the base hardware X. Each row's dy/dx are added to the base. Clobbers A and advances HL.</summary>
    /// <param name="baseSlot">The first shadow slot to fill.</param>
    /// <param name="spriteCount">The number of rows/slots.</param>
    public void EmitDrawMetasprite(int baseSlot, int spriteCount) {
        for (var index = 0; (index < spriteCount); index++) {
            var slot = (baseSlot + index);

            m_emitter.LoadAFromHlIncrement();                       // dy
            m_emitter.Arithmetic(op: AluOp.Add, source: Reg8.B);
            m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 0));
            m_emitter.LoadAFromHlIncrement();                       // dx
            m_emitter.Arithmetic(op: AluOp.Add, source: Reg8.C);
            m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 1));
            m_emitter.LoadAFromHlIncrement();                       // tile
            m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 2));
            m_emitter.LoadAFromHlIncrement();                       // attributes
            m_emitter.StoreAToAddress(address: SpriteAddress(slot: slot, byteIndex: 3));
        }
    }
}
