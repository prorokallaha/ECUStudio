// The API allows named float literals in JSON; values the UI receives are always finite, so drop the literal unions.
import { readFileSync, writeFileSync } from "node:fs";
const p = new URL("../src/types/api.generated.ts", import.meta.url);
writeFileSync(p, readFileSync(p, "utf8").replaceAll(' | ("NaN" | "Infinity" | "-Infinity")', ""));
