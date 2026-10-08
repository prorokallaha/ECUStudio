import { forwardRef, type ButtonHTMLAttributes } from "react";
import { cn } from "@/lib/cn";

type Variant = "primary" | "secondary" | "ghost" | "danger" | "ai";
type Size = "xs" | "sm" | "md" | "icon";

const variants: Record<Variant, string> = {
  primary: "bg-calc text-white hover:bg-calc/90 border-transparent",
  secondary: "bg-panel-2 text-fg hover:bg-border border-border",
  ghost: "bg-transparent text-fg-muted hover:text-fg hover:bg-panel-2 border-transparent",
  danger: "bg-danger/15 text-danger hover:bg-danger/25 border-danger/30",
  ai: "bg-ai/15 text-ai hover:bg-ai/25 border-ai/30",
};
const sizes: Record<Size, string> = {
  xs: "h-6 px-2 text-2xs gap-1",
  sm: "h-7 px-2.5 text-xs gap-1.5",
  md: "h-8 px-3 text-[13px] gap-2",
  icon: "h-7 w-7 justify-center",
};

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(({ className, variant = "secondary", size = "sm", ...props }, ref) => (
  <button
    ref={ref}
    className={cn(
      "inline-flex items-center rounded-md border font-medium whitespace-nowrap transition-colors select-none",
      "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-calc/50 disabled:opacity-40 disabled:pointer-events-none",
      variants[variant], sizes[size], className,
    )}
    {...props}
  />
));
Button.displayName = "Button";
