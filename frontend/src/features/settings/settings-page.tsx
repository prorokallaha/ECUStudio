"use client";
import { PageHeader } from "@/components/layout/page";
import { Card, CardHeader, KV, Kbd, Segmented } from "@/components/ui";
import { useInfo } from "@/hooks/use-analysis";
import { useUI } from "@/stores/ui";
import { LANGS, useLang, useT, type TKey } from "@/i18n";
import { AcquisitionSettingsCard, TorrentSourcesCard } from "./torrent-sources";

const SHORTCUTS: [string, TKey][] = [
  ["Ctrl K", "settings.shortcuts.palette"], ["Ctrl P", "settings.shortcuts.find"], ["Ctrl S", "settings.shortcuts.save"], ["Ctrl Z", "settings.shortcuts.undo"],
  ["Ctrl Shift Z", "settings.shortcuts.redo"], ["F", "settings.shortcuts.fit"], ["↑ ↓ ← →", "settings.shortcuts.cursor"],
];

export function SettingsPage() {
  const info = useInfo();
  const ui = useUI();
  const t = useT();
  const { lang, setLang } = useLang();
  return (
    <div>
      <PageHeader title={t("settings.title")} />
      <div className="grid max-w-5xl grid-cols-1 gap-4 p-4 xl:grid-cols-2">
        <TorrentSourcesCard className="xl:col-span-2" />
        <AcquisitionSettingsCard />
        <Card>
          <CardHeader title={t("settings.appearance")} />
          <div className="space-y-2 p-3 text-xs">
            <div className="flex items-center justify-between"><span>{t("settings.language")}</span><Segmented value={lang} onChange={setLang} options={LANGS} /></div>
            <div className="flex items-center justify-between"><span>{t("settings.theme")}</span><Segmented value={ui.theme} onChange={ui.setTheme} options={[{ value: "dark", label: t("settings.dark") }, { value: "light", label: t("settings.light") }]} /></div>
            <div className="flex items-center justify-between"><span>{t("settings.hexEndian")}</span><Segmented value={ui.hexEndian} onChange={(v) => ui.setHex({ hexEndian: v })} options={[{ value: "Big", label: t("endian.Big") }, { value: "Little", label: t("endian.Little") }]} /></div>
          </div>
        </Card>
        <Card>
          <CardHeader title={t("settings.engine")} />
          <div className="divide-y divide-border p-3 text-xs">
            <KV k={t("settings.analysisVersion")}>{info.data?.version ?? "…"}</KV>
            <KV k={t("settings.aiProvider")}>{info.data?.aiConfigured ? t("settings.aiConfigured") : t("settings.aiMissing")}</KV>
            {info.data?.plugins.map((p) => <KV key={p.id} k={t("settings.plugin", { id: p.id })}>{p.name} · {p.families.join(", ")} · {p.commonRail ? t("settings.commonRail") : t("settings.unitInjector")}</KV>)}
          </div>
        </Card>
        <Card>
          <CardHeader title={t("settings.keyboard")} />
          <div className="divide-y divide-border p-3 text-xs">
            {SHORTCUTS.map(([k, v]) => <div key={k} className="flex justify-between py-1"><span className="text-fg-muted">{t(v)}</span><Kbd>{k}</Kbd></div>)}
          </div>
        </Card>
        <Card>
          <CardHeader title={t("settings.data")} />
          <div className="space-y-1.5 p-3 text-xs text-fg-muted">
            <p>{t("settings.dataDesktop")}</p>
            <p>{t("settings.dataBrowser")}</p>
          </div>
        </Card>
      </div>
    </div>
  );
}
