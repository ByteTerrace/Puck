namespace Puck.Forge.Framework;

/// <summary>
/// Battery-save discipline over the MBC1's 8 KiB SRAM window. The persisted block at <see cref="SramBase"/> is
/// <c>magic(2) | version(1) | payload(N) | sum16-of-payload LE(2)</c>; the game only ever reads and writes the
/// WORK-RAM MIRROR at <see cref="FrameworkMemoryMap.SaveMirror"/>. Load validates magic + version + checksum and
/// copies SRAM → mirror; ANY mismatch silently falls back to the ROM defaults instead — the module never trusts
/// SRAM (a fresh cartridge, a corrupt save, or a future version bump all land on defaults). Store writes the block
/// and recomputes the checksum. SRAM is enabled (0x0A → 0x0000) only inside the two subroutines and disabled again
/// on the way out, the classic anti-corruption discipline.
/// </summary>
internal sealed class SaveModule {
    /// <summary>The first byte of the persisted block in the SRAM window.</summary>
    public const ushort SramBase = 0xA000;
    /// <summary>The block header size (magic + version) preceding the payload.</summary>
    public const int HeaderByteCount = 3;
    /// <summary>The first magic byte ('P').</summary>
    public const byte MagicLow = 0x50;
    /// <summary>The second magic byte ('F' — "Puck Framework").</summary>
    public const byte MagicHigh = 0x46;

    private const ushort PayloadBase = (ushort)(SramBase + HeaderByteCount);
    private const ushort RamEnableAddress = 0x0000;
    private const byte RamEnableValue = 0x0A;

    private readonly RomTable m_defaults;
    private readonly Sm83Emitter m_emitter;
    private readonly int m_loadLabel;
    private readonly int m_storeLabel;
    private readonly int m_checksumLabel;
    private readonly byte m_version;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="defaults">The ROM defaults table (exactly the payload size; used whenever validation fails).</param>
    /// <param name="version">The block's version byte (bump it to orphan old saves on a layout change).</param>
    public SaveModule(Sm83Emitter emitter, RomTable defaults, byte version) {
        ArgumentNullException.ThrowIfNull(emitter);

        if ((defaults.Length < 1) || (defaults.Length > FrameworkMemoryMap.SaveMirrorCapacity)) {
            throw new ArgumentException(message: $"The save payload is {defaults.Length} bytes; the mirror holds 1..{FrameworkMemoryMap.SaveMirrorCapacity}.", paramName: nameof(defaults));
        }

        m_defaults = defaults;
        m_emitter = emitter;
        m_loadLabel = emitter.NewLabel();
        m_storeLabel = emitter.NewLabel();
        m_checksumLabel = emitter.NewLabel();
        m_version = version;
    }

    /// <summary>The payload size in bytes (the defaults table's length).</summary>
    public int PayloadByteCount => m_defaults.Length;

    /// <summary>Emits a call to the load subroutine (validate SRAM → mirror, else defaults → mirror).</summary>
    public void EmitLoad() => m_emitter.Call(label: m_loadLabel);

    /// <summary>Emits a call to the store subroutine (mirror → SRAM block with a fresh checksum).</summary>
    public void EmitStore() => m_emitter.Call(label: m_storeLabel);

    /// <summary>Emits the module's library subroutines. Called once by the framework facade.</summary>
    public void EmitLibrary() {
        EmitLoadSubroutine();
        EmitStoreSubroutine();
        EmitChecksumSubroutine();
    }

    private void EmitLoadSubroutine() {
        var useDefaults = m_emitter.NewLabel();
        var done = m_emitter.NewLabel();
        var payloadCount = (byte)m_defaults.Length;
        var checksumAddress = (ushort)(PayloadBase + payloadCount);

        m_emitter.MarkLabel(label: m_loadLabel);
        EmitRamEnable();

        // Header check: magic + version.
        m_emitter.LoadAFromAddress(address: SramBase);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: MagicLow);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: useDefaults);
        m_emitter.LoadAFromAddress(address: (ushort)(SramBase + 1));
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: MagicHigh);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: useDefaults);
        m_emitter.LoadAFromAddress(address: (ushort)(SramBase + 2));
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: m_version);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: useDefaults);

        // Checksum check: recompute over the SRAM payload and compare with the stored little-endian sum.
        m_emitter.LoadImmediate(pair: Reg16.Hl, value: PayloadBase);
        m_emitter.Call(label: m_checksumLabel);
        m_emitter.LoadAFromAddress(address: checksumAddress);
        m_emitter.Arithmetic(op: AluOp.Compare, source: Reg8.C);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: useDefaults);
        m_emitter.LoadAFromAddress(address: (ushort)(checksumAddress + 1));
        m_emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: useDefaults);

        // Valid: SRAM payload → mirror.
        FrameworkKernel.EmitBlockCopy(emitter: m_emitter, sourceAddress: PayloadBase, destinationAddress: FrameworkMemoryMap.SaveMirror, byteCount: (ushort)payloadCount);
        m_emitter.JumpRelative(label: done);

        // Invalid in any way: ROM defaults → mirror. SRAM contents are never partially trusted.
        m_emitter.MarkLabel(label: useDefaults);
        FrameworkKernel.EmitBlockCopy(emitter: m_emitter, sourceAddress: m_defaults.Address, destinationAddress: FrameworkMemoryMap.SaveMirror, byteCount: (ushort)payloadCount);

        m_emitter.MarkLabel(label: done);
        EmitRamDisable();
        m_emitter.Return();
    }
    private void EmitStoreSubroutine() {
        var payloadCount = (byte)m_defaults.Length;
        var checksumAddress = (ushort)(PayloadBase + payloadCount);

        m_emitter.MarkLabel(label: m_storeLabel);
        EmitRamEnable();

        m_emitter.LoadAImmediate(value: MagicLow);
        m_emitter.StoreAToAddress(address: SramBase);
        m_emitter.LoadAImmediate(value: MagicHigh);
        m_emitter.StoreAToAddress(address: (ushort)(SramBase + 1));
        m_emitter.LoadAImmediate(value: m_version);
        m_emitter.StoreAToAddress(address: (ushort)(SramBase + 2));

        FrameworkKernel.EmitBlockCopy(emitter: m_emitter, sourceAddress: FrameworkMemoryMap.SaveMirror, destinationAddress: PayloadBase, byteCount: (ushort)payloadCount);

        m_emitter.LoadImmediate(pair: Reg16.Hl, value: PayloadBase);
        m_emitter.Call(label: m_checksumLabel);
        m_emitter.Load(destination: Reg8.A, source: Reg8.C);
        m_emitter.StoreAToAddress(address: checksumAddress);
        m_emitter.Load(destination: Reg8.A, source: Reg8.B);
        m_emitter.StoreAToAddress(address: (ushort)(checksumAddress + 1));

        EmitRamDisable();
        m_emitter.Return();
    }
    private void EmitChecksumSubroutine() {
        var loop = m_emitter.NewLabel();

        // saveChecksum: HL = payload start → B:C = sum16 of the payload bytes. Clobbers A, D, advances HL.
        m_emitter.MarkLabel(label: m_checksumLabel);
        m_emitter.XorA();
        m_emitter.Load(destination: Reg8.B, source: Reg8.A);
        m_emitter.Load(destination: Reg8.C, source: Reg8.A);
        m_emitter.LoadImmediate(destination: Reg8.D, value: (byte)m_defaults.Length);
        m_emitter.MarkLabel(label: loop);
        m_emitter.Load(destination: Reg8.A, source: Reg8.C);
        m_emitter.Arithmetic(op: AluOp.Add, source: Reg8.Memory);
        m_emitter.Load(destination: Reg8.C, source: Reg8.A);
        m_emitter.Load(destination: Reg8.A, source: Reg8.B);
        m_emitter.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        m_emitter.Load(destination: Reg8.B, source: Reg8.A);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Decrement(register: Reg8.D);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: loop);
        m_emitter.Return();
    }
    private void EmitRamEnable() {
        m_emitter.LoadAImmediate(value: RamEnableValue);
        m_emitter.StoreAToAddress(address: RamEnableAddress);
    }
    private void EmitRamDisable() {
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: RamEnableAddress);
    }
}
