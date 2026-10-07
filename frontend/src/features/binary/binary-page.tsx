"use client";
import { useEffect, useMemo, useState } from "react";
import { Bookmark, BookmarkPlus, ChevronDown, ChevronUp, CornerDownRight, Search, Trash2 } from "lucide-react";
import { toast } from "sonner";
import type { AnalysisReport, DataType } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Badge, Button, Card, CardHeader, Input, KV, SectionTitle, Segmented, Select } from "@/components/ui";
import { HexViewer, type HexMode } from "@/components/binary/hex-viewer";
import { readWord, useHexPages } from "@/components/binary/use-hex-pages";
import { useWorkspace } from "@/hooks/use-workspace";
import { useUI } from "@/stores/ui";
import { useSelection } from "@/stores/selection";
import { api, ApiError } from "@/services/api";
import { hex } from "@/lib/format";

export function BinaryPage() {
  return <WithReport>{(r) => <BinaryViewer r={r} />}</WithReport>;
}

function BinaryViewer({ r }: { r: AnalysisReport }) {
  const { params, href, router } = useWorkspace();
  const ui = useUI();
  const select = useSelection((s) => s.select);
  const fileSize = r.ecu.flashSize || 0;
  const loader = useHexPages(r.id, fileSize);
  const initial = Number(params.get("offset") ?? NaN);
  const [selected, setSelected] = useState<number | null>(Number.isFinite(initial) ? initial : null);
  const [scrollTo, setScrollTo] = useState<{ offset: number; seq: number } | null>(Number.isFinite(initial) ? { offset: initial, seq: 1 } : null);
  const [mode, setMode] = useState<HexMode>(r.stockSha256 ? "modified" : "modified");
  const [decimal, setDecimal] = useState(false);
  const [goto, setGoto] = useState("");
  const [sq, setSq] = useState("");
  const [smode, setSmode] = useState<"hex" | "ascii" | "int" | "float">("hex");
  const [results, setResults] = useState<number[]>([]);
  const [ri, setRi] = useState(0);
  const [hl, setHl] = useState<{ start: number; length: number } | null>(null);
  const bookmarks = ui.bookmarks[r.modifiedSha256] ?? [];

  const jump = (o: number, len = 1) => { const n = Math.max(0, Math.min(fileSize - 1, o)); setSelected(n); setScrollTo({ offset: n, seq: Date.now() }); setHl({ start: n, length: len }); };
  useEffect(() => { if (selected !== null) select({ kind: "hex", offset: selected, length: ui.hexWord / 8 }); }, [selected, select, ui.hexWord]);

  const wordSize = (ui.hexWord / 8) as 1 | 2 | 4;
  const doSearch = async () => {
    if (!sq.trim()) return;
    try {
      const type: DataType = smode === "int" ? (`${ui.hexSigned ? "Int" : "UInt"}${ui.hexWord}` as DataType) : "Float32";
      const res = await api.analyses.search(r.id, smode, sq.trim(), type, ui.hexEndian);
      setResults(res); setRi(0);
      if (res.length) jump(res[0], smode === "hex" ? Math.ceil(sq.replace(/\s/g, "").length / 2) : smode === "ascii" ? sq.length : wordSize);
      else toast("No matches");
    } catch (e) { toast.error(e instanceof ApiError ? e.message : String(e)); }
  };
  const step = (d: number) => { if (!results.length) return; const n = (ri + d + results.length) % results.length; setRi(n); jump(results[n], hl?.length ?? 1); };

  const info = useMemo(() => {
    if (selected === null) return null;
    const bytes = (n: number, which: "mod" | "stock") => Array.from({ length: n }, (_, k) => { const b = loader.byteAt(selected + k); return b ? (which === "mod" ? b.mod : b.stock) : null; });
    const mod4 = bytes(4, "mod"), st4 = bytes(4, "stock");
    const regs = loader.regionsAt(selected);
    return {
      regs,
      rows: [
        ["u8", readWord(mod4, 1, ui.hexEndian, false), readWord(st4, 1, ui.hexEndian, false)],
        ["i8", readWord(mod4, 1, ui.hexEndian, true), readWord(st4, 1, ui.hexEndian, true)],
        ["u16", readWord(mod4, 2, ui.hexEndian, false), readWord(st4, 2, ui.hexEndian, false)],
        ["i16", readWord(mod4, 2, ui.hexEndian, true), readWord(st4, 2, ui.hexEndian, true)],
        ["u32", readWord(mod4, 4, ui.hexEndian, false), readWord(st4, 4, ui.hexEndian, false)],
        ["i32", readWord(mod4, 4, ui.hexEndian, true), readWord(st4, 4, ui.hexEndian, true)],
        ["f32", readWord(mod4, 4, ui.hexEndian, false, true), readWord(st4, 4, ui.hexEndian, false, true)],
      ] as [string, number | null, number | null][],
    };
  }, [selected, loader, ui.hexEndian]);

  const changedRanges = useMemo(() => [
    ...r.maps.filter((m) => m.modified).map((m) => ({ start: m.address, len: m.rows * m.cols * 2, label: m.name })),
    ...(r.unmappedChanges ?? []).map((u) => ({ start: u.start, len: u.length, label: `unmapped (${u.section})` })),
  ].sort((a, b) => a.start - b.start), [r]);

  return (
    <div className="flex h-full flex-col">
      <PageHeader
        title="Binary"
        subtitle={`${r.modifiedName} · ${fileSize.toLocaleString()} bytes · ${r.changedBytes ?? 0} changed bytes vs stock`}
        actions={<>
          <form className="flex items-center gap-1" onSubmit={(e) => { e.preventDefault(); const n = parseInt(goto.replace(/^0x/i, ""), 16); if (Number.isFinite(n)) jump(n); }}>
            <Input value={goto} onChange={(e) => setGoto(e.target.value)} placeholder="Go to 0x…" className="num w-28" />
            <Button size="icon" type="submit" title="Go to address"><CornerDownRight className="size-3.5" /></Button>
          </form>
        </>}
      />
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        <Segmented value={mode} onChange={setMode} options={[{ value: "modified", label: "Modified" }, { value: "stock", label: "Stock" }, { value: "split", label: "Stock | Mod" }].filter((o) => r.stockSha256 || o.value === "modified") as any} />
        <Segmented value={String(ui.hexWord) as "8" | "16" | "32"} onChange={(v) => ui.setHex({ hexWord: Number(v) as 8 | 16 | 32 })} options={[{ value: "8", label: "8-bit" }, { value: "16", label: "16-bit" }, { value: "32", label: "32-bit" }]} />
        <Segmented value={ui.hexEndian} onChange={(v) => ui.setHex({ hexEndian: v })} options={[{ value: "Big", label: "BE" }, { value: "Little", label: "LE" }]} />
        <Segmented value={ui.hexSigned ? "s" : "u"} onChange={(v) => ui.setHex({ hexSigned: v === "s" })} options={[{ value: "u", label: "unsigned" }, { value: "s", label: "signed" }]} />
        <Segmented value={decimal ? "dec" : "hex"} onChange={(v) => setDecimal(v === "dec")} options={[{ value: "hex", label: "HEX" }, { value: "dec", label: "DEC" }]} />
        <form className="ml-auto flex items-center gap-1" onSubmit={(e) => { e.preventDefault(); doSearch(); }}>
          <Select value={smode} onChange={(e) => setSmode(e.target.value as any)}><option value="hex">hex</option><option value="ascii">ascii</option><option value="int">int</option><option value="float">float</option></Select>
          <Input value={sq} onChange={(e) => setSq(e.target.value)} placeholder={smode === "hex" ? "1037 ?? 99" : smode === "ascii" ? "EDC16" : "value"} className="num w-36" />
          <Button size="icon" type="submit"><Search className="size-3.5" /></Button>
          {results.length > 0 && <><span className="num text-[11px] text-fg-muted">{ri + 1}/{results.length}</span><Button size="icon" variant="ghost" type="button" onClick={() => step(-1)}><ChevronUp className="size-3.5" /></Button><Button size="icon" variant="ghost" type="button" onClick={() => step(1)}><ChevronDown className="size-3.5" /></Button></>}
        </form>
      </div>
      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1">
          <HexViewer analysisId={r.id} fileSize={fileSize} hasStock={!!r.stockSha256} mode={mode} wordSize={wordSize} endian={ui.hexEndian} signed={ui.hexSigned} decimal={decimal}
            selected={selected} highlight={hl} scrollTo={scrollTo} bookmarks={bookmarks.map((b) => b.offset)} onSelect={(o) => { setSelected(o); setHl(null); }} loader={loader} />
        </div>
        <aside className="w-72 shrink-0 space-y-3 overflow-y-auto border-l border-border bg-bg-elev p-3">
          {info && selected !== null ? (
            <>
              <div className="flex items-center justify-between">
                <span className="num text-sm font-semibold">{hex(selected)}</span>
                <Button size="xs" variant="ghost" onClick={() => ui.addBookmark(r.modifiedSha256, selected, prompt("Bookmark label", hex(selected)) ?? hex(selected))}><BookmarkPlus className="size-3" />Bookmark</Button>
              </div>
              {info.regs.map((g, i) => (
                <div key={i} className="flex items-center gap-1.5 text-xs">
                  <Badge tone={g.kind === "candidate" ? "ai" : g.kind === "section" ? "unknown" : "ok"}>{g.kind}</Badge>
                  {g.mapId ? <button className="truncate text-calc hover:underline" onClick={() => router.push(href(`maps/${g.mapId}`))}>{g.label}</button> : <span className="truncate">{g.label}</span>}
                </div>
              ))}
              <div>
                <SectionTitle className="mb-1">Value at offset ({ui.hexEndian === "Big" ? "big" : "little"}-endian)</SectionTitle>
                <table className="w-full text-xs"><thead className="text-[10px] text-fg-subtle"><tr><th className="text-left font-medium">type</th><th className="text-right font-medium">modified</th>{r.stockSha256 && <th className="text-right font-medium">stock</th>}</tr></thead>
                  <tbody>{info.rows.map(([t, m, s]) => (
                    <tr key={t} className={m !== s && s !== null ? "text-warn" : ""}><td className="text-fg-subtle">{t}</td><td className="num text-right">{fmtVal(m)}</td>{r.stockSha256 && <td className="num text-right text-fg-muted">{fmtVal(s)}</td>}</tr>
                  ))}</tbody>
                </table>
              </div>
            </>
          ) : <div className="text-xs text-fg-muted">Click a byte to inspect values, region and stock difference. Arrow keys move the cursor.</div>}

          <div>
            <SectionTitle className="mb-1">Changed regions</SectionTitle>
            <div className="max-h-48 space-y-0.5 overflow-y-auto">
              {changedRanges.map((c) => <button key={c.start} onClick={() => jump(c.start, c.len)} className="flex w-full justify-between rounded px-1 py-0.5 text-left text-[11px] hover:bg-panel-2"><span className="num text-warn">{hex(c.start)}</span><span className="truncate pl-2 text-fg-muted">{c.label}</span></button>)}
              {!changedRanges.length && <div className="text-[11px] text-fg-subtle">{r.stockSha256 ? "No changes" : "No stock file to compare"}</div>}
            </div>
          </div>
          <div>
            <SectionTitle className="mb-1">Bookmarks</SectionTitle>
            {bookmarks.map((b) => (
              <div key={b.offset} className="flex items-center gap-1 text-[11px]">
                <Bookmark className="size-3 text-attn" />
                <button onClick={() => jump(b.offset)} className="num text-calc hover:underline">{hex(b.offset)}</button>
                <span className="flex-1 truncate text-fg-muted">{b.label}</span>
                <button onClick={() => ui.removeBookmark(r.modifiedSha256, b.offset)} className="text-fg-subtle hover:text-danger"><Trash2 className="size-3" /></button>
              </div>
            ))}
            {!bookmarks.length && <div className="text-[11px] text-fg-subtle">None</div>}
          </div>
          <div>
            <SectionTitle className="mb-1">Maps</SectionTitle>
            <div className="max-h-56 space-y-0.5 overflow-y-auto">
              {r.maps.map((m) => <button key={m.id} onClick={() => jump(m.address, m.rows * m.cols * 2)} className="flex w-full justify-between rounded px-1 py-0.5 text-left text-[11px] hover:bg-panel-2"><span className="num text-fg-muted">{hex(m.address)}</span><span className={`truncate pl-2 ${m.modified ? "text-calc" : ""}`}>{m.name}</span></button>)}
            </div>
          </div>
          <KV k="SHA-256">{r.modifiedSha256.slice(0, 16)}…</KV>
        </aside>
      </div>
    </div>
  );
}

const fmtVal = (v: number | null) => (v === null ? "—" : Number.isInteger(v) ? v.toString() : v.toPrecision(6));
