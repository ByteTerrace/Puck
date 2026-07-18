namespace Puck.Scripting;

/// <summary>
/// The frozen <c>puck.addon.v1</c> ABI contract: the single source of truth for the byte layout,
/// export names, and pinned budgets a WASM addon and its host agree on. Every multi-byte value is
/// little-endian; every fixed-point value is <see cref="Puck.Maths.FixedQ4816"/> raw <c>i64</c> bits;
/// no floating point ever crosses the boundary.
/// </summary>
public static class AddonAbi {
    /// <summary>The ABI version a guest must report from <c>puck_abi_version</c> for an exact-match handshake (<c>1</c>).</summary>
    public const int AbiVersion = 1;
    /// <summary>The size in bytes of a single command output record the guest writes (<c>24</c>).</summary>
    public const int CommandRecordBytes = 24;
    /// <summary>The default per-tick fuel budget before a deterministic halt (<c>1_000_000</c>).</summary>
    public const long DefaultFuelPerTick = 1_000_000L;
    /// <summary>The maximum number of 24-byte command slots the host accepts from a guest (<c>64</c>).</summary>
    public const int MaxCommandRecords = 64;
    /// <summary>The guest execution stack ceiling in bytes, guarding runaway recursion (<c>512 * 1024</c>).</summary>
    public const int MaxStackBytes = (512 * 1024);
    /// <summary>The <see cref="Puck.Maths.FixedQ4816"/> raw-bit value of <c>1.0</c> (<c>0x1_0000</c>).</summary>
    public const long One = 0x1_0000L;
    /// <summary>The size in bytes of the snapshot input region the host writes each tick (<c>40</c>).</summary>
    public const int SnapshotBytes = 40;

    /// <summary>The by-name guest exports the host binds at instantiation.</summary>
    public static class Exports {
        /// <summary>The <c>() -&gt; i32</c> export returning the guest's ABI version.</summary>
        public const string AbiVersion = "puck_abi_version";
        /// <summary>The <c>() -&gt; i32</c> export returning the count of 24-byte command slots reserved at <see cref="CommandsPtr"/>.</summary>
        public const string CommandsCap = "puck_commands_cap";
        /// <summary>The <c>() -&gt; i32</c> export returning the byte offset of the command output region.</summary>
        public const string CommandsPtr = "puck_commands_ptr";
        /// <summary>The optional <c>() -&gt; ()</c> export called once after instantiation, before the first tick.</summary>
        public const string Init = "puck_init";
        /// <summary>The exported guest linear memory the host reads and writes.</summary>
        public const string Memory = "memory";
        /// <summary>The <c>() -&gt; i32</c> export the host drives once per sim tick; returns the record count.</summary>
        public const string OnTick = "puck_on_tick";
        /// <summary>The <c>() -&gt; i32</c> export returning the byte offset of the snapshot input region.</summary>
        public const string SnapshotPtr = "puck_snapshot_ptr";
    }

    /// <summary>The little-endian field offsets within a 24-byte command output record (A.3).</summary>
    public static class RecordOffsets {
        /// <summary>The <c>u16</c> pad vocabulary id at byte <c>0</c>.</summary>
        public const int PadId = 0;
        /// <summary>The <c>u8</c> command phase at byte <c>2</c>.</summary>
        public const int Phase = 2;
        /// <summary>The <c>u8</c> reserved-must-be-zero byte at <c>3</c>.</summary>
        public const int Reserved0 = 3;
        /// <summary>The <c>u32</c> reserved-must-be-zero field at byte <c>4</c>.</summary>
        public const int Reserved1 = 4;
        /// <summary>The <c>i64</c> primary/X fixed-point value at byte <c>8</c>.</summary>
        public const int ValueX = 8;
        /// <summary>The <c>i64</c> Y fixed-point value at byte <c>16</c>.</summary>
        public const int ValueY = 16;
    }

    /// <summary>The little-endian field offsets within the 40-byte snapshot input region (A.2).</summary>
    public static class SnapshotOffsets {
        /// <summary>The <c>u32</c> digital-button bitfield at byte <c>32</c>.</summary>
        public const int Buttons = 32;
        /// <summary>The <c>i64</c> local-space X position at byte <c>8</c>.</summary>
        public const int PosLocalX = 8;
        /// <summary>The <c>i64</c> local-space Y position at byte <c>16</c>.</summary>
        public const int PosLocalY = 16;
        /// <summary>The <c>i64</c> local-space Z position at byte <c>24</c>.</summary>
        public const int PosLocalZ = 24;
        /// <summary>The <c>u32</c> reserved-must-be-zero field at byte <c>36</c>.</summary>
        public const int Reserved0 = 36;
        /// <summary>The <c>i64</c> engine tick at byte <c>0</c>.</summary>
        public const int Tick = 0;
    }
}
