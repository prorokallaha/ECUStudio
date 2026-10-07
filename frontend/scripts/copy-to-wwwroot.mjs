// Copies the static export into ECUStudio.Api/wwwroot so the server and the desktop exe serve the UI.
import { cpSync, rmSync, existsSync } from "node:fs";
const out = new URL("../out/", import.meta.url);
const target = new URL("../../src/ECUStudio.Api/wwwroot/", import.meta.url);
if (!existsSync(out)) throw new Error("Run `next build` first");
rmSync(target, { recursive: true, force: true });
cpSync(out, target, { recursive: true });
console.log("Frontend copied to", target.pathname);
