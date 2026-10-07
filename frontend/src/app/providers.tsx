"use client";
import { useEffect, useState, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { Toaster } from "sonner";
import { useUI } from "@/stores/ui";
import { ApiError } from "@/services/api";

export function Providers({ children }: { children: ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            refetchOnWindowFocus: false,
            retry: (count, err) => !(err instanceof ApiError && err.status >= 400 && err.status < 500) && count < 2,
          },
        },
      }),
  );
  const theme = useUI((s) => s.theme);
  useEffect(() => { document.documentElement.dataset.theme = theme; }, [theme]);
  return (
    <QueryClientProvider client={client}>
      {children}
      <Toaster theme={theme} position="bottom-right" richColors closeButton toastOptions={{ className: "text-xs" }} />
    </QueryClientProvider>
  );
}
