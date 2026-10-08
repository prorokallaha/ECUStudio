"use client";
import { useEffect, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, FileUp, Link2, Link2Off, Play } from "lucide-react";
import { toast } from "sonner";
import type { DefinitionPreview, DefinitionSource, LibraryEntry, ProjectDefinition } from "@/types/library";
import { libraryApi } from "@/services/api-library";
import { ApiError } from "@/services/api";
import { keys, useProject, useRunAnalysis } from "@/hooks/use-analysis";
import { errorText, useUnbindDefinition } from "@/hooks/use-library";
import { Badge, Button, Dialog, KV, SectionTitle, Spinner } from "@/components/ui";
import { CompatibilityBadge, CompatibilityDetails, FormatBadge, Identifiers } from "./badges";
import { fmtDate } from "@/lib/format";
import { useT } from "@/i18n";

const ACCEPT = ".a2l,.xdf,.json,.dam,.damos,.ols,.kp";

/**
 * "Импорт definition": upload an A2L / XDF / .ecudef.json (or pick a library entry), check it against the project's
 * binary, then bind. An incompatible definition is only bound after an explicit "bind anyway".
 */
export function DefinitionImportDialog({ open, onClose, projectId, entry }: { open: boolean; onClose: () => void; projectId: string; entry?: LibraryEntry | null }) {
  const t = useT();
  const qc = useQueryClient();
  const project = useProject(projectId);
  const input = useRef<HTMLInputElement>(null);
  const [source, setSource] = useState<DefinitionSource | null>(null);
  const [serverRefused, setServerRefused] = useState(false);

  const preview = useMutation({
    mutationFn: (s: DefinitionSource) => (s.kind === "file" ? libraryApi.definition.preview(projectId, s.file) : libraryApi.definition.previewLibrary(projectId, s.entry.id)),
  });
  const bind = useMutation({
    mutationFn: ({ s, force }: { s: DefinitionSource; force: boolean }) =>
      s.kind === "file" ? libraryApi.definition.bind(projectId, s.file, force) : libraryApi.definition.bindLibrary(projectId, s.entry.id, force),
    onSuccess: (p) => {
      qc.setQueryData(keys.project(projectId), p);
      toast.success(t("library.bound", { name: p.definition?.name ?? "" }), { description: t("library.rerunHint") });
      close();
    },
    onError: (e) => {
      if (e instanceof ApiError && e.code === "DEFINITION_INCOMPATIBLE") setServerRefused(true);
      else toast.error(errorText(e));
    },
  });

  const pick = (s: DefinitionSource) => {
    setSource(s);
    setServerRefused(false);
    bind.reset();
    preview.mutate(s);
  };

  // Opened from a library match: preview that entry straight away.
  useEffect(() => {
    if (open && entry) pick({ kind: "library", entry });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, entry?.id]);

  const close = () => {
    setSource(null);
    setServerRefused(false);
    preview.reset();
    bind.reset();
    onClose();
  };

  const p = preview.data;
  const incompatible = p?.compatibility?.status === "Incompatible" || serverRefused;
  const canBind = !!source && !!p && p.importable && !p.error && !preview.isPending;

  return (
    <Dialog
      open={open}
      onClose={close}
      title={t("library.importTitle")}
      className="max-w-2xl"
      footer={<>
        <Button variant="ghost" onClick={close}>{t("common.cancel")}</Button>
        {incompatible
          ? <Button variant="danger" disabled={!canBind || bind.isPending} onClick={() => source && bind.mutate({ s: source, force: true })}><AlertTriangle className="size-3.5" />{t("library.bindAnyway")}</Button>
          : <Button variant="primary" disabled={!canBind || bind.isPending} onClick={() => source && bind.mutate({ s: source, force: false })}><Link2 className="size-3.5" />{t("library.bind")}</Button>}
      </>}
    >
      <div className="max-h-[62vh] space-y-4 overflow-y-auto text-xs">
        {project.data && <CurrentDefinition projectId={projectId} definition={project.data.definition ?? null} />}

        <div className="space-y-2">
          <SectionTitle>{t("library.newDefinition")}</SectionTitle>
          <div className="flex items-center gap-2">
            <input ref={input} type="file" hidden accept={ACCEPT} onChange={(e) => { const f = e.target.files?.[0]; if (f) pick({ kind: "file", file: f }); e.target.value = ""; }} />
            <Button size="sm" onClick={() => input.current?.click()}><FileUp className="size-3.5" />{t("library.chooseDefinition")}</Button>
            {source && <span className="truncate text-fg-muted">{source.kind === "file" ? source.file.name : source.entry.title ?? source.entry.relativePath}</span>}
          </div>
          <p className="text-fg-subtle">{t("library.importHint")}</p>
        </div>

        {preview.isPending && <div className="flex items-center gap-2 text-fg-muted"><Spinner />{t("library.checking")}</div>}
        {preview.error && <div className="rounded border border-danger/35 bg-danger/10 px-2.5 py-2 text-danger">{errorText(preview.error)}</div>}
        {p && <PreviewView p={p} />}

        {incompatible && p && !p.error && (
          <div className="flex gap-2 rounded border border-danger/35 bg-danger/10 px-2.5 py-2 text-danger">
            <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
            <div>{serverRefused && <div className="font-medium">{t("library.serverIncompatible")}</div>}{t("library.incompatibleWarn")}</div>
          </div>
        )}
      </div>
    </Dialog>
  );
}

function PreviewView({ p }: { p: DefinitionPreview }) {
  const t = useT();
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{p.name}</span>
        <FormatBadge format={p.format} />
        {!p.importable && <Badge tone="warn">{t("library.notImportable")}</Badge>}
        {p.importable && <span className="text-fg-muted">{t("library.mapCount")}: <span className="num text-fg">{p.mapCount}</span></span>}
      </div>
      {p.error && <div className="rounded border border-warn/35 bg-warn/10 px-2.5 py-2 text-warn">{p.error}</div>}

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div className="rounded border border-border p-2.5">
          <SectionTitle className="mb-1">{t("library.binarySide")}</SectionTitle>
          {p.binary ? (
            <div className="divide-y divide-border">
              <KV k={t("library.file")}>{p.binary}</KV>
              <KV k={t("library.idFamily")}>{p.ecuFamily ?? t("common.unknown")}</KV>
              <KV k={t("library.idSw")}><span className="num">{p.binarySoftware ?? t("common.unknown")}</span></KV>
              <KV k={t("library.idHw")}><span className="num">{p.binaryHardware ?? t("common.unknown")}</span></KV>
            </div>
          ) : <div className="text-fg-muted">{t("library.noBinary")}</div>}
        </div>
        <div className="rounded border border-border p-2.5">
          <SectionTitle className="mb-1">{t("library.definitionSide")}</SectionTitle>
          <Identifiers ids={p.identifiers} />
        </div>
      </div>

      {p.compatibility && (
        <div className="space-y-1">
          <SectionTitle>{t("library.compatibility")}</SectionTitle>
          <CompatibilityDetails c={p.compatibility} />
        </div>
      )}
      {p.notes.length > 0 && (
        <div className="space-y-1">
          <SectionTitle>{t("library.notes")}</SectionTitle>
          <ul className="list-disc space-y-0.5 pl-4 text-fg-muted">{p.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>
        </div>
      )}
    </div>
  );
}

/** The definition currently bound to the project, with unbind and a shortcut to re-run the analysis. */
export function CurrentDefinition({ projectId, definition }: { projectId: string; definition: ProjectDefinition | null }) {
  const t = useT();
  const unbind = useUnbindDefinition(projectId);
  const run = useRunAnalysis();
  return (
    <div className="space-y-1.5">
      <SectionTitle>{t("library.current")}</SectionTitle>
      {definition ? (
        <div className="space-y-1.5 rounded border border-border p-2.5">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium">{definition.name}</span>
            <FormatBadge format={definition.format} />
            <Badge tone="unknown">{t.tx(`library.origin.${definition.origin}`, definition.origin)}</Badge>
            {definition.compatibility && <CompatibilityBadge status={definition.compatibility.status} />}
            <span className="ml-auto flex gap-1">
              <Button size="xs" variant="ghost" disabled={run.isPending} onClick={() => run.mutate({ projectId })}><Play className="size-3" />{t("common.rerun")}</Button>
              <Button size="xs" variant="danger" disabled={unbind.isPending} onClick={() => { if (confirm(t("library.confirmUnbind", { name: definition.name }))) unbind.mutate(undefined, { onSuccess: () => toast.success(t("library.unbound")) }); }}>
                <Link2Off className="size-3" />{t("library.unbind")}
              </Button>
            </span>
          </div>
          <div className="text-fg-muted">
            {t("library.mapCount")}: <span className="num text-fg">{definition.mapCount}</span> · {t("library.boundAt", { date: fmtDate(definition.boundAt) })}
            {definition.checkedAgainst && <> · {t("library.checkedAgainst", { name: definition.checkedAgainst })}</>}
          </div>
          <Identifiers ids={definition.identifiers} />
          {definition.compatibility && definition.compatibility.reasons.length > 0 && (
            <ul className="list-disc space-y-0.5 pl-4 text-fg-muted">{definition.compatibility.reasons.map((r, i) => <li key={i}>{r}</li>)}</ul>
          )}
        </div>
      ) : <div className="text-fg-muted">{t("library.noCurrent")}</div>}
    </div>
  );
}
