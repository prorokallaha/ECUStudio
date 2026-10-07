"use client";
import { useEffect, useMemo } from "react";
import { Background, Controls, Handle, MarkerType, Position, ReactFlow, ReactFlowProvider, useReactFlow, useStore, type Edge, type Node, type NodeProps } from "@xyflow/react";
import type { DependencyGraph, DependencyNode } from "@/types/domain";
import { nodeStateTone, toneSoft, cssVar } from "@/lib/colors";
import { useHotkeys } from "@/hooks/use-hotkeys";
import { useUI } from "@/stores/ui";
import { cn } from "@/lib/cn";

type N = Node<{ n: DependencyNode; dim: boolean }>;

function MapNode({ data }: NodeProps<N>) {
  const n = data.n;
  const tone = nodeStateTone[n.state];
  return (
    <div className={cn("w-44 rounded-md border px-2.5 py-1.5 text-xs shadow-sm transition-opacity", toneSoft[tone], "bg-panel", data.dim && "opacity-25")}>
      <Handle type="target" position={Position.Left} className="!size-1.5 !border-0 !bg-border-strong" />
      <div className="truncate font-semibold text-fg">{n.label}</div>
      <div className="flex items-center justify-between text-[10px]"><span className="uppercase tracking-wide">{n.state === "NotApplicable" ? "N/A" : n.state}</span>{!n.mapId && n.state === "Unknown" && <span className="text-unknown">not identified</span>}</div>
      {n.detail && <div className="mt-0.5 truncate text-[10px] text-fg-muted" title={n.detail}>{n.detail}</div>}
      <Handle type="source" position={Position.Right} className="!size-1.5 !border-0 !bg-border-strong" />
    </div>
  );
}

function QuantityNode({ data }: NodeProps<N>) {
  return (
    <div className={cn("rounded-full border border-border-strong bg-bg px-2.5 py-0.5 text-[11px] text-fg-muted transition-opacity", data.dim && "opacity-25")}>
      <Handle type="target" position={Position.Left} className="!size-1 !border-0 !bg-border-strong" />
      {data.n.label}
      <Handle type="source" position={Position.Right} className="!size-1 !border-0 !bg-border-strong" />
    </div>
  );
}

const nodeTypes = { map: MapNode, quantity: QuantityNode };

/**
 * Layered layout (longest path from sources) with barycentre ordering — deterministic,
 * no layout library needed for ~30 nodes. Columns holding only quantity pills are narrower.
 */
function layout(g: DependencyGraph) {
  const inc = new Map<string, string[]>();
  g.nodes.forEach((n) => inc.set(n.id, []));
  g.edges.forEach((e) => inc.get(e.to)?.push(e.from));
  const depth = new Map<string, number>();
  const visit = (id: string, stack: Set<string>): number => {
    if (depth.has(id)) return depth.get(id)!;
    if (stack.has(id)) return 0;
    stack.add(id);
    const parents = (inc.get(id) ?? []).map((p) => visit(p, stack));
    const d = parents.length ? Math.max(...parents) + 1 : 0;
    stack.delete(id);
    depth.set(id, d);
    return d;
  };
  g.nodes.forEach((n) => visit(n.id, new Set()));
  const cols: DependencyNode[][] = [];
  g.nodes.forEach((n) => { const d = depth.get(n.id) ?? 0; (cols[d] ??= []).push(n); });

  const ROW = 64;
  const order = new Map<string, number>();
  const pos = new Map<string, { x: number; y: number }>();
  let x = 0;
  cols.forEach((ns = []) => {
    // Barycentre of already-placed parents reduces edge crossings between adjacent layers.
    const bary = (n: DependencyNode) => {
      const ps = (inc.get(n.id) ?? []).filter((p) => order.has(p)).map((p) => order.get(p)!);
      return ps.length ? ps.reduce((a, b) => a + b, 0) / ps.length : Number.MAX_SAFE_INTEGER;
    };
    const sorted = [...ns].sort((a, b) => bary(a) - bary(b));
    sorted.forEach((n, i) => {
      const y = (i - (sorted.length - 1) / 2) * ROW;
      order.set(n.id, y);
      pos.set(n.id, { x, y });
    });
    x += ns.some((n) => n.kind === "Map") ? 230 : 160;
  });
  return pos;
}

function Graph({ graph, focus, onOpenMap }: { graph: DependencyGraph; focus?: string | null; onOpenMap: (mapId: string) => void }) {
  const flow = useReactFlow();
  const theme = useUI((s) => s.theme);
  const neighbourhood = useMemo(() => {
    if (!focus) return null;
    const set = new Set([focus]);
    for (let hop = 0; hop < 2; hop++) graph.edges.forEach((e) => { if (set.has(e.from)) set.add(e.to); if (set.has(e.to)) set.add(e.from); });
    return set;
  }, [graph, focus]);
  const { nodes, edges } = useMemo(() => {
    const pos = layout(graph);
    const nodes: N[] = graph.nodes.map((n) => ({ id: n.id, type: n.kind === "Map" ? "map" : "quantity", position: pos.get(n.id)!, data: { n, dim: !!neighbourhood && !neighbourhood.has(n.id) } }));
    const stroke = cssVar("border-strong");
    const edges: Edge[] = graph.edges.map((e, i) => {
      const active = !neighbourhood || (neighbourhood.has(e.from) && neighbourhood.has(e.to));
      return { id: `e${i}`, source: e.from, target: e.to, label: e.relation, animated: active && !!neighbourhood, style: { stroke: active ? cssVar("calc", 0.8) : stroke, opacity: active ? 1 : 0.25 }, labelStyle: { fill: cssVar("fg-subtle"), fontSize: 9 }, labelBgStyle: { fill: cssVar("bg") }, markerEnd: { type: MarkerType.ArrowClosed, width: 14, height: 14, color: active ? cssVar("calc") : stroke } };
    });
    return { nodes, edges };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graph, neighbourhood, theme]);

  const height = useStore((s) => s.height);
  // Fit the whole chain when it is legible; otherwise open at a readable zoom anchored at the inputs (left).
  useEffect(() => {
    const t = setTimeout(async () => {
      await flow.fitView({ padding: 0.05, maxZoom: 1.1 });
      const MIN_READABLE = 0.8;
      if (flow.getZoom() < MIN_READABLE) flow.setViewport({ x: 24, y: height / 2, zoom: MIN_READABLE });
    }, 60);
    return () => clearTimeout(t);
  }, [flow, graph, height]);
  useHotkeys({ f: () => flow.fitView({ padding: 0.06, duration: 300 }) });

  return (
    <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} nodesDraggable nodesConnectable={false} minZoom={0.2} maxZoom={2}
      onNodeDoubleClick={(_, n) => { const mapId = (n.data as N["data"]).n.mapId; if (mapId) onOpenMap(mapId); }}
      proOptions={{ hideAttribution: true }}>
      <Background color={cssVar("border")} gap={20} size={1} />
      <Controls showInteractive={false} className="!border-border !bg-panel [&>button]:!border-border [&>button]:!bg-panel [&>button]:!fill-fg-muted" />
    </ReactFlow>
  );
}

export function DependencyGraphView(props: { graph: DependencyGraph; focus?: string | null; onOpenMap: (mapId: string) => void }) {
  return <ReactFlowProvider><Graph {...props} /></ReactFlowProvider>;
}
