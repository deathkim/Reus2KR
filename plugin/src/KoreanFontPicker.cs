using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TextCore.LowLevel;

namespace Reus2KR
{
    internal static class KoreanFontPicker
    {
        private static ConfigEntry<string> selection;
        private static TMP_FontAsset active;
        private static bool dirty = true;
        private static bool reportApplied;
        private static bool ticking;
        private static float nextTick;
        private static readonly Dictionary<string, TMP_FontAsset> fonts = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<TMP_Text, Original> originals = new Dictionary<TMP_Text, Original>();
        private static readonly Dictionary<GameplaySettingsUI, DropdownUI> rows = new Dictionary<GameplaySettingsUI, DropdownUI>();
        private static readonly Dictionary<GameplaySettingsUI, SliderUI> sizeRows = new Dictionary<GameplaySettingsUI, SliderUI>();
        private static readonly Dictionary<Font, string> sourceFiles = new Dictionary<Font, string>();
        private static readonly FieldInfo WindowDropdown = AccessTools.Field(typeof(VideoSettingsUI), "windowModeDropdown");
        private static readonly FieldInfo ScaleSlider = AccessTools.Field(typeof(GameplaySettingsUI), "uiScaleSlider");
        private static readonly FieldInfo SliderValue = AccessTools.Field(typeof(SliderUI), "sliderValue");
        private static readonly FieldInfo SliderValueLabel = AccessTools.Field(typeof(SliderUI), "valueLabel");
        private sealed class Original { internal TMP_FontAsset Font; internal Material Material; }

        internal static bool TrySourceFile(Font source, out string path)
        {
            path = null;
            return source != null && sourceFiles.TryGetValue(source, out path);
        }

        internal static void Initialize(ConfigFile config)
        {
            KoreanFontSize.Initialize(config);
            selection = config.Bind("KoreanFont", "FileName", "",
                "TTF file name in BepInEx/plugins/Reus2KR; empty uses the game's default font.");
            selection.SettingChanged += delegate { dirty = true; };
            Canvas.willRenderCanvases += RenderTick;
        }

        private static void RenderTick()
        {
            PrewarmStep();
            if (KoreanFontSize.NeedsRefresh)
            {
                Tick();
                KoreanFontSize.RefreshNow();
                return;
            }
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 0.5f;
            Tick();
        }

        // Pre-populate the dynamic atlas with every glyph used by ko.csv so the
        // chosen font does not rasterize characters on first display. The work
        // is spread over frames with a small time budget.
        private static TMP_FontAsset prewarmAsset;
        private static char[] prewarmChars;
        private static int prewarmIndex;
        private static int prewarmDone;
        private static double prewarmMs;
        private static readonly Regex PrewarmMarkup =
            new Regex(@"<[^>]*>|\[[^\]]*\]|\{[^}]*\}", RegexOptions.Compiled);

        private static void StartPrewarm(TMP_FontAsset asset)
        {
            prewarmAsset = null;
            try
            {
                var set = new SortedDictionary<char, bool>();
                var table = KoreanTranslationLoader.Load(Reus2KRPlugin.KoCsvPath);
                foreach (string value in table.keyToTranslation.Values)
                {
                    if (String.IsNullOrEmpty(value)) continue;
                    foreach (char c in PrewarmMarkup.Replace(value, String.Empty))
                        if (c > ' ' && !Char.IsControl(c)) set[c] = true;
                }
                var list = new List<char>(set.Keys);
                prewarmChars = list.ToArray();
                prewarmIndex = 0;
                prewarmDone = 0;
                prewarmMs = 0;
                prewarmAsset = asset;
            }
            catch (Exception error)
            {
                Reus2KRPlugin.Log.LogWarning("Korean font pre-warm skipped: " + error.Message);
            }
        }

        private static void PrewarmStep()
        {
            TMP_FontAsset asset = prewarmAsset;
            if (asset == null) return;
            if (asset != active || prewarmChars == null || prewarmIndex >= prewarmChars.Length)
            {
                if (asset == active && prewarmChars != null)
                    Reus2KRPlugin.Log.LogInfo("Korean font pre-warmed: " + prewarmDone + "/" +
                        prewarmChars.Length + " glyphs in " + prewarmMs.ToString("F0") + " ms.");
                prewarmAsset = null;
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                while (prewarmIndex < prewarmChars.Length && watch.Elapsed.TotalMilliseconds < 3.0)
                {
                    if (asset.HasCharacter(prewarmChars[prewarmIndex], false, true)) prewarmDone++;
                    prewarmIndex++;
                }
            }
            catch (Exception error)
            {
                Reus2KRPlugin.Log.LogWarning("Korean font pre-warm stopped: " + error.Message);
                prewarmAsset = null;
            }
            prewarmMs += watch.Elapsed.TotalMilliseconds;
        }

        internal static string[] GetFontFiles(string folder)
        {
            var names = new List<string>();
            if (Directory.Exists(folder))
                foreach (string path in Directory.GetFiles(folder))
                    if (String.Equals(Path.GetExtension(path), ".ttf", StringComparison.OrdinalIgnoreCase))
                        names.Add(Path.GetFileName(path));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        internal static bool IsSafeFileName(string name)
        {
            return !String.IsNullOrEmpty(name) && name == Path.GetFileName(name) &&
                name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
                String.Equals(Path.GetExtension(name), ".ttf", StringComparison.OrdinalIgnoreCase);
        }

        private static string Folder { get { return Path.GetDirectoryName(Reus2KRPlugin.KoCsvPath); } }

        private static TMP_FontAsset Load(string filename)
        {
            if (!IsSafeFileName(filename)) throw new InvalidDataException("Invalid TTF file name.");
            TMP_FontAsset cached;
            if (fonts.TryGetValue(filename, out cached) && cached != null) return cached;
            string path = Path.Combine(Folder, filename);
            if (!File.Exists(path)) throw new FileNotFoundException("Selected TTF is missing.", filename);
            if (FontEngine.InitializeFontEngine() != FontEngineError.Success ||
                FontEngine.LoadFontFace(path, 90) != FontEngineError.Success)
                throw new InvalidDataException("Unity FontEngine could not load the TTF.");
            string family = FontEngine.GetFaceInfo().familyName;
            // TMP 3.x requires a Unity Font handle. Only this owned handle is
            // redirected to the exact TTF by PatchPrivateFontFace below.
            // This avoids installing fonts and distinguishes Bold/Light files
            // that share a family name.
            Font source = Font.CreateDynamicFontFromOSFont("Arial", 90);
            if (source == null) throw new InvalidDataException("Unity could not create the font: " + family);
            sourceFiles[source] = path;
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(source, 90, 9,
                GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
            if (asset == null) throw new InvalidDataException("TMP could not create a dynamic font asset.");
            asset.name = "Reus2KR_" + Path.GetFileNameWithoutExtension(filename);
            if (asset.fallbackFontAssetTable == null)
                asset.fallbackFontAssetTable = new List<TMP_FontAsset>();
            UnityEngine.Object.DontDestroyOnLoad(source);
            UnityEngine.Object.DontDestroyOnLoad(asset);
            if (!asset.HasCharacter('한', true, true))
            {
                UnityEngine.Object.Destroy(asset);
                sourceFiles.Remove(source);
                UnityEngine.Object.Destroy(source);
                throw new InvalidDataException("The selected TTF could not provide Korean glyphs.");
            }
            fonts[filename] = asset;
            StartPrewarm(asset);
            Reus2KRPlugin.Log.LogInfo("Loaded Korean TTF: " + filename + " (" + family + ")");
            return asset;
        }

        internal static void Tick()
        {
            if (ticking) return;
            ticking = true;
            try
            {
                TickCore();
                if (active == null) KoreanFontSize.Refresh();
            }
            catch (Exception error)
            {
                Reus2KRPlugin.Log.LogWarning("Korean font update reverted to default: " + error);
                Restore(); active = null;
                if (selection != null) selection.Value = "";
            }
            finally { ticking = false; }
        }

        private static void TickCore()
        {
            if (selection == null) return;
            if (!RuntimeUiFallback.KoreanActive || String.IsNullOrEmpty(selection.Value))
            {
                Restore(); active = null; return;
            }
            if (dirty)
            {
                dirty = false;
                try { active = Load(selection.Value); }
                catch (Exception error)
                {
                    Restore(); active = null;
                    Reus2KRPlugin.Log.LogWarning("Korean font reverted to default: " + error.Message);
                    selection.Value = "";
                    return;
                }
                reportApplied = true;
            }
            // Switching back from another language must restore the cached choice.
            if (active == null)
            {
                dirty = true;
                return;
            }
            int applied = 0;
            KoreanFontSize.Prune();
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                KoreanFontSize.Apply(text);
                Original original;
                if (!originals.TryGetValue(text, out original))
                {
                    original = new Original { Font = text.font, Material = text.fontSharedMaterial };
                    // Dropdown popup items are cloned from live templates. A
                    // clone may inherit our custom font; never save it as its
                    // "original" or add one custom font as another's fallback.
                    if (text.font != null && fonts.ContainsValue(text.font) && text.font.fallbackFontAssetTable != null)
                        foreach (var fallback in text.font.fallbackFontAssetTable)
                            if (fallback != null && !fonts.ContainsValue(fallback))
                            {
                                original = new Original { Font = fallback, Material = fallback.material };
                                break;
                            }
                    originals.Add(text, original);
                }
                if (original.Font != null && !fonts.ContainsValue(original.Font) && !active.fallbackFontAssetTable.Contains(original.Font))
                    active.fallbackFontAssetTable.Add(original.Font);
                if (text.font == active) continue;
                text.font = active;
                text.fontSharedMaterial = active.material;
                text.SetAllDirty();
                applied++;
            }
            if (reportApplied)
            {
                reportApplied = false;
                Reus2KRPlugin.Log.LogInfo("Korean font applied to " + applied + " texts; face=" + active.faceInfo.familyName + "/" + active.faceInfo.styleName);
            }
            KoreanFontSize.RefreshLegacy();
        }

        private static void Restore()
        {
            foreach (var pair in originals)
                if (pair.Key != null)
                {
                    pair.Key.font = pair.Value.Font;
                    pair.Key.fontSharedMaterial = pair.Value.Material;
                    pair.Key.SetAllDirty();
                }
            originals.Clear();
        }

        internal static void Render(GameplaySettingsUI owner)
        {
            if (selection == null || owner == null) return;
            DropdownUI row;
            if (!rows.TryGetValue(owner, out row) || row == null)
            {
                if (!RuntimeUiFallback.KoreanActive) return;
                var parent = owner.transform.parent;
                var video = parent.GetComponentInChildren<VideoSettingsUI>(true);
                var template = video == null ? null : WindowDropdown.GetValue(video) as DropdownUI;
                if (template == null) return;
                row = UnityEngine.Object.Instantiate(template, owner.transform, false);
                row.name = "Reus2KR_FontDropdown";
                row.transform.SetAsFirstSibling();
                rows[owner] = row;
                Reus2KRPlugin.Log.LogInfo("Korean font dropdown added to Interface settings.");
            }
            row.gameObject.SetActive(RuntimeUiFallback.KoreanActive);
            SliderUI sizeRow;
            if (!sizeRows.TryGetValue(owner, out sizeRow) || sizeRow == null)
            {
                var template = ScaleSlider.GetValue(owner) as SliderUI;
                if (template == null) return;
                sizeRow = UnityEngine.Object.Instantiate(template, owner.transform, false);
                sizeRow.name = "Reus2KR_FontSizeSlider";
                sizeRows[owner] = sizeRow;
            }
            sizeRow.gameObject.SetActive(RuntimeUiFallback.KoreanActive);
            if (!RuntimeUiFallback.KoreanActive) return;
            row.transform.SetSiblingIndex(0);
            sizeRow.transform.SetSiblingIndex(1);
            sizeRow.SetProps(new SliderProps
            {
                clickable = true, enabled = true, label = "글꼴 크기 (%)",
                valueMin = KoreanFontSize.MinPercent, valueMax = KoreanFontSize.MaxPercent,
                value = KoreanFontSize.SliderValue,
                tooltip = "기본값은 100%입니다. 게임 안의 글자를 80~120% 범위에서 1%씩 조절합니다. 설정 화면은 100%를 유지하며, 고정 박스에서는 공간에 맞춰 크기를 제한합니다.",
                onValueChanged = delegate(float value)
                {
                    KoreanFontSize.SetPercent(value);
                    if (SliderValue != null) SliderValue.SetValue(sizeRow, KoreanFontSize.SliderValue);
                    var nativeSlider = sizeRow.GetComponentInChildren<UnityEngine.UI.Slider>(true);
                    if (nativeSlider != null) nativeSlider.SetValueWithoutNotify(KoreanFontSize.SliderValue);
                    var valueLabel = SliderValueLabel.GetValue(sizeRow) as TMP_Text;
                    if (valueLabel != null) valueLabel.text = KoreanFontSize.Percent.ToString();
                    Tick();
                },
                onRelease = delegate { KoreanFontSize.Save(); Tick(); }
            });
            var files = GetFontFiles(Folder);
            var values = new List<string> { "기본 글꼴" };
            int index = 0;
            for (int i = 0; i < files.Length; i++)
            {
                values.Add(Path.GetFileNameWithoutExtension(files[i]));
                if (String.Equals(selection.Value, files[i], StringComparison.OrdinalIgnoreCase)) index = i + 1;
            }
            row.SetProps(new DropdownProps
            {
                clickable = true, label = "한국어 글꼴", values = values, currentValue = index,
                tooltip = "Reus2KR 폴더의 TTF 글꼴을 선택합니다. 기본 글꼴을 선택하면 원래 글꼴로 돌아갑니다.",
                onValueChanged = delegate(int chosen)
                {
                    selection.Value = chosen > 0 && chosen <= files.Length ? files[chosen - 1] : "";
                    dirty = true;
                    Tick();
                    Render(owner);
                }
            });
            SettingsTextStyle.Apply(owner);
            LayoutRebuilder.MarkLayoutForRebuild(owner.transform as RectTransform);
        }
    }

    [HarmonyPatch]
    internal static class PatchPrivateFontFace
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in typeof(FontEngine).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var args = method.GetParameters();
                if (method.Name == "LoadFontFace" && args.Length > 0 && args[0].ParameterType == typeof(Font))
                    yield return method;
            }
        }

        private static bool Prefix(object[] __args, ref FontEngineError __result)
        {
            string path;
            if (!KoreanFontPicker.TrySourceFile(__args[0] as Font, out path)) return true;
            int size = __args.Length > 1 ? (int)__args[1] : 90;
            int face = __args.Length > 2 ? (int)__args[2] : 0;
            __result = FontEngine.LoadFontFace(path, size, face);
            return false;
        }
    }

    [HarmonyPatch(typeof(GameplaySettingsUI), "Render")]
    internal static class PatchKoreanFontMenu
    {
        private static void Postfix(GameplaySettingsUI __instance)
        {
            try { KoreanFontPicker.Render(__instance); }
            catch (Exception error) { Reus2KRPlugin.Log.LogWarning("Font menu: " + error.Message); }
        }
    }
}
