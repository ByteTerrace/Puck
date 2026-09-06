import React, { useState, useEffect } from "react";
import {
  Modal,
  TextInput,
  Select,
  SegmentedControl,
  Button,
  Group,
  Stack,
  Text,
  Badge,
  Paper,
  Divider,
  SimpleGrid,
  ColorInput,
  ThemeIcon,
} from "@mantine/core";
import {
  RiUser3Line,
  RiCheckLine,
  RiTrophyLine,
  RiPaletteLine,
  RiSparklingLine,
} from "@remixicon/react";

export interface PlayerHudDefinition {
  id: number;
  label: string;
  token: string;
  color: string;
}

export interface WorldHudConfig {
  turnVariable: string;
  players: PlayerHudDefinition[];
  victoryTemplate: string;
  themePreset: string;
}

export interface WorldHudDesignerModalProps {
  opened: boolean;
  onClose: () => void;
  stateVariables: string[];
  initialConfig?: Partial<WorldHudConfig>;
  onSaveHudConfig: (config: WorldHudConfig) => void;
}

export const THEME_PRESETS = [
  {
    name: "BDS Theorem (Rose-Coral & Jade)",
    p1Color: "#f2879a",
    p2Color: "#4fd0b4",
  },
  {
    name: "Cyberpunk (Electric Cyan & Amber)",
    p1Color: "#00e5ff",
    p2Color: "#ff9100",
  },
  {
    name: "Royal Velvet (Amethyst & Emerald)",
    p1Color: "#b388ff",
    p2Color: "#00e676",
  },
  {
    name: "Solar Flare (Ruby & Sunburst)",
    p1Color: "#ff5252",
    p2Color: "#ffd740",
  },
];

export const TOKEN_PRESETS = [
  { label: "Classic Marks (X / O)", p1: "X", p2: "O" },
  { label: "Chess Royals (♚ / ♛)", p1: "♚", p2: "♛" },
  { label: "Geometric (▲ / ■)", p1: "▲", p2: "■" },
  { label: "Disc Glyphs (● / ○)", p1: "●", p2: "○" },
  { label: "Numeric (1 / 2)", p1: "1", p2: "2" },
];

export const WorldHudDesignerModal: React.FC<WorldHudDesignerModalProps> = ({
  opened,
  onClose,
  stateVariables,
  initialConfig,
  onSaveHudConfig,
}) => {
  const [turnVariable, setTurnVariable] = useState(
    initialConfig?.turnVariable ?? stateVariables.find((s) => s.includes("Active") || s.includes("Turn")) ?? "tttActive"
  );
  const [selectedPreset, setSelectedPreset] = useState("0");
  const [p1Label, setP1Label] = useState(initialConfig?.players?.[0]?.label ?? "Player 1");
  const [p1Token, setP1Token] = useState(initialConfig?.players?.[0]?.token ?? "X");
  const [p1Color, setP1Color] = useState(initialConfig?.players?.[0]?.color ?? "#f2879a");

  const [p2Label, setP2Label] = useState(initialConfig?.players?.[1]?.label ?? "Player 2");
  const [p2Token, setP2Token] = useState(initialConfig?.players?.[1]?.token ?? "O");
  const [p2Color, setP2Color] = useState(initialConfig?.players?.[1]?.color ?? "#4fd0b4");

  const [victoryTemplate, setVictoryTemplate] = useState(
    initialConfig?.victoryTemplate ?? "Victory: Player {winner} Wins!"
  );

  useEffect(() => {
    if (opened && initialConfig) {
      if (initialConfig.turnVariable) setTurnVariable(initialConfig.turnVariable);
      if (initialConfig.players?.[0]) {
        setP1Label(initialConfig.players[0].label);
        setP1Token(initialConfig.players[0].token);
        setP1Color(initialConfig.players[0].color);
      }
      if (initialConfig.players?.[1]) {
        setP2Label(initialConfig.players[1].label);
        setP2Token(initialConfig.players[1].token);
        setP2Color(initialConfig.players[1].color);
      }
      if (initialConfig.victoryTemplate) setVictoryTemplate(initialConfig.victoryTemplate);
    }
  }, [opened, initialConfig]);

  const handleApplyThemePreset = (idxStr: string) => {
    setSelectedPreset(idxStr);
    const preset = THEME_PRESETS[Number(idxStr)];
    if (preset) {
      setP1Color(preset.p1Color);
      setP2Color(preset.p2Color);
    }
  };

  const handleApplyTokenPreset = (val: string) => {
    const found = TOKEN_PRESETS.find((t) => t.label === val);
    if (found) {
      setP1Token(found.p1);
      setP2Token(found.p2);
    }
  };

  const handleSave = () => {
    const config: WorldHudConfig = {
      turnVariable,
      players: [
        { id: 1, label: p1Label.trim() || "Player 1", token: p1Token.trim() || "X", color: p1Color },
        { id: 2, label: p2Label.trim() || "Player 2", token: p2Token.trim() || "O", color: p2Color },
      ],
      victoryTemplate,
      themePreset: THEME_PRESETS[Number(selectedPreset)]?.name ?? "Custom",
    };
    onSaveHudConfig(config);
    onClose();
  };

  return (
    <Modal
      closeButtonProps={{"aria-label":"Close designer"}}
      opened={opened}
      onClose={onClose}
      title={
        <Group gap="xs">
          <RiPaletteLine size={20} color="var(--accent)" />
          <Text fw={700} style={{ fontFamily: "Outfit, sans-serif" }}>
            Player HUD, Token & Scoreboard Configurator
          </Text>
        </Group>
      }
      size="lg"
      styles={{
        content: {
          background: "var(--paper-2)",
          border: "1px solid var(--border)",
        },
      }}
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          Customize player tokens, vibrant chromatic themes, active turn expressions, and victory banner templates.
        </Text>

        {/* Turn State Binding */}
        <Paper p="sm" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
          <Text size="xs" fw={700} c="dimmed" mb="xs">
            SIMULATION STATE BINDING
          </Text>
          <Select
            label="Active Turn Register"
            description="The integer scalar state that determines which player's turn is active (1 vs 2)."
            size="xs"
            value={turnVariable}
            onChange={(v) => v && setTurnVariable(v)}
            data={
              stateVariables.length > 0
                ? stateVariables.map((s) => ({ value: s, label: s }))
                : [{ value: "tttActive", label: "tttActive (default)" }]
            }
          />
        </Paper>

        {/* Quick Theme Presets */}
        <Paper p="sm" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
          <Text size="xs" fw={700} c="dimmed" mb="xs">
            CHROMATIC PALETTE PRESETS
          </Text>
          <SegmentedControl
            size="xs"
            fullWidth
            value={selectedPreset}
            onChange={handleApplyThemePreset}
            data={THEME_PRESETS.map((p, idx) => ({
              label: p.name.split(" ")[0],
              value: String(idx),
            }))}
          />
          <Group mt="xs" gap="xs">
            <Select
              label="Token Glyphs Preset"
              size="xs"
              placeholder="Select preset"
              data={TOKEN_PRESETS.map((t) => ({ value: t.label, label: t.label }))}
              onChange={(v) => v && handleApplyTokenPreset(v)}
              style={{ flex: 1 }}
            />
          </Group>
        </Paper>

        {/* Player 1 & Player 2 Customization */}
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          {/* Player 1 Card */}
          <Paper p="sm" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
            <Group justify="space-between" mb="xs">
              <Group gap="xs">
                <ThemeIcon color="pink" variant="light" size="sm">
                  <RiUser3Line size={14} />
                </ThemeIcon>
                <Text size="sm" fw={700} c={p1Color}>
                  PLAYER 1 (Default: X)
                </Text>
              </Group>
              <Badge size="sm" color="pink" variant="filled">
                Token: {p1Token}
              </Badge>
            </Group>

            <Stack gap="xs">
              <TextInput
                label="Player Name / Label"
                size="xs"
                value={p1Label}
                onChange={(e) => setP1Label(e.currentTarget.value)}
              />
              <TextInput
                label="Token Glyph"
                description="Single symbol or character"
                size="xs"
                value={p1Token}
                onChange={(e) => setP1Token(e.currentTarget.value.slice(0, 2))}
              />
              <ColorInput
                label="Aesthetic Accent Color"
                size="xs"
                value={p1Color}
                onChange={setP1Color}
              />
            </Stack>
          </Paper>

          {/* Player 2 Card */}
          <Paper p="sm" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
            <Group justify="space-between" mb="xs">
              <Group gap="xs">
                <ThemeIcon color="teal" variant="light" size="sm">
                  <RiUser3Line size={14} />
                </ThemeIcon>
                <Text size="sm" fw={700} c={p2Color}>
                  PLAYER 2 (Default: O)
                </Text>
              </Group>
              <Badge size="sm" color="teal" variant="filled">
                Token: {p2Token}
              </Badge>
            </Group>

            <Stack gap="xs">
              <TextInput
                label="Player Name / Label"
                size="xs"
                value={p2Label}
                onChange={(e) => setP2Label(e.currentTarget.value)}
              />
              <TextInput
                label="Token Glyph"
                description="Single symbol or character"
                size="xs"
                value={p2Token}
                onChange={(e) => setP2Token(e.currentTarget.value.slice(0, 2))}
              />
              <ColorInput
                label="Aesthetic Accent Color"
                size="xs"
                value={p2Color}
                onChange={setP2Color}
              />
            </Stack>
          </Paper>
        </SimpleGrid>

        {/* Victory Banner Template */}
        <Paper p="sm" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
          <Group gap="xs" mb="xs">
            <RiTrophyLine size={16} color="var(--accent)" />
            <Text size="xs" fw={700} c="dimmed">
              VICTORY & BANNER TEMPLATE
            </Text>
          </Group>
          <TextInput
            label="Victory Announcement String"
            description="Use {winner} for player number/name"
            size="xs"
            value={victoryTemplate}
            onChange={(e) => setVictoryTemplate(e.currentTarget.value)}
          />
        </Paper>

        {/* Live Preview HUD */}
        <Paper p="sm" withBorder style={{ background: "var(--code-bg)", borderColor: "var(--border)" }}>
          <Text size="11px" fw={600} c="dimmed" mb="xs">
            LIVE HUD PREVIEW:
          </Text>
          <Group justify="space-between" align="center">
            <Group gap="xs">
              <Badge
                size="md"
                variant="light"
                style={{
                  color: p1Color,
                  borderColor: p1Color,
                  background: `${p1Color}15`,
                }}
              >
                Turn: {p1Label} ({p1Token})
              </Badge>
              <Badge
                size="md"
                variant="light"
                style={{
                  color: p2Color,
                  borderColor: p2Color,
                  background: `${p2Color}15`,
                }}
              >
                Waiting: {p2Label} ({p2Token})
              </Badge>
            </Group>
            <Group gap="xs">
              <RiSparklingLine size={16} color={p1Color} />
              <Text size="xs" fw={600} c="dimmed">
                {victoryTemplate.replace("{winner}", p1Label)}
              </Text>
            </Group>
          </Group>
        </Paper>

        <Divider my="xs" />

        <Group justify="flex-end">
          <Button variant="subtle" color="gray" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="filled"
            color="pink"
            leftSection={<RiCheckLine size={16} />}
            onClick={handleSave}
          >
            Apply HUD Configuration
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
};

export default WorldHudDesignerModal;
