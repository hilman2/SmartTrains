using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Colossal;
using Newtonsoft.Json;

namespace SmartTrains.Localization
{
    /// <summary>
    /// Feeds one language to the game's localization manager.
    ///
    /// Texts live in Localization/&lt;locale&gt;.json, embedded in the DLL, and
    /// reach the game with a "SmartTrains." prefix, which the panel adds back
    /// when it asks (src/SmartTrains.UI/src/localization.ts). A key missing in
    /// a language falls back to the English text, so a partial translation
    /// never shows IDs.
    /// </summary>
    internal sealed class LocaleSource : IDictionarySource
    {
        private const string kResourcePrefix = "SmartTrains.Localization.";
        private const string kKeyPrefix = "SmartTrains.";
        private const string kFallbackLocale = "en-US";

        private readonly Dictionary<string, string> m_Entries;

        private LocaleSource(Dictionary<string, string> entries)
        {
            m_Entries = entries;
        }

        /// <summary>Registers every embedded language with the game.</summary>
        public static void RegisterAll()
        {
            Assembly assembly = typeof(LocaleSource).Assembly;
            Dictionary<string, string> fallback = Read(assembly, kFallbackLocale) ?? new Dictionary<string, string>();
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(kResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
                    continue;
                string locale = resource.Substring(kResourcePrefix.Length, resource.Length - kResourcePrefix.Length - ".json".Length);
                Dictionary<string, string> texts = Read(assembly, locale);
                if (texts == null)
                    continue;
                foreach (KeyValuePair<string, string> entry in fallback)
                {
                    if (!texts.ContainsKey(entry.Key))
                        texts[entry.Key] = entry.Value;
                }
                var entries = new Dictionary<string, string>();
                foreach (KeyValuePair<string, string> entry in texts)
                    entries[kKeyPrefix + entry.Key] = entry.Value;
                Game.SceneFlow.GameManager.instance.localizationManager.AddSource(locale, new LocaleSource(entries));
            }
        }

        private static Dictionary<string, string> Read(Assembly assembly, string locale)
        {
            using (Stream stream = assembly.GetManifestResourceStream(kResourcePrefix + locale + ".json"))
            {
                if (stream == null)
                    return null;
                using (var reader = new StreamReader(stream))
                {
                    try
                    {
                        return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
                    }
                    catch (JsonException e)
                    {
                        Mod.Log.Error(e, $"Localization file {locale}.json is broken and was skipped.");
                        return null;
                    }
                }
            }
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return m_Entries;
        }

        public void Unload()
        {
        }
    }
}
