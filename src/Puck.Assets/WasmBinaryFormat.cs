namespace Puck.Assets;

/// <summary>The WebAssembly binary format's preamble: the four magic bytes <c>\0asm</c> and the little-endian version
/// word that follows them. The scripting host reads it to tell a binary module from WAT text, and the world transpiler
/// reads it before inspecting an addon module's imports and exports.</summary>
public static class WasmBinaryFormat {
    /// <summary>The version word of the WebAssembly 1.0 binary format.</summary>
    public const uint Version1 = 1;

    /// <summary>Gets the four magic bytes every WebAssembly binary module begins with.</summary>
    public static ReadOnlySpan<byte> Magic => "\0asm"u8;
}
