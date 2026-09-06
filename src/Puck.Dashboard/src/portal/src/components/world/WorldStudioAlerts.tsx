import React from "react";
import { Text } from "@mantine/core";
import { SimulationContext } from "../../context/SimulationContext";
export const WorldStudioAlerts: React.FC = () => {
  const error = SimulationContext.useSelector(s => s.context.error);
  const issues = SimulationContext.useSelector(s => s.context.previewIssues);
  return <div
    className="studio-status"
    role="status"
    aria-live="polite">
    <Text
      size="sm"
      c={error ? "var(--accent)" : "var(--ink-soft)"}>
      {error || (issues.length ? "Document loaded · Offline preview unavailable: " + issues[0] : "Offline preview · Supported integer rules only. Export for native engine validation.")}
    </Text>
  </div>;
};
export default WorldStudioAlerts;
