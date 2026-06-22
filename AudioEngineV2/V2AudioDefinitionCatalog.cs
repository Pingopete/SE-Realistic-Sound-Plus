using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2AudioDefinitionCatalog
    {
        private static readonly Dictionary<string, SoundInfo> Sounds = new Dictionary<string, SoundInfo>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static bool TryGet(string subtypeId, out SoundInfo info)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(subtypeId))
            {
                info = default(SoundInfo);
                return false;
            }

            return Sounds.TryGetValue(subtypeId, out info);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            string dataPath = ResolveDataPath();
            if (string.IsNullOrWhiteSpace(dataPath) || !Directory.Exists(dataPath))
            {
                V2DebugLog.WriteEvent("audio-defs", "data path missing");
                return;
            }

            int loaded = 0;
            try
            {
                foreach (string path in Directory.GetFiles(dataPath, "Audio*.sbc", SearchOption.TopDirectoryOnly))
                    loaded += LoadFile(path);

                V2DebugLog.WriteEvent("audio-defs", "loaded=" + loaded.ToString(CultureInfo.InvariantCulture) + " from " + dataPath);
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("audio-defs", "load failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static int LoadFile(string path)
        {
            int loaded = 0;
            XDocument document = XDocument.Load(path, LoadOptions.None);
            foreach (XElement sound in document.Descendants("Sound"))
            {
                string subtypeId = GetElementValue(sound.Element("Id"), "SubtypeId");
                if (string.IsNullOrWhiteSpace(subtypeId))
                    continue;

                float maxDistance = ParseFloat(GetElementValue(sound, "MaxDistance"), 0f);
                float volume = ParseFloat(GetElementValue(sound, "Volume"), 1f);
                string category = GetElementValue(sound, "Category") ?? string.Empty;
                if (maxDistance <= 0f)
                    continue;

                Sounds[subtypeId] = new SoundInfo(subtypeId, category, maxDistance, volume);
                loaded++;
            }

            return loaded;
        }

        private static string ResolveDataPath()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(baseDirectory ?? string.Empty, "..", "Content", "Data")),
                Path.GetFullPath(Path.Combine(baseDirectory ?? string.Empty, "..", "..", "Content", "Data")),
                @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Data"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (Directory.Exists(candidates[i]))
                    return candidates[i];
            }

            return null;
        }

        private static string GetElementValue(XElement parent, string name)
        {
            return parent?.Element(name)?.Value?.Trim();
        }

        private static float ParseFloat(string value, float fallback)
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return parsed;

            return fallback;
        }

        internal struct SoundInfo
        {
            public readonly string SubtypeId;
            public readonly string Category;
            public readonly float MaxDistance;
            public readonly float Volume;

            public SoundInfo(string subtypeId, string category, float maxDistance, float volume)
            {
                SubtypeId = subtypeId;
                Category = category;
                MaxDistance = maxDistance;
                Volume = volume;
            }
        }
    }
}
