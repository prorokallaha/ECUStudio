"use client";
import { PageHeader } from "@/components/layout/page";
import { Card, CardHeader, KV, Kbd, Segmented } from "@/components/ui";
import { useInfo } from "@/hooks/use-analysis";
import { useUI } from "@/stores/ui";

const SHORTCUTS: [string, string][] = [
  ["Ctrl K", "Command palette"], ["Ctrl P", "Find map or address"], ["Ctrl S", "Save (projects autosave)"], ["Ctrl Z", "Undo hardware / decision change"],
  ["Ctrl Shift Z", "Redo"], ["F", "Fit dependency graph"], ["↑ ↓ ← →", "Move cursor in hex viewer"],
];

export function SettingsPage() {
  const info = useInfo();
  const ui = useUI();
  return (
    <div>
      <PageHeader title="Settings" />
      <div className="grid max-w-5xl grid-cols-1 gap-4 p-4 xl:grid-cols-2">
        <Card>
          <CardHeader title="Appearance" />
          <div className="space-y-2 p-3 text-xs">
            <div className="flex items-center justify-between"><span>Theme</span><Segmented value={ui.theme} onChange={ui.setTheme} options={[{ value: "dark", label: "Dark" }, { value: "light", label: "Light" }]} /></div>
            <div className="flex items-center justify-between"><span>Hex default endianness</span><Segmented value={ui.hexEndian} onChange={(v) => ui.setHex({ hexEndian: v })} options={[{ value: "Big", label: "Big" }, { value: "Little", label: "Little" }]} /></div>
          </div>
        </Card>
        <Card>
          <CardHeader title="Engine" />
          <div className="divide-y divide-border p-3 text-xs">
            <KV k="Analysis version">{info.data?.version ?? "…"}</KV>
            <KV k="AI provider">{info.data?.aiConfigured ? "Claude (configured)" : "not configured — set ANTHROPIC_API_KEY"}</KV>
            {info.data?.plugins.map((p) => <KV key={p.id} k={`Plugin ${p.id}`}>{p.name} · {p.families.join(", ")}{p.commonRail ? " · common rail" : " · unit injector"}</KV>)}
          </div>
        </Card>
        <Card>
          <CardHeader title="Keyboard" />
          <div className="divide-y divide-border p-3 text-xs">
            {SHORTCUTS.map(([k, v]) => <div key={k} className="flex justify-between py-1"><span className="text-fg-muted">{v}</span><Kbd>{k}</Kbd></div>)}
          </div>
        </Card>
        <Card>
          <CardHeader title="Data" />
          <div className="space-y-1.5 p-3 text-xs text-fg-muted">
            <p>Desktop: projects live in an embedded SQLite database in %LOCALAPPDATA%\ECUStudio. Server: PostgreSQL.</p>
            <p>Layout, bookmarks and viewer preferences are stored per browser profile.</p>
          </div>
        </Card>
      </div>
    </div>
  );
}
