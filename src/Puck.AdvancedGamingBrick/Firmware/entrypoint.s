@ SPDX-License-Identifier: Apache-2.0 OR MIT
@ Copyright 2020 - 2021 DenSinH and fleroviux
@ Copyright (c) 2026 ByteTerrace
@ Exception/IRQ structure adapted from Cult-of-GBA/BIOS, commit
@ a30e9a96df083628b650724b7d4d7112b4070b98. See README.md and LICENSE.
.syntax unified
.cpu arm7tdmi
.arm
.section .vectors,"ax",%progbits
.global puck_reset
.global puck_soft_reset
.global puck_download_enter
.global puck_fast_boot
.global puck_delay
.global puck_mul_hi

    b puck_reset
    b .fault
    b .software_interrupt
    b .fault
    b .fault
    b .fault
    b .interrupt
    b .fault

@ A second public entry permits fast boot through the SAME initialization and
@ handoff instructions, with only the visible presentation omitted. No ROM patch.
puck_fast_boot:
    mov r10, #1
    b .reset_common
puck_reset:
    mov r10, #0
.reset_common:
    msr cpsr_c, #0xD3
    mov r0, #0x04000000
    mov r1, #0
    str r1, [r0, #0x208]
    ldr sp, =0x03007FE0
    msr spsr_fsxc, r1
    msr cpsr_c, #0xD2
    ldr sp, =0x03007FA0
    mov lr, #0
    msr spsr_fsxc, r1
    msr cpsr_c, #0xDF
    ldr sp, =0x03007F00
    mov r0, #0xFF
    bl puck_register_reset
    cmp r10, #0
    bleq .boot_thunk
    mov r0, #0xFF
    bl puck_register_reset
    mov r0, #0x04000000
    ldr r1, =0x880E
    strh r1, [r0, #0x82]
    mov r1, #0x200
    strh r1, [r0, #0x88]
    mov r1, #1
    strb r1, [r0, #0x300]
    ldr r0, =0x03007FFA
    mov r1, #0
    strb r1, [r0]
    b puck_soft_reset

@ ARMv4T has no BLX instruction. A conditional BL to this ARM thunk is sufficient.
.boot_thunk:
    ldr r12, =puck_boot
    bx r12

puck_download_enter:
    mov r3, #0x02000000
    add r3, r3, #0xC0
    b .reset_registers

puck_soft_reset:
    ldr r0, =0x03007FFA
    ldrb r3, [r0]
    cmp r3, #0
    moveq r3, #0x08000000
    movne r3, #0x02000000
.reset_registers:
    mov r0, #0
    msr cpsr_c, #0xD3
    ldr sp, =0x03007FE0
    mov lr, #0
    msr spsr_fsxc, r0
    msr cpsr_c, #0xD2
    ldr sp, =0x03007FA0
    mov lr, #0
    msr spsr_fsxc, r0
    msr cpsr_c, #0xDF
    ldr sp, =0x03007F00
    ldr r1, =0x03007E00
    mov r2, #0x200
.clear_stack:
    str r0, [r1], #4
    subs r2, r2, #4
    bne .clear_stack
    mov lr, r3
    mov r1, #0
    mov r2, #0
    mov r3, #0
    mov r4, #0
    mov r5, #0
    mov r6, #0
    mov r7, #0
    mov r8, #0
    mov r9, #0
    mov r10, #0
    mov r11, #0
    mov r12, #0
    msr cpsr_fsxc, #0x1F
    bx lr

.interrupt:
    stmdb sp!, {r0-r3, r12, lr}
    mov r0, #0x04000000
    adr lr, .interrupt_return
    ldr r3, [r0, #-4]
    bx r3
.interrupt_return:
    ldmia sp!, {r0-r3, r12, lr}
    subs pc, lr, #4

.software_interrupt:
    stmdb sp!, {r4-r12, lr}
    mrs r5, spsr
    stmdb sp!, {r5, r6}
    ldrb r4, [lr, #-2]
    and r5, r5, #0xC0
    orr r5, r5, #0x1F
    msr cpsr_c, r5
    stmdb sp!, {r0-r3, r12, lr}
    mov r0, sp
    mov r1, r4
    bl puck_dispatch
    ldmia sp!, {r0-r3, r12, lr}
    msr cpsr_c, #0xD3
    ldmia sp!, {r4, r5}
    msr spsr_fsxc, r4
    ldmia sp!, {r4-r12, lr}
    movs pc, lr

.fault:
    b .fault

puck_delay:
    subs r0, r0, #1
    bgt puck_delay
    bx lr
puck_mul_hi:
    umull r2, r3, r0, r1
    mov r0, r3
    bx lr
.ltorg
