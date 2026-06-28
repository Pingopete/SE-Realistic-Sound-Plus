using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2ToolLoopWaveCatalog
    {
        private static readonly Dictionary<string, ToolLoopWaveInfo> Loops = new Dictionary<string, ToolLoopWaveInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ToolLoopWaveInfo> ReverbInputs = new Dictionary<string, ToolLoopWaveInfo>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static bool TryGetLoopWave(string cueName, out ToolLoopWaveInfo info)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(cueName))
            {
                info = default(ToolLoopWaveInfo);
                return false;
            }

            return TryGetWave(Loops, cueName.Trim(), out info);
        }

        public static bool TryGetReverbWave(string cueName, out ToolLoopWaveInfo info)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(cueName))
            {
                info = default(ToolLoopWaveInfo);
                return false;
            }

            return TryGetWave(ReverbInputs, cueName.Trim(), out info);
        }

        public static void Warmup()
        {
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            string contentPath = ResolveContentPath();
            if (string.IsNullOrWhiteSpace(contentPath))
            {
                V2DebugLog.WriteEvent("tool-loop-wav", "content path missing");
                return;
            }

            string dataPath = Path.Combine(contentPath, "Data");
            if (!Directory.Exists(dataPath))
            {
                V2DebugLog.WriteEvent("tool-loop-wav", "data path missing " + dataPath);
                return;
            }

            int inspected = 0;
            try
            {
                foreach (string path in Directory.GetFiles(dataPath, "Audio*.sbc", SearchOption.TopDirectoryOnly))
                    inspected += LoadFile(path, contentPath);

                V2DebugLog.WriteEvent(
                    "tool-loop-wav",
                    string.Format(CultureInfo.InvariantCulture, "loops={0} fallbacks={1} inspected={2}", Loops.Count, ReverbInputs.Count, inspected));
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("tool-loop-wav", "load failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static int LoadFile(string path, string contentPath)
        {
            int inspected = 0;
            XDocument document = XDocument.Load(path, LoadOptions.None);
            foreach (XElement sound in document.Descendants("Sound"))
            {
                string subtypeId = GetElementValue(sound.Element("Id"), "SubtypeId");
                if (string.IsNullOrWhiteSpace(subtypeId))
                    continue;

                inspected++;
                if (V2AuxCueClassifier.IsToolActionCue(subtypeId))
                {
                    string loop = ResolvePreferredLoop(sound, contentPath);
                    if (!string.IsNullOrWhiteSpace(loop))
                        AddWave(Loops, subtypeId, contentPath, loop);
                }

                string fallback = ResolvePreferredReverbInput(sound, contentPath);
                if (!string.IsNullOrWhiteSpace(fallback))
                    AddWave(ReverbInputs, subtypeId, contentPath, fallback);
            }

            return inspected;
        }

        private static void AddWave(Dictionary<string, ToolLoopWaveInfo> target, string subtypeId, string contentPath, string relativePath)
        {
            string absolute = ResolveAudioPath(contentPath, relativePath);
            if (string.IsNullOrWhiteSpace(absolute) || !File.Exists(absolute))
                return;

            target[subtypeId] = new ToolLoopWaveInfo(subtypeId, relativePath, absolute);
        }

        private static bool TryGetWave(Dictionary<string, ToolLoopWaveInfo> source, string cueName, out ToolLoopWaveInfo info)
        {
            if (source.TryGetValue(cueName, out info))
                return true;

            if (TryGetPairedCueName(cueName, out string pairedCueName) && source.TryGetValue(pairedCueName, out ToolLoopWaveInfo paired))
            {
                info = new ToolLoopWaveInfo(cueName, paired.RelativePath, paired.AbsolutePath);
                return true;
            }

            info = default(ToolLoopWaveInfo);
            return false;
        }

        private static bool TryGetPairedCueName(string cueName, out string pairedCueName)
        {
            pairedCueName = null;
            if (string.IsNullOrWhiteSpace(cueName))
                return false;

            if (cueName.StartsWith("Arc", StringComparison.OrdinalIgnoreCase))
                pairedCueName = "Real" + cueName.Substring(3);
            else if (cueName.StartsWith("Real", StringComparison.OrdinalIgnoreCase))
                pairedCueName = "Arc" + cueName.Substring(4);

            return !string.IsNullOrWhiteSpace(pairedCueName);
        }

        private static string ResolvePreferredLoop(XElement sound, string contentPath)
        {
            string d3Loop = null;
            string d2Loop = null;
            XElement waves = sound.Element("Waves");
            if (waves == null)
                return null;

            foreach (XElement wave in waves.Elements("Wave"))
            {
                string type = wave.Attribute("Type")?.Value ?? string.Empty;
                string loop = GetElementValue(wave, "Loop");
                if (string.IsNullOrWhiteSpace(loop))
                    continue;

                if (type.IndexOf("D3", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    d3Loop = loop;
                    break;
                }

                if (d2Loop == null && type.IndexOf("D2", StringComparison.OrdinalIgnoreCase) >= 0)
                    d2Loop = loop;
            }

            string resolved = ResolveWaveCandidate(contentPath, d3Loop);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;

            return ResolveWaveCandidate(contentPath, Convert2dNameTo3d(d2Loop));
        }

        private static string ResolvePreferredReverbInput(XElement sound, string contentPath)
        {
            XElement waves = sound.Element("Waves");
            if (waves == null)
                return null;

            string d3Start = null;
            string d3Loop = null;
            string d3End = null;
            string d2Start = null;
            string d2Loop = null;
            string d2End = null;
            foreach (XElement wave in waves.Elements("Wave"))
            {
                string type = wave.Attribute("Type")?.Value ?? string.Empty;
                bool isD3 = type.IndexOf("D3", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isD2 = type.IndexOf("D2", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isD3 && !isD2)
                    continue;

                string start = GetElementValue(wave, "Start");
                string loop = GetElementValue(wave, "Loop");
                string end = GetElementValue(wave, "End");
                if (isD3)
                {
                    if (d3Start == null) d3Start = start;
                    if (d3Loop == null) d3Loop = loop;
                    if (d3End == null) d3End = end;
                }
                else
                {
                    if (d2Start == null) d2Start = start;
                    if (d2Loop == null) d2Loop = loop;
                    if (d2End == null) d2End = end;
                }
            }

            string[] candidates =
            {
                d3Start,
                d3Loop,
                d3End,
                Convert2dNameTo3d(d2Start),
                Convert2dNameTo3d(d2Loop),
                Convert2dNameTo3d(d2End)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string resolved = ResolveWaveCandidate(contentPath, candidates[i]);
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }

            return null;
        }

        private static string ResolveWaveCandidate(string contentPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            string direct = EnsureWavExtension(relativePath);
            if (File.Exists(ResolveAudioPath(contentPath, direct)))
                return direct;

            string converted = Convert2dNameTo3d(direct);
            if (!string.Equals(converted, direct, StringComparison.OrdinalIgnoreCase)
                && File.Exists(ResolveAudioPath(contentPath, converted)))
            {
                return converted;
            }

            return null;
        }

        private static string EnsureWavExtension(string relativePath)
        {
            string extension = Path.GetExtension(relativePath);
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
                return relativePath;

            if (string.Equals(extension, ".xwm", StringComparison.OrdinalIgnoreCase))
                return Path.ChangeExtension(relativePath, ".wav");

            return relativePath;
        }

        private static string Convert2dNameTo3d(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return relativePath;

            int index = relativePath.LastIndexOf("2d", StringComparison.OrdinalIgnoreCase);
            return index < 0
                ? relativePath
                : relativePath.Substring(0, index) + "3d" + relativePath.Substring(index + 2);
        }

        private static string ResolveAudioPath(string contentPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(contentPath) || string.IsNullOrWhiteSpace(relativePath))
                return null;

            string normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(contentPath, "Audio", normalized));
        }

        private static string ResolveContentPath()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(baseDirectory ?? string.Empty, "..", "Content")),
                Path.GetFullPath(Path.Combine(baseDirectory ?? string.Empty, "..", "..", "Content")),
                @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content"
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

        internal struct ToolLoopWaveInfo
        {
            public readonly string CueName;
            public readonly string RelativePath;
            public readonly string AbsolutePath;

            public ToolLoopWaveInfo(string cueName, string relativePath, string absolutePath)
            {
                CueName = cueName;
                RelativePath = relativePath;
                AbsolutePath = absolutePath;
            }
        }
    }
}
