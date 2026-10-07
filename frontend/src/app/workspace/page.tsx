"use client";
import { useEffect } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";

function Redirect() {
  const router = useRouter();
  const params = useSearchParams();
  useEffect(() => { router.replace(`/workspace/dashboard/?${params.toString()}`); }, [router, params]);
  return null;
}

export default function WorkspaceIndex() {
  return <Suspense><Redirect /></Suspense>;
}
