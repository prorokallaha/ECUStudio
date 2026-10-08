"use client";
import Link from "next/link";
import { ChevronsLeft, ChevronsRight, FolderOpen } from "lucide-react";
import { NAV } from "./nav-items";
import { useWorkspace } from "@/hooks/use-workspace";
import { useUI } from "@/stores/ui";
import { cn } from "@/lib/cn";
import { Tooltip } from "@/components/ui";
import { useT } from "@/i18n";

export function SideNav({ hasReport }: { hasReport: boolean }) {
  const { section, href } = useWorkspace();
  const collapsed = useUI((s) => s.navCollapsed);
  const toggle = useUI((s) => s.toggleNav);
  const t = useT();
  let lastGroup = "";
  return (
    <nav className={cn("flex shrink-0 flex-col border-r border-border bg-bg-elev transition-[width]", collapsed ? "w-12" : "w-48")}>
      <Link href="/" className="flex h-9 items-center gap-2 px-3.5 text-xs text-fg-muted hover:text-fg border-b border-border">
        <FolderOpen className="size-4 shrink-0" />{!collapsed && t("nav.projects")}
      </Link>
      <div className="flex-1 overflow-y-auto py-1.5">
        {NAV.map((item) => {
          const sep = item.group !== lastGroup && lastGroup !== "";
          lastGroup = item.group;
          const disabled = item.needsReport && !hasReport;
          const active = section === item.section;
          const Icon = item.icon;
          const link = (
            <Link
              href={disabled ? "#" : href(item.section)}
              aria-disabled={disabled}
              className={cn(
                "group relative mx-1.5 flex h-7 items-center gap-2.5 rounded-md px-2 text-[13px] transition-colors",
                active ? "bg-calc/12 text-fg" : "text-fg-muted hover:bg-panel-2 hover:text-fg",
                disabled && "pointer-events-none opacity-35",
              )}
            >
              {active && <span className="absolute -left-1.5 top-1 bottom-1 w-0.5 rounded bg-calc" />}
              <Icon className={cn("size-4 shrink-0", active && "text-calc")} />
              {!collapsed && <span className="truncate">{t(item.label)}</span>}
            </Link>
          );
          return (
            <div key={item.section}>
              {sep && <div className="mx-3 my-1.5 border-t border-border" />}
              {collapsed ? <Tooltip content={t(item.label)} side="right">{link}</Tooltip> : link}
            </div>
          );
        })}
      </div>
      <button onClick={toggle} className="flex h-8 items-center justify-center border-t border-border text-fg-subtle hover:text-fg" title={t("layout.collapseNav")}>
        {collapsed ? <ChevronsRight className="size-4" /> : <ChevronsLeft className="size-4" />}
      </button>
    </nav>
  );
}
