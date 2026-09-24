#ifndef FIXTURE_COMMON_HLSLI
#define FIXTURE_COMMON_HLSLI

#include "math.hlsli"

float Brighten(float value) {
    return Scale(value, 2.0);
}

#endif
