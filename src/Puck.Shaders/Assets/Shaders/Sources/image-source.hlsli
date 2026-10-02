// image-source.hlsli — reads an uploaded image source's region and decodes its pixels.
//
// The region layout is Puck.Abstractions.Sources.ImageSourceUploadLayout: eight little-endian uints of header (width,
// height, format code, color word, plane 0 byte offset and row stride, plane 1 byte offset and row stride), then the
// planes. The color word packs the Y'CbCr matrix in bits 0-7, the code range in 8-15, the transfer function in 16-23
// and the primaries in 24-31, with the codes of ImageYuvMatrix, ImageYuvRange and ImageTransferFunction. The arithmetic
// here is the one ImageSourceConversion states on the CPU; a change to either changes both.

#ifndef IMAGE_SOURCE_HLSLI
#define IMAGE_SOURCE_HLSLI

// ImagePixelFormat codes.
#define IMAGE_FORMAT_R8G8B8A8 1u
#define IMAGE_FORMAT_B8G8R8A8 2u
#define IMAGE_FORMAT_INDEXED8 3u
#define IMAGE_FORMAT_NV12 4u
#define IMAGE_FORMAT_R10G10B10A2 5u
#define IMAGE_FORMAT_R16G16B16A16F 6u

// ImageYuvMatrix, ImageYuvRange and ImageTransferFunction codes.
#define IMAGE_MATRIX_BT601 0u
#define IMAGE_MATRIX_BT709 1u
#define IMAGE_MATRIX_BT2020 2u
#define IMAGE_RANGE_LIMITED 0u
#define IMAGE_RANGE_FULL 1u
#define IMAGE_TRANSFER_SRGB 0u
#define IMAGE_TRANSFER_LINEAR 1u
#define IMAGE_TRANSFER_PQ 2u

// ImageColorPrimaries codes.
#define IMAGE_PRIMARIES_BT709 0u
#define IMAGE_PRIMARIES_BT2020 1u

// DisplayOutput.SdrWhiteNits: the luminance of an scRGB (linear) value of one.
#define IMAGE_SDR_WHITE_NITS 80.0

struct ImageSourceHeader {
    uint width;
    uint height;
    uint format;
    uint color;
    uint plane0;
    uint stride0;
    uint plane1;
    uint stride1;
};

ImageSourceHeader imageSourceHeader(ByteAddressBuffer region) {
    uint4 first = region.Load4(0);
    uint4 second = region.Load4(16);
    ImageSourceHeader header;

    header.width = first.x;
    header.height = first.y;
    header.format = first.z;
    header.color = first.w;
    header.plane0 = second.x;
    header.stride0 = second.y;
    header.plane1 = second.z;
    header.stride1 = second.w;

    return header;
}

uint imageSourceByte(ByteAddressBuffer region, uint address) {
    return ((region.Load(address & ~3u) >> ((address & 3u) * 8u)) & 0xFFu);
}

float4 imageSourceUnpackRgba8(uint word) {
    return (float4(word & 0xFFu, (word >> 8) & 0xFFu, (word >> 16) & 0xFFu, word >> 24) / 255.0);
}

// Y'CbCr codes to R'G'B', unclamped; ImageSourceConversion.YuvToRgb.
float3 imageSourceYuvToRgb(uint y, uint cb, uint cr, uint color) {
    uint matrixCode = (color & 0xFFu);
    uint rangeCode = ((color >> 8) & 0xFFu);
    float kr = 0.2126;
    float kb = 0.0722;

    if (matrixCode == IMAGE_MATRIX_BT601) {
        kr = 0.299;
        kb = 0.114;
    } else if (matrixCode == IMAGE_MATRIX_BT2020) {
        kr = 0.2627;
        kb = 0.0593;
    }

    float luma;
    float blue;
    float red;

    if (rangeCode == IMAGE_RANGE_FULL) {
        luma = (float(y) / 255.0);
        blue = ((float(cb) - 128.0) / 255.0);
        red = ((float(cr) - 128.0) / 255.0);
    } else {
        luma = ((float(y) - 16.0) / 219.0);
        blue = ((float(cb) - 128.0) / 224.0);
        red = ((float(cr) - 128.0) / 224.0);
    }

    float r = (luma + ((2.0 - (2.0 * kr)) * red));
    float b = (luma + ((2.0 - (2.0 * kb)) * blue));
    float g = ((luma - (kr * r) - (kb * b)) / (1.0 - kr - kb));

    return float3(r, g, b);
}

// SMPTE ST 2084 decode to cd/m²; ImageSourceConversion.PqToNits.
float imageSourcePqToNits(float value) {
    const float c1 = (3424.0 / 4096.0);
    const float c2 = ((2413.0 / 4096.0) * 32.0);
    const float c3 = ((2392.0 / 4096.0) * 32.0);
    const float m1 = (2610.0 / 16384.0);
    const float m2 = ((2523.0 / 4096.0) * 128.0);
    float power = pow(saturate(value), (1.0 / m2));

    return (10000.0 * pow((max((power - c1), 0.0) / (c2 - (c3 * power))), (1.0 / m1)));
}

float imageSourceSrgbToLinear(float value) {
    float clamped = saturate(value);

    return ((clamped <= 0.04045) ? (clamped / 12.92) : pow(((clamped + 0.055) / 1.055), 2.4));
}

// One stored value to linear light relative to the paper-white level; ImageSourceConversion.ToLinear. An sRGB value is
// relative to SDR white already, a linear value is scRGB (one at 80 cd/m²) and is not clamped, and a perceptual-quantizer
// value is its luminance over the paper white.
float imageSourceToLinear(uint transfer, float value, float paperWhiteNits) {
    if (transfer == IMAGE_TRANSFER_PQ) {
        return (imageSourcePqToNits(value) / paperWhiteNits);
    }

    if (transfer == IMAGE_TRANSFER_LINEAR) {
        return ((value * IMAGE_SDR_WHITE_NITS) / paperWhiteNits);
    }

    return imageSourceSrgbToLinear(value);
}

// Linear light to a working value: the sRGB curve extended past one and mirrored below zero, the curve the display encode
// decodes; ImageSourceConversion.LinearToWorking.
float imageSourceLinearToWorking(float value) {
    float magnitude = abs(value);
    float encoded = ((magnitude <= 0.0031308) ? (magnitude * 12.92) : ((1.055 * pow(magnitude, (1.0 / 2.4))) - 0.055));

    return ((value < 0.0) ? -encoded : encoded);
}

// One sample's stored channels to working values: decoded to linear light relative to the paper white, BT.2020 primaries
// moved to BT.709 (ITU-R BT.2407), then encoded; ImageSourceConversion.ToWorking.
float3 imageSourceToWorking(uint color, float3 stored, float paperWhiteNits) {
    uint transfer = ((color >> 16) & 0xFFu);
    float3 linearLight = float3(
        imageSourceToLinear(transfer, stored.r, paperWhiteNits),
        imageSourceToLinear(transfer, stored.g, paperWhiteNits),
        imageSourceToLinear(transfer, stored.b, paperWhiteNits)
    );

    if ((color >> 24) == IMAGE_PRIMARIES_BT2020) {
        const float3x3 bt2020ToBt709 = {
            1.6604910, -0.5876411, -0.0728499,
            -0.1245505, 1.1328999, -0.0083494,
            -0.0181508, -0.1005789, 1.1187297,
        };

        linearLight = mul(bt2020ToBt709, linearLight);
    }

    return float3(imageSourceLinearToWorking(linearLight.r), imageSourceLinearToWorking(linearLight.g), imageSourceLinearToWorking(linearLight.b));
}

#endif
