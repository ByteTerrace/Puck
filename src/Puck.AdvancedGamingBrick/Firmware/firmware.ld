/* SPDX-License-Identifier: MIT */
OUTPUT_ARCH(arm)
ENTRY(puck_reset)
SECTIONS
{
    . = 0;
    .text : { KEEP(*(.vectors)) *(.text .text.*) *(.rodata .rodata.*) }
    . = ALIGN(4);
    __firmware_end = .;
    ASSERT(__firmware_end <= 0x3FFF, "Puck AGB firmware exceeds 16 KiB")
    .padding 0x3FFF : { BYTE(0) }
    /DISCARD/ : { *(.ARM.exidx*) *(.ARM.extab*) *(.comment) *(.note*) }
    .forbidden : { *(.data .data.*) *(.bss .bss.*) *(COMMON) }
    ASSERT(SIZEOF(.forbidden) == 0, "Firmware cannot carry mutable ROM globals")
}
