"use client";
import { create } from "zustand";
import type { JobEvent, StepProgress } from "@/types/domain";

export interface JobView {
  jobId: string;
  kind: "analysis" | "ai";
  projectId?: string;
  analysisId?: string;
  status: "Queued" | "Running" | "Completed" | "Failed";
  steps: StepProgress[];
  error?: string | null;
  resultId?: string | null;
}

interface JobsState {
  jobs: Record<string, JobView>;
  track: (j: Omit<JobView, "status" | "steps">) => void;
  apply: (e: JobEvent) => void;
  dismiss: (jobId: string) => void;
}

export const useJobs = create<JobsState>()((set) => ({
  jobs: {},
  track: (j) => set((s) => ({ jobs: { ...s.jobs, [j.jobId]: { ...j, status: "Queued", steps: [] } } })),
  apply: (e) =>
    set((s) => {
      const cur = s.jobs[e.jobId];
      if (!cur) return s;
      const steps = [...cur.steps];
      if (e.step) {
        const i = steps.findIndex((x) => x.step === e.step!.step);
        if (i >= 0) steps[i] = e.step; else steps.push(e.step);
      }
      return { jobs: { ...s.jobs, [e.jobId]: { ...cur, status: e.status, steps, error: e.error, resultId: e.analysisId ?? cur.resultId } } };
    }),
  dismiss: (jobId) => set((s) => { const { [jobId]: _, ...rest } = s.jobs; return { jobs: rest }; }),
}));
