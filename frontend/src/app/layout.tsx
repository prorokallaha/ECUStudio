import type { Metadata } from "next";
import "./globals.css";
import "@xyflow/react/dist/style.css";
import { Providers } from "./providers";

export const metadata: Metadata = {
  title: "ECUStudio",
  description: "ECU binary analysis, virtual dyno and risk review — engineering estimates, not measurements.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="ru" data-theme="dark" suppressHydrationWarning>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
