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
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiCodeSSlashLine size={18} color="var(--accent)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            World Definition Workbench & Linter
          </Text>
        </Group>

        <Group gap="xs">
          <FileButton onChange={handleFileUpload} accept=".json">
            {(props) => (
              <Button {...props} size="xs" variant="light" color="coral" leftSection={<RiUpload2Line size={14} />}>
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
          <Alert
            color="coral"
            title="Parsing Refusal"
            icon={<RiAlertFill />}
            style={{
              background: "var(--quote-bg)",
              border: "1px solid var(--rule)",
              borderLeft: "3px solid var(--accent)",
              color: "var(--ink)",
            }}
          >
            <Text size="xs" ff="monospace">
              {parseError}
            </Text>
          </Alert>
        )}

        {lintIssues.length > 0 && (
          <Alert
            color="coral"
            title={`Linter Diagnosed ${lintIssues.length} Issue(s)`}
            icon={<RiAlertFill />}
            style={{
              background: "var(--quote-bg)",
              border: "1px solid var(--rule)",
              borderLeft: "3px solid var(--accent)",
              color: "var(--ink)",
            }}
          >
            <List size="xs" spacing={4}>
              {lintIssues.map((issue, idx) => (
                <List.Item key={idx}>
                  <Text span fw={issue.severity === "error" ? 700 : 500} style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                    [{issue.severity.toUpperCase()}] {issue.message}
                  </Text>
                  {issue.context && (
                    <Text span size="xs" c="var(--ink-faint)" ml={6} style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                      ({issue.context})
                    </Text>
                  )}
                </List.Item>
              ))}
            </List>
          </Alert>
        )}

        {!parseError && lintIssues.length === 0 && (
          <Alert
            color="jade"
            icon={<RiCheckFill />}
            title="Puck Invariants Verified"
            style={{
              background: "var(--quote-bg)",
              border: "1px solid var(--rule)",
              borderLeft: "3px solid var(--accent-2)",
              color: "var(--ink)",
            }}
          >
            <Text size="xs" style={{ fontFamily: '"Lora", Georgia, serif' }}>
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
              fontFamily: '"JetBrains Mono", ui-monospace, monospace',
              fontSize: 12,
              background: "var(--code-bg)",
              color: "var(--code-fg)",
              border: "1px solid var(--rule)",
              borderRadius: 10,
              lineHeight: 1.55,
            },
          }}
        />
      </Stack>
    </Card>
  );
};

export default WorldWorkbench;
