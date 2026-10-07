"use client";
import { ScrollText } from "lucide-react";
import { PageHeader } from "@/components/layout/page";
import { EmptyState } from "@/components/ui";

export function LogsPage() {
  return (
    <div>
      <PageHeader title="Logs" />
      <EmptyState icon={<ScrollText className="size-8" />} title="Diagnostic log import is not implemented yet">
        Planned: CSV logs (VCDS / generic OBD) with RPM, requested/actual boost, MAF, IQ and SOI. Logged values will replace model assumptions
        (source “Diagnostic log”), narrow every range and raise data availability. Until then, all values on the other pages are model estimates.
      </EmptyState>
    </div>
  );
}
