"use client";
import type { ReactNode } from "react";
import { FileWarning, Play } from "lucide-react";
import type { AnalysisReport } from "@/types/domain";
import { useProject, useReport, useRunAnalysis } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { Button, EmptyState, Skeleton } from "@/components/ui";
import { ApiError } from "@/services/api";
import { cn } from "@/lib/cn";

export function PageHeader({ title, subtitle, actions, className }: { title: ReactNode; subtitle?: ReactNode; actions?: ReactNode; className?: string }) {
  return (
    <div className={cn("flex items-center gap-3 border-b border-border px-4 h-11 shrink-0", className)}>
      <h1 className="text-sm font-semibold">{title}</h1>
      {subtitle && <div className="truncate text-xs text-fg-muted">{subtitle}</div>}
      {actions && <div className="ml-auto flex items-center gap-1.5">{actions}</div>}
    </div>
  );
}

export function PageSkeleton() {
  return (
    <div className="space-y-3 p-4">
      <Skeleton className="h-6 w-64" />
      <div className="grid grid-cols-5 gap-3">{Array.from({ length: 10 }).map((_, i) => <Skeleton key={i} className="h-24" />)}</div>
      <Skeleton className="h-64" />
    </div>
  );
}

/** Renders children only when an analysis report is loaded; otherwise offers to run one. */
export function WithReport({ children }: { children: (r: AnalysisReport) => ReactNode }) {
  const { projectId, analysisId } = useWorkspace();
  const report = useReport();
  const project = useProject(projectId);
  const run = useRunAnalysis();
  if (!analysisId) {
    const hasFiles = (project.data?.files?.length ?? 0) > 0;
    return (
      <EmptyState
        icon={<FileWarning className="size-8" />}
        title={hasFiles ? "No analysis yet" : "No binary in this project"}
        action={hasFiles && projectId ? <Button variant="primary" disabled={run.isPending} onClick={() => run.mutate({ projectId })}><Play className="size-3.5" />Run analysis</Button> : null}
      >
        {hasFiles ? "Run the analysis pipeline: identification, maps, diff, simulation and risk." : "Upload a .bin on the Vehicle page or from the projects list."}
      </EmptyState>
    );
  }
  if (report.isLoading) return <PageSkeleton />;
  if (report.error) {
    const e = report.error;
    return <EmptyState icon={<FileWarning className="size-8 text-danger" />} title="Could not load analysis">{e instanceof ApiError ? `${e.code}: ${e.message}` : String(e)}</EmptyState>;
  }
  return <>{report.data && children(report.data)}</>;
}
