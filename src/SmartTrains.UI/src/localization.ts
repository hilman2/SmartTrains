import * as l10n from "cs2/l10n";

interface Localization {
  translate(id: string, fallback?: string | null): string | null;
}

// The template's types (types/l10n.d.ts) declare useCachedLocalization, but
// the game's cs2/l10n module does not export it at runtime (1.6.2f1); calling
// it throws and takes the panel down. useLocalization is exported and is what
// other mods use, so it is looked up by name.
const useLocalization: () => Localization = (l10n as unknown as { useLocalization: () => Localization }).useLocalization;

export type Translate = (key: string, fallback: string) => string;

/**
 * Returns two translators. `t` is for the panel's own texts, with the keys of
 * src/SmartTrains/Localization/*.json, without the "SmartTrains." prefix the
 * C# side adds. `game` is for the game's own texts, e.g. resource names, by
 * their full ID. Both show the fallback if the key is missing, e.g. while the
 * C# part of the mod is older than the panel.
 */
export function useTranslate(): { t: Translate; game: Translate } {
  const localization = useLocalization();
  return {
    t: (key, fallback) => localization.translate("SmartTrains." + key, fallback) ?? fallback,
    game: (id, fallback) => localization.translate(id, fallback) ?? fallback,
  };
}
