import type { HTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/cn";

export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("rounded-lg border border-border bg-panel", className)} {...props} />;
}

export function CardHeader({ title, subtitle, actions, className, icon }: { title: ReactNode; subtitle?: ReactNode; actions?: ReactNode; className?: string; icon?: ReactNode }) {
  return (
    <div className={cn("flex items-center gap-2 border-b border-border px-3 h-9", className)}>
      {icon && <span className="text-fg-subtle">{icon}</span>}
      <div className="min-w-0 flex-1">
        <div className="truncate text-xs font-semibold uppercase tracking-wide text-fg-muted">{title}</div>
      </div>
      {subtitle && <div className="text-2xs text-fg-subtle truncate">{subtitle}</div>}
      {actions && <div className="flex items-center gap-1">{actions}</div>}
    </div>
  );
}

export function CardBody({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("p-3", className)} {...props} />;
}
