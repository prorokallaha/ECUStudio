"use client";
import { useEffect, useState } from "react";
import { Magnet } from "lucide-react";
import { toast } from "sonner";
import type { AcquisitionSettings } from "@/types/library";
import { Badge, Button, Card, CardHeader, EmptyState, Input, Skeleton, Spinner } from "@/components/ui";
import { useAcquisitionSettings, useTorrentSources } from "@/hooks/use-acquisition";
import { fmtBytes } from "@/lib/format";
import { useT } from "@/i18n";

/** Settings → Definition library → Torrent sources, plus the automatic acquisition switches. */
export function TorrentSourcesCard({ className }: { className?: string }) {
  const t = useT();
  const sources = useTorrentSources();
  const list = sources.data?.sources ?? [];
  return (
    <Card className={className}>
      <CardHeader title={t("torrents.title")} subtitle={sources.data ? t("torrents.client", { v: sources.data.client }) : undefined} icon={<Magnet className="size-3.5" />} />
      {sources.isLoading && <div className="p-3"><Skeleton className="h-10" /></div>}
      {sources.data && !list.length && <EmptyState title={t("torrents.empty")} className="py-6" />}
      {list.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-xs" data-testid="torrent-sources">
            <thead className="text-left text-fg-subtle">
              <tr className="border-b border-border [&>th]:px-3 [&>th]:py-1.5 [&>th]:font-medium">
                <th>{t("torrents.colName")}</th><th>{t("torrents.colHash")}</th><th>{t("torrents.colFiles")}</th><th>{t("torrents.colIndexed")}</th>
                <th>{t("torrents.colDownloaded")}</th><th>{t("torrents.colDir")}</th><th>{t("torrents.colEnabled")}</th><th>{t("torrents.colPriority")}</th>
              </tr>
            </thead>
            <tbody>
              {list.map((s) => (
                <tr key={s.id} className="border-b border-border last:border-0 [&>td]:px-3 [&>td]:py-1.5">
                  <td className="max-w-56 truncate font-medium" title={s.name}>{s.name}</td>
                  <td className="num text-fg-muted" title={s.infoHash ?? undefined}>{s.infoHash ? `${s.infoHash.slice(0, 8)}…` : "—"}</td>
                  <td className="num whitespace-nowrap">{s.fileCount.toLocaleString()} <span className="text-fg-subtle">· {fmtBytes(s.totalBytes)}</span></td>
                  <td>{s.metadataPending ? <Badge tone="attn">{t("torrents.pending")}</Badge> : s.indexed ? <Badge tone="ok">{t("torrents.indexed")}</Badge> : <Badge tone="unknown">{t("torrents.notIndexed")}</Badge>}</td>
                  <td className="num">{s.downloaded}</td>
                  <td className="max-w-64 truncate text-fg-muted" title={s.downloadDirectory ?? undefined}>{s.downloadDirectory ?? "—"}</td>
                  <td><input type="checkbox" checked={s.enabled} onChange={(e) => sources.update.mutate({ id: s.id, enabled: e.target.checked })} aria-label={t("torrents.colEnabled")} /></td>
                  <td><Input type="number" className="h-6 w-16" defaultValue={s.priority} onBlur={(e) => { const p = Number(e.target.value); if (Number.isFinite(p) && p !== s.priority) sources.update.mutate({ id: s.id, priority: p }); }} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <MagnetForm />
      <p className="px-3 pb-3 text-[11px] text-fg-subtle">{t("torrents.noSeed")}</p>
    </Card>
  );
}

function MagnetForm() {
  const t = useT();
  const { addMagnet } = useTorrentSources();
  const [uri, setUri] = useState("");
  const submit = () => addMagnet.mutate({ uri: uri.trim() }, { onSuccess: () => { toast.success(t("torrents.magnetAdded")); setUri(""); } });
  return (
    <div className="flex items-center gap-2 border-t border-border p-3 text-xs">
      <Input value={uri} onChange={(e) => setUri(e.target.value)} placeholder={t("torrents.magnet")} className="flex-1" onKeyDown={(e) => e.key === "Enter" && uri.trim() && submit()} />
      <Button disabled={!uri.trim().startsWith("magnet:") || addMagnet.isPending} onClick={submit}>{addMagnet.isPending && <Spinner />}{t("torrents.addMagnet")}</Button>
    </div>
  );
}

export function AcquisitionSettingsCard({ className }: { className?: string }) {
  const t = useT();
  const settings = useAcquisitionSettings();
  const [draft, setDraft] = useState<AcquisitionSettings | null>(null);
  useEffect(() => { if (settings.data) setDraft(settings.data); }, [settings.data]);
  if (!draft) return <Card className={className}><CardHeader title={t("torrents.settings")} /><div className="p-3"><Skeleton className="h-16" /></div></Card>;
  const save = (next: AcquisitionSettings) => { setDraft(next); settings.save.mutate(next, { onSuccess: () => toast.success(t("torrents.saved")) }); };
  const Toggle = ({ k, label }: { k: "autoAcquire" | "autoDownloadExact" | "autoTestProbable"; label: string }) => (
    <label className="flex items-center justify-between gap-3 py-1">
      <span>{label}</span>
      <input type="checkbox" checked={draft[k]} onChange={(e) => save({ ...draft, [k]: e.target.checked })} />
    </label>
  );
  return (
    <Card className={className}>
      <CardHeader title={t("torrents.settings")} />
      <div className="divide-y divide-border p-3 text-xs" data-testid="acquisition-settings">
        <Toggle k="autoAcquire" label={t("torrents.autoAcquire")} />
        <Toggle k="autoDownloadExact" label={t("torrents.autoExact")} />
        <Toggle k="autoTestProbable" label={t("torrents.autoProbable")} />
        <label className="flex items-center justify-between gap-3 py-1">
          <span>{t("torrents.maxDownloads")}</span>
          <Input type="number" min={1} max={8} className="h-6 w-16" value={draft.maxConcurrentDownloads}
            onChange={(e) => setDraft({ ...draft, maxConcurrentDownloads: Number(e.target.value) })} onBlur={() => save(draft)} />
        </label>
        <label className="block space-y-1 py-1">
          <span>{t("torrents.cachePath")}</span>
          <Input value={draft.cachePath ?? ""} placeholder={t("torrents.cachePlaceholder")} onChange={(e) => setDraft({ ...draft, cachePath: e.target.value || null })} onBlur={() => save(draft)} />
        </label>
      </div>
    </Card>
  );
}
