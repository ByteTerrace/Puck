import React, { Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Badge, Button, Checkbox, Group, NumberInput, SegmentedControl, Select, Slider, Stack, Text, TextInput } from "@mantine/core";
import { SimulationContext } from "../../../context/SimulationContext";
import { authoredCells, selectionForTopology, parseCellAddresses, type CellReference } from "../../../authoring/documentTools";
import { projectRelationships, projectTopology } from "../../../authoring/sceneProjection";
import { appearanceFor, readPresentation } from "../../../authoring/presentation";
import UniversalTopologyView from "../UniversalTopologyView";
import type { ViewportMetrics } from "../SpatialTopology3D";
const SpatialTopology3D = lazy(() => import("../SpatialTopology3D"));
const emptyValues: Record<number, number> = {};

class ViewportBoundary extends React.Component<{ children: React.ReactNode; onFallback: () => void }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() { return this.state.failed ? <div className="studio-viewport-fallback"><Text>3D is unavailable on this device.</Text><Button onClick={this.props.onFallback}>Use accessible 2D view</Button></div> : this.props.children; }
}

export interface JsonReference { path: (string | number)[]; request: number }
export function AuthoringWorkspace({ draftDirty, onRevealJson }: { draftDirty: boolean; onRevealJson: (ref: JsonReference) => void }) {
  const actor = SimulationContext.useActorRef();
  const mask = SimulationContext.useSelector(s => s.context.hoveredMask);
  const ray = SimulationContext.useSelector(s => s.context.probedRayBeam);
  const world = SimulationContext.useSelector(s => s.context.parsedWorld);
  const topologies = SimulationContext.useSelector(s => s.context.topologies);
  const topologyName = SimulationContext.useSelector(s => s.context.selectedTopologyName);
  const allSelection = SimulationContext.useSelector(s => s.context.selection);
  const rows = SimulationContext.useSelector(s => s.context.stateDefinitions);
  const preview = SimulationContext.useSelector(s => s.context.history[s.context.historyIndex]);
  const previewIssues = SimulationContext.useSelector(s => s.context.previewIssues);
  const topology = topologies.find(t => t.name === topologyName);
  const [rowName, setRowName] = useState("");
  const domainRows = useMemo(() => rows.filter(r => r.domain?.topology === topologyName), [rows, topologyName]);
  const row = domainRows.find(r => r.name === rowName) ?? domainRows[0];
  const [view, setView] = useState<"3d" | "2d">("3d");
  const [source, setSource] = useState("author");
  const [layer, setLayer] = useState("all");
  const [separation, setSeparation] = useState(1.6);
  const [orthographic, setOrthographic] = useState(false);
  const [showHidden, setShowHidden] = useState(true);
  const [showRelationships, setShowRelationships] = useState(false);
  const [cameraRequest, setCameraRequest] = useState<{ id: number; axis: "orbit" | "top" | "front" | "side"; selection: boolean }>({ id: 0, axis: "orbit", selection: false });
  const projection = useMemo(() => {
    try { return { scene: topology ? projectTopology(topology, separation) : null, error: null }; }
    catch (e) { return { scene: null, error: (e as Error).message }; }
  }, [topology, separation]);
  const scene = projection.scene;
  useEffect(() => {
    setLayer("all"); setSource("author");
    if (topology) { try { setView(projectTopology(topology).layers.length > 1 ? "3d" : "2d"); } catch { setView("2d"); } }
  }, [topologyName]);
  const selection = useMemo(() => selectionForTopology(allSelection, topology), [allSelection, topology]);
  const highlights = useMemo(() => new Set(scene?.cells.filter(c => ray?.cells.includes(c.ref.index) || (mask != null && c.ref.index < 64 && (mask & (1n << BigInt(c.ref.index))) !== 0n)).map(c => c.ref.index) ?? []), [scene, mask, ray]);
  const authored = useMemo(() => authoredCells(row), [row]);
  const values = source === "author" ? authored : preview?.boardCells[row?.name ?? ""] ?? emptyValues;
  const empty = row?.domain?.empty ?? 0;
  const bindings = useMemo(() => readPresentation(world)[row?.name ?? ""], [world, row?.name]);
  const requestedLayer = layer === "all" || scene?.layers.includes(Number(layer)) ? layer : "all";
  const actualLayer = view === "2d" && requestedLayer === "all" && (scene?.layers.length ?? 0) > 1 ? String(scene?.cells.find(c => selection.has(c.ref.index))?.coordinate.z ?? scene!.layers[0]) : requestedLayer;
  const visible = useMemo(() => scene?.cells.filter(cell => (actualLayer === "all" || cell.coordinate.z === Number(actualLayer)) &&
    (showHidden || !appearanceFor(values[cell.ref.index] ?? empty, bindings).hidden)) ?? [], [scene, actualLayer, showHidden, values, empty, bindings]);
  const relationships = useMemo(() => {
    if (!topology || !scene || !showRelationships) return undefined;
    return projectRelationships(topology, scene);
  }, [topology, scene, showRelationships]);
  const onSelect = useCallback((ref: CellReference, additive: boolean) => {
    const previous = actor.getSnapshot().context.selection;
    const selected = previous.some(c => c.topology === ref.topology && c.index === ref.index);
    actor.send({ type: "SELECT_CELLS", selection: additive ? selected ? previous.filter(c => c.topology !== ref.topology || c.index !== ref.index) : [...previous.filter(c => c.topology === ref.topology), ref] : [ref] });
  }, [actor]);
  const frame = (axis = cameraRequest.axis, selected = false) => setCameraRequest(c => ({ id: c.id + 1, axis, selection: selected }));
  const [paintValue, setPaintValue] = useState<string | number>(1);
  const [address, setAddress] = useState("0");
  const [localError, setLocalError] = useState<string | null>(null);
  const [label, setLabel] = useState("");
  const [color, setColor] = useState("#74c9ba");
  const [shape, setShape] = useState<"cube" | "sphere" | "diamond">("cube");
  const [hidden, setHidden] = useState(false);
  const binding = appearanceFor(Number(paintValue), bindings);
  useEffect(() => { setLabel(binding.label); setColor(binding.color); setHidden(binding.hidden ?? false); setShape(binding.shape ?? "cube"); }, [binding.label, binding.color, binding.hidden, binding.shape]);
  const primary = scene?.cells.find(c => selection.has(c.ref.index));
  const selectedValues = new Set([...selection].map(index => values[index] ?? empty));
  const visibleSelected = visible.filter(cell => selection.has(cell.ref.index)).length;
  const readMetrics = useRef<(() => ViewportMetrics) | null>(null);
  const onMetricsReady = useCallback((read: (() => ViewportMetrics) | null) => { readMetrics.current = read; }, []);
  const [metrics, setMetrics] = useState("");
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  const measure = () => {
    const read = readMetrics.current;
    const start = read?.();
    if (!start) return;
    setMetrics("Measuring for one second; leave the viewport idle…");
    clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      if (readMetrics.current !== read) { setMetrics("The viewport changed. Measure again when ready."); return; }
      const end = read?.();
      if (end) setMetrics((end.frames - start.frames) + " frames in 1 s · " + end.calls + " draw calls · " + end.triangles.toLocaleString() + " triangles · " + end.geometries + " geometries · " + end.textures + " textures");
    }, 1000);
  };
  const switchView = (value: string) => {
    setView(value as "2d" | "3d");
    if (value === "2d" && actualLayer === "all" && (scene?.layers.length ?? 0) > 1) setLayer(String(primary?.coordinate.z ?? scene!.layers[0]));
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
      </Stack>
      <Text size="xs" c="dimmed" mt="lg" mb="xs">CELL STATES</Text>
      <Stack gap={5}>{domainRows.map(r => <button className="studio-explorer-item" key={r.name} aria-pressed={r.name === row?.name}
        onClick={() => setRowName(r.name)}><span>{r.name}</span><small>{r.kind}</small></button>)}</Stack>
      {!domainRows.length && <Text size="sm" c="dimmed">No cell state. Add a domain in Design → State Schema & Domains.</Text>}
      <Text size="xs" c="dimmed" mt="lg">{rows.filter(r => !r.domain).length} registers · {world.rules?.length ?? 0} rules</Text>
      <Text size="xs" c="dimmed" mt="md">Selection uses native cell addresses. View controls do not change the document.</Text>
    </aside>
    <div className="studio-viewport-column">
      <div className="studio-viewport-toolbar">
        <Group justify="space-between" gap="sm">
          <div><Text component="h2" m={0} fw={600} size="lg">{topology?.name ?? "No topology"}</Text><Text size="xs" c="dimmed">{scene?.cells.length ?? 0} cells · {row?.name ?? "Geometry"}</Text></div>
          <SegmentedControl aria-label="Viewport dimension" value={view} onChange={switchView} data={[{ value: "3d", label: "3D" }, { value: "2d", label: "2D" }]} />
        </Group>
        <Group gap="xs" mt="sm">
          <SegmentedControl aria-label="Displayed state source" size="xs" value={source} onChange={setSource} data={[{ value: "author", label: "Author" }, { value: "preview", label: "Preview" }]} />
          <Badge color={source === "author" ? "teal" : "yellow"} variant="light">{source === "author" ? "Authored values" : "Temporary preview"}</Badge>
          <Select aria-label="Visible layer" size="xs" w={140} allowDeselect={false} value={actualLayer}
            data={[...(view === "3d" || (scene?.layers.length ?? 0) <= 1 ? [{ value: "all", label: "All layers" }] : []), ...(scene?.layers ?? []).map(z => ({ value: String(z), label: "Layer z = " + z }))]}
            onChange={value => setLayer(value ?? "all")} />
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
      {scene?.cells.length ? view === "3d" ? <ViewportBoundary key={topologyName} onFallback={() => switchView("2d")}>
        <Suspense fallback={<div className="studio-viewport-fallback">Loading spatial tools…</div>}>
          <SpatialTopology3D scene={scene} visible={visible} values={values} empty={empty} bindings={bindings} selection={selection} highlights={highlights} onSelect={onSelect}
            cameraRequest={cameraRequest} orthographic={orthographic} relationships={actualLayer === "all" && showHidden ? relationships : undefined} onMetricsReady={onMetricsReady} />
        </Suspense>
      </ViewportBoundary> : <UniversalTopologyView key={topologyName + actualLayer} cells={visible} values={values} empty={empty} selection={selection} highlights={highlights} bindings={bindings} hex={topology?.$type === "hex"} onSelect={onSelect} />
        : <div className="studio-viewport-fallback"><Text>{projection.error ?? "This topology has no offline geometry. Author its definition in JSON."}</Text></div>}
      <div className="studio-viewport-footer">
        <Group justify="space-between"><Text size="xs" c="dimmed">{view === "3d" ? "Drag to orbit · Right-drag to pan · Scroll to zoom" : "Native cell order · Keyboard selection"}</Text>
          <Text size="xs">{selection.size} selected{selection.size !== visibleSelected ? " · " + (selection.size - visibleSelected) + " outside view" : ""}</Text></Group>
        <Group gap="xs" mt="xs">
          <Button size="compact-xs" variant="default" disabled={!visible.length} onClick={() => actor.send({ type: "SELECT_CELLS", selection: visible.map(c => c.ref) })}>Select visible</Button>
          <Button size="compact-xs" variant="subtle" disabled={!allSelection.length} onClick={() => actor.send({ type: "SELECT_CELLS", selection: [] })}>Clear selection</Button>
          <Checkbox size="xs" label="Show hidden values" checked={showHidden} onChange={e => setShowHidden(e.currentTarget.checked)} />
        </Group>
        {view === "3d" && <details className="studio-view-options"><summary>View options & performance</summary>
          <Text size="xs" mt="xs">Layer separation · {separation.toFixed(1)}×</Text>
          <Slider aria-label="Layer separation" min={1} max={4} step={0.1} value={separation} onChange={setSeparation} mb="md" />
          <Checkbox size="xs" label="Authored directions (all layers)" checked={showRelationships} onChange={e => setShowRelationships(e.currentTarget.checked)} />
          <Button size="compact-xs" variant="default" mt="sm" onClick={measure}>Measure viewport</Button>
          <Text size="xs" role="status" mt="xs">{metrics || "Demand rendering · pixel ratio capped at 1.5 · no shadows"}</Text>
        </details>}
      </div>
    </div>
    <aside className="studio-inspector" aria-label="Selection inspector">
      <Text className="kicker" mb="md">Inspector</Text>
      <Text fw={600} size="sm" role="status">{selection.size ? selection.size + (selection.size === 1 ? " cell selected" : " cells selected") : "Select a cell"}</Text>
      {primary && <Text size="xs" c="dimmed" mt={4}>#{primary.ref.index} · ({primary.coordinate.x}, {primary.coordinate.y}, {primary.coordinate.z}){selection.size > 1 ? " and " + (selection.size - 1) + " more" : ""}</Text>}
      <Text size="sm" mt="xs">{selection.size ? selectedValues.size === 1 ? "Value: " + [...selectedValues][0] : "Mixed values (" + selectedValues.size + ")" : "Click to select. Shift-click adds or removes."}</Text>
      <Group align="end" gap={6} mt="md">
        <TextInput label="Cell address" description="Ranges: 0-15, 32, 63" value={address} onChange={e => setAddress(e.currentTarget.value)} style={{ flex: 1 }} size="xs" />
        <Button size="xs" variant="default" disabled={!scene?.cells.length} onClick={() => {
          try {
            const refs = parseCellAddresses(address, topology!);
            actor.send({ type: "SELECT_CELLS", selection: refs });
            setLayer(view === "3d" ? "all" : String(scene!.cells[refs[0].index].coordinate.z)); setShowHidden(true); setLocalError(null);
          } catch (error) { setLocalError((error as Error).message); }
        }}>Select address</Button>
      </Group>
      <Button fullWidth size="xs" variant="subtle" mt="xs" disabled={!row} onClick={() => {
        const rowIndex = rows.findIndex(r => r.name === row?.name), cellIndex = row?.cells?.findIndex(c => Number(c.key) === primary?.ref.index) ?? -1;
        onRevealJson({ path: ["state", "world", rowIndex, ...(cellIndex >= 0 ? ["cells", cellIndex, "value"] : [])], request: Date.now() });
      }}>Reveal in JSON</Button>
      <div className="studio-inspector-section">
        <Text fw={600} size="sm">Paint authored cells</Text>
        <Text size="xs" c="dimmed" mb="sm">One apply is one undo step. Preview restarts after a document edit.</Text>
        <NumberInput hideControls label="Paint value" value={paintValue} onChange={setPaintValue} allowDecimal={false} size="sm" />
        <Button fullWidth mt="sm" disabled={draftDirty || source !== "author" || !row || !selection.size} onClick={() => {
          if (paintValue === "") { setLocalError("Enter a paint value."); return; }
          actor.send({ type: "PAINT_CELLS", stateName: row!.name, value: Number(paintValue) }); setLocalError(null);
        }}>Apply to {selection.size} {selection.size === 1 ? "cell" : "cells"}</Button>
        {draftDirty && <Text size="xs" mt="xs">Apply or export the JSON draft before painting.</Text>}
        {source === "preview" && <Text size="xs" mt="xs">Switch to Author to edit the document.</Text>}
      </div>
      <details className="studio-inspector-section"><summary>Value appearance</summary>
        <Text size="xs" c="dimmed" mt="xs">Maps paint value {paintValue} in {row?.name ?? "a cell state"} to a label and color in both views.</Text>
        <TextInput label="Value label" value={label} maxLength={80} onChange={e => setLabel(e.currentTarget.value)} size="xs" mt="xs" />
        <TextInput label="Value color" value={color} onChange={e => setColor(e.currentTarget.value)} size="xs" mt="xs" />
        <Select label="Value shape" data={["cube", "sphere", "diamond"]} value={shape} onChange={value => setShape((value ?? "cube") as typeof shape)} allowDeselect={false} size="xs" mt="xs" />
        <Checkbox label="Hide this value" size="xs" checked={hidden} onChange={e => setHidden(e.currentTarget.checked)} mt="sm" />
        <Button size="xs" fullWidth variant="default" mt="sm" disabled={!row || draftDirty || paintValue === ""} onClick={() => actor.send({ type: "BIND_APPEARANCE", stateName: row!.name, value: Number(paintValue), appearance: { label, color, hidden, shape } })}>Save value appearance</Button>
      </details>
      {source === "preview" && <div className="studio-inspector-section">
        <Text fw={600} size="sm">Preview action</Text>
        <Text size="xs" c="dimmed" mb="sm">Explicit input through the document's supported demo adapter.</Text>
        <Button variant="default" size="xs" fullWidth disabled={selection.size !== 1 || previewIssues.length > 0} onClick={() => actor.send({ type: "PREVIEW_CELL", cellIdx: primary!.ref.index })}>Submit selected cell</Button>
      </div>}
      {localError && <Text role="alert" size="sm" c="red" mt="sm">{localError}</Text>}
    </aside>
  </section>;
}
