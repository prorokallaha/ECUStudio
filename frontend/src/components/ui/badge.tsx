import type { HTMLAttributes } from "react";
import { cn } from "@/lib/cn";
import { toneSoft, type Tone } from "@/lib/colors";

export function Badge({ tone = "unknown", className, ...props }: HTMLAttributes<HTMLSpanElement> & { tone?: Tone }) {
  return (
    <span
      className={cn("inline-flex items-center gap-1 rounded border px-1.5 h-[18px] text-[10px] font-semibold uppercase tracking-wide whitespace-nowrap", toneSoft[tone], className)}
      {...props}
    />
  );
}
