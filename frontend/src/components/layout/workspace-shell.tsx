"use client";
import { useEffect, useState, type ReactNode } from "react";
import { Panel, PanelGroup, PanelResizeHandle } from "react-resizable-panels";
import { toast } from "sonner";
import { TopBar } from "./top-bar";
import { SideNav } from "./side-nav";
import { CommandPalette, type PaletteMode } from "./command-palette";
import { JobProgress } from "./job-progress";
import { AIPanel } from "@/components/ai/ai-panel";
import { useProject, useReport } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useHotkeys } from "@/hooks/use-hotkeys";
import { useUI } from "@/stores/ui";
import { useHistory } from "@/stores/history";
import { useJobs } from "@/stores/jobs";
import { useT } from "@/i18n";

export function WorkspaceShell({ children }: { children: ReactNode }) {
  const t = useT();
  const { projectId, router } = useWorkspace();
  const project = useProject(projectId);
  const report = useReport();
  const aiOpen = useUI((s) => s.aiPanelOpen);
  const history = useHistory();
  const [palette, setPalette] = useState<{ open: boolean; mode: PaletteMode }>({ open: false, mode: "all" });
  const jobs = useJobs((s) => s.jobs);
  const dismiss = useJobs((s) => s.dismiss);

  // Completed jobs fade out after a moment; failures stay until dismissed.
  useEffect(() => {
    const done = Object.values(jobs).filter((j) => j.status === "Completed");
    if (!done.length) return;
    const timer = setTimeout(() => done.forEach((j) => dismiss(j.jobId)), 2500);
    return () => clearTimeout(timer);
  }, [jobs, dismiss]);

  useEffect(() => { if (!projectId) router.replace("/"); }, [projectId, router]);

  useHotkeys({
    "mod+k": () => setPalette({ open: true, mode: "all" }),
    "mod+p": () => setPalette({ open: true, mode: "maps" }),
    "mod+s": () => toast.success(t("layout.allSaved")),
    "mod+z": async () => { const l = await history.undo(); if (l) toast(t("layout.undone", { label: l })); },
    "mod+shift+z": async () => { const l = await history.redo(); if (l) toast(t("layout.redone", { label: l })); },
    "mod+y": async () => { const l = await history.redo(); if (l) toast(t("layout.redone", { label: l })); },
  });

  return (
    <div className="flex h-screen flex-col overflow-hidden">
      <TopBar project={project.data} report={report.data} onPalette={() => setPalette({ open: true, mode: "all" })} />
      <div className="flex min-h-0 flex-1">
        <SideNav hasReport={!!report.data} />
        <PanelGroup direction="horizontal" autoSaveId="ecustudio.workspace" className="min-w-0 flex-1">
          <Panel id="main" order={1} minSize={45}>
            <main className="h-full overflow-auto">{children}</main>
          </Panel>
          {aiOpen && (
            <>
              <PanelResizeHandle className="w-px bg-border transition-colors hover:bg-ai data-[resize-handle-state=drag]:bg-ai" />
              <Panel id="ai" order={2} defaultSize={24} minSize={16} maxSize={45}>
                <AIPanel />
              </Panel>
            </>
          )}
        </PanelGroup>
      </div>
      <CommandPalette open={palette.open} mode={palette.mode} onClose={() => setPalette((p) => ({ ...p, open: false }))} report={report.data} project={project.data} />
      <JobProgress />
    </div>
  );
}
