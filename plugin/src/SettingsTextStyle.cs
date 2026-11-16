using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Reus2KR
{
    internal static class SettingsTextStyle
    {
        // Native settings label size; tabs/title/tooltips are outside these rows.
        internal const float BaseSize = 23f;
        private static readonly HashSet<TMP_Text> SingleLineLabels = new HashSet<TMP_Text>();
        internal static bool IsSingleLineLabel(TMP_Text text) { return SingleLineLabels.Contains(text); }
        private static readonly FieldInfo Scale = AccessTools.Field(typeof(GameplaySettingsUI), "uiScaleSlider");
        private static readonly FieldInfo SliderLabel = AccessTools.Field(typeof(SliderUI), "label");
        private static readonly Dictionary<Type, FieldInfo> Labels = new Dictionary<Type, FieldInfo>
        {
            { typeof(SliderUI), SliderLabel },
            { typeof(DropdownUI), AccessTools.Field(typeof(DropdownUI), "label") },
            { typeof(ToggleUI), AccessTools.Field(typeof(ToggleUI), "label") }
        };
        private static readonly Dictionary<Type, FieldInfo> Inputs = new Dictionary<Type, FieldInfo>
        {
            { typeof(SliderUI), AccessTools.Field(typeof(SliderUI), "sliderRectTransform") },
            { typeof(DropdownUI), AccessTools.Field(typeof(DropdownUI), "dropdownButton") },
            { typeof(ToggleUI), AccessTools.Field(typeof(ToggleUI), "toggleButton") }
        };

        private static void WidenLabel(Component control, Type type, TMP_Text label)
        {
            FieldInfo field;
            if (!Inputs.TryGetValue(type, out field) || field == null) return;
            var input = field.GetValue(control) as Component;
            var target = label.rectTransform;
            var parent = target.parent as RectTransform;
            var inputRect = input == null ? null : input.transform as RectTransform;
            if (parent == null || inputRect == null) return;
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            float left = parent.InverseTransformPoint(corners[0]).x;
            inputRect.GetWorldCorners(corners);
            float right = parent.InverseTransformPoint(corners[0]).x - 8f;
            if (right <= left) return;
            target.anchorMin = new Vector2(0, target.anchorMin.y);
            target.anchorMax = new Vector2(0, target.anchorMax.y);
            target.pivot = new Vector2(0, target.pivot.y);
            target.anchoredPosition = new Vector2(left - parent.rect.xMin, target.anchoredPosition.y);
            target.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, right - left);
            label.enableWordWrapping = false;
            SingleLineLabels.Add(label);
            label.overflowMode = TextOverflowModes.Overflow;
        }

        internal static void Apply(Component owner)
        {
            if (owner == null || !RuntimeUiFallback.KoreanActive) return;
            var parent = owner.transform.parent;
            var gameplay = parent == null ? null : parent.GetComponentInChildren<GameplaySettingsUI>(true);
            var slider = gameplay == null ? null : Scale.GetValue(gameplay) as SliderUI;
            var reference = slider == null ? null : SliderLabel.GetValue(slider) as TMP_Text;
            if (reference == null) return;
            foreach (var type in Labels)
                foreach (Component control in owner.GetComponentsInChildren(type.Key, true))
                {
                    var label = type.Value.GetValue(control) as TMP_Text;
                    if (label == null) continue;
                    var a = label.rectTransform;
                    var b = reference.rectTransform;
                    a.anchorMin = new Vector2(b.anchorMin.x, a.anchorMin.y);
                    a.anchorMax = new Vector2(b.anchorMax.x, a.anchorMax.y);
                    a.pivot = new Vector2(b.pivot.x, a.pivot.y);
                    a.anchoredPosition = new Vector2(b.anchoredPosition.x, a.anchoredPosition.y);
                    a.sizeDelta = new Vector2(b.sizeDelta.x, a.sizeDelta.y);
                    label.margin = new Vector4(reference.margin.x, label.margin.y, reference.margin.z, label.margin.w);
                    label.horizontalAlignment = HorizontalAlignmentOptions.Left;
                    WidenLabel(control, type.Key, label);
                    foreach (TMP_Text text in control.GetComponentsInChildren<TMP_Text>(true))
                        KoreanFontSize.SetSettingSize(text, BaseSize);
                }
            if (owner is UserProfileUI)
                foreach (TMP_Text text in owner.GetComponentsInChildren<TMP_Text>(true))
                    if (!String.IsNullOrWhiteSpace(text.text)) KoreanFontSize.SetSettingSize(text, BaseSize);
        }
    }

    [HarmonyPatch]
    internal static class PatchSettingsTextStyle
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VideoSettingsUI), "Render");
            yield return AccessTools.Method(typeof(AudioSettingsUI), "Render");
            yield return AccessTools.Method(typeof(UserProfileUI), "Render");
        }
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Component __instance) { SettingsTextStyle.Apply(__instance); }
    }
}
