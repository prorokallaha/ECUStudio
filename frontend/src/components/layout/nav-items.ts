import {
  Activity, Binary, Bot, Car, Cpu, FileBarChart, Gauge, GitCompareArrows, LayoutDashboard, Map, Network, Puzzle,
  ScrollText, Settings, ShieldAlert, type LucideIcon,
} from "lucide-react";
import type { Section } from "@/hooks/use-workspace";
import type { TKey } from "@/i18n";

export const NAV: { section: Section; label: TKey; icon: LucideIcon; group: "overview" | "calibration" | "physics" | "review" | "system"; needsReport?: boolean }[] = [
  { section: "dashboard", label: "nav.dashboard", icon: LayoutDashboard, group: "overview" },
  { section: "vehicle", label: "nav.vehicle", icon: Car, group: "overview" },
  { section: "ecu", label: "nav.ecu", icon: Cpu, group: "overview", needsReport: true },
  { section: "binary", label: "nav.binary", icon: Binary, group: "calibration", needsReport: true },
  { section: "maps", label: "nav.maps", icon: Map, group: "calibration", needsReport: true },
  { section: "diff", label: "nav.diff", icon: GitCompareArrows, group: "calibration", needsReport: true },
  { section: "dependencies", label: "nav.dependencies", icon: Network, group: "calibration", needsReport: true },
  { section: "simulation", label: "nav.simulation", icon: Activity, group: "physics", needsReport: true },
  { section: "dyno", label: "nav.dyno", icon: Gauge, group: "physics", needsReport: true },
  { section: "components", label: "nav.components", icon: Puzzle, group: "physics", needsReport: true },
  { section: "risks", label: "nav.risks", icon: ShieldAlert, group: "review", needsReport: true },
  { section: "logs", label: "nav.logs", icon: ScrollText, group: "review" },
  { section: "ai", label: "nav.ai", icon: Bot, group: "review", needsReport: true },
  { section: "reports", label: "nav.reports", icon: FileBarChart, group: "review", needsReport: true },
  { section: "settings", label: "nav.settings", icon: Settings, group: "system" },
];
