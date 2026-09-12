/* SPDX-License-Identifier: Apache-2.0 OR MIT
 * Copyright (c) 2026 ByteTerrace
 * Native ARM7TDMI firmware: no host callbacks, C runtime, or mutable BIOS data.
 */
#ifndef PUCK_FIRMWARE_H
#define PUCK_FIRMWARE_H

typedef unsigned char u8;
typedef signed char s8;
typedef unsigned short u16;
typedef signed short s16;
typedef unsigned int u32;
typedef signed int s32;
typedef unsigned long long u64;

#define REG8(address) (*(volatile u8 *)(address))
#define REG16(address) (*(volatile u16 *)(address))
#define REG32(address) (*(volatile u32 *)(address))
#define IO 0x04000000u
#define SOUND_AREA 0x03007FF0u

typedef struct { u32 r0, r1, r2, r3; } PuckSwiFrame;

void puck_reset(void) __attribute__((noreturn));
void puck_soft_reset(void) __attribute__((noreturn));
void puck_download_enter(void) __attribute__((noreturn));
void puck_boot(void);
void puck_dispatch(PuckSwiFrame *frame, u32 number);
void puck_register_reset(u32 flags);
void puck_wait_frame(void);
void puck_delay(u32 iterations);
u32 puck_udiv(u32 numerator, u32 denominator);
u32 puck_mul_hi(u32 left, u32 right);
s32 puck_sdiv(s32 numerator, s32 denominator);
void puck_div(PuckSwiFrame *frame);
u32 puck_sqrt(u32 value);
s32 puck_atan(s32 tangent);
u32 puck_atan2(s32 x, s32 y);
void puck_affine(u32 source, u32 destination, u32 count, u32 stride, u32 object);
void puck_bit_unpack(u32 source, u32 destination, u32 info);
void puck_lz(u32 source, u32 destination, u32 wide);
void puck_rl(u32 source, u32 destination, u32 wide);
void puck_diff(u32 source, u32 destination, u32 width);
void puck_huffman(u32 source, u32 destination);
void puck_sound(PuckSwiFrame *frame, u32 number);
u32 puck_multiboot(u32 parameters, u32 mode);
u32 puck_download_requested(void);
void puck_receive_download(void) __attribute__((noreturn));

#endif
