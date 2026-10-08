"use client";
import { forwardRef, memo, useState, type ReactNode } from "react";
import { AlertTriangle, Check, ClipboardPaste, Copy, Equal, Percent, Plus, Redo2, Spline, TrendingUp, Undo2, Waves, X, RotateCcw } from "lucide-react";
import type { MapEditPreview, MapOperationKind } from "@/types/maps-edit";
import { Badge, Button, Spinner } from "@/components/ui";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

export interface ToolbarProps {
  count: number;
  disabled: boolean;
  busy: boolean;
  canUndo: boolean;
  canRedo: boolean;
  canRevert: boolean;
  onOp: (kind: MapOperationKind, operand: number) => void;
  onCopy: () => void;
  onPaste: () => void;
  onRevert: () => void;
  onUndo: () => void;
  onRedo: () => void;
  /** Right-hand slot (save as a new file). */
  extra?: ReactNode;
}

/** Parses the operand box: "12" set, "+5" add, "+-5" add −5, "*1.1" or "x1.1" multiply, "5%" percent, "=−5" set. */
export function parseOperand(text: string): { kind: MapOperationKind; operand: number } | null {
  const s = text.trim().replace(",", ".").replace("−", "-");
  if (!s) return null;
  const num = (x: string) => { const v = Number(x); return x.trim() !== "" && Number.isFinite(v) ? v : null; };
  let m: number | null;
  if (s.endsWith("%") && (m = num(s.slice(0, -1).replace(/^\+/, ""))) !== null) return { kind: "Percent", operand: m };
  if ((s[0] === "*" || s[0] === "x" || s[0] === "×") && (m = num(s.slice(1))) !== null) return { kind: "Multiply", operand: m };
  if (s[0] === "+" && (m = num(s.slice(1))) !== null) return { kind: "Add", operand: m };
  if (s[0] === "=" && (m = num(s.slice(1))) !== null) return { kind: "Set", operand: m };
  if ((m = num(s)) !== null) return { kind: "Set", operand: m };
  return null;
}

/** Operations toolbar of the map table. Every button only requests a preview; nothing is applied from here. */
export const MapEditToolbar = memo(forwardRef<HTMLInputElement, ToolbarProps & { operand: string; setOperand: (v: string) => void }>(function MapEditToolbar(p, ref) {
  const t = useT();
  const parsed = parseOperand(p.operand);
  const num = parsed?.operand ?? null;
  const off = p.disabled || p.busy || p.count === 0;
  const run = (kind: MapOperationKind) => { if (kind === "Interpolate" || kind === "Linearize" || kind === "Smooth" || num !== null) p.onOp(kind, num ?? 0); };
  return (
    <div className="flex flex-wrap items-center gap-1 border-b border-border px-2 py-1.5">
      <input
        ref={ref}
        value={p.operand}
        onChange={(e) => p.setOperand(e.target.value)}
        onKeyDown={(e) => { if (e.key === "Enter" && parsed && !off) { e.preventDefault(); p.onOp(parsed.kind, parsed.operand); } }}
        placeholder={t("mapEditor.operandPlaceholder")}
        title={t("mapEditor.operandHint")}
        disabled={p.disabled}
        className={cn("num h-6 w-28 rounded border bg-bg px-1.5 text-[11px] outline-none focus:border-calc", p.operand && !parsed ? "border-danger" : "border-border")}
      />
      <Btn title={t("mapEditor.opSet")} disabled={off || num === null} onClick={() => run("Set")}><Equal className="size-3" /></Btn>
      <Btn title={t("mapEditor.opAdd")} disabled={off || num === null} onClick={() => run("Add")}><Plus className="size-3" /></Btn>
      <Btn title={t("mapEditor.opMultiply")} disabled={off || num === null} onClick={() => run("Multiply")}><X className="size-3" /></Btn>
      <Btn title={t("mapEditor.opPercent")} disabled={off || num === null} onClick={() => run("Percent")}><Percent className="size-3" /></Btn>
      <Sep />
      <Btn title={t("mapEditor.opInterpolate")} disabled={off || p.count < 2} onClick={() => run("Interpolate")}><Spline className="size-3" />{t("mapEditor.interpolate")}</Btn>
      <Btn title={t("mapEditor.opLinearize")} disabled={off || p.count < 2} onClick={() => run("Linearize")}><TrendingUp className="size-3" />{t("mapEditor.linearize")}</Btn>
      <Btn title={t("mapEditor.opSmooth")} disabled={off || p.count < 2} onClick={() => run("Smooth")}><Waves className="size-3" />{t("mapEditor.smooth")}</Btn>
      <Sep />
      <Btn title={t("mapEditor.copy")} disabled={p.count === 0} onClick={p.onCopy}><Copy className="size-3" /></Btn>
      <Btn title={t("mapEditor.paste")} disabled={p.disabled || p.busy} onClick={p.onPaste}><ClipboardPaste className="size-3" /></Btn>
      <Sep />
      <Btn title={t("mapEditor.undo")} disabled={p.disabled || p.busy || !p.canUndo} onClick={p.onUndo}><Undo2 className="size-3" /></Btn>
      <Btn title={t("mapEditor.redo")} disabled={p.disabled || p.busy || !p.canRedo} onClick={p.onRedo}><Redo2 className="size-3" /></Btn>
      <Btn title={t("mapEditor.revertHint")} disabled={p.disabled || p.busy || !p.canRevert} onClick={p.onRevert}><RotateCcw className="size-3" />{t("mapEditor.revert")}</Btn>
      <span className="ml-auto flex items-center gap-1.5 text-[10px] text-fg-subtle">
        {p.busy && <Spinner className="size-3" />}
        {t("mapEditor.selected", { n: p.count })}
      </span>
      {p.extra}
    </div>
  );
}));

function Btn({ children, title, disabled, onClick }: { children: ReactNode; title: string; disabled?: boolean; onClick: () => void }) {
  return <Button size="xs" variant="ghost" title={title} aria-label={title} disabled={disabled} onClick={onClick}>{children}</Button>;
}
const Sep = () => <span className="mx-0.5 h-4 w-px bg-border" />;

/**
 * Confirmation of a previewed operation: every changed cell with old → stored value (and the requested value when
 * quantisation or clamping changed it), its address and bytes, the clamped-cell warning and the changed byte count.
 */
export function MapEditConfirm({ preview, title, xAxis, yAxis, decimals, unit, busy, onApply, onCancel }: {
  preview: MapEditPreview; title: string; xAxis: number[]; yAxis: number[]; decimals: number; unit?: string; busy: boolean;
  onApply: () => void; onCancel: () => void;
}) {
  const t = useT();
  const [all, setAll] = useState(false);
  const LIMIT = 300;
  const rows = all ? preview.changes : preview.changes.slice(0, LIMIT);
  const fmt = (v: number) => v.toFixed(decimals);
  const empty = preview.changes.length === 0;
  return (
    <div className="border-b border-border bg-ai/5 px-3 py-2 text-xs">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-semibold">{t("mapEditor.confirmTitle", { op: title })}</span>
        <Badge tone="calc">{t("mapEditor.confirmCells", { n: preview.changes.length })}</Badge>
        <Badge tone="unknown">{t("mapEditor.confirmBytes", { n: preview.changedBytes })}</Badge>
        {preview.clampedCells > 0 && <Badge tone="warn"><AlertTriangle className="mr-1 inline size-3" />{t("mapEditor.confirmClamped", { n: preview.clampedCells })}</Badge>}
        <span className="ml-auto flex gap-1">
          <Button size="xs" variant="ghost" onClick={onCancel} disabled={busy}>{t("common.cancel")} <span className="text-fg-subtle">Esc</span></Button>
          <Button size="xs" variant="primary" onClick={onApply} disabled={busy || empty}>{busy ? <Spinner className="size-3" /> : <Check className="size-3" />}{t("mapEditor.applyPatch")}</Button>
        </span>
      </div>
      {preview.clampedCells > 0 && <div className="mt-1 text-[11px] text-warn">{t("mapEditor.clampedHint")}</div>}
      {empty ? <div className="mt-1 text-[11px] text-fg-muted">{t("mapEditor.noChanges")}</div> : (
        <div className="mt-1.5 max-h-48 overflow-auto rounded border border-border bg-bg">
          <table className="w-full font-mono text-[10px]">
            <thead className="sticky top-0 bg-panel-2 text-fg-subtle">
              <tr>
                <th className="px-1.5 py-0.5 text-left font-medium">{t("mapEditor.colCell")}</th>
                <th className="px-1.5 py-0.5 text-right font-medium">{t("mapEditor.colOld")}</th>
                <th className="px-1.5 py-0.5 text-right font-medium">{t("mapEditor.colStored")}</th>
                <th className="px-1.5 py-0.5 text-right font-medium">{t("mapEditor.colRequested")}</th>
                <th className="px-1.5 py-0.5 text-left font-medium">{t("mapEditor.colAddress")}</th>
                <th className="px-1.5 py-0.5 text-left font-medium">{t("mapEditor.colBytes")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((c) => (
                <tr key={`${c.row}:${c.col}`} className={cn("border-t border-border/40", c.clamped && "bg-warn/10")}>
                  <td className="px-1.5 py-0.5 text-fg-muted">{xAxis[c.col]} × {yAxis[c.row]} <span className="text-fg-subtle">[{c.row},{c.col}]</span></td>
                  <td className="px-1.5 py-0.5 text-right">{fmt(c.old)}</td>
                  <td className="px-1.5 py-0.5 text-right font-semibold">→ {fmt(c.stored)}{unit ? <span className="font-normal text-fg-subtle"> {unit}</span> : null}</td>
                  <td className={cn("px-1.5 py-0.5 text-right", Math.abs(c.requested - c.stored) > 1e-9 ? (c.clamped ? "text-warn" : "text-fg-muted") : "text-fg-subtle")}>
                    {Math.abs(c.requested - c.stored) > 1e-9 ? c.requested.toFixed(Math.max(decimals, 2)) : "="}{c.clamped ? ` · ${t("mapEditor.clamped")}` : ""}
                  </td>
                  <td className="px-1.5 py-0.5 text-fg-subtle">{hex(c.address)}</td>
                  <td className="px-1.5 py-0.5">{c.oldBytes} → <span className="text-calc">{c.newBytes}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
          {!all && preview.changes.length > LIMIT && (
            <button className="w-full py-1 text-[10px] text-calc hover:underline" onClick={() => setAll(true)}>{t("mapEditor.showAll", { n: preview.changes.length - LIMIT })}</button>
          )}
        </div>
      )}
    </div>
  );
}
