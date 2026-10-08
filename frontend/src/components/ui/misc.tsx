import type { HTMLAttributes, InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from "react";
import { forwardRef } from "react";
import { cn } from "@/lib/cn";

export function Skeleton({ className }: { className?: string }) {
  return <div className={cn("skeleton rounded", className)} />;
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="rounded border border-border-strong bg-panel-2 px-1 py-px font-mono text-[10px] text-fg-muted">{children}</kbd>;
}

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(({ className, ...props }, ref) => (
  <input
    ref={ref}
    className={cn("h-7 w-full rounded-md border border-border bg-bg px-2 text-xs text-fg placeholder:text-fg-subtle focus:border-calc focus:outline-none", className)}
    {...props}
  />
));
Input.displayName = "Input";

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(({ className, children, ...props }, ref) => (
  <select ref={ref} className={cn("h-7 rounded-md border border-border bg-bg px-1.5 text-xs text-fg focus:border-calc focus:outline-none", className)} {...props}>
    {children}
  </select>
));
Select.displayName = "Select";

export function Segmented<T extends string>({ value, options, onChange, className, size = "sm" }: { value: T; options: { value: T; label: ReactNode; title?: string }[]; onChange: (v: T) => void; className?: string; size?: "xs" | "sm" }) {
  return (
    <div className={cn("inline-flex rounded-md border border-border bg-bg p-0.5", className)}>
      {options.map((o) => (
        <button
          key={o.value}
          title={o.title}
          onClick={() => onChange(o.value)}
          className={cn(
            "rounded px-2 font-medium transition-colors",
            size === "xs" ? "h-5 text-[11px]" : "h-6 text-xs",
            o.value === value ? "bg-panel-2 text-fg shadow-sm" : "text-fg-subtle hover:text-fg",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function EmptyState({ icon, title, children, action, className }: { icon?: ReactNode; title: ReactNode; children?: ReactNode; action?: ReactNode; className?: string }) {
  return (
    <div className={cn("flex flex-col items-center justify-center gap-2 py-12 text-center", className)}>
      {icon && <div className="text-fg-subtle">{icon}</div>}
      <div className="text-sm font-medium">{title}</div>
      {children && <div className="max-w-md text-xs text-fg-muted">{children}</div>}
      {action}
    </div>
  );
}

export function KV({ k, children, className }: { k: ReactNode; children: ReactNode; className?: string }) {
  return (
    <div className={cn("flex items-baseline justify-between gap-3 py-1 text-xs", className)}>
      <span className="text-fg-subtle shrink-0">{k}</span>
      <span className="text-right min-w-0 truncate">{children}</span>
    </div>
  );
}

export function SectionTitle({ children, className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("text-[11px] font-semibold uppercase tracking-wider text-fg-subtle", className)} {...props}>{children}</div>;
}

export function Spinner({ className }: { className?: string }) {
  return <span className={cn("inline-block size-3.5 animate-spin rounded-full border-2 border-current border-t-transparent", className)} />;
}
