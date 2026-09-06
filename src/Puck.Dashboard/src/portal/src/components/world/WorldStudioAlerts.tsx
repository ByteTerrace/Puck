import React from "react";
import { Alert, Text, Group, Badge } from "@mantine/core";
import { RiTrophyLine, RiSparklingLine } from "@remixicon/react";
import { SimulationContext } from "../../context/SimulationContext";
import {
  selectWinner,
  selectDisplayedRayBeam,
  selectSpeculativeResult,
  selectHoveredCell,
} from "../../machines/worldSimulationMachine";

export const WorldStudioAlerts: React.FC = () => {
  const winner = SimulationContext.useSelector(selectWinner);
  const displayedRayBeam = SimulationContext.useSelector(selectDisplayedRayBeam);
  const speculativeResult = SimulationContext.useSelector(selectSpeculativeResult);
  const hoveredCell = SimulationContext.useSelector(selectHoveredCell);

  return (
    <>
      {/* Game Over / Winner Banner */}
      {winner !== 0 && (
        <Alert
          icon={<RiTrophyLine size={20} />}
          title={
            winner === 3
              ? "Game Concluded: Draw"
              : `Victory: Player ${winner} (${winner === 1 ? "Rose-Coral 'X'" : "Jade 'O'"}) Wins!`
          }
          color={winner === 1 ? "coral" : winner === 2 ? "jade" : "gray"}
          mb="md"
          style={{
            background: "var(--quote-bg)",
            border: "1px solid var(--rule)",
            borderLeft: `4px solid ${winner === 1 ? "var(--accent)" : winner === 2 ? "var(--accent-2)" : "var(--rule)"}`,
          }}
        >
          <Text size="sm" style={{ fontFamily: '"Lora", Georgia, serif' }}>
            {displayedRayBeam
              ? `Four collinear marks verified along lattice ray ${displayedRayBeam.name}! Glowing vector is illuminated on the 3D topology.`
              : "World reached terminal win state according to Level reactive rules."}
          </Text>
        </Alert>
      )}

      {/* Speculative Ghost Hover HUD Alert */}
      {speculativeResult && winner === 0 && (
        <Alert
          icon={
            <RiSparklingLine
              size={18}
              color={speculativeResult.isWinningMove ? "var(--accent)" : "var(--accent-2)"}
            />
          }
          color={
            speculativeResult.isWinningMove
              ? "yellow"
              : speculativeResult.illegal
              ? "red"
              : "teal"
          }
          variant="light"
          mb="md"
          title={`Speculative Move Preview [Cell #${hoveredCell}]`}
        >
          {speculativeResult.illegal ? (
            <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
              Illegal Action: {speculativeResult.message}
            </Text>
          ) : (
            <Group gap="md">
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                Rule: <strong>{speculativeResult.firedRuleName}</strong> will fire
              </Text>
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                Next Turn: Player {speculativeResult.nextPlayer}
              </Text>
              {speculativeResult.isWinningMove && (
                <Badge size="xs" color="yellow" variant="filled">
                  CRITICAL WINNING MOVE!
                </Badge>
              )}
            </Group>
          )}
        </Alert>
      )}
    </>
  );
};

export default WorldStudioAlerts;
