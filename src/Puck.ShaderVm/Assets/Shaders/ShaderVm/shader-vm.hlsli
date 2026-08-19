#ifndef PUCK_SHADER_VM_HLSLI
#define PUCK_SHADER_VM_HLSLI

// Generic packed-value interpreter. KEEP IN SYNC with Puck.ShaderVm ShaderIsa, ShaderInput, ShaderOp,
// ShaderInstruction and ShaderInterpreter, whose host evaluation is the reference semantics of every opcode below.
// The including kernel supplies StructuredBuffer<uint> shaderVmWords and StructuredBuffer<float4> shaderVmParameters;
// no environment or sky concept crosses this boundary.
//
// Program layout, one word each unless noted:
//   0            magic
//   1            isa version
//   2            instruction count
//   3            constant count
//   4..          instructions, opcode in bits 0-7 and operand in bits 8-31
//   ..           constants, four words each
static const uint ShaderVmMagic = 0x4D564853u;
static const uint ShaderVmVersion = 2u;
static const uint ShaderVmHeaderWordCount = 4u;
static const uint ShaderVmMaxInstructions = 16384u;
static const uint ShaderVmMaxConstants = 512u;
// The stack and register file are per-lane arrays, dynamically indexed, so they land in scratch memory and their
// size sets occupancy. A kernel that knows its program's measured demand (ShaderProgramStatistics) defines these
// before including; the defaults are the ISA ceilings, which every valid program fits but no real program needs.
#ifndef PUCK_SHADER_VM_STACK_DEPTH
#define PUCK_SHADER_VM_STACK_DEPTH 64
#endif
#ifndef PUCK_SHADER_VM_LOCALS
#define PUCK_SHADER_VM_LOCALS 64
#endif
static const uint ShaderVmMaxStackDepth = ((uint)PUCK_SHADER_VM_STACK_DEPTH);
static const uint ShaderVmMaxLocals = ((uint)PUCK_SHADER_VM_LOCALS);
static const uint ShaderVmPcgMultiplier = 1664525u;
static const uint ShaderVmPcgIncrement = 1013904223u;
static const float ShaderVmInverseTwoPow32 = (1.0 / 4294967296.0);
static const float4 ShaderVmRefused = float4(1.0, 0.0, 1.0, 1.0);

static const uint ShaderVmInputCoordinate = 0u;
static const uint ShaderVmInputTime = 1u;
static const uint ShaderVmInputSampleIndex = 2u;

static const uint ShaderVmOpLoadInput = 0u;
static const uint ShaderVmOpLoadParameter = 1u;
static const uint ShaderVmOpLoadConstant = 2u;
static const uint ShaderVmOpLoadConstantDynamic = 3u;
static const uint ShaderVmOpLoadLocal = 4u;
static const uint ShaderVmOpStoreLocal = 5u;
static const uint ShaderVmOpSwizzle = 6u;
static const uint ShaderVmOpDuplicate = 7u;
static const uint ShaderVmOpSwap = 8u;
static const uint ShaderVmOpDrop = 9u;
static const uint ShaderVmOpPick = 10u;

static const uint ShaderVmOpAbsolute = 16u;
static const uint ShaderVmOpNegate = 17u;
static const uint ShaderVmOpFloor = 18u;
static const uint ShaderVmOpCeiling = 19u;
static const uint ShaderVmOpFraction = 20u;
static const uint ShaderVmOpSaturate = 21u;
static const uint ShaderVmOpTruncate = 22u;
static const uint ShaderVmOpRound = 23u;
static const uint ShaderVmOpSign = 24u;
static const uint ShaderVmOpReciprocal = 25u;
static const uint ShaderVmOpSquareRoot = 26u;
static const uint ShaderVmOpInverseSquareRoot = 27u;
static const uint ShaderVmOpExponential = 28u;
static const uint ShaderVmOpNaturalLogarithm = 29u;
static const uint ShaderVmOpSine = 30u;
static const uint ShaderVmOpCosine = 31u;
static const uint ShaderVmOpNormalize2 = 32u;
static const uint ShaderVmOpNormalize3 = 33u;
static const uint ShaderVmOpLength2 = 34u;
static const uint ShaderVmOpLength3 = 35u;
static const uint ShaderVmOpHash3 = 36u;
static const uint ShaderVmOpBitsToUnitFloat = 37u;
static const uint ShaderVmOpIntegerBits = 38u;

static const uint ShaderVmOpAdd = 64u;
static const uint ShaderVmOpSubtract = 65u;
static const uint ShaderVmOpMultiply = 66u;
static const uint ShaderVmOpDivide = 67u;
static const uint ShaderVmOpMinimum = 68u;
static const uint ShaderVmOpMaximum = 69u;
static const uint ShaderVmOpPower = 70u;
static const uint ShaderVmOpModulo = 71u;
static const uint ShaderVmOpStep = 72u;
static const uint ShaderVmOpDot2 = 73u;
static const uint ShaderVmOpDot3 = 74u;
static const uint ShaderVmOpCross3 = 75u;
static const uint ShaderVmOpLess = 76u;
static const uint ShaderVmOpGreater = 77u;
static const uint ShaderVmOpArcTangent2 = 78u;

static const uint ShaderVmOpLerp = 128u;
static const uint ShaderVmOpSmoothStep = 129u;
static const uint ShaderVmOpClamp = 130u;
static const uint ShaderVmOpSelect = 131u;

static const uint ShaderVmOpJump = 192u;
static const uint ShaderVmOpJumpIfZero = 193u;

static const uint ShaderVmOpValueNoise2 = 240u;
static const uint ShaderVmOpValueNoise3 = 241u;
static const uint ShaderVmOpFbm2 = 242u;

static const uint ShaderVmOpHalt = 255u;

struct ShaderVmContext {
    float4 coordinate;
    float time;
    uint sampleIndex;
};

uint3 shaderVmPcg3d(uint3 v) {
    v = ((v * ShaderVmPcgMultiplier) + ShaderVmPcgIncrement);
    v.x += (v.y * v.z); v.y += (v.z * v.x); v.z += (v.x * v.y);
    v ^= (v >> 16u);
    v.x += (v.y * v.z); v.y += (v.z * v.x); v.z += (v.x * v.y);

    return v;
}
// The lattice corner value: the cell coordinates are hashed by their float bit patterns, so a negative cell is as
// distinct as a positive one.
float shaderVmCorner(float x, float y, uint seed) {
    return ((float)shaderVmPcg3d(uint3(asuint(x), asuint(y), seed)).x * ShaderVmInverseTwoPow32);
}
float shaderVmQuintic(float value) {
    return ((value * value * value) * ((value * ((value * 6.0) - 15.0)) + 10.0));
}
float shaderVmLatticeNoise2(float2 p, uint seed) {
    float2 cell = floor(p);
    float a = shaderVmCorner(cell.x, cell.y, seed);
    float b = shaderVmCorner((cell.x + 1.0), cell.y, seed);
    float c = shaderVmCorner(cell.x, (cell.y + 1.0), seed);
    float d = shaderVmCorner((cell.x + 1.0), (cell.y + 1.0), seed);
    float u = shaderVmQuintic(p.x - cell.x);
    float v = shaderVmQuintic(p.y - cell.y);

    return lerp(lerp(a, b, u), lerp(c, d, u), v);
}
float shaderVmLatticeNoise3(float3 p, uint seed) {
    float cell = floor(p.z);
    float lower = shaderVmLatticeNoise2(p.xy, (seed ^ asuint(cell)));
    float upper = shaderVmLatticeNoise2(p.xy, (seed ^ asuint(cell + 1.0)));

    return lerp(lower, upper, shaderVmQuintic(p.z - cell));
}
// Lacunarity 2 and gain 1/2, each octave on its own seed and offset by 17 cells so the octaves do not align.
float shaderVmFbm2(float2 p, uint seed, uint octaves) {
    float amplitude = 0.5;
    float normalizer = 0.0;
    float value = 0.0;

    [loop]
    for (uint octave = 0u; (octave < octaves); octave++) {
        value += (amplitude * shaderVmLatticeNoise2(p, (seed + octave)));
        normalizer += amplitude;
        p = ((p * 2.0) + 17.0);
        amplitude *= 0.5;
    }

    return (value / normalizer);
}
float4 shaderVmNormalize(float4 value) {
    float squared = dot(value, value);

    return ((squared > 0.0) ? (value * rsqrt(squared)) : (float4)0.0);
}
float4 shaderVmInput(ShaderVmContext context, uint input) {
    if (input == ShaderVmInputCoordinate) { return context.coordinate; }
    if (input == ShaderVmInputTime) { return context.time.xxxx; }
    if (input == ShaderVmInputSampleIndex) { return ((float)context.sampleIndex).xxxx; }

    return ShaderVmRefused;
}
float4 shaderVmConstant(uint constantBase, uint index) {
    uint word = (constantBase + (index * 4u));

    return asfloat(uint4(shaderVmWords[word], shaderVmWords[(word + 1u)], shaderVmWords[(word + 2u)], shaderVmWords[(word + 3u)]));
}

float4 shaderVmEvaluate(ShaderVmContext context) {
    uint instructionCount = shaderVmWords[2];
    uint constantCount = shaderVmWords[3];

    if (
        (shaderVmWords[0] != ShaderVmMagic) ||
        (shaderVmWords[1] != ShaderVmVersion) ||
        (instructionCount > ShaderVmMaxInstructions) ||
        (constantCount > ShaderVmMaxConstants)
    ) {
        return ShaderVmRefused;
    }

    uint constantBase = (ShaderVmHeaderWordCount + instructionCount);
    float4 locals[ShaderVmMaxLocals];
    float4 stack[ShaderVmMaxStackDepth];
    uint depth = 0u;
    uint pointer = 0u;

    [loop]
    for (uint local = 0u; (local < ShaderVmMaxLocals); local++) {
        locals[local] = (float4)0.0;
    }

    // Jumps are forward-only, so the program can retire at most one instruction per step and this bound is exact.
    [loop]
    for (uint retired = 0u; (retired < instructionCount); retired++) {
        uint instruction = shaderVmWords[ShaderVmHeaderWordCount + pointer];
        uint op = (instruction & 0xFFu);
        uint operand = (instruction >> 8u);

        pointer++;

        switch (op) {
            case ShaderVmOpLoadInput: stack[depth++] = shaderVmInput(context, operand); break;
            case ShaderVmOpLoadParameter: stack[depth++] = shaderVmParameters[operand]; break;
            case ShaderVmOpLoadConstant: stack[depth++] = shaderVmConstant(constantBase, operand); break;
            case ShaderVmOpLoadConstantDynamic:
                stack[(depth - 1u)] = shaderVmConstant(constantBase, (uint)clamp((int)stack[(depth - 1u)].x, 0, (int)constantCount - 1));
                break;
            case ShaderVmOpLoadLocal: stack[depth++] = locals[operand]; break;
            case ShaderVmOpStoreLocal: locals[operand] = stack[--depth]; break;
            case ShaderVmOpSwizzle: {
                float4 source = stack[(depth - 1u)];

                stack[(depth - 1u)] = float4(
                    source[(operand & 3u)],
                    source[((operand >> 2u) & 3u)],
                    source[((operand >> 4u) & 3u)],
                    source[((operand >> 6u) & 3u)]
                );

                break;
            }
            case ShaderVmOpDuplicate: stack[depth] = stack[(depth - 1u)]; depth++; break;
            case ShaderVmOpSwap: {
                float4 top = stack[(depth - 1u)];

                stack[(depth - 1u)] = stack[(depth - 2u)];
                stack[(depth - 2u)] = top;

                break;
            }
            case ShaderVmOpDrop: depth--; break;
            case ShaderVmOpPick: stack[depth] = stack[((depth - 1u) - operand)]; depth++; break;

            case ShaderVmOpAbsolute: stack[(depth - 1u)] = abs(stack[(depth - 1u)]); break;
            case ShaderVmOpNegate: stack[(depth - 1u)] = -stack[(depth - 1u)]; break;
            case ShaderVmOpFloor: stack[(depth - 1u)] = floor(stack[(depth - 1u)]); break;
            case ShaderVmOpCeiling: stack[(depth - 1u)] = ceil(stack[(depth - 1u)]); break;
            case ShaderVmOpFraction: stack[(depth - 1u)] = frac(stack[(depth - 1u)]); break;
            case ShaderVmOpSaturate: stack[(depth - 1u)] = saturate(stack[(depth - 1u)]); break;
            case ShaderVmOpTruncate: stack[(depth - 1u)] = trunc(stack[(depth - 1u)]); break;
            case ShaderVmOpRound: stack[(depth - 1u)] = round(stack[(depth - 1u)]); break;
            case ShaderVmOpSign: stack[(depth - 1u)] = sign(stack[(depth - 1u)]); break;
            case ShaderVmOpReciprocal: stack[(depth - 1u)] = (1.0 / stack[(depth - 1u)]); break;
            case ShaderVmOpSquareRoot: stack[(depth - 1u)] = sqrt(stack[(depth - 1u)]); break;
            case ShaderVmOpInverseSquareRoot: stack[(depth - 1u)] = (1.0 / sqrt(stack[(depth - 1u)])); break;
            case ShaderVmOpExponential: stack[(depth - 1u)] = exp(stack[(depth - 1u)]); break;
            case ShaderVmOpNaturalLogarithm: stack[(depth - 1u)] = log(stack[(depth - 1u)]); break;
            case ShaderVmOpSine: stack[(depth - 1u)] = sin(stack[(depth - 1u)]); break;
            case ShaderVmOpCosine: stack[(depth - 1u)] = cos(stack[(depth - 1u)]); break;
            case ShaderVmOpNormalize2:
                stack[(depth - 1u)] = shaderVmNormalize(float4(stack[(depth - 1u)].xy, 0.0, 0.0));
                break;
            case ShaderVmOpNormalize3:
                stack[(depth - 1u)] = shaderVmNormalize(float4(stack[(depth - 1u)].xyz, 0.0));
                break;
            case ShaderVmOpLength2: stack[(depth - 1u)] = length(stack[(depth - 1u)].xy).xxxx; break;
            case ShaderVmOpLength3: stack[(depth - 1u)] = length(stack[(depth - 1u)].xyz).xxxx; break;
            case ShaderVmOpHash3:
                stack[(depth - 1u)] = float4(asfloat(shaderVmPcg3d(asuint(stack[(depth - 1u)].xyz))), 0.0);
                break;
            case ShaderVmOpBitsToUnitFloat:
                stack[(depth - 1u)] = (float4(asuint(stack[(depth - 1u)])) * ShaderVmInverseTwoPow32);
                break;
            case ShaderVmOpIntegerBits:
                stack[(depth - 1u)] = asfloat((uint4)stack[(depth - 1u)]);
                break;

            case ShaderVmOpAdd: stack[(depth - 2u)] += stack[(depth - 1u)]; depth--; break;
            case ShaderVmOpSubtract: stack[(depth - 2u)] -= stack[(depth - 1u)]; depth--; break;
            case ShaderVmOpMultiply: stack[(depth - 2u)] *= stack[(depth - 1u)]; depth--; break;
            case ShaderVmOpDivide: stack[(depth - 2u)] /= stack[(depth - 1u)]; depth--; break;
            case ShaderVmOpMinimum: stack[(depth - 2u)] = min(stack[(depth - 2u)], stack[(depth - 1u)]); depth--; break;
            case ShaderVmOpMaximum: stack[(depth - 2u)] = max(stack[(depth - 2u)], stack[(depth - 1u)]); depth--; break;
            case ShaderVmOpPower: stack[(depth - 2u)] = pow(stack[(depth - 2u)], stack[(depth - 1u)]); depth--; break;
            case ShaderVmOpModulo: stack[(depth - 2u)] = fmod(stack[(depth - 2u)], stack[(depth - 1u)]); depth--; break;
            case ShaderVmOpStep: stack[(depth - 2u)] = step(stack[(depth - 2u)], stack[(depth - 1u)]); depth--; break;
            case ShaderVmOpDot2: stack[(depth - 2u)] = dot(stack[(depth - 2u)].xy, stack[(depth - 1u)].xy).xxxx; depth--; break;
            case ShaderVmOpDot3: stack[(depth - 2u)] = dot(stack[(depth - 2u)].xyz, stack[(depth - 1u)].xyz).xxxx; depth--; break;
            case ShaderVmOpCross3:
                stack[(depth - 2u)] = float4(cross(stack[(depth - 2u)].xyz, stack[(depth - 1u)].xyz), 0.0);
                depth--;
                break;
            case ShaderVmOpLess:
                stack[(depth - 2u)] = (float4)(stack[(depth - 2u)] < stack[(depth - 1u)]);
                depth--;
                break;
            case ShaderVmOpGreater:
                stack[(depth - 2u)] = (float4)(stack[(depth - 2u)] > stack[(depth - 1u)]);
                depth--;
                break;
            case ShaderVmOpArcTangent2:
                stack[(depth - 2u)] = atan2(stack[(depth - 2u)], stack[(depth - 1u)]);
                depth--;
                break;

            case ShaderVmOpLerp:
                stack[(depth - 3u)] = lerp(stack[(depth - 3u)], stack[(depth - 2u)], stack[(depth - 1u)]);
                depth -= 2u;
                break;
            case ShaderVmOpSmoothStep:
                stack[(depth - 3u)] = smoothstep(stack[(depth - 3u)], stack[(depth - 2u)], stack[(depth - 1u)]);
                depth -= 2u;
                break;
            case ShaderVmOpClamp:
                stack[(depth - 3u)] = clamp(stack[(depth - 3u)], stack[(depth - 2u)], stack[(depth - 1u)]);
                depth -= 2u;
                break;
            case ShaderVmOpSelect:
                stack[(depth - 3u)] = lerp(stack[(depth - 3u)], stack[(depth - 2u)], (float4)(stack[(depth - 1u)] != 0.0));
                depth -= 2u;
                break;

            case ShaderVmOpJump: pointer = operand; break;
            case ShaderVmOpJumpIfZero:
                depth--;
                if (stack[depth].x == 0.0) { pointer = operand; }
                break;

            case ShaderVmOpValueNoise2:
                stack[(depth - 1u)] = shaderVmLatticeNoise2(stack[(depth - 1u)].xy, asuint(stack[(depth - 1u)].w)).xxxx;
                break;
            case ShaderVmOpValueNoise3:
                stack[(depth - 1u)] = shaderVmLatticeNoise3(stack[(depth - 1u)].xyz, asuint(stack[(depth - 1u)].w)).xxxx;
                break;
            case ShaderVmOpFbm2:
                stack[(depth - 1u)] = shaderVmFbm2(stack[(depth - 1u)].xy, asuint(stack[(depth - 1u)].w), operand).xxxx;
                break;

            case ShaderVmOpHalt: return stack[(depth - 1u)];

            default: return ShaderVmRefused;
        }
    }

    return ShaderVmRefused;
}

#endif
