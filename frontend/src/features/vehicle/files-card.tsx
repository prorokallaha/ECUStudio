"use client";
import { useRef } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Play, Upload } from "lucide-react";
import { toast } from "sonner";
import type { FileRole, Project } from "@/types/domain";
import { api, ApiError } from "@/services/api";
import { keys, useRunAnalysis } from "@/hooks/use-analysis";
import { useFileDrop } from "@/hooks/use-file-drop";
import { Button, Card, CardHeader, Select, SeverityBadge, EstimateValue } from "@/components/ui";
import { fmtBytes, fmtDate } from "@/lib/format";
import { cn } from "@/lib/cn";

/** Project binaries and their versions (version history: every file keeps its own analysis summary). */
export function FilesCard({ project }: { project: Project }) {
  const qc = useQueryClient();
  const run = useRunAnalysis();
  const input = useRef<HTMLInputElement>(null);
  const upload = async (files: File[]) => {
    for (const f of files) {
      try {
        await api.projects.upload(project.id, f);
        toast.success(`${f.name} added`);
      } catch (e) {
        toast.error(e instanceof ApiError ? `${f.name}: ${e.message}` : String(e));
      }
    }
    qc.invalidateQueries({ queryKey: keys.project(project.id) });
  };
  const { over, bind } = useFileDrop(upload);
  const setRole = async (fileId: string, role: FileRole) => {
    await api.projects.setFileRole(project.id, fileId, role);
    qc.invalidateQueries({ queryKey: keys.project(project.id) });
  };
  return (
    <Card className={cn(over && "ring-2 ring-calc")} {...bind}>
      <CardHeader
        title="Binaries & versions"
        subtitle="drop .bin files here"
        actions={<>
          <input ref={input} type="file" multiple hidden onChange={(e) => e.target.files && upload(Array.from(e.target.files))} />
          <Button size="xs" onClick={() => input.current?.click()}><Upload className="size-3" />Upload</Button>
        </>}
      />
      <table className="w-full text-xs">
        <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
          <tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>Version</th><th>Role</th><th>Size</th><th>Changed maps</th><th>Peak power</th><th>Risk</th><th>Uploaded</th><th /></tr>
        </thead>
        <tbody>
          {(project.files ?? []).map((f) => (
            <tr key={f.id} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5">
              <td><div className="font-medium">{f.label}</div><div className="num text-[10px] text-fg-subtle">{f.name} · {f.sha256.slice(0, 10)}</div></td>
              <td>
                <Select value={f.role} onChange={(e) => setRole(f.id, e.target.value as FileRole)} className="h-6">
                  <option value="Stock">Stock</option><option value="Modified">Modified</option><option value="Version">Version</option>
                </Select>
              </td>
              <td className="num text-fg-muted">{fmtBytes(f.size)}</td>
              <td className="num">{f.summary?.changedMaps ?? "—"}</td>
              <td>{f.summary?.peakPowerHp ? <EstimateValue e={f.summary.peakPowerHp} size="sm" /> : "—"}</td>
              <td>{f.summary ? <SeverityBadge severity={f.summary.risk} /> : <span className="text-fg-subtle">—</span>}</td>
              <td className="text-fg-muted">{fmtDate(f.uploadedAt)}</td>
              <td className="text-right">
                <Button size="xs" variant="ghost" disabled={run.isPending} onClick={() => run.mutate({ projectId: project.id, modifiedFileId: f.id })}><Play className="size-3" />Analyse</Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {!(project.files ?? []).length && <div className="px-3 py-6 text-center text-xs text-fg-muted">No binaries yet. Drop the stock file first, then modified versions.</div>}
    </Card>
  );
}
