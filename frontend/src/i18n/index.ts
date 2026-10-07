"use client";
import { create } from "zustand";
import { persist, createJSONStorage } from "zustand/middleware";
import { ru } from "./ru";
import { en } from "./en";

/**
 * UI localisation. `ru` is the reference dictionary (and the default language); `en` must have the same shape,
 * which the type system enforces. Components never contain user-visible literals: they call `t("section.key")`.
 * Values may contain `{name}` placeholders, filled from the params object.
 */
export type Lang = "ru" | "en";
export const LANGS: { value: Lang; label: string }[] = [{ value: "ru", label: "Русский" }, { value: "en", label: "English" }];

type Widen<T> = { [K in keyof T]: T[K] extends string ? string : Widen<T[K]> };
export type Dictionary = Widen<typeof ru>;
const dictionaries: Record<Lang, Dictionary> = { ru, en };

type Paths<T, P extends string = ""> = { [K in keyof T & string]: T[K] extends string ? `${P}${K}` : Paths<T[K], `${P}${K}.`> }[keyof T & string];
export type TKey = Paths<Dictionary>;
export type TParams = Record<string, string | number | null | undefined>;

const safeStorage = createJSONStorage(() => {
  try { return window.localStorage; } catch { return { getItem: () => null, setItem: () => {}, removeItem: () => {} }; }
});

interface LangState { lang: Lang; setLang: (l: Lang) => void }
export const useLang = create<LangState>()(persist((set) => ({ lang: "ru", setLang: (lang) => set({ lang }) }), { name: "ecustudio.lang", storage: safeStorage }));

function lookup(dict: Dictionary, key: string): string | undefined {
  let node: unknown = dict;
  for (const part of key.split(".")) {
    if (node && typeof node === "object" && part in (node as Record<string, unknown>)) node = (node as Record<string, unknown>)[part];
    else return undefined;
  }
  return typeof node === "string" ? node : undefined;
}

function format(template: string, params?: TParams): string {
  if (!params) return template;
  return template.replace(/\{(\w+)\}/g, (_, name: string) => (params[name] ?? "") + "");
}

/** Translate outside React (toasts in callbacks, non-component code). */
export function translate(lang: Lang, key: TKey, params?: TParams): string {
  return format(lookup(dictionaries[lang], key) ?? lookup(dictionaries.ru, key) ?? key, params);
}

/**
 * Translate a key that is only known at runtime (enum values, finding codes from the API). Falls back to `fallback`
 * (usually the server's English text) instead of showing a raw key.
 */
export function translateDynamic(lang: Lang, key: string, fallback?: string, params?: TParams): string {
  const v = lookup(dictionaries[lang], key) ?? (lang === "en" ? undefined : lookup(dictionaries.en, key));
  return v === undefined ? fallback ?? key : format(v, params);
}

export function useT() {
  const lang = useLang((s) => s.lang);
  const t = (key: TKey, params?: TParams) => translate(lang, key, params);
  /** Runtime key with fallback, e.g. tx(`role.${map.role}`, map.role). */
  const tx = (key: string, fallback?: string, params?: TParams) => translateDynamic(lang, key, fallback, params);
  /** Localises the sentinel strings produced by lib/format (fmtParam / fmtEstimate): "UNKNOWN", "N/A". */
  const val = (s: string) => localizeValue(lang, s);
  return Object.assign(t, { tx, val, lang });
}

/** "UNKNOWN" / "N/A" sentinels from lib/format → localised text; any other string is returned unchanged. */
export function localizeValue(lang: Lang, s: string): string {
  if (s === "UNKNOWN") return translate(lang, "common.unknown");
  if (s === "N/A") return translate(lang, "common.na");
  return s;
}

export function currentLang(): Lang {
  return useLang.getState().lang;
}
