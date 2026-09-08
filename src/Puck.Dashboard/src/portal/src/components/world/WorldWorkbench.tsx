import React, { Suspense, lazy, useCallback, useEffect, useMemo, useState } from "react";
import { Badge, Button, Checkbox, Group, Select, SegmentedControl, Slider, Stack, Text, TextInput } from "@mantine/core";
import { StudioContext, useStudioDocument, useStudioGeometry, useStudioPreview, useStudioSelection } from "../../context/StudioContext";
import {
  authoredCellValues,
  canPaintRow,
  emptyValueFor,
  listTopologies,
  parseCellAddresses,
  parsePaintValue,
  rowsBoundToTopology,
} from "../../authoring/documentTools";
import { isVolumetric, projectDirections, projectScene } from "../../authoring/sceneProjection";
import { appearanceFor, presentationEdit, readPresentation, type ValueAppearance } from "../../authoring/presentation";
import { cellJsonPath } from "../../authoring/jsonReference";
import { pathToPointer } from "../../document/jsonPath";
import UniversalTopologyView from "./UniversalTopologyView";

const SpatialTopology3D = lazy(() => import("./SpatialTopology3D"));
const EMPTY_GEOMETRY: readonly import("../../native/engineTypes").EngineCell[] = [];
const ignoreMetrics = () => {};

class ViewportBoundary extends React.Component<{ children: React.ReactNode; onFallback: () => void }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    return this.state.failed
      ? <div className="studio-viewport-fallback"><Text>3D is unavailable on this device.</Text><Button onClick={this.props.onFallback}>Use accessible 2D view</Button></div>
      : this.props.children;
  }
}

/**
 * The studio's spatial surface, bound entirely to `StudioContext` — no props: a topology explorer,
 * the matching viewport (`UniversalTopologyView` for a single-layer topology, `SpatialTopology3D`
 * for a volumetric one — decided by the engine's own geometry, never a topology `$type` special
 * case), and an inspector for selection, painting authored cells, and (in Preview mode) writing a
 * live cell. Geometry comes only from `useStudioGeometry()` — the engine's own `cells()` answer —
 * never a TypeScript ordinal formula.
 */
export const WorldWorkbench: React.FC = () => {
  const actor = StudioContext.useActorRef();
  const document = useStudioDocument();
  const geometryMap = useStudioGeometry();
  const selection = useStudioSelection();
  const preview = useStudioPreview();

  const topologies = useMemo(() => listTopologies(document.value), [document.value]);
  const topologyName = selection.topology;
  useEffect(() => {
    if (!topologyName && topologies.length > 0) {
      actor.send({ type: "SELECT_TOPOLOGY", name: topologies[0].name });
    }
  }, [topologyName, topologies, actor]);
  const topology = topologies.find(t => t.name === topologyName);
  const geometry = topologyName ? (geometryMap[topologyName] ?? EMPTY_GEOMETRY) : EMPTY_GEOMETRY;

  const domainRows = useMemo(() => (topologyName ? rowsBoundToTopology(document.value, topologyName) : []), [document.value, topologyName]);
  const [rowName, setRowName] = useState<string | null>(null);
  useEffect(() => { setRowName(null); }, [topologyName]);
  const row = domainRows.find(r => r.name === rowName) ?? domainRows[0];

  const [view, setView] = useState<"3d" | "2d">("2d");
  const [layerIndex, setLayerIndex] = useState<number | "all">("all");
  const [source, setSource] = useState<"author" | "preview">("author");
  useEffect(() => {
    setView(isVolumetric(geometry) ? "3d" : "2d");
    setLayerIndex("all");
    setSource("author");
  }, [topologyName, geometry]);

  const [separation, setSeparation] = useState(1.6);
  const [orthographic, setOrthographic] = useState(false);
  const [showHidden, setShowHidden] = useState(true);
  const [showRelationships, setShowRelationships] = useState(false);
  const [cameraRequest, setCameraRequest] = useState<{ id: number; axis: "orbit" | "top" | "front" | "side"; selection: boolean }>({ id: 0, axis: "orbit", selection: false });

  const scene = useMemo(() => projectScene(geometry, topology?.cellSize ?? 1, separation), [geometry, topology?.cellSize, separation]);

  const authoredValues = useMemo(() => authoredCellValues(row), [row]);
  const livePreviewRow = preview.rows.find(r => r.name === row?.name);
  const previewValues = useMemo(
    () => new Map((livePreviewRow?.cells ?? []).map(cell => [Number(cell.key), cell.value] as const)),
    [livePreviewRow],
  );
  const values = source === "author" ? authoredValues : previewValues;
  const empty = emptyValueFor(row);
  const bindings = useMemo(() => readPresentation(document.value)[row?.name ?? ""], [document.value, row?.name]);

  const selectedSet = useMemo(() => new Set(selection.ordinals), [selection.ordinals]);
  const effectiveLayerIndex = (view === "2d" && layerIndex === "all" && scene.layers.length > 1)
    ? (scene.cells.find(c => selectedSet.has(c.ordinal))?.layerIndex ?? 0)
    : layerIndex;
  const visible = useMemo(
    () => scene.cells.filter(cell =>
      (effectiveLayerIndex === "all" || cell.layerIndex === effectiveLayerIndex) &&
      (showHidden || !appearanceFor(values.get(cell.ordinal) ?? empty, bindings).hidden)),
    [scene, effectiveLayerIndex, showHidden, values, empty, bindings],
  );
  const relationships = useMemo(() => (topology && showRelationships ? projectDirections(topology, scene) : undefined), [topology, scene, showRelationships]);

  const onSelect = useCallback((ordinal: number, additive: boolean) => {
    actor.send({ type: "SELECT_CELLS", ordinals: [ordinal], mode: additive ? "toggle" : "replace" });
  }, [actor]);
  const frame = (axis = cameraRequest.axis, selectionOnly = false) => setCameraRequest(c => ({ id: c.id + 1, axis, selection: selectionOnly }));

  const [paintValueText, setPaintValueText] = useState("1");
  const [address, setAddress] = useState("0");
  const [localError, setLocalError] = useState<string | null>(null);
  const [label, setLabel] = useState("");
  const [color, setColor] = useState("#74c9ba");
  const [shape, setShape] = useState<"cube" | "sphere" | "diamond">("cube");
  const [hidden, setHidden] = useState(false);

  const paintValue = useMemo(() => {
    if (!row) return undefined;
    try { return parsePaintValue(row, paintValueText); } catch { return undefined; }
  }, [row, paintValueText]);
  const binding = paintValue !== undefined ? appearanceFor(paintValue, bindings) : undefined;
  useEffect(() => {
    if (binding) { setLabel(binding.label); setColor(binding.color); setHidden(binding.hidden ?? false); setShape(binding.shape ?? "cube"); }
  }, [binding?.label, binding?.color, binding?.hidden, binding?.shape]);

  const primary = scene.cells.find(c => selectedSet.has(c.ordinal));
  const selectedValues = new Set([...selectedSet].map(ordinal => (values.get(ordinal) ?? empty).toString()));
  const visibleSelected = visible.filter(cell => selectedSet.has(cell.ordinal)).length;
  const revealPath = (row && primary) ? cellJsonPath(document.value, row.name, primary.ordinal) : null;

  const switchView = (value: string) => {
    setView(value as "2d" | "3d");
    if (value === "2d" && effectiveLayerIndex === "all" && scene.layers.length > 1) {
      setLayerIndex(primary?.layerIndex ?? 0);
    }
  };

  return <section className="studio-authoring" aria-label="World authoring workspace">
    <aside className="studio-explorer" aria-label="Document explorer">
      <Text className="kicker" mb="md">Document explorer</Text>
      <Text size="xs" c="dimmed" mb="xs">TOPOLOGIES</Text>
      <Stack gap={5}>
        {topologies.map(t => <button className="studio-explorer-item" key={t.name} aria-pressed={t.name === topologyName}
          onClick={() => { actor.send({ type: "SELECT_TOPOLOGY", name: t.name }); setLocalError(null); }}>
          <span>{t.name}</span><small>{t.$type}</small>
        </button>)}
        {!topologies.length && <Text size="sm" c="dimmed">No topology declared. Author one under state.lattices.</Text>}
      </Stack>
      <Text size="xs" c="dimmed" mt="lg" mb="xs">CELL STATES</Text>
      <Stack gap={5}>{domainRows.map(r => <button className="studio-explorer-item" key={r.name} aria-pressed={r.name === row?.name}
        onClick={() => setRowName(r.name)}><span>{r.name}</span><small>{r.kind}</small></button>)}</Stack>
      {!domainRows.length && topologyName && <Text size="sm" c="dimmed">No cell state bound to this topology yet.</Text>}
      <Text size="xs" c="dimmed" mt="md">Selection uses native cell addresses. View controls do not change the document.</Text>
    </aside>
    <div className="studio-viewport-column">
      <div className="studio-viewport-toolbar">
        <Group justify="space-between" gap="sm">
          <div><Text component="h2" m={0} fw={600} size="lg">{topology?.name ?? "No topology"}</Text><Text size="xs" c="dimmed">{scene.cells.length} cells · {row?.name ?? "Geometry"}</Text></div>
          <SegmentedControl aria-label="Viewport dimension" value={view} onChange={switchView} data={[{ value: "3d", label: "3D" }, { value: "2d", label: "2D" }]} />
        </Group>
        <Group gap="xs" mt="sm">
          <SegmentedControl aria-label="Displayed state source" size="xs" value={source} onChange={value => setSource(value as "author" | "preview")}
            data={[{ value: "author", label: "Author" }, { value: "preview", label: "Preview" }]} />
          <Badge color={source === "author" ? "teal" : "yellow"} variant="light">{source === "author" ? "Authored values" : "Live preview"}</Badge>
          <Select aria-label="Visible layer" size="xs" w={140} allowDeselect={false} value={String(effectiveLayerIndex)}
            data={[...(view === "3d" || scene.layers.length <= 1 ? [{ value: "all", label: "All layers" }] : []),
              ...scene.layers.map((_, index) => ({ value: String(index), label: "Layer " + (index + 1) + " / " + scene.layers.length }))]}
            onChange={value => setLayerIndex(value === "all" || value === null ? "all" : Number(value))} />
        </Group>
        {view === "3d" && <Group gap={6} mt="sm">
          <Button size="compact-xs" variant="default" onClick={() => frame("orbit")}>Fit world</Button>
          <Button size="compact-xs" variant="default" disabled={!visibleSelected} onClick={() => frame(cameraRequest.axis, true)}>Fit selection</Button>
          <Button size="compact-xs" variant="subtle" onClick={() => frame("top")}>Top</Button>
          <Button size="compact-xs" variant="subtle" onClick={() => frame("front")}>Front</Button>
          <Button size="compact-xs" variant="subtle" onClick={() => frame("side")}>Side</Button>
          <Checkbox size="xs" label="Orthographic" checked={orthographic} onChange={e => setOrthographic(e.currentTarget.checked)} />
        </Group>}
      </div>
      {scene.cells.length
        ? view === "3d"
          ? <ViewportBoundary key={topologyName ?? ""} onFallback={() => switchView("2d")}>
            <Suspense fallback={<div className="studio-viewport-fallback">Loading spatial tools…</div>}>
              <SpatialTopology3D scene={scene} visible={visible} values={values} empty={empty} bindings={bindings} selection={selectedSet}
                onSelect={onSelect} cameraRequest={cameraRequest} orthographic={orthographic}
                relationships={effectiveLayerIndex === "all" && showHidden ? relationships : undefined} onMetricsReady={ignoreMetrics} />
            </Suspense>
          </ViewportBoundary>
          : <UniversalTopologyView key={(topologyName ?? "") + String(effectiveLayerIndex)} cells={visible} values={values} empty={empty}
            selection={selectedSet} bindings={bindings} onSelect={onSelect} />
        : <div className="studio-viewport-fallback"><Text>{topologyName ? "This topology has no computed geometry yet." : "Select a topology to view its cells."}</Text></div>}
      <div className="studio-viewport-footer">
        <Group justify="space-between"><Text size="xs" c="dimmed">{view === "3d" ? "Drag to orbit · Right-drag to pan · Scroll to zoom" : "Native cell order · Keyboard selection"}</Text>
          <Text size="xs">{selectedSet.size} selected{selectedSet.size !== visibleSelected ? " · " + (selectedSet.size - visibleSelected) + " outside view" : ""}</Text></Group>
        <Group gap="xs" mt="xs">
          <Button size="compact-xs" variant="default" disabled={!visible.length} onClick={() => actor.send({ type: "SELECT_CELLS", ordinals: visible.map(c => c.ordinal), mode: "replace" })}>Select visible</Button>
          <Button size="compact-xs" variant="subtle" disabled={!selection.ordinals.length} onClick={() => actor.send({ type: "SELECT_CELLS", ordinals: [], mode: "replace" })}>Clear selection</Button>
          <Checkbox size="xs" label="Show hidden values" checked={showHidden} onChange={e => setShowHidden(e.currentTarget.checked)} />
        </Group>
        {view === "3d" && <details className="studio-view-options"><summary>View options</summary>
          <Text size="xs" mt="xs">Layer separation · {separation.toFixed(1)}×</Text>
          <Slider aria-label="Layer separation" min={1} max={4} step={0.1} value={separation} onChange={setSeparation} mb="md" />
          <Checkbox size="xs" label="Authored directions (grid/box only)" checked={showRelationships} onChange={e => setShowRelationships(e.currentTarget.checked)} />
        </details>}
      </div>
    </div>
    <aside className="studio-inspector" aria-label="Selection inspector">
      <Text className="kicker" mb="md">Inspector</Text>
      <Text fw={600} size="sm" role="status">{selectedSet.size ? selectedSet.size + (selectedSet.size === 1 ? " cell selected" : " cells selected") : "Select a cell"}</Text>
      {primary && <Text size="xs" c="dimmed" mt={4}>#{primary.ordinal}{selectedSet.size > 1 ? " and " + (selectedSet.size - 1) + " more" : ""}</Text>}
      <Text size="sm" mt="xs">{selectedSet.size ? (selectedValues.size === 1 ? "Value: " + [...selectedValues][0] : "Mixed values (" + selectedValues.size + ")") : "Click to select. Shift-click adds or removes."}</Text>
      <Group align="end" gap={6} mt="md">
        <TextInput label="Cell address" description="Ranges: 0-15, 32, 63" value={address} onChange={e => setAddress(e.currentTarget.value)} style={{ flex: 1 }} size="xs" />
        <Button size="xs" variant="default" disabled={!geometry.length} onClick={() => {
          try {
            const ordinals = parseCellAddresses(address, geometry);
            actor.send({ type: "SELECT_CELLS", ordinals, mode: "replace" });
            setLocalError(null);
          } catch (error) { setLocalError((error as Error).message); }
        }}>Select address</Button>
      </Group>
      {revealPath && <Text size="xs" c="dimmed" mt="xs" ff="monospace">JSON: {pathToPointer(revealPath)}</Text>}
      <div className="studio-inspector-section">
        <Text fw={600} size="sm">Paint authored cells</Text>
        <Text size="xs" c="dimmed" mb="sm">One apply is one undo step.</Text>
        <TextInput label="Paint value" value={paintValueText} onChange={e => setPaintValueText(e.currentTarget.value)} size="sm" styles={{ input: { fontFamily: "ui-monospace,monospace" } }} />
        <Button fullWidth mt="sm" disabled={document.validation !== "clean" || source !== "author" || !row || !canPaintRow(row) || !selection.ordinals.length || paintValue === undefined} onClick={() => {
          if (!row || paintValue === undefined || !topologyName) return;
          actor.send({ type: "PAINT_CELLS", topology: topologyName, row: row.name, ordinals: [...selection.ordinals], value: paintValue });
          setLocalError(null);
        }}>Apply to {selectedSet.size} {selectedSet.size === 1 ? "cell" : "cells"}</Button>
        {row && !canPaintRow(row) && <Text size="xs" mt="xs">This state's kind cannot be painted here.</Text>}
        {source === "preview" && <Text size="xs" mt="xs">Switch to Author to edit the document.</Text>}
      </div>
      <details className="studio-inspector-section"><summary>Value appearance</summary>
        <Text size="xs" c="dimmed" mt="xs">Maps paint value {paintValueText} in {row?.name ?? "a cell state"} to a label and color in both views.</Text>
        <TextInput label="Value label" value={label} maxLength={80} onChange={e => setLabel(e.currentTarget.value)} size="xs" mt="xs" />
        <TextInput label="Value color" value={color} onChange={e => setColor(e.currentTarget.value)} size="xs" mt="xs" />
        <Select label="Value shape" data={["cube", "sphere", "diamond"]} value={shape} onChange={value => setShape((value ?? "cube") as typeof shape)} allowDeselect={false} size="xs" mt="xs" />
        <Checkbox label="Hide this value" size="xs" checked={hidden} onChange={e => setHidden(e.currentTarget.checked)} mt="sm" />
        <Button size="xs" fullWidth variant="default" mt="sm" disabled={!row || paintValue === undefined} onClick={() => {
          if (!row || paintValue === undefined) return;
          const edit = presentationEdit(document.value, row.name, paintValue, { label, color, hidden, shape } satisfies ValueAppearance);
          actor.send({ type: "EDIT_DOCUMENT", path: edit.path, value: edit.value, label: edit.label });
        }}>Save value appearance</Button>
      </details>
      {source === "preview" && <div className="studio-inspector-section">
        <Text fw={600} size="sm">Preview action</Text>
        <Text size="xs" c="dimmed" mb="sm">Writes the paint value into the selected cell of this preview session.</Text>
        <Button variant="default" size="xs" fullWidth disabled={preview.status !== "ready" || !row || paintValue === undefined || selectedSet.size !== 1} onClick={() => {
          if (!row || paintValue === undefined || !primary) return;
          actor.send({ type: "PREVIEW_WRITE", row: row.name, key: livePreviewRow?.keyed ? String(primary.ordinal) : undefined, value: paintValue, write: "set" });
        }}>Submit selected cell</Button>
      </div>}
      {localError && <Text role="alert" size="sm" c="red" mt="sm">{localError}</Text>}
    </aside>
  </section>;
};

export default WorldWorkbench;
