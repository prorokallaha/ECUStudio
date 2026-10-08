import type { NextConfig } from "next";

// Static export: the .NET host (server or desktop) serves the files from wwwroot, so no Node runtime is needed.
const config: NextConfig = {
  output: "export",
  trailingSlash: true,
  images: { unoptimized: true },
  reactStrictMode: true,
  // In `next dev` the API runs separately on :5080 (CORS enabled in Development).
  env: { NEXT_PUBLIC_API_BASE: process.env.NEXT_PUBLIC_API_BASE ?? "" },
};

export default config;
