"use client";
import { useEffect, useState } from "react";
import type { VinInfo } from "@/types/domain";
import { api } from "@/services/api";
import { Button, Dialog, Input, SectionTitle } from "@/components/ui";

export function NewProjectDialog({ open, onClose, onCreate }: { open: boolean; onClose: () => void; onCreate: (p: { name: string; vin?: string; file?: File }) => Promise<void> }) {
  const [name, setName] = useState("");
  const [vin, setVin] = useState("");
  const [file, setFile] = useState<File | undefined>();
  const [vinInfo, setVinInfo] = useState<VinInfo | null>(null);
  const [vinError, setVinError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { if (open) { setName(""); setVin(""); setFile(undefined); setVinInfo(null); setVinError(null); } }, [open]);
  useEffect(() => {
    const v = vin.trim().toUpperCase();
    setVinInfo(null); setVinError(null);
    if (v.length !== 17) return;
    const t = setTimeout(() => api.decodeVin(v).then(setVinInfo).catch((e) => setVinError(e.message)), 250);
    return () => clearTimeout(t);
  }, [vin]);

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="New project"
      footer={<>
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!name.trim() || busy} onClick={async () => { setBusy(true); try { await onCreate({ name: name.trim(), vin: vin.trim() || undefined, file }); onClose(); } finally { setBusy(false); } }}>Create</Button>
      </>}
    >
      <div className="space-y-3">
        <label className="block space-y-1"><SectionTitle>Name</SectionTitle><Input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="Golf V 1.9 TDI — customer A" /></label>
        <label className="block space-y-1">
          <SectionTitle>VIN (optional)</SectionTitle>
          <Input value={vin} onChange={(e) => setVin(e.target.value.toUpperCase())} maxLength={17} placeholder="WVWZZZ1KZ6W123456" className="num" />
          {vinInfo && <div className="text-[11px] text-fg-muted">{vinInfo.manufacturer} · {vinInfo.region} · model year {vinInfo.modelYear ?? "?"} · platform {vinInfo.platformCode ?? "?"}{vinInfo.checkDigitValid === false && <span className="text-warn"> · check digit mismatch</span>}</div>}
          {vinError && <div className="text-[11px] text-danger">{vinError}</div>}
          <div className="text-[11px] text-fg-subtle">The VIN identifies the vehicle, not the installed turbo, injectors or gearbox.</div>
        </label>
        <label className="block space-y-1">
          <SectionTitle>Binary (optional)</SectionTitle>
          <input type="file" accept=".bin,.ori,.mod,application/octet-stream" onChange={(e) => setFile(e.target.files?.[0])} className="block w-full text-xs text-fg-muted file:mr-2 file:rounded file:border file:border-border file:bg-panel-2 file:px-2 file:py-1 file:text-xs file:text-fg" />
        </label>
      </div>
    </Dialog>
  );
}
