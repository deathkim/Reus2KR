using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Reus2KR
{
    // Grow only through an existing preferred-size layout chain. Arbitrarily
    // adding fitters to fixed cards can push adjacent UI off-screen.
    internal static class AdaptiveTextLayout
    {
        private sealed class Saved { internal LayoutElement Element; internal bool Created; internal float Height; }
        private static readonly Dictionary<TMP_Text, Saved> changed = new Dictionary<TMP_Text, Saved>();

        internal static bool CanExpand(TMP_Text text)
        {
            if (text == null || KoreanFontSize.IsSettingsText(text)) return false;
            var parent = text.transform.parent;
            if (parent == null) return false;
            var group = parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
            var parentFitter = parent.GetComponent<ContentSizeFitter>();
            var containingGroup = parent.parent == null ? null : parent.parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
            var outerFitter = parent.parent == null ? null : parent.parent.GetComponent<ContentSizeFitter>();
            return group != null && group.enabled && group.childControlHeight &&
                ((parentFitter != null && parentFitter.enabled && parentFitter.verticalFit == ContentSizeFitter.FitMode.PreferredSize) ||
                 (outerFitter != null && outerFitter.enabled && outerFitter.verticalFit == ContentSizeFitter.FitMode.PreferredSize)) &&
                containingGroup != null && containingGroup.enabled && containingGroup.childControlHeight;
        }

        internal static void Expand(TMP_Text text)
        {
            float width = text.rectTransform.rect.width - text.margin.x - text.margin.z;
            if (width <= 0) return;
            var preferred = text.GetPreferredValues(text.text, width, Mathf.Infinity);
            Saved saved;
            if (!changed.TryGetValue(text, out saved))
            {
                var element = text.GetComponent<LayoutElement>();
                bool created = element == null;
                if (created) element = text.gameObject.AddComponent<LayoutElement>();
                saved = new Saved { Element = element, Created = created, Height = element.preferredHeight };
                changed.Add(text, saved);
            }
            if (saved.Element == null) return;
            float height = preferred.y + text.margin.y + text.margin.w;
            if (Mathf.Abs(saved.Element.preferredHeight - height) > 0.1f)
            {
                saved.Element.preferredHeight = height;
                LayoutRebuilder.MarkLayoutForRebuild(text.rectTransform);
            }
        }

        internal static void Restore(TMP_Text text)
        {
            Saved saved;
            if (!changed.TryGetValue(text, out saved)) return;
            if (saved.Element != null)
            {
                if (saved.Created) Object.Destroy(saved.Element);
                else saved.Element.preferredHeight = saved.Height;
            }
            changed.Remove(text);
        }
    }
}
