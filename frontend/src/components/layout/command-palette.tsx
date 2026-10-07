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

export type PaletteMode = "all" | "maps";

/** Ctrl+K: everything. Ctrl+P: maps and addresses (type 0x5A000 to jump in the hex viewer). */
export function CommandPalette({ open, mode, onClose, report, project }: { open: boolean; mode: PaletteMode; onClose: () => void; report?: AnalysisReport; project?: Project }) {
  const { href, router } = useWorkspace();
  const [q, setQ] = useState("");
  const ui = useUI();
  const run = useRunAnalysis();
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
        label="Command palette"
        className="w-full max-w-xl overflow-hidden rounded-lg border border-border-strong bg-bg-elev shadow-2xl"
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.key === "Escape" && onClose()}
        loop
      >
        <Command.Input
          autoFocus
          value={q}
          onValueChange={setQ}
          placeholder={mode === "maps" ? "Map name, role or address (0x…)" : "Type a command, page or map…"}
          className="h-11 w-full border-b border-border bg-transparent px-4 text-sm outline-none placeholder:text-fg-subtle"
        />
        <Command.List className="max-h-[50vh] overflow-y-auto p-1.5 text-[13px] [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1 [&_[cmdk-group-heading]]:text-[10px] [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-fg-subtle">
          <Command.Empty className="px-3 py-6 text-center text-xs text-fg-muted">No matches</Command.Empty>
          {address !== null && report && (
            <Command.Group heading="Address">
              <Item onSelect={() => go(href("binary", { offset: address }))} icon={<Binary className="size-4" />} value={`address ${q}`}>
                Go to {hex(address)} in Binary Viewer
              </Item>
            </Command.Group>
          )}
          {report && (
            <Command.Group heading="Maps">
              {report.maps.map((m) => (
                <Item key={m.id} value={`${m.name} ${m.role} ${m.id} ${hex(m.address)}`} onSelect={() => go(href(`maps/${m.id}`))} icon={<Map className="size-4" />}>
                  <span className="flex-1 truncate">{m.name}</span>
                  {m.modified && <span className="text-2xs text-calc">modified</span>}
                  <span className="num text-2xs text-fg-subtle">{hex(m.address)}</span>
                </Item>
              ))}
            </Command.Group>
          )}
          {mode === "all" && (
            <>
              <Command.Group heading="Navigate">
                {NAV.filter((n) => !n.needsReport || report).map((n) => (
                  <Item key={n.section} value={`go ${n.label}`} onSelect={() => go(href(n.section))} icon={<n.icon className="size-4" />}>{n.label}</Item>
                ))}
                <Item value="projects list" onSelect={() => go("/")} icon={<Map className="size-4" />}>All projects</Item>
              </Command.Group>
              <Command.Group heading="Actions">
                {project && <Item value="run analysis recompute" onSelect={() => { run.mutate({ projectId: project.id }); onClose(); }} icon={<Play className="size-4" />}>Run analysis</Item>}
                {report && <Item value="export report markdown" onSelect={() => { window.open(api.analyses.reportMarkdownUrl(report.id), "_blank"); onClose(); }} icon={<Download className="size-4" />}>Export report</Item>}
                <Item value="toggle theme dark light" onSelect={() => { ui.setTheme(ui.theme === "dark" ? "light" : "dark"); onClose(); }} icon={<Moon className="size-4" />}>Toggle theme</Item>
                <Item value="toggle ai panel" onSelect={() => { ui.setAIPanel(!ui.aiPanelOpen); onClose(); }} icon={<Bot className="size-4" />}>Toggle AI panel</Item>
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
