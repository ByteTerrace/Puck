using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Recording;
using static Puck.Platform.Windows.Recording.MfEncoder;

namespace Puck.Platform.Windows.Recording;

// The video codec the encoder produces. The ladder token maps to a Matroska codec id and a container config-record
// assembly strategy.
internal enum EncoderCodec {
    H264,
    Av1,
}

/// <summary>
/// An <see cref="IVideoEncoder"/> driving a hardware Media Foundation encoder MFT: NV12 in, H.264/AV1 out. Configures
/// the transform (frame size/rate, bitrate, ~2 s keyframe spacing, zero B-frames for SimpleBlock-friendly PTS order),
/// then pumps it either through the asynchronous event model (hardware MFTs) or the classic ProcessInput/ProcessOutput
/// loop (a software MFT), converting each frame with the CPU NV12 converter. CodecPrivate is assembled from the first
/// keyframe's bitstream (avcC parsed from Annex-B SPS/PPS; av1C from the sequence-header OBU).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MediaFoundationVideoEncoder : IVideoEncoder {
    private readonly EncoderCodec m_codec;
    private readonly int m_frameRate;
    private readonly IMFMediaEventGenerator? m_eventGenerator;
    private readonly bool m_isAsync;
    private readonly bool m_providesSamples;
    private readonly uint m_outputSampleSize;
    private readonly IMFTransform m_transform;
    private readonly List<RecordedPacket> m_packets = [];
    private byte[] m_codecPrivate = [];
    private bool m_disposed;
    private bool m_firstOutputSeen;
    private int m_needInputCredits;
    private byte[] m_nv12Scratch = [];
    private byte[] m_outputScratch = [];
    private byte[]? m_sps;
    private byte[]? m_pps;

    public MediaFoundationVideoEncoder(IMFTransform transform, EncoderCodec codec, string mftName, int width, int height, int frameRate, int bitrateKilobitsPerSecond) {
        // Hold an independent Media Foundation startup reference for the encoder's whole lifetime — the factory's own
        // startup/shutdown pair only brackets enumeration and is released before the first EncodeFrame.
        MfInterop.Check(hr: MfInterop.MFStartup(Version: MfInterop.MfVersion, dwFlags: 0));

        m_transform = transform;
        m_codec = codec;
        m_frameRate = frameRate;
        MftName = mftName;
        CodecId = ((codec == EncoderCodec.Av1) ? "V_AV1" : "V_MPEG4/ISO/AVC");

        try {
            // Unlocking is a no-op on a synchronous MFT; a hardware async MFT refuses to stream without it.
            if (transform.GetAttributes(pAttributes: out var attributes) >= 0) {
                var unlockKey = MF_TRANSFORM_ASYNC_UNLOCK;

                _ = attributes.SetUINT32(guidKey: ref unlockKey, unValue: 1);
            }

            ConfigureTypes(width: width, height: height, frameRate: frameRate, bitrateKilobitsPerSecond: bitrateKilobitsPerSecond);

            Check(hr: m_transform.GetOutputStreamInfo(dwOutputStreamID: 0, pStreamInfo: out var streamInfo));

            m_providesSamples = ((streamInfo.dwFlags & MftOutputStreamProvidesSamples) != 0);
            m_outputSampleSize = Math.Max(val1: streamInfo.cbSize, val2: (uint)(width * height * 4));
            m_eventGenerator = TryQueryEventGenerator(transform: transform);
            m_isAsync = (m_eventGenerator is not null);

            TuneCodec(bitrateKilobitsPerSecond: bitrateKilobitsPerSecond, frameRate: frameRate);

            Check(hr: m_transform.ProcessMessage(eMessage: MftMessageNotifyBeginStreaming, ulParam: 0));

            if (m_isAsync) {
                Check(hr: m_transform.ProcessMessage(eMessage: MftMessageNotifyStartOfStream, ulParam: 0));
            }
        } catch {
            _ = MfInterop.MFShutdown();

            throw;
        }

        Console.Error.WriteLine(value: $"[recording] video encoder '{mftName}' ({CodecId}, {(m_isAsync ? "async" : "sync")}, {(m_providesSamples ? "provides-samples" : "caller-allocates")}) at {width}x{height}@{frameRate} {bitrateKilobitsPerSecond}kbps.");
    }

    /// <summary>The Media Foundation MFT's friendly name (for status echoes).</summary>
    public string MftName { get; }

    /// <inheritdoc/>
    public string CodecId { get; }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> CodecPrivate => m_codecPrivate;

    /// <inheritdoc/>
    public IReadOnlyList<RecordedPacket> EncodeFrame(ReadOnlySpan<byte> pixels, SurfaceFormat format, int width, int height, long timestampNanoseconds) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        m_packets.Clear();

        var input = BuildInputSample(pixels: pixels, format: format, width: width, height: height, timestampNanoseconds: timestampNanoseconds);

        try {
            if (m_isAsync) {
                var generator = m_eventGenerator!;

                while (m_needInputCredits == 0) {
                    Check(hr: generator.GetEvent(dwFlags: 0, ppEvent: out var blockingEvent));
                    HandleEvent(mediaEvent: blockingEvent);
                }

                m_needInputCredits--;
                Check(hr: m_transform.ProcessInput(dwInputStreamID: 0, pSample: input, dwFlags: 0));
                DrainReadyEvents(generator: generator);
            } else {
                Check(hr: m_transform.ProcessInput(dwInputStreamID: 0, pSample: input, dwFlags: 0));
                DrainSynchronousOutputs();
            }
        } finally {
            _ = Marshal.ReleaseComObject(o: input);
        }

        return m_packets;
    }

    /// <inheritdoc/>
    public IReadOnlyList<RecordedPacket> Drain() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        m_packets.Clear();

        _ = m_transform.ProcessMessage(eMessage: MftMessageNotifyEndOfStream, ulParam: 0);
        _ = m_transform.ProcessMessage(eMessage: MftMessageCommandDrain, ulParam: 0);

        if (m_isAsync) {
            var generator = m_eventGenerator!;
            var draining = true;

            while (draining) {
                if (generator.GetEvent(dwFlags: 0, ppEvent: out var mediaEvent) < 0) {
                    break;
                }

                draining = HandleEvent(mediaEvent: mediaEvent);
            }
        } else {
            DrainSynchronousOutputs();
        }

        _ = m_transform.ProcessMessage(eMessage: MftMessageNotifyEndStreaming, ulParam: 0);

        return m_packets;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_eventGenerator is not null) {
            _ = Marshal.ReleaseComObject(o: m_eventGenerator);
        }

        _ = Marshal.ReleaseComObject(o: m_transform);
        _ = MfInterop.MFShutdown();
    }

    private void ConfigureTypes(int width, int height, int frameRate, int bitrateKilobitsPerSecond) {
        Check(hr: MFCreateMediaType(ppMFType: out var outputType));

        var majorKey = MF_MT_MAJOR_TYPE;
        var video = MFMediaType_Video;
        var subtypeKey = MF_MT_SUBTYPE;
        var codecSubtype = ((m_codec == EncoderCodec.Av1) ? MFVideoFormat_AV1 : MFVideoFormat_H264);
        var frameSizeKey = MF_MT_FRAME_SIZE;
        var frameRateKey = MF_MT_FRAME_RATE;
        var parKey = MF_MT_PIXEL_ASPECT_RATIO;
        var interlaceKey = MF_MT_INTERLACE_MODE;
        var bitrateKey = MF_MT_AVG_BITRATE;
        var keyframeKey = MF_MT_MAX_KEYFRAME_SPACING;

        Check(hr: outputType.SetGUID(guidKey: ref majorKey, guidValue: ref video));
        Check(hr: outputType.SetGUID(guidKey: ref subtypeKey, guidValue: ref codecSubtype));
        Check(hr: outputType.SetUINT64(guidKey: ref frameSizeKey, unValue: PackU32Pair(high: (uint)width, low: (uint)height)));
        Check(hr: outputType.SetUINT64(guidKey: ref frameRateKey, unValue: PackU32Pair(high: (uint)frameRate, low: 1)));
        Check(hr: outputType.SetUINT64(guidKey: ref parKey, unValue: PackU32Pair(high: 1, low: 1)));
        Check(hr: outputType.SetUINT32(guidKey: ref interlaceKey, unValue: InterlaceModeProgressive));
        Check(hr: outputType.SetUINT32(guidKey: ref bitrateKey, unValue: (uint)(bitrateKilobitsPerSecond * 1000)));
        _ = outputType.SetUINT32(guidKey: ref keyframeKey, unValue: (uint)(frameRate * 2));

        if (m_codec == EncoderCodec.H264) {
            var profileKey = MF_MT_MPEG2_PROFILE;

            _ = outputType.SetUINT32(guidKey: ref profileKey, unValue: 77); // eAVEncH264VProfile_Main
        }

        Check(hr: m_transform.SetOutputType(dwOutputStreamID: 0, pType: outputType, dwFlags: 0));
        _ = Marshal.ReleaseComObject(o: outputType);

        Check(hr: MFCreateMediaType(ppMFType: out var inputType));

        var nv12 = MFVideoFormat_NV12;

        Check(hr: inputType.SetGUID(guidKey: ref majorKey, guidValue: ref video));
        Check(hr: inputType.SetGUID(guidKey: ref subtypeKey, guidValue: ref nv12));
        Check(hr: inputType.SetUINT64(guidKey: ref frameSizeKey, unValue: PackU32Pair(high: (uint)width, low: (uint)height)));
        Check(hr: inputType.SetUINT64(guidKey: ref frameRateKey, unValue: PackU32Pair(high: (uint)frameRate, low: 1)));
        Check(hr: inputType.SetUINT64(guidKey: ref parKey, unValue: PackU32Pair(high: 1, low: 1)));
        Check(hr: inputType.SetUINT32(guidKey: ref interlaceKey, unValue: InterlaceModeProgressive));
        Check(hr: m_transform.SetInputType(dwInputStreamID: 0, pType: inputType, dwFlags: 0));
        _ = Marshal.ReleaseComObject(o: inputType);
    }

    // Best-effort rate-control/GOP/B-frame configuration through ICodecAPI; hardware encoders honour a subset, so each
    // set is tolerated to fail.
    private void TuneCodec(int bitrateKilobitsPerSecond, int frameRate) {
        ICodecAPI? codecApi;

        try {
            codecApi = (ICodecAPI)m_transform;
        } catch (InvalidCastException) {
            return;
        }

        TrySetU32(codecApi: codecApi, api: CODECAPI_AVEncCommonRateControlMode, value: 0); // CBR
        TrySetU32(codecApi: codecApi, api: CODECAPI_AVEncCommonMeanBitRate, value: (uint)(bitrateKilobitsPerSecond * 1000));
        TrySetU32(codecApi: codecApi, api: CODECAPI_AVEncMPVGOPSize, value: (uint)(frameRate * 2));
        TrySetU32(codecApi: codecApi, api: CODECAPI_AVEncMPVDefaultBPictureCount, value: 0);
    }

    private static void TrySetU32(ICodecAPI codecApi, Guid api, uint value) {
        var apiGuid = api;
        var variant = new CodecApiVariant {
            vt = VtUi4,
            value = value,
        };

        _ = codecApi.SetValue(Api: ref apiGuid, Value: ref variant);
    }

    private static IMFMediaEventGenerator? TryQueryEventGenerator(IMFTransform transform) {
        try {
            return (IMFMediaEventGenerator)transform;
        } catch (InvalidCastException) {
            return null;
        }
    }

    private IMFSample2 BuildInputSample(ReadOnlySpan<byte> pixels, SurfaceFormat format, int width, int height, long timestampNanoseconds) {
        var nv12Size = PixelToNv12Converter.Nv12Size(width: width, height: height);

        if (m_nv12Scratch.Length < nv12Size) {
            m_nv12Scratch = new byte[nv12Size];
        }

        PixelToNv12Converter.Convert(pixels: pixels, format: format, width: width, height: height, destination: m_nv12Scratch.AsSpan(start: 0, length: nv12Size));

        Check(hr: MFCreateMemoryBuffer(cbMaxLength: (uint)nv12Size, ppBuffer: out var buffer));
        Check(hr: buffer.Lock(ppbBuffer: out var pointer, pcbMaxLength: out _, pcbCurrentLength: out _));
        Marshal.Copy(source: m_nv12Scratch, startIndex: 0, destination: pointer, length: nv12Size);
        Check(hr: buffer.Unlock());
        Check(hr: buffer.SetCurrentLength(cbCurrentLength: (uint)nv12Size));

        Check(hr: MFCreateSample(ppIMFSample: out var sample));
        Check(hr: sample.AddBuffer(pBuffer: buffer));
        Check(hr: sample.SetSampleTime(hnsSampleTime: (timestampNanoseconds / 100)));
        Check(hr: sample.SetSampleDuration(hnsSampleDuration: (10_000_000L / m_frameRate)));
        _ = Marshal.ReleaseComObject(o: buffer);

        return sample;
    }

    // Handles one async event. Returns false when the drain has completed (the caller stops pumping).
    private bool HandleEvent(IMFMediaEvent mediaEvent) {
        try {
            Check(hr: mediaEvent.GetType(pmet: out var eventType));

            switch (eventType) {
                case MeTransformNeedInput: {
                    m_needInputCredits++;

                    break;
                }
                case MeTransformHaveOutput: {
                    CollectOutput();

                    break;
                }
                case MeTransformDrainComplete: {
                    return false;
                }
                default: {
                    break;
                }
            }

            return true;
        } finally {
            _ = Marshal.ReleaseComObject(o: mediaEvent);
        }
    }

    private void DrainReadyEvents(IMFMediaEventGenerator generator) {
        while (true) {
            var hr = generator.GetEvent(dwFlags: MfEventFlagNoWait, ppEvent: out var mediaEvent);

            if (hr == MfENoEventsAvailable) {
                return;
            }

            Check(hr: hr);
            _ = HandleEvent(mediaEvent: mediaEvent);
        }
    }

    private void DrainSynchronousOutputs() {
        while (true) {
            if (!CollectOutput()) {
                return;
            }
        }
    }

    // Pulls one output through ProcessOutput. Returns false when the MFT needs more input (no packet produced). A
    // hardware MFT provides its own sample (pSample null going in); a software MFT reads into a caller-allocated one.
    private bool CollectOutput() {
        var allocated = (m_providesSamples ? null : CreateOutputSample());
        var dataBuffer = new MftOutputDataBuffer {
            dwStreamID = 0,
            pSample = ((allocated is not null) ? Marshal.GetIUnknownForObject(o: allocated) : 0),
        };

        try {
            var hr = m_transform.ProcessOutput(dwFlags: 0, cOutputBufferCount: 1, pOutputSamples: ref dataBuffer, pdwStatus: out _);

            if ((hr == MfETransformNeedMoreInput) || (hr == MfETransformStreamChange)) {
                return false;
            }

            Check(hr: hr);

            var sample = (allocated ?? (IMFSample2)Marshal.GetObjectForIUnknown(pUnk: dataBuffer.pSample));

            try {
                ReadOutputSample(sample: sample);
            } finally {
                if (allocated is null) {
                    _ = Marshal.ReleaseComObject(o: sample);
                }
            }

            return true;
        } finally {
            if (dataBuffer.pSample != 0) {
                _ = Marshal.Release(pUnk: dataBuffer.pSample);
            }

            if (dataBuffer.pEvents != 0) {
                _ = Marshal.Release(pUnk: dataBuffer.pEvents);
            }

            if (allocated is not null) {
                _ = Marshal.ReleaseComObject(o: allocated);
            }
        }
    }

    private void ReadOutputSample(IMFSample2 sample) {
        var timestampNanoseconds = ((sample.GetSampleTime(phnsSampleTime: out var hns) >= 0) ? (hns * 100) : 0);
        var cleanPointKey = MFSampleExtension_CleanPoint;
        var cleanResult = sample.GetUINT32(guidKey: ref cleanPointKey, punValue: out var clean);
        var isKeyframe = ((cleanResult >= 0) ? (clean != 0) : !m_firstOutputSeen);

        m_firstOutputSeen = true;

        Check(hr: sample.ConvertToContiguousBuffer(ppBuffer: out var buffer));

        try {
            Check(hr: buffer.GetCurrentLength(pcbCurrentLength: out var length));
            Check(hr: buffer.Lock(ppbBuffer: out var pointer, pcbMaxLength: out _, pcbCurrentLength: out _));

            try {
                var count = (int)length;

                if (m_outputScratch.Length < count) {
                    m_outputScratch = new byte[count];
                }

                Marshal.Copy(source: pointer, destination: m_outputScratch, startIndex: 0, length: count);
                EmitPacket(encoded: m_outputScratch.AsSpan(start: 0, length: count), timestampNanoseconds: timestampNanoseconds, isKeyframe: isKeyframe);
            } finally {
                _ = buffer.Unlock();
            }
        } finally {
            _ = Marshal.ReleaseComObject(o: buffer);
        }
    }

    private void EmitPacket(ReadOnlySpan<byte> encoded, long timestampNanoseconds, bool isKeyframe) {
        if (m_codec == EncoderCodec.H264) {
            var payload = AvcConfigRecord.ToLengthPrefixed(annexB: encoded, sps: ref m_sps, pps: ref m_pps);

            if ((m_codecPrivate.Length == 0) && (m_sps is not null) && (m_pps is not null)) {
                m_codecPrivate = AvcConfigRecord.Build(sps: m_sps, pps: m_pps);
            }

            if (payload.Length == 0) {
                return;
            }

            m_packets.Add(item: new RecordedPacket(Data: payload, TimestampNanoseconds: timestampNanoseconds, IsKeyframe: isKeyframe));
        } else {
            var payload = encoded.ToArray();

            if ((m_codecPrivate.Length == 0) && isKeyframe) {
                m_codecPrivate = Av1ConfigRecord.Build(temporalUnit: payload);
            }

            m_packets.Add(item: new RecordedPacket(Data: payload, TimestampNanoseconds: timestampNanoseconds, IsKeyframe: isKeyframe));
        }
    }

    private IMFSample2 CreateOutputSample() {
        Check(hr: MFCreateMemoryBuffer(cbMaxLength: m_outputSampleSize, ppBuffer: out var buffer));
        Check(hr: MFCreateSample(ppIMFSample: out var sample));
        Check(hr: sample.AddBuffer(pBuffer: buffer));
        _ = Marshal.ReleaseComObject(o: buffer);

        return sample;
    }

    private static ulong PackU32Pair(uint high, uint low) => (((ulong)high) << 32) | low;
}
