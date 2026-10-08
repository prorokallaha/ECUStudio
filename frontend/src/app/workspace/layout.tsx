import { Suspense } from "react";
import { WorkspaceShell } from "@/components/layout/workspace-shell";
import { PageSkeleton } from "@/components/layout/page";

export default function WorkspaceLayout({ children }: { children: React.ReactNode }) {
  return (
    <Suspense fallback={<PageSkeleton />}>
      <WorkspaceShell>{children}</WorkspaceShell>
    </Suspense>
  );
}
