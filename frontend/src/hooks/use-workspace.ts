"use client";
import { useCallback, useMemo } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";

export type Section =
  | "dashboard" | "vehicle" | "ecu" | "binary" | "maps" | "diff" | "dependencies" | "simulation" | "dyno"
  | "components" | "risks" | "logs" | "ai" | "reports" | "settings";

/**
 * Workspace location lives in the URL (static export ⇒ query parameters, not dynamic segments):
 * /workspace/<section>/?p=<projectId>&a=<analysisId>&map=<mapId>…
 */
export function useWorkspace() {
  const params = useSearchParams();
  const pathname = usePathname();
  const router = useRouter();
  const projectId = params.get("p");
  const analysisId = params.get("a");
  const section = (pathname.split("/").filter(Boolean)[1] ?? "dashboard") as Section;

  const href = useCallback(
    (target: Section | string, extra?: Record<string, string | number | undefined | null>) => {
      const [path, inlineQuery] = target.split("?");
      const [sec, sub] = path.split("/");
      const q = new URLSearchParams();
      if (projectId) q.set("p", projectId);
      if (analysisId) q.set("a", analysisId);
      if (sub) q.set(sec === "maps" || sec === "diff" ? "map" : "item", sub);
      if (inlineQuery) new URLSearchParams(inlineQuery).forEach((v, k) => q.set(k, v));
      for (const [k, v] of Object.entries(extra ?? {})) if (v !== undefined && v !== null && v !== "") q.set(k, String(v));
      return `/workspace/${sec}/?${q.toString()}`;
    },
    [projectId, analysisId],
  );

  const setParam = useCallback(
    (key: string, value: string | null) => {
      const q = new URLSearchParams(params.toString());
      if (value === null) q.delete(key); else q.set(key, value);
      router.replace(`${pathname}?${q.toString()}`, { scroll: false });
    },
    [params, pathname, router],
  );

  return useMemo(() => ({ projectId, analysisId, section, params, href, setParam, router }), [projectId, analysisId, section, params, href, setParam, router]);
}
