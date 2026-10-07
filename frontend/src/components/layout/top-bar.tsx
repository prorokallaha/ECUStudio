"use client";
import { Bot, Download, Moon, Play, Save, Search, Sun } from "lucide-react";
import { toast } from "sonner";
import type { AnalysisReport, Project } from "@/types/domain";
import { Badge, Button, ConfidenceBadge, Select, SeverityBadge, Spinner, Tooltip } from "@/components/ui";
import { fmtParam } from "@/lib/format";
import { useUI } from "@/stores/ui";
import { api } from "@/services/api";
import { useRunAnalysis } from "@/hooks/use-analysis";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";
import { DefinitionStatus } from "./definition-status";

function Field({ label, value, mono, warn, className }: { label: string; value?: string | null; mono?: boolean; warn?: boolean; className?: string }) {
  return (
    <div className={cn("flex min-w-0 flex-col justify-center px-2.5 border-r border-border last:border-r-0", className)}>
      <span className="text-[10px] uppercase tracking-wider text-fg-subtle leading-none">{label}</span>
      <span className={cn("truncate text-xs leading-tight mt-0.5", mono && "num", warn ? "text-unknown" : "text-fg")} title={value ?? undefined}>{value || "—"}</span>
    </div>
  );
}

export function TopBar({ project, report, onPalette }: { project?: Project; report?: AnalysisReport; onPalette: () => void }) {
  const t = useT();
  const { theme, setTheme, aiPanelOpen, setAIPanel } = useUI();
  const run = useRunAnalysis();
  const profile = report?.vehicle.profile;
  const vehicle = profile ? `${fmtParam(profile.make)} ${fmtParam(profile.model)}`.replace(/UNKNOWN/g, "").trim() || "UNKNOWN" : project?.headline?.vehicle;
  const files = project?.files ?? [];
  const currentFile = files.find((f) => f.sha256 === report?.modifiedSha256);
  const hasStock = !!report?.stockSha256;

  return (
    <header className="flex h-11 shrink-0 items-stretch border-b border-border bg-bg-elev">
      <div className="flex items-center gap-2 px-3 border-r border-border">
        <div className="grid size-6 place-items-center rounded bg-calc/15 text-calc font-bold text-[11px]">ES</div>
        <div className="max-w-44 truncate text-[13px] font-semibold" title={project?.name}>{project?.name ?? "ECUStudio"}</div>
      </div>
      <div className="flex min-w-0 flex-1 items-stretch overflow-hidden py-1">
        <Field label={t("layout.vehicle")} value={vehicle && t.val(vehicle)} warn={vehicle === "UNKNOWN"} className="min-w-[120px] flex-1 max-w-[220px]" />
        <Field label="VIN" value={project?.vin ?? (profile ? t.val(fmtParam(profile.vin)) : undefined)} mono className="shrink-0" />
        <Field label={t("layout.engine")} value={profile ? t.val(fmtParam(profile.engineCode)) : project?.headline?.engineCode} className="shrink-0" />
        <Field label={t("layout.ecu")} value={report?.ecu.ecuFamily ?? project?.headline?.ecu} className="shrink-0" />
        <Field label="HW" value={report ? t.val(fmtParam(report.ecu.hardwareNumber)) : undefined} mono className="shrink-0" />
        <Field label="SW" value={report ? t.val(fmtParam(report.ecu.softwareNumber)) : undefined} mono className="shrink-0" />
        <DefinitionStatus project={project} report={report} />
        <div className="flex items-center gap-1.5 px-2.5 border-r border-border">
          <div className="flex flex-col">
            <span className="text-[10px] uppercase tracking-wider text-fg-subtle leading-none">{t("layout.currentBin")}</span>
            <Select
              className="mt-0.5 h-5 max-w-40 border-none bg-transparent px-0 text-xs"
              value={currentFile?.id ?? ""}
              disabled={!project || run.isPending}
              onChange={(e) => project && run.mutate({ projectId: project.id, modifiedFileId: e.target.value })}
            >
              {!currentFile && <option value="">{report?.modifiedName ?? "—"}</option>}
              {files.map((f) => <option key={f.id} value={f.id}>{f.label} · {t.tx(`fileRole.${f.role}`, f.role)}</option>)}
            </Select>
          </div>
          <Badge tone={hasStock ? "calc" : "unknown"}>{hasStock ? t("layout.stockVsMod") : t("layout.noStock")}</Badge>
        </div>
        <div className="flex items-center gap-1.5 px-2.5">
          {run.isPending ? <Badge tone="calc"><Spinner className="size-2.5" />{t("layout.analysing")}</Badge> : report ? <SeverityBadge severity={report.risk.overall} /> : <Badge>{t("layout.notAnalysed")}</Badge>}
          {report && <ConfidenceBadge score={report.risk.confidence} />}
        </div>
      </div>
      <div className="flex items-center gap-1 px-2 border-l border-border">
        <Tooltip content={t("layout.paletteTip")}><Button variant="ghost" size="icon" onClick={onPalette} className="text-fg-subtle"><Search className="size-3.5" /></Button></Tooltip>
        {project && (
          <Tooltip content={t("layout.rerunTip")}>
            <Button size="icon" variant="ghost" disabled={run.isPending} onClick={() => run.mutate({ projectId: project.id, modifiedFileId: currentFile?.id })}><Play className="size-3.5" /></Button>
          </Tooltip>
        )}
        <Tooltip content={t("layout.saveTip")}>
          <Button size="icon" variant="ghost" onClick={() => toast.success(t("layout.allSaved"))}><Save className="size-3.5" /></Button>
        </Tooltip>
        <Tooltip content={t("layout.exportTip")}>
          <Button size="icon" variant="ghost" disabled={!report} onClick={() => report && window.open(api.analyses.reportMarkdownUrl(report.id), "_blank")}><Download className="size-3.5" /></Button>
        </Tooltip>
        <Tooltip content={theme === "dark" ? t("layout.lightTheme") : t("layout.darkTheme")}>
          <Button size="icon" variant="ghost" onClick={() => setTheme(theme === "dark" ? "light" : "dark")}>{theme === "dark" ? <Sun className="size-3.5" /> : <Moon className="size-3.5" />}</Button>
        </Tooltip>
        <Tooltip content={t("layout.aiPanel")}>
          <Button size="icon" variant={aiPanelOpen ? "ai" : "ghost"} onClick={() => setAIPanel(!aiPanelOpen)}><Bot className="size-3.5" /></Button>
        </Tooltip>
      </div>
    </header>
  );
}
