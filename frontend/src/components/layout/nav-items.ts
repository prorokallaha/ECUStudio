import {
  Activity, Binary, Bot, Car, Cpu, FileBarChart, Gauge, GitCompareArrows, LayoutDashboard, Map, Network, Puzzle,
  ScrollText, Settings, ShieldAlert, type LucideIcon,
} from "lucide-react";
import type { Section } from "@/hooks/use-workspace";

export const NAV: { section: Section; label: string; icon: LucideIcon; group: "overview" | "calibration" | "physics" | "review" | "system"; needsReport?: boolean }[] = [
  { section: "dashboard", label: "Dashboard", icon: LayoutDashboard, group: "overview" },
  { section: "vehicle", label: "Vehicle", icon: Car, group: "overview" },
  { section: "ecu", label: "ECU", icon: Cpu, group: "overview", needsReport: true },
  { section: "binary", label: "Binary", icon: Binary, group: "calibration", needsReport: true },
  { section: "maps", label: "Maps", icon: Map, group: "calibration", needsReport: true },
  { section: "diff", label: "Diff", icon: GitCompareArrows, group: "calibration", needsReport: true },
  { section: "dependencies", label: "Dependencies", icon: Network, group: "calibration", needsReport: true },
  { section: "simulation", label: "Simulation", icon: Activity, group: "physics", needsReport: true },
  { section: "dyno", label: "Virtual Dyno", icon: Gauge, group: "physics", needsReport: true },
  { section: "components", label: "Components", icon: Puzzle, group: "physics", needsReport: true },
  { section: "risks", label: "Risks", icon: ShieldAlert, group: "review", needsReport: true },
  { section: "logs", label: "Logs", icon: ScrollText, group: "review" },
  { section: "ai", label: "AI Analyst", icon: Bot, group: "review", needsReport: true },
  { section: "reports", label: "Reports", icon: FileBarChart, group: "review", needsReport: true },
  { section: "settings", label: "Settings", icon: Settings, group: "system" },
];
