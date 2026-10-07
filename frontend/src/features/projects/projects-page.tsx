"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Beaker, FolderPlus, Search, Trash2, Upload } from "lucide-react";
import { toast } from "sonner";
import type { Project } from "@/types/domain";
import { api, ApiError } from "@/services/api";
import { keys, useInfo, useRunAnalysis } from "@/hooks/use-analysis";
import { useFileDrop } from "@/hooks/use-file-drop";
import { Badge, Button, EmptyState, Input, Kbd, SeverityBadge, Skeleton } from "@/components/ui";
import { JobProgress } from "@/components/layout/job-progress";
import { NewProjectDialog } from "./new-project-dialog";
import { fmtDate } from "@/lib/format";
import { cn } from "@/lib/cn";

export function ProjectsPage() {
  const router = useRouter();
  const qc = useQueryClient();
  const info = useInfo();
  const projects = useQuery({ queryKey: keys.projects, queryFn: api.projects.list });
  const [filter, setFilter] = useState("");
  const [dialog, setDialog] = useState(false);
  const run = useRunAnalysis();

  const open = (p: Project) => router.push(`/workspace/dashboard/?p=${p.id}${p.latestAnalysisId ? `&a=${p.latestAnalysisId}` : ""}`);

  async function createWith(name: string, vin?: string, file?: File) {
    const p = await api.projects.create({ name, vin });
    if (file) await api.projects.upload(p.id, file, "Stock");
    qc.invalidateQueries({ queryKey: keys.projects });
    if (file) run.mutate({ projectId: p.id, navigate: false }, { onSuccess: (a) => router.push(`/workspace/dashboard/?p=${p.id}&a=${a}`) });
    else router.push(`/workspace/vehicle/?p=${p.id}`);
  }

  const demo = useMutation({
    mutationFn: api.projects.demo,
    onSuccess: (p) => { qc.invalidateQueries({ queryKey: keys.projects }); run.mutate({ projectId: p.id, navigate: false }, { onSuccess: (a) => router.push(`/workspace/dashboard/?p=${p.id}&a=${a}`) }); },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : String(e)),
  });

  const remove = useMutation({
    mutationFn: api.projects.remove,
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.projects }),
  });

  const { over, bind } = useFileDrop((files) => {
    const f = files[0];
    createWith(f.name.replace(/\.[^.]+$/, ""), undefined, f).catch((e) => toast.error(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e)));
  });

  const list = (projects.data ?? []).filter((p) => !filter || `${p.name} ${p.vin ?? ""} ${p.headline?.vehicle ?? ""} ${p.headline?.engineCode ?? ""}`.toLowerCase().includes(filter.toLowerCase()));

  return (
    <div className={cn("flex h-screen flex-col", over && "ring-2 ring-inset ring-calc")} {...bind}>
      <header className="flex h-12 items-center gap-3 border-b border-border bg-bg-elev px-5">
        <div className="grid size-7 place-items-center rounded bg-calc/15 font-bold text-calc text-xs">ES</div>
        <div className="text-sm font-semibold">ECUStudio</div>
        <div className="text-xs text-fg-subtle">Calibration review without a dyno — estimates with ranges, never fake precision</div>
        <div className="ml-auto flex items-center gap-2 text-[11px] text-fg-subtle">
          engine {info.data?.version ?? "…"} · plugins {info.data?.plugins.map((p) => p.name).join(", ") ?? "…"} ·
          <span className={info.data?.aiConfigured ? "text-ok" : "text-unknown"}>AI {info.data?.aiConfigured ? "on" : "off"}</span>
        </div>
      </header>

      <div className="mx-auto flex w-full max-w-6xl min-h-0 flex-1 flex-col px-5 py-5">
        <div className="mb-3 flex items-center gap-2">
          <h1 className="text-base font-semibold">Projects</h1>
          <div className="relative ml-4 w-72">
            <Search className="absolute left-2 top-1.5 size-3.5 text-fg-subtle" />
            <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter by name, VIN, vehicle, engine" className="pl-7" />
          </div>
          <div className="ml-auto flex gap-2">
            <Button onClick={() => demo.mutate()} disabled={demo.isPending || run.isPending}><Beaker className="size-3.5" />Demo project (synthetic)</Button>
            <Button variant="primary" onClick={() => setDialog(true)}><FolderPlus className="size-3.5" />New project</Button>
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-auto rounded-lg border border-border bg-panel">
          <table className="w-full text-xs">
            <thead className="sticky top-0 bg-panel-2 text-[11px] uppercase tracking-wide text-fg-subtle">
              <tr className="[&>th]:px-3 [&>th]:py-2 [&>th]:text-left [&>th]:font-medium">
                <th>Project</th><th>Vehicle</th><th>Engine</th><th>ECU</th><th>Files</th><th>Risk</th><th>Updated</th><th />
              </tr>
            </thead>
            <tbody>
              {projects.isLoading && Array.from({ length: 4 }).map((_, i) => (
                <tr key={i}><td colSpan={8} className="px-3 py-2"><Skeleton className="h-5" /></td></tr>
              ))}
              {list.map((p) => (
                <tr key={p.id} onClick={() => open(p)} className="cursor-pointer border-t border-border hover:bg-panel-2 [&>td]:px-3 [&>td]:py-2">
                  <td>
                    <div className="font-medium">{p.name}</div>
                    {p.vin && <div className="num text-[11px] text-fg-subtle">{p.vin}</div>}
                  </td>
                  <td className="text-fg-muted">{p.headline?.vehicle ?? "—"}</td>
                  <td>{p.headline?.engineCode ?? "—"}</td>
                  <td className="text-fg-muted">{p.headline?.ecu ?? "—"}</td>
                  <td>
                    <div className="flex gap-1">
                      {(p.files ?? []).map((f) => <Badge key={f.id} tone={f.role === "Stock" ? "ok" : f.role === "Modified" ? "calc" : "unknown"} className="normal-case">{f.label}</Badge>)}
                    </div>
                  </td>
                  <td>{p.headline?.risk ? <SeverityBadge severity={p.headline.risk} /> : <span className="text-fg-subtle">not analysed</span>}</td>
                  <td className="text-fg-muted whitespace-nowrap">{fmtDate(p.updatedAt)}</td>
                  <td className="text-right">
                    <Button size="icon" variant="ghost" title="Delete project" onClick={(e) => { e.stopPropagation(); if (confirm(`Delete project "${p.name}" and its binaries?`)) remove.mutate(p.id); }}><Trash2 className="size-3.5" /></Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {!projects.isLoading && list.length === 0 && (
            <EmptyState icon={<Upload className="size-8" />} title={filter ? "No matching projects" : "Drop an ECU .bin here to start"}>
              A project holds the stock file, modified versions, VIN, hardware changes and all analyses. Supported now: Bosch EDC16U34 (VAG 1.9 TDI PD).
              Use <Kbd>Demo project</Kbd> to explore with clearly labelled synthetic data.
            </EmptyState>
          )}
        </div>
      </div>
      <NewProjectDialog open={dialog} onClose={() => setDialog(false)} onCreate={async ({ name, vin, file }) => {
        try { await createWith(name, vin, file); } catch (e) { toast.error(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e)); throw e; }
      }} />
      <JobProgress />
    </div>
  );
}
