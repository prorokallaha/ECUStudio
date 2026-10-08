"use client";
import { useDeferredValue, useRef, useState } from "react";
import { FolderPlus, Magnet, Pencil, RefreshCw, Search, Trash2 } from "lucide-react";
import { toast } from "sonner";
import type { LibraryFormat, LibraryRootStatus, ScanState } from "@/types/library";
import { LIBRARY_FORMATS } from "@/types/library";
import type { Tone } from "@/lib/colors";
import { useLibraryEntries, useLibraryMutations, useLibraryRoots, errorText } from "@/hooks/use-library";
import { PageHeader } from "@/components/layout/page";
import { Badge, Button, Card, CardBody, CardHeader, Dialog, EmptyState, Input, Select, Skeleton, Spinner, Tooltip } from "@/components/ui";
import { AvailabilityBadge, FormatBadge, Identifiers } from "./badges";
import { fmtBytes, fmtDate } from "@/lib/format";
import { useT } from "@/i18n";

const PAGE = 50;
const stateTone: Record<ScanState, Tone> = { Idle: "ok", Scanning: "calc", Failed: "danger" };

export function LibraryPage() {
  const t = useT();
  return (
    <div>
      <PageHeader title={t("library.title")} subtitle={t("library.subtitle")} />
      <div className="grid grid-cols-1 gap-4 p-4 xl:grid-cols-2">
        <AddDirectoryCard />
        <AddTorrentCard />
        <RootsCard className="xl:col-span-2" />
        <EntriesCard className="xl:col-span-2" />
      </div>
    </div>
  );
}

function AddDirectoryCard() {
  const t = useT();
  const { addRoot } = useLibraryMutations();
  const [path, setPath] = useState("");
  const [name, setName] = useState("");
  const submit = () => addRoot.mutate({ path: path.trim(), name: name.trim() || null }, {
    onSuccess: (r) => { toast.success(t("library.rootAdded", { name: r.name ?? r.path })); setPath(""); setName(""); },
  });
  return (
    <Card>
      <CardHeader title={t("library.addDirectory")} icon={<FolderPlus className="size-3.5" />} />
      <CardBody className="space-y-2 text-xs">
        <label className="block space-y-1"><span className="text-fg-subtle">{t("library.path")}</span><Input value={path} onChange={(e) => setPath(e.target.value)} placeholder={t("library.pathPlaceholder")} onKeyDown={(e) => e.key === "Enter" && path.trim() && submit()} /></label>
        <label className="block space-y-1"><span className="text-fg-subtle">{t("library.nameOptional")}</span><Input value={name} onChange={(e) => setName(e.target.value)} /></label>
        <p className="text-fg-muted">{t("library.directoryHint")}</p>
        <div className="flex justify-end"><Button variant="primary" disabled={!path.trim() || addRoot.isPending} onClick={submit}>{addRoot.isPending && <Spinner />}{t("library.add")}</Button></div>
      </CardBody>
    </Card>
  );
}

function AddTorrentCard() {
  const t = useT();
  const { addTorrent } = useLibraryMutations();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [downloadPath, setDownloadPath] = useState("");
  const submit = () => file && addTorrent.mutate({ file, downloadPath: downloadPath.trim() || null }, {
    onSuccess: (r) => { toast.success(t("library.rootAdded", { name: r.name ?? r.path })); setFile(null); setDownloadPath(""); },
  });
  return (
    <Card>
      <CardHeader title={t("library.addTorrent")} icon={<Magnet className="size-3.5" />} />
      <CardBody className="space-y-2 text-xs">
        <div className="space-y-1">
          <span className="text-fg-subtle">{t("library.torrentFile")}</span>
          <div className="flex items-center gap-2">
            <input ref={input} type="file" hidden accept=".torrent" onChange={(e) => { setFile(e.target.files?.[0] ?? null); e.target.value = ""; }} />
            <Button size="sm" onClick={() => input.current?.click()}>{t("library.chooseFile")}</Button>
            <span className="truncate text-fg-muted">{file?.name ?? t("library.noFile")}</span>
          </div>
        </div>
        <label className="block space-y-1"><span className="text-fg-subtle">{t("library.downloadPath")}</span><Input value={downloadPath} onChange={(e) => setDownloadPath(e.target.value)} placeholder={t("library.downloadPathPlaceholder")} /></label>
        <p className="text-fg-muted">{t("library.torrentHint")}</p>
        <div className="flex justify-end"><Button variant="primary" disabled={!file || addTorrent.isPending} onClick={submit}>{addTorrent.isPending && <Spinner />}{t("library.add")}</Button></div>
      </CardBody>
    </Card>
  );
}

function RootsCard({ className }: { className?: string }) {
  const t = useT();
  const roots = useLibraryRoots();
  const { scan, removeRoot } = useLibraryMutations();
  const [editing, setEditing] = useState<LibraryRootStatus | null>(null);
  const list = roots.data ?? [];
  return (
    <Card className={className}>
      <CardHeader title={t("library.roots")} subtitle={roots.data ? t("library.rootsCount", { n: list.length }) : undefined} />
      {roots.isLoading && <div className="p-3"><Skeleton className="h-12" /></div>}
      {roots.error && <div className="p-3 text-xs text-danger">{errorText(roots.error)}</div>}
      {roots.data && !list.length && <EmptyState title={t("library.rootsEmpty")} className="py-8" />}
      {list.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
              <tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium">
                <th>{t("library.colSource")}</th><th>{t("library.colState")}</th><th>{t("library.colFiles")}</th><th>{t("library.colDefinitions")}</th>
                <th>{t("library.colBinaries")}</th><th>{t("library.colUnavailable")}</th><th>{t("library.colSize")}</th><th>{t("library.colLastScan")}</th><th />
              </tr>
            </thead>
            <tbody>
              {list.map((s) => {
                const r = s.root;
                return (
                  <tr key={r.id} className="border-t border-border align-top [&>td]:px-3 [&>td]:py-1.5">
                    <td className="max-w-[28rem]">
                      <div className="flex items-center gap-1.5"><Badge tone={r.kind === "Torrent" ? "ai" : "calc"}>{t.tx(`library.kind.${r.kind}`, r.kind)}</Badge><span className="truncate font-medium">{r.name ?? r.path}</span></div>
                      <div className="num truncate text-[10px] text-fg-subtle" title={r.path}>{r.path}</div>
                      {r.kind === "Torrent" && <div className="num truncate text-[10px] text-fg-subtle">{t("library.downloadDir", { v: r.downloadPath ?? t("library.notSet") })}</div>}
                      {r.lastError && <div className="mt-0.5 text-[11px] text-danger">{r.lastError}</div>}
                    </td>
                    <td><Badge tone={stateTone[s.state]}>{s.state === "Scanning" && <Spinner className="size-2.5 border" />}{t.tx(`library.state.${s.state}`, s.state)}</Badge></td>
                    <td className="num">{r.fileCount}</td>
                    <td className="num">{s.definitions}</td>
                    <td className="num">{s.binaries}</td>
                    <td className="num">{s.unavailable ? <span className="text-unknown">{s.unavailable}</span> : 0}</td>
                    <td className="num text-fg-muted">{fmtBytes(r.totalBytes)}</td>
                    <td className="text-fg-muted">{r.lastScanAt ? fmtDate(r.lastScanAt) : t("library.never")}</td>
                    <td className="whitespace-nowrap text-right">
                      <Tooltip content={t("library.rescan")}><Button size="icon" variant="ghost" disabled={s.state === "Scanning" || scan.isPending} onClick={() => scan.mutate(r.id, { onSuccess: () => toast(t("library.scanStarted")) })}><RefreshCw className="size-3.5" /></Button></Tooltip>
                      <Tooltip content={t("library.edit")}><Button size="icon" variant="ghost" onClick={() => setEditing(s)}><Pencil className="size-3.5" /></Button></Tooltip>
                      <Tooltip content={t("library.remove")}><Button size="icon" variant="ghost" onClick={() => { if (confirm(t("library.confirmRemove", { name: r.name ?? r.path }))) removeRoot.mutate(r.id); }}><Trash2 className="size-3.5" /></Button></Tooltip>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {editing && <EditRootDialog status={editing} onClose={() => setEditing(null)} />}
    </Card>
  );
}

function EditRootDialog({ status, onClose }: { status: LibraryRootStatus; onClose: () => void }) {
  const t = useT();
  const { updateRoot } = useLibraryMutations();
  const r = status.root;
  const [name, setName] = useState(r.name ?? "");
  const [downloadPath, setDownloadPath] = useState(r.downloadPath ?? "");
  const save = () => updateRoot.mutate(
    { rootId: r.id, name: name.trim() || null, ...(r.kind === "Torrent" ? { downloadPath: downloadPath.trim() || null } : {}) },
    { onSuccess: onClose },
  );
  return (
    <Dialog
      open
      onClose={onClose}
      title={t("library.editTitle")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("common.cancel")}</Button><Button variant="primary" disabled={updateRoot.isPending} onClick={save}>{t("common.save")}</Button></>}
    >
      <div className="space-y-2 text-xs">
        <div className="num break-all text-fg-subtle">{r.path}</div>
        <label className="block space-y-1"><span className="text-fg-subtle">{t("library.name")}</span><Input value={name} onChange={(e) => setName(e.target.value)} /></label>
        {r.kind === "Torrent" && (
          <>
            <label className="block space-y-1"><span className="text-fg-subtle">{t("library.downloadPath")}</span><Input value={downloadPath} onChange={(e) => setDownloadPath(e.target.value)} placeholder={t("library.downloadPathPlaceholder")} /></label>
            <p className="text-fg-muted">{t("library.torrentHint")}</p>
          </>
        )}
      </div>
    </Dialog>
  );
}

function EntriesCard({ className }: { className?: string }) {
  const t = useT();
  const roots = useLibraryRoots();
  const [q, setQ] = useState("");
  const [format, setFormat] = useState<LibraryFormat | "">("");
  const [definitionsOnly, setDefinitionsOnly] = useState(false);
  const [offset, setOffset] = useState(0);
  const deferredQ = useDeferredValue(q.trim());
  const entries = useLibraryEntries({ q: deferredQ, format, definitionsOnly, offset, limit: PAGE });
  const rootName = (id: string) => {
    const r = roots.data?.find((s) => s.root.id === id)?.root;
    return r ? r.name ?? r.path : "—";
  };
  const total = entries.data?.total ?? 0;
  const rows = entries.data?.entries ?? [];
  return (
    <Card className={className}>
      <CardHeader
        title={t("library.entries")}
        subtitle={entries.data ? t("library.total", { n: total }) : undefined}
        actions={entries.isFetching ? <Spinner className="text-fg-subtle" /> : null}
      />
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
        <div className="relative min-w-64 flex-1">
          <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-subtle" />
          <Input value={q} onChange={(e) => { setQ(e.target.value); setOffset(0); }} placeholder={t("library.search")} className="pl-7" />
        </div>
        <Select value={format} onChange={(e) => { setFormat(e.target.value as LibraryFormat | ""); setOffset(0); }}>
          <option value="">{t("library.allFormats")}</option>
          {LIBRARY_FORMATS.map((f) => <option key={f} value={f}>{t.tx(`library.format.${f}`, f)}</option>)}
        </Select>
        <label className="flex items-center gap-1 whitespace-nowrap text-[11px] text-fg-muted">
          <input type="checkbox" checked={definitionsOnly} onChange={(e) => { setDefinitionsOnly(e.target.checked); setOffset(0); }} />{t("library.definitionsOnly")}
        </label>
      </div>
      {entries.isLoading && <div className="p-3"><Skeleton className="h-24" /></div>}
      {entries.error && <div className="p-3 text-xs text-danger">{errorText(entries.error)}</div>}
      {entries.data && !rows.length && <EmptyState title={t("library.noEntries")} className="py-8" />}
      {rows.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
              <tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium">
                <th>{t("library.colFile")}</th><th>{t("library.colFormat")}</th><th>{t("library.colIdentifiers")}</th><th>{t("library.colObjects")}</th>
                <th>{t("library.colSize")}</th><th>{t("library.colAvailability")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((e) => (
                <tr key={e.id} className="border-t border-border align-top [&>td]:px-3 [&>td]:py-1.5">
                  <td className="max-w-[30rem]">
                    <div className="truncate font-medium" title={e.relativePath}>{e.title ?? e.relativePath.split(/[\\/]/).pop()}</div>
                    <div className="num truncate text-[10px] text-fg-subtle" title={e.relativePath}>{rootName(e.rootId)} · {e.relativePath}</div>
                    {e.error && <div className="text-[11px] text-danger">{e.error}</div>}
                  </td>
                  <td><FormatBadge format={e.format} /></td>
                  <td className="text-[11px]">
                    <Identifiers ids={e.identifiers} />
                    {!e.identifiers.isEmpty && !e.contentIdentified && <div className="text-[10px] text-fg-subtle">{t("library.byName")}</div>}
                  </td>
                  <td className="num">{e.objectCount ?? "—"}</td>
                  <td className="num text-fg-muted">{fmtBytes(e.size)}</td>
                  <td><AvailabilityBadge entry={e} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {total > PAGE && (
        <div className="flex items-center justify-end gap-2 border-t border-border px-3 py-2 text-xs text-fg-muted">
          <span>{t("library.page", { from: offset + 1, to: Math.min(offset + PAGE, total), total })}</span>
          <Button size="xs" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("library.prev")}</Button>
          <Button size="xs" disabled={offset + PAGE >= total} onClick={() => setOffset(offset + PAGE)}>{t("library.next")}</Button>
        </div>
      )}
    </Card>
  );
}
