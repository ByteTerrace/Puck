namespace Puck.HumbleGamingDeck.Forge;

/// <summary>The documented 6502 opcodes the Deck's programs are written with, named by mnemonic and addressing mode
/// (https://www.nesdev.org/wiki/CPU_unofficial_opcodes lists the full matrix; only official opcodes appear here).</summary>
public static class DeckOpcodes {
    /// <summary>ADC #imm.</summary>
    public const byte AdcImmediate = 0x69;
    /// <summary>ADC zp.</summary>
    public const byte AdcZeroPage = 0x65;
    /// <summary>ADC abs.</summary>
    public const byte AdcAbsolute = 0x6D;
    /// <summary>ADC abs,X.</summary>
    public const byte AdcAbsoluteX = 0x7D;
    /// <summary>AND #imm.</summary>
    public const byte AndImmediate = 0x29;
    /// <summary>AND zp.</summary>
    public const byte AndZeroPage = 0x25;
    /// <summary>AND abs,Y.</summary>
    public const byte AndAbsoluteY = 0x39;
    /// <summary>AND abs,X.</summary>
    public const byte AndAbsoluteX = 0x3D;
    /// <summary>ASL A.</summary>
    public const byte AslAccumulator = 0x0A;
    /// <summary>ASL zp.</summary>
    public const byte AslZeroPage = 0x06;
    /// <summary>BCC rel.</summary>
    public const byte Bcc = 0x90;
    /// <summary>BCS rel.</summary>
    public const byte Bcs = 0xB0;
    /// <summary>BEQ rel.</summary>
    public const byte Beq = 0xF0;
    /// <summary>BIT abs.</summary>
    public const byte BitAbsolute = 0x2C;
    /// <summary>BMI rel.</summary>
    public const byte Bmi = 0x30;
    /// <summary>BNE rel.</summary>
    public const byte Bne = 0xD0;
    /// <summary>BPL rel.</summary>
    public const byte Bpl = 0x10;
    /// <summary>CLC.</summary>
    public const byte Clc = 0x18;
    /// <summary>CLD.</summary>
    public const byte Cld = 0xD8;
    /// <summary>CMP #imm.</summary>
    public const byte CmpImmediate = 0xC9;
    /// <summary>CMP zp.</summary>
    public const byte CmpZeroPage = 0xC5;
    /// <summary>CMP abs,X.</summary>
    public const byte CmpAbsoluteX = 0xDD;
    /// <summary>CMP abs,Y.</summary>
    public const byte CmpAbsoluteY = 0xD9;
    /// <summary>CMP (zp),Y.</summary>
    public const byte CmpIndirectY = 0xD1;
    /// <summary>CPX #imm.</summary>
    public const byte CpxImmediate = 0xE0;
    /// <summary>CPX zp.</summary>
    public const byte CpxZeroPage = 0xE4;
    /// <summary>CPY #imm.</summary>
    public const byte CpyImmediate = 0xC0;
    /// <summary>CPY zp.</summary>
    public const byte CpyZeroPage = 0xC4;
    /// <summary>DEC zp.</summary>
    public const byte DecZeroPage = 0xC6;
    /// <summary>DEC zp,X.</summary>
    public const byte DecZeroPageX = 0xD6;
    /// <summary>DEX.</summary>
    public const byte Dex = 0xCA;
    /// <summary>DEY.</summary>
    public const byte Dey = 0x88;
    /// <summary>EOR #imm.</summary>
    public const byte EorImmediate = 0x49;
    /// <summary>EOR zp.</summary>
    public const byte EorZeroPage = 0x45;
    /// <summary>INC zp.</summary>
    public const byte IncZeroPage = 0xE6;
    /// <summary>INC zp,X.</summary>
    public const byte IncZeroPageX = 0xF6;
    /// <summary>INX.</summary>
    public const byte Inx = 0xE8;
    /// <summary>INY.</summary>
    public const byte Iny = 0xC8;
    /// <summary>JMP abs.</summary>
    public const byte Jmp = 0x4C;
    /// <summary>JSR abs.</summary>
    public const byte Jsr = 0x20;
    /// <summary>LDA #imm.</summary>
    public const byte LdaImmediate = 0xA9;
    /// <summary>LDA zp.</summary>
    public const byte LdaZeroPage = 0xA5;
    /// <summary>LDA zp,X.</summary>
    public const byte LdaZeroPageX = 0xB5;
    /// <summary>LDA abs.</summary>
    public const byte LdaAbsolute = 0xAD;
    /// <summary>LDA abs,X.</summary>
    public const byte LdaAbsoluteX = 0xBD;
    /// <summary>LDA abs,Y.</summary>
    public const byte LdaAbsoluteY = 0xB9;
    /// <summary>LDA (zp),Y.</summary>
    public const byte LdaIndirectY = 0xB1;
    /// <summary>LDX #imm.</summary>
    public const byte LdxImmediate = 0xA2;
    /// <summary>LDX zp.</summary>
    public const byte LdxZeroPage = 0xA6;
    /// <summary>LDY #imm.</summary>
    public const byte LdyImmediate = 0xA0;
    /// <summary>LDY zp.</summary>
    public const byte LdyZeroPage = 0xA4;
    /// <summary>LSR A.</summary>
    public const byte LsrAccumulator = 0x4A;
    /// <summary>LSR zp.</summary>
    public const byte LsrZeroPage = 0x46;
    /// <summary>ORA #imm.</summary>
    public const byte OraImmediate = 0x09;
    /// <summary>ORA zp.</summary>
    public const byte OraZeroPage = 0x05;
    /// <summary>PHA.</summary>
    public const byte Pha = 0x48;
    /// <summary>PLA.</summary>
    public const byte Pla = 0x68;
    /// <summary>ROL A.</summary>
    public const byte RolAccumulator = 0x2A;
    /// <summary>ROL zp.</summary>
    public const byte RolZeroPage = 0x26;
    /// <summary>RTI.</summary>
    public const byte Rti = 0x40;
    /// <summary>RTS.</summary>
    public const byte Rts = 0x60;
    /// <summary>SBC #imm.</summary>
    public const byte SbcImmediate = 0xE9;
    /// <summary>SBC zp.</summary>
    public const byte SbcZeroPage = 0xE5;
    /// <summary>SEC.</summary>
    public const byte Sec = 0x38;
    /// <summary>SEI.</summary>
    public const byte Sei = 0x78;
    /// <summary>STA zp.</summary>
    public const byte StaZeroPage = 0x85;
    /// <summary>STA zp,X.</summary>
    public const byte StaZeroPageX = 0x95;
    /// <summary>STA abs.</summary>
    public const byte StaAbsolute = 0x8D;
    /// <summary>STA abs,X.</summary>
    public const byte StaAbsoluteX = 0x9D;
    /// <summary>STA abs,Y.</summary>
    public const byte StaAbsoluteY = 0x99;
    /// <summary>STA (zp),Y.</summary>
    public const byte StaIndirectY = 0x91;
    /// <summary>STX zp.</summary>
    public const byte StxZeroPage = 0x86;
    /// <summary>STX abs.</summary>
    public const byte StxAbsolute = 0x8E;
    /// <summary>STY zp.</summary>
    public const byte StyZeroPage = 0x84;
    /// <summary>TAX.</summary>
    public const byte Tax = 0xAA;
    /// <summary>TAY.</summary>
    public const byte Tay = 0xA8;
    /// <summary>TXA.</summary>
    public const byte Txa = 0x8A;
    /// <summary>TXS.</summary>
    public const byte Txs = 0x9A;
    /// <summary>TYA.</summary>
    public const byte Tya = 0x98;
}
