using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// One Direct3D 12 device's <c>ID3D12PipelineLibrary</c>: seeded from the device's <see cref="GpuPipelineCacheFile"/>,
/// asked first by every compute and graphics pipeline creation on the device, and serialized back by
/// <see cref="Persist"/> and on disposal. A pipeline is stored under a name hashed from everything that defines it (its
/// bytecode, its serialized root signature, and the fixed state the caller names), so a changed kernel is a new name
/// rather than a mismatch. The file owns where the library lives and when it is written; this type owns only the
/// native library.
/// <para>
/// The runtime validates a library blob when the library is created: a blob from another adapter or driver, or a
/// corrupt one, is refused, reported by name, and the library starts empty. A device without
/// <c>ID3D12Device1</c>, or a library the runtime cannot create at all, leaves every creation uncached and counted as a
/// miss: the library never stops a device. The library keeps the blob it was created from alive until it is released,
/// as the runtime requires, and synchronizes its own loads and stores, which the runtime does not guarantee for two
/// threads loading one name.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXPipelineLibrary : IDisposable {
    private readonly GpuPipelineCacheFile m_file;
    private readonly Lock m_gate = new();
    // The names this library stored since it was created: two threads that both missed one name each create its
    // pipeline, and only the first stores it, since storing a name twice is a debug-layer warning.
    private readonly HashSet<string> m_stored = new(comparer: StringComparer.Ordinal);

    private void* m_blob;
    private bool m_disposed;
    private ID3D12PipelineLibrary* m_library;

    private DirectXPipelineLibrary(ID3D12PipelineLibrary* library, void* blob, GpuPipelineCacheFile file) {
        m_blob = blob;
        m_file = file;
        m_library = library;
    }

    private static ID3D12PipelineLibrary* CreateLibrary(ID3D12Device1* device, void* blob, nuint length) {
        void* library;

        device->CreatePipelineLibrary(
            BlobLength: length,
            pLibraryBlob: blob,
            ppPipelineLibrary: out library,
            riid: ID3D12PipelineLibrary.IID_Guid
        );

        return ((ID3D12PipelineLibrary*)library);
    }
    private static string NameOf(string kind, ReadOnlySpan<ReadOnlyMemory<byte>> parts) =>
        $"{kind}-{GpuPipelineCacheStore.ContentKeyOf(parts: parts)}";
    // The library's current contents, or empty when the runtime could not serialize them (reported).
    private static ReadOnlyMemory<byte> Serialize(DirectXPipelineLibrary owner) {
        var library = owner.m_library;
        var size = library->GetSerializedSize();
        var data = new byte[size];

        try {
            fixed (byte* pointer = data) {
                library->Serialize(
                    DataSizeInBytes: size,
                    pData: pointer
                );
            }
        } catch (COMException exception) {
            Console.Error.WriteLine(value: $"[pipeline-cache] not written {owner.m_file.Path}: Serialize failed (0x{exception.HResult:X8})");

            return ReadOnlyMemory<byte>.Empty;
        }

        return data;
    }

    /// <summary>Creates a device's pipeline library, seeded from its file when the runtime accepts it.</summary>
    /// <param name="deviceHandle">The <c>ID3D12Device</c>.</param>
    /// <param name="file">The device's cache file.</param>
    /// <returns>The library; creations through it are uncached when the runtime could not create one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    public static DirectXPipelineLibrary Create(nint deviceHandle, GpuPipelineCacheFile file) {
        ArgumentNullException.ThrowIfNull(argument: file);

        if (((IUnknown*)deviceHandle)->QueryInterface(
            ppvObject: out var devicePointer,
            riid: in ID3D12Device1.IID_Guid
        ).Failed) {
            Console.Error.WriteLine(value: "[pipeline-cache] directx: the device has no ID3D12Device1; pipelines are created uncached");

            return new DirectXPipelineLibrary(
                blob: null,
                file: file,
                library: null
            );
        }

        var device = ((ID3D12Device1*)devicePointer);

        try {
            var data = file.Read();

            if (data is { Length: > 0 }) {
                var blob = NativeMemory.Alloc(byteCount: ((nuint)data.Length));

                data.CopyTo(destination: new Span<byte>(
                    length: data.Length,
                    pointer: blob
                ));

                try {
                    return new DirectXPipelineLibrary(
                        blob: blob,
                        file: file,
                        library: CreateLibrary(
                            blob: blob,
                            device: device,
                            length: ((nuint)data.Length)
                        )
                    );
                } catch (COMException exception) {
                    NativeMemory.Free(ptr: blob);
                    file.Refuse(reason: $"CreatePipelineLibrary refused it (0x{exception.HResult:X8})");
                }
            }

            try {
                return new DirectXPipelineLibrary(
                    blob: null,
                    file: file,
                    library: CreateLibrary(
                        blob: null,
                        device: device,
                        length: 0
                    )
                );
            } catch (COMException exception) {
                Console.Error.WriteLine(value: $"[pipeline-cache] directx: CreatePipelineLibrary failed (0x{exception.HResult:X8}); pipelines are created uncached");

                return new DirectXPipelineLibrary(
                    blob: null,
                    file: file,
                    library: null
                );
            }
        } finally {
            _ = ((IUnknown*)device)->Release();
        }
    }
    /// <summary>Loads a compute pipeline from the library, or creates it and stores it there.</summary>
    /// <param name="device">The device the library belongs to.</param>
    /// <param name="description">The pipeline's description.</param>
    /// <param name="identity">Everything that defines the pipeline besides its description's pointers: its bytecode
    /// and serialized root signature at least.</param>
    /// <returns>The <c>ID3D12PipelineState</c>, owned by the caller.</returns>
    public nint CreateComputePipeline(ID3D12Device* device, in D3D12_COMPUTE_PIPELINE_STATE_DESC description, params ReadOnlySpan<ReadOnlyMemory<byte>> identity) {
        var name = NameOf(
            kind: "compute",
            parts: identity
        );
        var iid = ID3D12PipelineState.IID_Guid;
        void* pipeline = null;

        fixed (D3D12_COMPUTE_PIPELINE_STATE_DESC* pointer = &description)
        fixed (char* pName = name) {
            if (TryLoad(
                description: pointer,
                iid: &iid,
                name: pName,
                pipeline: &pipeline,
                slot: DirectXConstants.LoadComputePipelineSlot
            )) {
                return ((nint)pipeline);
            }

            device->CreateComputePipelineState(
                pDesc: in description,
                ppPipelineState: out pipeline,
                riid: in iid
            );
            Store(
                name: pName,
                pipeline: pipeline
            );
        }

        return ((nint)pipeline);
    }
    /// <summary>Loads a graphics pipeline from the library, or creates it and stores it there.</summary>
    /// <param name="device">The device the library belongs to.</param>
    /// <param name="description">The pipeline's description.</param>
    /// <param name="identity">Everything that defines the pipeline besides its description's pointers: both stages'
    /// bytecode, the serialized root signature, and the render-target format and vertex layout the caller sets.</param>
    /// <returns>The <c>ID3D12PipelineState</c>, owned by the caller.</returns>
    public nint CreateGraphicsPipeline(ID3D12Device* device, in D3D12_GRAPHICS_PIPELINE_STATE_DESC description, params ReadOnlySpan<ReadOnlyMemory<byte>> identity) {
        var name = NameOf(
            kind: "graphics",
            parts: identity
        );
        var iid = ID3D12PipelineState.IID_Guid;
        void* pipeline = null;

        fixed (D3D12_GRAPHICS_PIPELINE_STATE_DESC* pointer = &description)
        fixed (char* pName = name) {
            if (TryLoad(
                description: pointer,
                iid: &iid,
                name: pName,
                pipeline: &pipeline,
                slot: DirectXConstants.LoadGraphicsPipelineSlot
            )) {
                return ((nint)pipeline);
            }

            device->CreateGraphicsPipelineState(
                pDesc: in description,
                ppPipelineState: out pipeline,
                riid: in iid
            );
            Store(
                name: pName,
                pipeline: pipeline
            );
        }

        return ((nint)pipeline);
    }
    /// <summary>Serializes the library to its file, then releases it and the blob it was created from. Call before the
    /// device is released.</summary>
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            PersistLocked();
            m_disposed = true;

            if (null != m_library) {
                _ = ((IUnknown*)m_library)->Release();
                m_library = null;
            }

            if (null != m_blob) {
                NativeMemory.Free(ptr: m_blob);
                m_blob = null;
            }
        }
    }
    /// <summary>Serializes the library to its file when a creation missed it since the last write. Safe on any thread;
    /// a failed serialization or write is reported and leaves the previous file whole.</summary>
    public void Persist() {
        lock (m_gate) {
            if (!m_disposed) {
                PersistLocked();
            }
        }
    }

    private void PersistLocked() {
        if (null == m_library) {
            return;
        }

        m_file.Persist(
            serialize: static owner => Serialize(owner: owner),
            state: this
        );
    }
    // Stores a pipeline just created, unless another thread that missed the same name stored it first.
    private void Store(char* name, void* pipeline) {
        m_file.Count(cacheHit: false);

        lock (m_gate) {
            if (
                m_disposed ||
                (null == m_library) ||
                !m_stored.Add(item: new string(value: name))
            ) {
                return;
            }

            _ = ((delegate* unmanaged[Stdcall]<ID3D12PipelineLibrary*, PCWSTR, ID3D12PipelineState*, HRESULT>)(*((void***)m_library))[DirectXConstants.StorePipelineSlot])(
                m_library,
                new PCWSTR(value: name),
                ((ID3D12PipelineState*)pipeline)
            );
        }
    }
    private bool TryLoad(void* description, Guid* iid, char* name, void** pipeline, int slot) {
        lock (m_gate) {
            if (m_disposed || (null == m_library)) {
                return false;
            }

            var result = ((delegate* unmanaged[Stdcall]<ID3D12PipelineLibrary*, PCWSTR, void*, Guid*, void**, HRESULT>)(*((void***)m_library))[slot])(
                m_library,
                new PCWSTR(value: name),
                description,
                iid,
                pipeline
            );

            if (result.Failed) {
                return false;
            }
        }

        m_file.Count(cacheHit: true);

        return true;
    }
}
