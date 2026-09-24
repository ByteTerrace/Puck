using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Compiles each Direct3D 11 compute kernel source's entry points to <c>cs_5_0</c> DXBC at build: the kernel-class probe
/// kernels and the camera frame converter's conversion kernels, which run on a camera graph's own Direct3D 11 device, so
/// a kernel is created from bytecode there and nothing compiles at run time. Each item's <c>Entries</c> metadata lists
/// its entry points; entry <c>e</c> of <c>kernel.hlsl</c> is written to <c>kernel.e.dxbc</c> beside the source, the
/// name <c>ProbeKindManifest.KernelBytecodePath</c> and <c>Win32D3D11CameraFrameConverter</c> read. The compiler is
/// <c>D3DCompile</c> from <c>d3dcompiler_47.dll</c>, which every supported Windows ships; a source with an
/// <c>#include</c> is refused, because the compile resolves none.
/// </summary>
/// <remarks>An output newer than its source is kept, so an unchanged kernel compiles once.</remarks>
public sealed class PuckCompileDirect3D11Kernels : Task {
    /// <summary>The kernel sources; each carries <c>Entries</c>, its entry points separated by semicolons.</summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The bytecode files, one per source and entry point.</summary>
    [Output]
    public ITaskItem[] Bytecode { get; private set; } = Array.Empty<ITaskItem>();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointer(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate UIntPtr GetBufferSize(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseBlob(IntPtr blob);

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] sourceData, UIntPtr sourceDataSize, string sourceName, IntPtr defines, IntPtr include, string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    public override bool Execute() {
        var outputs = new List<ITaskItem>();

        foreach (var source in Sources) {
            var sourcePath = source.GetMetadata(metadataName: "FullPath");
            var entries = source.GetMetadata(metadataName: "Entries").Split(options: StringSplitOptions.RemoveEmptyEntries, separator: new[] { ';' });
            var bytes = File.ReadAllBytes(path: sourcePath);

            if (Encoding.UTF8.GetString(bytes: bytes).Contains(value: "#include")) {
                Log.LogError(message: $"Kernel '{sourcePath}' includes a file; a Direct3D 11 kernel compiles alone.");
                continue;
            }

            foreach (var entry in entries) {
                var output = Path.Combine(
                    path1: Path.GetDirectoryName(path: sourcePath)!,
                    path2: $"{Path.GetFileNameWithoutExtension(path: sourcePath)}.{entry.Trim()}.dxbc"
                );

                if (
                    !File.Exists(path: output) ||
                    (File.GetLastWriteTimeUtc(path: output) < File.GetLastWriteTimeUtc(path: sourcePath))
                ) {
                    if (!Compile(
                        entry: entry.Trim(),
                        output: output,
                        source: bytes,
                        sourcePath: sourcePath
                    )) {
                        continue;
                    }
                }

                outputs.Add(item: new TaskItem(itemSpec: output));
            }
        }

        Bytecode = outputs.ToArray();

        return !Log.HasLoggedErrors;
    }

    private static IntPtr Slot(IntPtr instance, int slot) =>
        Marshal.ReadIntPtr(ptr: Marshal.ReadIntPtr(ptr: instance), ofs: (slot * IntPtr.Size));
    private static void Release(IntPtr blob) {
        if (blob != IntPtr.Zero) {
            // ID3DBlob: IUnknown's QueryInterface, AddRef, Release, then GetBufferPointer and GetBufferSize.
            Marshal.GetDelegateForFunctionPointer<ReleaseBlob>(ptr: Slot(instance: blob, slot: 2))(blob);
        }
    }
    private static byte[] Read(IntPtr blob) {
        var pointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointer>(ptr: Slot(instance: blob, slot: 3))(blob);
        var size = Marshal.GetDelegateForFunctionPointer<GetBufferSize>(ptr: Slot(instance: blob, slot: 4))(blob);
        var bytes = new byte[checked((int)size.ToUInt64())];

        Marshal.Copy(source: pointer, destination: bytes, startIndex: 0, length: bytes.Length);

        return bytes;
    }
    private bool Compile(byte[] source, string sourcePath, string entry, string output) {
        var result = D3DCompile(
            code: out var code,
            defines: IntPtr.Zero,
            entryPoint: entry,
            errors: out var errors,
            flags1: 0,
            flags2: 0,
            include: IntPtr.Zero,
            sourceData: source,
            sourceDataSize: new UIntPtr(value: ((uint)source.Length)),
            sourceName: Path.GetFileName(path: sourcePath),
            target: "cs_5_0"
        );

        try {
            if (result < 0) {
                var message = ((errors == IntPtr.Zero)
                    ? $"HRESULT 0x{result:X8}"
                    : Encoding.UTF8.GetString(bytes: Read(blob: errors)).Trim());

                Log.LogError(message: $"Kernel '{sourcePath}' entry '{entry}' does not compile at cs_5_0: {message}");

                return false;
            }

            var temporary = (output + ".tmp");

            File.WriteAllBytes(path: temporary, bytes: Read(blob: code));
            File.Copy(destFileName: output, overwrite: true, sourceFileName: temporary);
            File.Delete(path: temporary);

            return true;
        } finally {
            Release(blob: code);
            Release(blob: errors);
        }
    }
}
