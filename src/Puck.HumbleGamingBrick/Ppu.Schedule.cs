namespace Puck.HumbleGamingBrick;

/// <summary>The picture processor's CPU-visible schedule: the per-line events that land LY, the comparison LY, the
/// polled mode bits, and the interrupt-side mode on their own dots, the coincidence and STAT-line logic that runs
/// every dot, and the quiet-dot derivation that lets a tick skip all of it where nothing can fire.</summary>
public sealed partial class Ppu {
    // Apply this dot's scheduled LY/LYC/STAT events. The schedule runs in two passes: the current line's events
    // (shifted by the injected phases, which can push an event past either boundary) and the NEXT line's earliest
    // events, which a negative phase pulls onto this line's tail — the hardware arms parts of the next line's group
    // before the counter wraps.
    private void ApplyStatSchedule() {
        // The polled mode-2→3 edge trails the internal transition on the physical line (later still on the first line
        // after an LCD enable, where the polled mode holds at 0 through the scan and shows 3 as drawing engages).
        if (m_ly < VisibleScanlines) {
            if (m_firstLineAfterEnable) {
                if (m_dot == FirstLinePolledMode3Dot) {
                    m_statMode = 3;
                }
            } else if (m_dot == (OamScanDots + PolledMode3Lag)) {
                m_statMode = 3;
            }
        }

        ApplyLineSchedule(
            line: m_ly,
            nominalDot: (m_dot - LineEventPhase)
        );

        var lineLength = (m_firstLineAfterEnable
            ? FirstLineLength
            : DotsPerScanline);
        var nextLine = (((m_ly + 1) == ScanlinesPerFrame)
            ? 0
            : (m_ly + 1));

        ApplyLineSchedule(
            line: nextLine,
            nominalDot: ((m_dot - lineLength) - LineEventPhase)
        );
    }
    // Dispatch one line's event schedule by line kind. The nominal dot is the position within the line's OWN schedule;
    // callers translate physical dots into it, so an event fires exactly once wherever the phase pushed it.
    private void ApplyLineSchedule(int line, int nominalDot) {
        if (line < VisibleScanlines) {
            ApplyVisibleLineSchedule(
                line: line,
                nominalDot: nominalDot
            );
        } else if (line == VisibleScanlines) {
            ApplyVBlankEntrySchedule(nominalDot: nominalDot);
        } else if (line == (ScanlinesPerFrame - 1)) {
            ApplyLine153Schedule(nominalDot: nominalDot);
        } else {
            ApplyVBlankLineSchedule(
                line: line,
                nominalDot: nominalDot
            );
        }
    }
    // A visible line's schedule: the LY register lands first, opening the comparison gap (except on line 0, whose
    // comparison never lapses — LY 0 was already valid through the end of line 153); one dot later the comparison
    // becomes valid for the new line and the polled mode shows 2. The OAM interrupt condition is a short pulse that
    // runs ahead of the rest of the group by its own offset (and is skipped on line 0, where the vertical-blank source
    // still holds the line until the pulse dot, and on the first line after an LCD enable). The polled mode-2→3 edge
    // trails the internal transition on its own physical schedule, handled in Tick.
    private void ApplyVisibleLineSchedule(int line, int nominalDot) {
        // The first line after an LCD enable plays no line-start events: LY and its comparison were seeded by the
        // enable write and hold, and no OAM pulse is raised.
        if (
            m_firstLineAfterEnable &&
            (line == 0)
        ) {
            return;
        }

        var pulseDot = ((LineEventLyWriteVisibleDot + OamPulseOffset) + ((line == 0)
            ? 1
            : 0));

        if (
            (line != 0) &&
            (nominalDot == pulseDot)
        ) {
            m_irqMode = 2;
        }

        // Line 0's pulse never leads into line 153: it fires only from its own line's pass.
        if (
            (line == 0) &&
            (nominalDot == pulseDot) &&
            (nominalDot >= 0) &&
            (m_ly == 0)
        ) {
            m_irqMode = 2;
        }

        if (nominalDot == (pulseDot + 2)) {
            m_irqMode = -1;
        }

        if (nominalDot == (LineEventLyWriteVisibleDot + PolledEventPhase)) {
            m_lyRegister = ((byte)line);

            if (
                (line != 0) ||
                !m_supportsColor
            ) {
                m_statMode = 0;
            }
        }

        if (nominalDot == (LineEventLyWriteVisibleDot + LycEventPhase)) {
            m_lyForComparison = ((line != 0)
                ? LycNone
                : 0);
        }

        if (nominalDot == (LineEventComparisonDot + PolledEventPhase)) {
            m_statMode = 2;
        }

        if (nominalDot == (LineEventComparisonDot + LycEventPhase)) {
            m_lyForComparison = line;
        }
    }
    // The vertical-blank entry line (144): the comparison gap opens at the boundary, LY lands, and the frame's
    // VBlank interrupt plus the polled mode-1 bits arrive one dot after the comparison. Entering vertical blank also
    // asserts the OAM STAT source — twice, around the entry — as a direct interrupt request gated on the STAT line
    // being low (a held mode-0 or LYC condition blocks it), without disturbing the line's edge detector.
    private void ApplyVBlankEntrySchedule(int nominalDot) {
        if (nominalDot == LycEventPhase) {
            m_lyForComparison = LycNone;
        }

        if (nominalDot == (LineEventLyWriteVBlankDot + PolledEventPhase)) {
            m_lyRegister = VisibleScanlines;
        }

        if (nominalDot == LineEventLyWriteVBlankDot) {
            RequestVBlankOamQuirk();
        }

        if (nominalDot == (LineEventComparisonDot + LycEventPhase)) {
            m_lyForComparison = VisibleScanlines;
        }

        if (nominalDot == (VBlankEntryDot + PolledEventPhase)) {
            m_statMode = 1;
        }

        if (nominalDot == VBlankEntryDot) {
            m_irqMode = 1;

            m_interrupts.Request(kind: InterruptKind.VBlank);
            RequestVBlankOamQuirk();
        }
    }
    // A plain vertical-blank line (145–152): the comparison gap, the LY register, then the comparison — the mode stays 1.
    private void ApplyVBlankLineSchedule(int line, int nominalDot) {
        if (nominalDot == LycEventPhase) {
            m_lyForComparison = LycNone;
        }

        if (nominalDot == (LineEventLyWriteVBlankDot + PolledEventPhase)) {
            m_lyRegister = ((byte)line);
        }

        if (nominalDot == (LineEventComparisonDot + LycEventPhase)) {
            m_lyForComparison = line;
        }
    }
    // Line 153: LY reads 153 only briefly at the start of the line, then hands over to 0 for the remainder (at single
    // speed the register drops with the comparison handover; at double speed it holds a couple of dots longer and the
    // 153 comparison persists through the gap), and the LYC comparison follows 153 → gap → 0 — so LYC=0 matches from
    // late in line 153 seamlessly through line 0, whose own schedule never lapses it.
    private void ApplyLine153Schedule(int nominalDot) {
        if (nominalDot == LycEventPhase) {
            m_lyForComparison = LycNone;
        }

        if (nominalDot == (Line153LyWriteDot + PolledEventPhase)) {
            m_lyRegister = ((byte)(ScanlinesPerFrame - 1));
        }

        if (nominalDot == (Line153HandoverDot + PolledEventPhase)) {
            if (!m_key1.IsDoubleSpeed) {
                m_lyRegister = 0;
            }
        }

        if (nominalDot == (Line153HandoverDot + LycEventPhase)) {
            m_lyForComparison = (ScanlinesPerFrame - 1);
        }

        if (nominalDot == (Line153ComparisonNoneDot + PolledEventPhase)) {
            m_lyRegister = 0;
        }

        if (
            (nominalDot == (Line153ComparisonNoneDot + LycEventPhase)) &&
            !m_key1.IsDoubleSpeed
        ) {
            m_lyForComparison = LycNone;
        }

        if (nominalDot == (Line153ComparisonZeroDot + LycEventPhase)) {
            m_lyForComparison = 0;
        }
    }
    // The vertical-blank-entry OAM STAT quirk: a direct interrupt request, fired only while the STAT line is low, that
    // does not feed the edge detector (so a subsequent real source rise still produces its own edge).
    private void RequestVBlankOamQuirk() {
        if (
            ((InterruptStatSelect() & Mode2InterruptEnable) != 0) &&
            !m_previousStatLine
        ) {
            m_interrupts.Request(kind: InterruptKind.LcdStatus);
        }
    }
    // Re-latch the LYC comparison against the comparison LY. During the gap after a line advance the polled coincidence
    // bit reads not-equal while the interrupt latch holds its level (at double speed both hold), so the bit reports the
    // lag the hardware shows and the interrupt source rises only when the new line's comparison becomes valid. Runs
    // every dot, so a mid-line LYC or LCDC write is reflected on the next dot.
    private void UpdateLycComparison() {
        if (
            (m_lyForComparison == LycNone) &&
            m_key1.IsDoubleSpeed
        ) {
            return;
        }

        if (m_lyForComparison == m_lyc) {
            m_lycCoincidence = true;
            m_lycInterruptLine = true;
        } else {
            if (m_lyForComparison != LycNone) {
                m_lycInterruptLine = false;
            }

            m_lycCoincidence = false;
        }
    }
    // The STAT interrupt fires on the rising edge of the OR of every enabled STAT source — the scheduled interrupt-mode
    // conditions and the latched LY=LYC coincidence — so a level that stays high does not re-fire (the hardware's STAT
    // line, not per-condition). The interrupt-side mode deliberately runs ahead of the polled mode bits: the OAM source
    // is a pulse at the line boundary, the HBlank source switches at the true mode-0 edge, and the VBlank source holds
    // from entry through line 153.
    private void UpdateStatInterrupt() {
        var statSelect = InterruptStatSelect();
        var line =
            ((((statSelect & Mode0InterruptEnable) != 0) && (m_irqMode == 0)) ||
            (((statSelect & Mode1InterruptEnable) != 0) && (m_irqMode == 1)) ||
            (((statSelect & Mode2InterruptEnable) != 0) && (m_irqMode == 2)) ||
            (((statSelect & LycInterruptEnable) != 0) && m_lycInterruptLine));

        if (
            line &&
            !m_previousStatLine
        ) {
            m_interrupts.Request(kind: InterruptKind.LcdStatus);
        }

        m_previousStatLine = line;
    }
    // The dots after the current one on which this tick's whole body is a no-op, so the next ticks may only advance
    // the counter. A stretch qualifies when every countdown is spent, the comparison LY is valid (its double-speed
    // gap branch cannot flip), the window comparator cannot match on this line, and the dot lies past the line's last
    // scheduled event: the scan period after its line-start events, horizontal blank after the pipeline's flip, or
    // a vertical-blank line after its own events. The run stops before the dot on which the next line's own
    // schedule may fire (the vertical-blank lines schedule at nominal dot zero), so every event still runs on the
    // slow path at exactly its dot.
    private int QuietDotsAhead() {
        if (
            (m_traceSink is not null) ||
            m_firstLineAfterEnable ||
            m_key1.IsStopped ||
            ((m_polledMode0Countdown | m_irqMode0Countdown | m_oamReadUnlockCountdown | m_oamWriteUnlockCountdown | m_videoRamReadUnlockCountdown | m_videoRamWriteUnlockCountdown) != 0) ||
            (m_lyForComparison == LycNone) ||
            (!m_windowYTriggered && ((m_lcdc & WindowEnable) != 0) && (m_ly == m_windowY))
        ) {
            return 0;
        }

        int lastQuietDot;

        if (m_ly < VisibleScanlines) {
            if (m_mode == 2) {
                // The line-start group ends with the object pulse's release, one dot later on line 0.
                var lastEventDot = ((LineEventLyWriteVisibleDot + OamPulseOffset + 2 + LineEventPhase) + ((m_ly == 0)
                    ? 1
                    : 0));

                if (m_dot < lastEventDot) {
                    return 0;
                }

                lastQuietDot = (OamScanDots - 1);
            } else if (
                (m_mode == 0) &&
                (m_dot > (OamScanDots + PolledMode3Lag))
            ) {
                lastQuietDot = (((m_ly + 1) >= VisibleScanlines)
                    ? (DotsPerScanline - 2)
                    : (DotsPerScanline - 1));
            } else {
                return 0;
            }
        } else {
            var lastEventDot = ((m_ly == VisibleScanlines)
                ? (VBlankEntryDot + LineEventPhase)
                : ((m_ly == (ScanlinesPerFrame - 1))
                    ? (Line153ComparisonZeroDot + LycEventPhase + LineEventPhase)
                    : (LineEventComparisonDot + LycEventPhase + LineEventPhase)));

            if (m_dot < lastEventDot) {
                return 0;
            }

            lastQuietDot = ((m_ly == (ScanlinesPerFrame - 1))
                ? (DotsPerScanline - 1)
                : (DotsPerScanline - 2));
        }

        return Math.Max(
            val1: 0,
            val2: (lastQuietDot - m_dot)
        );
    }
}
