"use client";
import { CheckCircle2, Circle, Loader2, X, XCircle } from "lucide-react";
import { useJobs } from "@/stores/jobs";
import { StepBar } from "@/components/ui";
import { cn } from "@/lib/cn";

/** Live analysis progress (SSE). Shows each pipeline step as it runs. */
export function JobProgress() {
  const jobMap = useJobs((s) => s.jobs);
  const jobs = Object.values(jobMap);
  const dismiss = useJobs((s) => s.dismiss);
  const visible = jobs.filter((j) => j.status !== "Completed" || j.steps.length > 0).slice(-2);
  if (visible.length === 0) return null;
  return (
    <div className="pointer-events-none fixed bottom-4 left-1/2 z-[90] flex -translate-x-1/2 flex-col gap-2">
      {visible.map((j) => {
        const done = j.steps.filter((s) => s.state === "Done" || s.state === "Skipped").length;
        const total = Math.max(j.steps.length, j.kind === "analysis" ? 9 : 4);
        const running = j.steps.find((s) => s.state === "Running");
        return (
          <div key={j.jobId} className="pointer-events-auto w-[440px] rounded-lg border border-border-strong bg-bg-elev p-3 shadow-2xl">
            <div className="mb-2 flex items-center gap-2 text-xs">
              {j.status === "Failed" ? <XCircle className="size-4 text-danger" /> : j.status === "Completed" ? <CheckCircle2 className="size-4 text-ok" /> : <Loader2 className="size-4 animate-spin text-calc" />}
              <span className="font-semibold">{j.kind === "analysis" ? "Analysis" : "AI analysis"}</span>
              <span className="truncate text-fg-muted">{j.status === "Failed" ? j.error : j.status === "Completed" ? "completed" : running?.label ?? "queued"}</span>
              {(j.status === "Completed" || j.status === "Failed") && (
                <button className="ml-auto text-fg-subtle hover:text-fg" onClick={() => dismiss(j.jobId)}><X className="size-3.5" /></button>
              )}
            </div>
            <StepBar fraction={j.status === "Completed" ? 1 : done / total} />
            <div className="mt-2 grid grid-cols-3 gap-x-3 gap-y-1">
              {j.steps.map((s) => (
                <div key={s.step} className="flex items-center gap-1.5 text-[11px]">
                  {s.state === "Done" ? <CheckCircle2 className="size-3 text-ok" /> : s.state === "Running" ? <Loader2 className="size-3 animate-spin text-calc" /> : s.state === "Failed" ? <XCircle className="size-3 text-danger" /> : <Circle className="size-3 text-fg-subtle" />}
                  <span className={cn("truncate", s.state === "Running" ? "text-fg" : "text-fg-muted")}>{s.label}</span>
                </div>
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}
