using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using LegacyText = UnityEngine.UI.Text;

namespace Reus2KR
{
    internal static class KoreanFontSize
    {
        internal const int MinPercent = 80, DefaultPercent = 100, MaxPercent = 120, Step = 1;
        private static ConfigEntry<int> percent;
        private static int pending = DefaultPercent;
        private static bool writing;
        private static bool needsRefresh, refreshing;
        internal static bool NeedsRefresh { get { return needsRefresh && !refreshing; } }
        private static readonly Regex AbsoluteSize = new Regex(@"(<size\s*=\s*[""']?)(\d+(?:\.\d+)?)([""']?\s*>)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private sealed class Size
        {
            internal float Value, Min, Max;
            internal bool Auto;
            internal string RawText, AppliedText;
            internal float AppliedRatio = 1f;
        }
        private sealed class LegacySize { internal int Value, Min, Max; internal bool Auto; }
        private static readonly Dictionary<TMP_Text, Size> sizes = new Dictionary<TMP_Text, Size>();
        private static readonly Dictionary<LegacyText, LegacySize> legacy = new Dictionary<LegacyText, LegacySize>();
        private static readonly List<TMP_Text> dead = new List<TMP_Text>();
        private static readonly List<LegacyText> deadLegacy = new List<LegacyText>();
        internal static int ClampPercent(int value)
        {
            value = Math.Max(MinPercent, Math.Min(MaxPercent, value));
            return MinPercent + (int)Math.Round((value - MinPercent) / (double)Step, MidpointRounding.AwayFromZero) * Step;
        }
        internal static int Percent { get { return ClampPercent(pending); } }
        private static float Ratio { get { return RuntimeUiFallback.KoreanActive ? Percent / 100f : 1f; } }
        internal static bool IsSettingsText(Component text)
        {
            for (Transform t = text == null ? null : text.transform; t != null; t = t.parent)
                if (t.name == "SettingsWindowUI" || t.name.StartsWith("SettingsWindowUI(", StringComparison.Ordinal)) return true;
            return false;
        }
        private static float EffectiveRatio(Component text) { return IsSettingsText(text) ? 1f : Ratio; }
        internal static void Initialize(ConfigFile config)
        {
            percent = config.Bind("KoreanFont", "SizePercent", DefaultPercent,
                new ConfigDescription("Text scale 80-120%, in 1% steps; 100 = original. Settings stay at 100%.", new AcceptableValueRange<int>(MinPercent, MaxPercent)));
            pending = ClampPercent(percent.Value);
            percent.SettingChanged += delegate
            {
                int updated = ClampPercent(percent.Value);
                if (pending != updated) { pending = updated; needsRefresh = true; }
            };
        }
        internal static void SetPercent(float normalized)
        {
            int updated = ClampPercent((int)Math.Round(MinPercent + Math.Max(0, Math.Min(1, normalized)) * (MaxPercent - MinPercent)));
            if (pending != updated) { pending = updated; needsRefresh = true; }
        }
        internal static float SliderValue { get { return (Percent - MinPercent) / (float)(MaxPercent - MinPercent); } }
        internal static void Save() { if (percent != null) percent.Value = Percent; }
        private static Size Remember(TMP_Text text)
        {
            Size state;
            if (!sizes.TryGetValue(text, out state))
            {
                state = new Size { Value = text.fontSize, Min = text.fontSizeMin, Max = text.fontSizeMax, Auto = text.enableAutoSizing };
                sizes.Add(text, state);
                text.OnPreRenderText += PreserveSpriteSizes;
            }
            return state;
        }
        internal static void SetSettingSize(TMP_Text text, float value)
        {
            var state = Remember(text);
            state.Value = state.Min = state.Max = value;
            state.Auto = false;
            Apply(text);
        }
        internal static void Rewrite(TMP_Text text, string property, ref float value)
        {
            if (writing || text == null || percent == null) return;
            if (pending == DefaultPercent && sizes.Count == 0) return;
            float ratio = EffectiveRatio(text);
            if (ratio == 1f && !sizes.ContainsKey(text)) return;
            var state = Remember(text);
            if (property == "set_fontSize") state.Value = value;
            else if (property == "set_fontSizeMin") state.Min = value;
            else state.Max = value;
            value *= ratio;
        }
        internal static void RewriteAuto(TMP_Text text, ref bool value)
        {
            if (writing || text == null || percent == null) return;
            if (EffectiveRatio(text) == 1f && !sizes.ContainsKey(text)) return;
            Remember(text).Auto = value;
            if (EffectiveRatio(text) != 1f) value = !AdaptiveTextLayout.CanExpand(text);
        }
        internal static string ScaleSizeMarkup(string text, float ratio)
        {
            if (text == null || ratio == 1f || text.IndexOf("size", StringComparison.OrdinalIgnoreCase) < 0) return text;
            return AbsoluteSize.Replace(text, match => match.Groups[1].Value +
                (double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * ratio)
                    .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + match.Groups[3].Value);
        }
        internal static void RewriteText(TMP_Text text, ref string value)
        {
            if (writing || text == null || value == null || percent == null) return;
            Size state;
            if (EffectiveRatio(text) == 1f && !sizes.TryGetValue(text, out state)) return;
            state = Remember(text);
            string raw = value == state.AppliedText ? state.RawText : value;
            state.RawText = raw;
            value = ScaleSizeMarkup(raw, EffectiveRatio(text));
            state.AppliedText = value;
            state.AppliedRatio = EffectiveRatio(text);
        }
        private static string RawForCurrent(Size state, string current)
        {
            if (current == state.AppliedText) return state.RawText;
            if (state.AppliedRatio != 1f && state.AppliedText != null)
            {
                var a = AbsoluteSize.Matches(current);
                var b = AbsoluteSize.Matches(state.AppliedText);
                bool same = a.Count > 0 && a.Count == b.Count;
                for (int i = 0; same && i < a.Count; i++) same = a[i].Value == b[i].Value;
                if (same) return ScaleSizeMarkup(current, 1f / state.AppliedRatio);
            }
            return current;
        }
        internal static void Apply(TMP_Text text)
        {
            if (text == null || !text.gameObject.scene.IsValid()) return;
            float ratio = EffectiveRatio(text);
            Size state;
            if (ratio == 1f && !sizes.TryGetValue(text, out state)) return;
            state = Remember(text);
            writing = true;
            try
            {
                bool expandable = ratio != 1f && AdaptiveTextLayout.CanExpand(text);
                bool auto = ratio == 1f ? state.Auto : !expandable;
                if (text.enableAutoSizing != auto) text.enableAutoSizing = auto;
                if (!Mathf.Approximately(text.fontSize, state.Value * ratio)) text.fontSize = state.Value * ratio;
                float minimum = auto && ratio != 1f ? Math.Min(state.Value * ratio, 8f) : state.Min * ratio;
                float maximum = ratio == 1f ? state.Max : state.Value * ratio;
                if (!Mathf.Approximately(text.fontSizeMin, minimum)) text.fontSizeMin = minimum;
                if (!Mathf.Approximately(text.fontSizeMax, maximum)) text.fontSizeMax = maximum;
                string current = text.text;
                if (!String.IsNullOrEmpty(current))
                {
                    string raw = RawForCurrent(state, current);
                    string scaled = ScaleSizeMarkup(raw, ratio);
                    state.RawText = raw; state.AppliedText = scaled;
                    state.AppliedRatio = ratio;
                    if (scaled != current) text.text = scaled;
                }
                if (expandable) AdaptiveTextLayout.Expand(text);
                if (ratio == 1f)
                {
                    AdaptiveTextLayout.Restore(text);
                    text.OnPreRenderText -= PreserveSpriteSizes;
                    sizes.Remove(text);
                }
            }
            finally { writing = false; }
        }
        private static void PreserveSpriteSizes(TMP_TextInfo info)
        {
            float ratio = EffectiveRatio(info.textComponent);
            Size state;
            if (ratio != 1f && sizes.TryGetValue(info.textComponent, out state) && state.Value > 0)
                ratio = info.textComponent.fontSize / state.Value;
            if (Mathf.Approximately(ratio, 1f) || ratio <= 0) return;
            for (int i = 0; i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                if (!ch.isVisible || ch.elementType != TMP_TextElementType.Sprite) continue;
                var vertices = info.meshInfo[ch.materialReferenceIndex].vertices;
                var origin = new Vector3(ch.origin, ch.baseLine, 0);
                for (int j = 0; j < 4; j++)
                {
                    int v = ch.vertexIndex + j;
                    vertices[v] = origin + (vertices[v] - origin) / ratio;
                }
            }
        }
        internal static void ApplyLegacy(LegacyText text)
        {
            if (text == null || !text.gameObject.scene.IsValid()) return;
            float ratio = EffectiveRatio(text);
            LegacySize state;
            if (ratio == 1f && !legacy.TryGetValue(text, out state)) return;
            if (!legacy.TryGetValue(text, out state))
                legacy[text] = state = new LegacySize { Value = text.fontSize, Min = text.resizeTextMinSize, Max = text.resizeTextMaxSize, Auto = text.resizeTextForBestFit };
            writing = true;
            try
            {
                text.resizeTextForBestFit = ratio == 1f ? state.Auto : true;
                int value = (int)Math.Round(state.Value * ratio);
                if (text.fontSize != value) text.fontSize = value;
                text.resizeTextMinSize = ratio == 1f ? state.Min : Math.Min(value, 8);
                text.resizeTextMaxSize = ratio == 1f ? state.Max : value;
                if (ratio == 1f) legacy.Remove(text);
            }
            finally { writing = false; }
        }
        internal static void RewriteLegacy(LegacyText text, ref int value)
        {
            if (writing || percent == null) return;
            LegacySize state;
            if (EffectiveRatio(text) == 1f && !legacy.TryGetValue(text, out state)) return;
            if (!legacy.TryGetValue(text, out state))
                legacy[text] = state = new LegacySize { Value = value, Min = text.resizeTextMinSize, Max = text.resizeTextMaxSize, Auto = text.resizeTextForBestFit };
            state.Value = value;
            value = (int)Math.Round(value * EffectiveRatio(text));
        }
        internal static void Refresh()
        {
            Prune();
            if (Ratio == 1f && sizes.Count == 0 && legacy.Count == 0) return;
            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>()) Apply(text);
            RefreshLegacy();
        }
        // Coalesce drag events into one rebuild before rendering. Existing
        // preferred-height/layout caches need invalidation as well as fontSize.
        internal static void RefreshNow()
        {
            if (!NeedsRefresh) return;
            refreshing = true;
            needsRefresh = false;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
                var layouts = new HashSet<RectTransform>();
                foreach (var text in texts)
                {
                    if (text == null || !text.gameObject.scene.IsValid() || IsSettingsText(text)) continue;
                    Apply(text);
                    text.havePropertiesChanged = true;
                    text.SetAllDirty();
                    RectTransform layout = null;
                    for (Transform p = text.transform; p != null; p = p.parent)
                        if (p.GetComponent<UnityEngine.UI.LayoutGroup>() != null) layout = p as RectTransform;
                    if (layout != null && layout.gameObject.activeInHierarchy) layouts.Add(layout);
                }
                foreach (var text in Resources.FindObjectsOfTypeAll<LegacyText>())
                {
                    if (text == null || !text.gameObject.scene.IsValid() || IsSettingsText(text)) continue;
                    ApplyLegacy(text);
                    text.SetAllDirty();
                    UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(text.rectTransform);
                }
                foreach (var layout in layouts) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(layout);
                foreach (var text in texts)
                    if (text != null && text.isActiveAndEnabled && text.gameObject.scene.IsValid() && !IsSettingsText(text))
                        text.ForceMeshUpdate(false, true);
                Reus2KRPlugin.Log?.LogInfo("Text scale refreshed live: " + Percent + "%; rebuilt layout roots=" + layouts.Count);
            }
            finally { refreshing = false; }
        }
        internal static void RefreshLegacy()
        {
            if (Ratio == 1f && legacy.Count == 0) return;
            foreach (var text in Resources.FindObjectsOfTypeAll<LegacyText>()) ApplyLegacy(text);
        }
        internal static void Prune()
        {
            dead.Clear(); foreach (var pair in sizes) if (pair.Key == null) dead.Add(pair.Key);
            foreach (var text in dead) sizes.Remove(text);
            deadLegacy.Clear(); foreach (var pair in legacy) if (pair.Key == null) deadLegacy.Add(pair.Key);
            foreach (var text in deadLegacy) legacy.Remove(text);
        }
    }
    [HarmonyPatch]
    internal static class PatchKoreanFontSize
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "fontSize", "fontSizeMin", "fontSizeMax" }) yield return AccessTools.PropertySetter(typeof(TMP_Text), name);
        }
        private static void Prefix(TMP_Text __instance, MethodBase __originalMethod, ref float __0)
        { KoreanFontSize.Rewrite(__instance, __originalMethod.Name, ref __0); }
    }
    [HarmonyPatch(typeof(TMP_Text), "set_enableAutoSizing")]
    internal static class PatchKoreanAutoSizing
    { private static void Prefix(TMP_Text __instance, ref bool __0) { KoreanFontSize.RewriteAuto(__instance, ref __0); } }
    [HarmonyPatch(typeof(LegacyText), "set_fontSize")]
    internal static class PatchLegacyFontSize
    { private static void Prefix(LegacyText __instance, ref int __0) { KoreanFontSize.RewriteLegacy(__instance, ref __0); } }

    // Serialized sizes bypass setters when Unity instantiates an object. Apply
    // before its first mesh generation, not at the next periodic sweep.
    [HarmonyPatch]
    internal static class PatchFirstTextMeshScale
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            yield return typeof(TextMeshProUGUI).GetMethod("GenerateTextMesh", flags);
            yield return typeof(TextMeshPro).GetMethod("GenerateTextMesh", flags);
        }
        [HarmonyPriority(Priority.First)]
        private static void Prefix(TMP_Text __instance) { KoreanFontSize.Apply(__instance); }
    }
    [HarmonyPatch(typeof(LegacyText), "OnPopulateMesh", new[] { typeof(UnityEngine.UI.VertexHelper) })]
    internal static class PatchFirstLegacyTextMeshScale
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(LegacyText __instance) { KoreanFontSize.ApplyLegacy(__instance); }
    }
}
