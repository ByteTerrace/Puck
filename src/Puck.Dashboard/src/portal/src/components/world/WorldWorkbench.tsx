import React, { useState } from "react";
import {
  Card,
  Group,
  Stack,
  Text,
  Button,
  Textarea,
  Alert,
  List,
  FileButton,
} from "@mantine/core";
import {
  RiCodeSSlashLine,
  RiCheckFill,
  RiAlertFill,
  RiUpload2Line,
  RiDownload2Line,
} from "@remixicon/react";

export interface LintIssue {
  severity: "error" | "warning";
  message: string;
  context?: string;
}

export interface WorldWorkbenchProps {
  worldJson: string;
  onWorldJsonChange: (newJson: string) => void;
}

export const WorldWorkbench: React.FC<WorldWorkbenchProps> = ({
  worldJson,
  onWorldJsonChange,
}) => {
  const [lintIssues, setLintIssues] = useState<LintIssue[]>([]);
  const [parseError, setParseError] = useState<string | null>(null);

  // Lint the world JSON against Puck invariants
  const runLinter = (jsonString: string) => {
    try {
      const parsed = JSON.parse(jsonString);
      setParseError(null);
      const issues: LintIssue[] = [];

      // 1. Check Lattices for opposing direction balance
      const lattices = parsed.state?.lattices ?? [];
      lattices.forEach((lat: any) => {
        if (lat.$type === "lattice" && Array.isArray(lat.directions)) {
          const dirs: Array<{ x: number; y: number; z: number; name: string }> = lat.directions;
          dirs.forEach((d) => {
            const hasOpposite = dirs.some(
              (other) => other.x === -d.x && other.y === -d.y && other.z === -d.z
            );
            if (!hasOpposite) {
              issues.push({
                severity: "error",
                message: `Lattice '${lat.name}' direction '${d.name}' (${d.x}, ${d.y}, ${d.z}) lacks an exact opposite (-x, -y, -z) in directions.`,
                context: "TopologyCompilation.cs invariant violation",
              });
            }
          });
        }
      });

      // 2. Check cellsOf domain references
      const stateRows = parsed.state?.world ?? [];
      const topologyNames = new Set(lattices.map((l: any) => l.name));
      stateRows.forEach((row: any) => {
        if (row.domain?.topology && !topologyNames.has(row.domain.topology)) {
          issues.push({
            severity: "error",
            message: `State row '${row.name}' references topology '${row.domain.topology}' which is not declared in state.lattices.`,
          });
        }
      });

      // 3. Check rule write targets
      const stateRowNames = new Set(stateRows.map((r: any) => r.name));
      const rules = parsed.rules ?? [];
      rules.forEach((rule: any) => {
        (rule.effects ?? []).forEach((eff: any) => {
          if (eff.state && !stateRowNames.has(eff.state)) {
            issues.push({
              severity: "warning",
              message: `Rule '${rule.name}' effect writes to state '${eff.state}' which is not declared in state.world.`,
            });
          }
        });
      });

      setLintIssues(issues);
    } catch (err: any) {
      setParseError(`JSON Syntax Error: ${err.message}`);
      setLintIssues([]);
    }
  };

  const handleTextChange = (value: string) => {
    onWorldJsonChange(value);
    runLinter(value);
  };

  const handleFileUpload = (file: File | null) => {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = (e) => {
      const content = e.target?.result as string;
      if (content) {
        handleTextChange(content);
      }
    };
    reader.readAsText(file);
  };

  const handleDownload = () => {
    const blob = new Blob([worldJson], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "world.json";
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--mantine-color-dark-8)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiCodeSSlashLine size={18} color="#3b82f6" />
          <Text fw={700} size="sm">
            World Definition Workbench & Linter
          </Text>
        </Group>

        <Group gap="xs">
          <FileButton onChange={handleFileUpload} accept=".json">
            {(props) => (
              <Button {...props} size="xs" variant="light" leftSection={<RiUpload2Line size={14} />}>
                Load JSON
              </Button>
            )}
          </FileButton>

          <Button size="xs" variant="default" leftSection={<RiDownload2Line size={14} />} onClick={handleDownload}>
            Export
          </Button>
        </Group>
      </Group>

      <Stack gap="sm">
        {parseError && (
          <Alert color="red" title="Parsing Refusal" icon={<RiAlertFill />}>
            {parseError}
          </Alert>
        )}

        {lintIssues.length > 0 && (
          <Alert
            color={lintIssues.some((i) => i.severity === "error") ? "red" : "yellow"}
            title={`Linter Diagnosed ${lintIssues.length} Issue(s)`}
            icon={<RiAlertFill />}
          >
            <List size="xs" spacing={4}>
              {lintIssues.map((issue, idx) => (
                <List.Item key={idx}>
                  <Text span fw={issue.severity === "error" ? 700 : 500}>
                    [{issue.severity.toUpperCase()}] {issue.message}
                  </Text>
                  {issue.context && (
                    <Text span size="xs" c="dimmed" ml={6}>
                      ({issue.context})
                    </Text>
                  )}
                </List.Item>
              ))}
            </List>
          </Alert>
        )}

        {!parseError && lintIssues.length === 0 && (
          <Alert color="teal" icon={<RiCheckFill />} title="Puck Invariants Verified">
            <Text size="xs">
              Lattice opposite directions, domain references, and rule targets satisfy all engine compilation laws.
            </Text>
          </Alert>
        )}

        <Textarea
          value={worldJson}
          onChange={(e) => handleTextChange(e.currentTarget.value)}
          minRows={14}
          maxRows={24}
          autosize
          styles={{
            input: {
              fontFamily: "monospace",
              fontSize: 12,
              background: "var(--mantine-color-dark-9)",
              color: "var(--mantine-color-gray-3)",
            },
          }}
        />
      </Stack>
    </Card>
  );
};

export default WorldWorkbench;
