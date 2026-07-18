namespace Puck.Recording.Matroska;

/// <summary>
/// The EBML element identifiers the <see cref="MatroskaMuxer"/> emits, each stored as its canonical encoded
/// form (the length-marker bits are already part of the value, so <see cref="EbmlWriter.WriteId"/> writes the
/// natural byte count). Values are from the Matroska/WebM specification.
/// </summary>
internal static class MatroskaIds {
    // EBML header.
    public const uint Ebml = 0x1A45DFA3;
    public const uint EbmlVersion = 0x4286;
    public const uint EbmlReadVersion = 0x42F7;
    public const uint EbmlMaxIdLength = 0x42F2;
    public const uint EbmlMaxSizeLength = 0x42F3;
    public const uint DocType = 0x4282;
    public const uint DocTypeVersion = 0x4287;
    public const uint DocTypeReadVersion = 0x4285;

    // Segment and its top-level children.
    public const uint Segment = 0x18538067;
    public const uint SeekHead = 0x114D9B74;
    public const uint Info = 0x1549A966;
    public const uint Tracks = 0x1654AE6B;
    public const uint Cluster = 0x1F43B675;
    public const uint Cues = 0x1C53BB6B;

    // Info.
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint MuxingApp = 0x4D80;
    public const uint WritingApp = 0x5741;

    // Tracks / TrackEntry.
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackUid = 0x73C5;
    public const uint TrackType = 0x83;
    public const uint FlagLacing = 0x9C;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint CodecDelay = 0x56AA;
    public const uint SeekPreRoll = 0x56BB;
    public const uint DefaultDuration = 0x23E383;

    // Video.
    public const uint Video = 0xE0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;

    // Video / Colour (BT.709 limited-range signalling for the encoded stream).
    public const uint Colour = 0x55B0;
    public const uint ColourRange = 0x55B9;
    public const uint ColourMatrixCoefficients = 0x55B1;
    public const uint ColourTransferCharacteristics = 0x55BA;
    public const uint ColourPrimaries = 0x55BB;

    /// <summary>The Matroska <see cref="ColourRange"/> value for broadcast (limited/studio-swing) range.</summary>
    public const byte ColourRangeLimited = 1;

    /// <summary>The Matroska colour-coefficient value for ITU-R BT.709 (matrix, transfer, and primaries).</summary>
    public const byte ColourBt709 = 1;

    // Audio.
    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint Channels = 0x9F;
    public const uint BitDepth = 0x6264;

    // Cluster.
    public const uint Timestamp = 0xE7;
    public const uint SimpleBlock = 0xA3;

    // Cues.
    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
    public const uint CueRelativePosition = 0xF0;

    /// <summary>The video <see cref="TrackType"/> value.</summary>
    public const byte TrackTypeVideo = 1;

    /// <summary>The audio <see cref="TrackType"/> value.</summary>
    public const byte TrackTypeAudio = 2;
}
