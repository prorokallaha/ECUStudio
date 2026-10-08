"use client";
import { useEffect, useMemo, useState } from "react";
import { Command } from "cmdk";
import { createPortal } from "react-dom";
import { Binary, Bot, Download, Map, Moon, Play } from "lucide-react";
import type { AnalysisReport, Project } from "@/types/domain";
import { NAV } from "./nav-items";
import { useWorkspace } from "@/hooks/use-workspace";
import { useUI } from "@/stores/ui";
import { useRunAnalysis } from "@/hooks/use-analysis";
import { api } from "@/services/api";
import { hex } from "@/lib/format";
import { useT } from "@/i18n";

export type PaletteMode = "all" | "maps";

/** Ctrl+K: everything. Ctrl+P: maps and addresses (type 0x5A000 to jump in the hex viewer). */
export function CommandPalette({ open, mode, onClose, report, project }: { open: boolean; mode: PaletteMode; onClose: () => void; report?: AnalysisReport; project?: Project }) {
  const { href, router } = useWorkspace();
  const [q, setQ] = useState("");
  const ui = useUI();
  const run = useRunAnalysis();
  const t = useT();
  useEffect(() => { if (open) setQ(""); }, [open, mode]);

  const address = useMemo(() => {
    const m = q.trim().match(/^(0x)?([0-9a-f]{3,8})$/i);
    if (!m || (!m[1] && !/[a-f]/i.test(m[2]) && q.length < 5)) return null;
    const n = parseInt(m[2], 16);
    return Number.isFinite(n) ? n : null;
  }, [q]);

  if (!open || typeof document === "undefined") return null;
  const go = (url: string) => { router.push(url); onClose(); };

  return createPortal(
    <div className="fixed inset-0 z-[120] flex items-start justify-center bg-black/55 pt-[14vh]" onMouseDown={onClose}>
      <Command
        label={t("layout.commandPalette")}
        className="w-full max-w-xl overflow-hidden rounded-lg border border-border-strong bg-bg-elev shadow-2xl"
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.key === "Escape" && onClose()}
        loop
      >
        <Command.Input
          autoFocus
          value={q}
          onValueChange={setQ}
          placeholder={mode === "maps" ? t("layout.paletteMapsPlaceholder") : t("layout.palettePlaceholder")}
          className="h-11 w-full border-b border-border bg-transparent px-4 text-sm outline-none placeholder:text-fg-subtle"
        />
        <Command.List className="max-h-[50vh] overflow-y-auto p-1.5 text-[13px] [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1 [&_[cmdk-group-heading]]:text-[10px] [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-fg-subtle">
          <Command.Empty className="px-3 py-6 text-center text-xs text-fg-muted">{t("layout.noMatches")}</Command.Empty>
          {address !== null && report && (
            <Command.Group heading={t("layout.groupAddress")}>
              <Item onSelect={() => go(href("binary", { offset: address }))} icon={<Binary className="size-4" />} value={`address ${q}`}>
                {t("layout.goToAddress", { addr: hex(address) })}
              </Item>
            </Command.Group>
          )}
          {report && (
            <Command.Group heading={t("layout.groupMaps")}>
              {report.maps.map((m) => (
                <Item key={m.id} value={`${m.name} ${m.role} ${m.id} ${hex(m.address)}`} onSelect={() => go(href(`maps/${m.id}`))} icon={<Map className="size-4" />}>
                  <span className="flex-1 truncate">{m.name}</span>
                  {m.modified && <span className="text-2xs text-calc">{t("layout.mapModified")}</span>}
                  <span className="num text-2xs text-fg-subtle">{hex(m.address)}</span>
                </Item>
              ))}
            </Command.Group>
          )}
          {mode === "all" && (
            <>
              <Command.Group heading={t("layout.groupNavigate")}>
                {NAV.filter((n) => !n.needsReport || report).map((n) => (
                  <Item key={n.section} value={`go ${n.section} ${t(n.label)}`} onSelect={() => go(href(n.section))} icon={<n.icon className="size-4" />}>{t(n.label)}</Item>
                ))}
                <Item value={`projects list ${t("layout.allProjects")}`} onSelect={() => go("/")} icon={<Map className="size-4" />}>{t("layout.allProjects")}</Item>
              </Command.Group>
              <Command.Group heading={t("layout.groupActions")}>
                {project && <Item value={`run analysis recompute ${t("common.runAnalysis")}`} onSelect={() => { run.mutate({ projectId: project.id }); onClose(); }} icon={<Play className="size-4" />}>{t("common.runAnalysis")}</Item>}
                {report && <Item value={`export report markdown ${t("layout.exportReport")}`} onSelect={() => { window.open(api.analyses.reportMarkdownUrl(report.id), "_blank"); onClose(); }} icon={<Download className="size-4" />}>{t("layout.exportReport")}</Item>}
                <Item value={`toggle theme dark light ${t("layout.toggleTheme")}`} onSelect={() => { ui.setTheme(ui.theme === "dark" ? "light" : "dark"); onClose(); }} icon={<Moon className="size-4" />}>{t("layout.toggleTheme")}</Item>
                <Item value={`toggle ai panel ${t("layout.toggleAI")}`} onSelect={() => { ui.setAIPanel(!ui.aiPanelOpen); onClose(); }} icon={<Bot className="size-4" />}>{t("layout.toggleAI")}</Item>
              </Command.Group>
            </>
          )}
        </Command.List>
      </Command>
    </div>,
    document.body,
  );
}

function Item({ children, onSelect, icon, value }: { children: React.ReactNode; onSelect: () => void; icon?: React.ReactNode; value: string }) {
  return (
    <Command.Item value={value} onSelect={onSelect} className="flex h-8 cursor-pointer items-center gap-2.5 rounded-md px-2 text-fg-muted data-[selected=true]:bg-calc/15 data-[selected=true]:text-fg">
      <span className="text-fg-subtle">{icon}</span>
      {children}
    </Command.Item>
  );
}
