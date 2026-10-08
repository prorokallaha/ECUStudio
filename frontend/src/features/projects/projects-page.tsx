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
import { useT } from "@/i18n";

export function ProjectsPage() {
  const t = useT();
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
        <div className="text-xs text-fg-subtle">{t("projects.tagline")}</div>
        <div className="ml-auto flex items-center gap-2 text-[11px] text-fg-subtle">
          {t("projects.engineInfo", { v: info.data?.version ?? "…", p: info.data?.plugins.map((p) => p.name).join(", ") ?? "…" })}
          <span className={info.data?.aiConfigured ? "text-ok" : "text-unknown"}>{info.data?.aiConfigured ? t("projects.aiOn") : t("projects.aiOff")}</span>
        </div>
      </header>

      <div className="mx-auto flex w-full max-w-6xl min-h-0 flex-1 flex-col px-5 py-5">
        <div className="mb-3 flex items-center gap-2">
          <h1 className="text-base font-semibold">{t("nav.projects")}</h1>
          <div className="relative ml-4 w-72">
            <Search className="absolute left-2 top-1.5 size-3.5 text-fg-subtle" />
            <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder={t("projects.filter")} className="pl-7" />
          </div>
          <div className="ml-auto flex gap-2">
            <Button onClick={() => demo.mutate()} disabled={demo.isPending || run.isPending}><Beaker className="size-3.5" />{t("projects.demo")}</Button>
            <Button variant="primary" onClick={() => setDialog(true)}><FolderPlus className="size-3.5" />{t("projects.newProject")}</Button>
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-auto rounded-lg border border-border bg-panel">
          <table className="w-full text-xs">
            <thead className="sticky top-0 bg-panel-2 text-[11px] uppercase tracking-wide text-fg-subtle">
              <tr className="[&>th]:px-3 [&>th]:py-2 [&>th]:text-left [&>th]:font-medium">
                <th>{t("projects.colProject")}</th><th>{t("projects.colVehicle")}</th><th>{t("projects.colEngine")}</th><th>{t("projects.colEcu")}</th><th>{t("projects.colFiles")}</th><th>{t("projects.colRisk")}</th><th>{t("projects.colUpdated")}</th><th />
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
                  <td>{p.headline?.risk ? <SeverityBadge severity={p.headline.risk} /> : <span className="text-fg-subtle">{t("projects.notAnalysed")}</span>}</td>
                  <td className="text-fg-muted whitespace-nowrap">{fmtDate(p.updatedAt)}</td>
                  <td className="text-right">
                    <Button size="icon" variant="ghost" title={t("projects.deleteProject")} onClick={(e) => { e.stopPropagation(); if (confirm(t("projects.confirmDelete", { name: p.name }))) remove.mutate(p.id); }}><Trash2 className="size-3.5" /></Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {!projects.isLoading && list.length === 0 && (
            <EmptyState icon={<Upload className="size-8" />} title={filter ? t("projects.noMatch") : t("projects.drop")}>
              {t("projects.emptyHint")}{" "}
              {t("projects.emptyDemoPre")} <Kbd>{t("projects.emptyDemoKbd")}</Kbd> {t("projects.emptyDemoPost")}
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
