#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using Localizor;
using TMPro;
using UI;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    internal static class GameHud
    {
        public static float UnitsPerDesignPixel
        {
            get
            {
                try
                {
                    var height = Scenes.MainMenu.Options.defaultReferenceResolution.y;
                    return height > 0f ? height / 1080f : 2f;
                }
                catch (Exception) { return 2f; }
            }
        }

        public static (Transform Parent, int SiblingIndex, Canvas Canvas)? Slot()
        {
            var uis = InstanceBehavior<UIs>.Instance;
            var tasks = uis != null ? uis.tasksUI : null;
            if (tasks == null) return null;
            var canvas = tasks.GetComponentInParent<Canvas>(true);
            if (canvas == null) return null;
            var root = canvas.rootCanvas;
            var branch = tasks.transform;
            while (branch.parent != null && branch.parent != root.transform) branch = branch.parent;
            var index = branch.parent == root.transform ? branch.GetSiblingIndex() + 1 : root.transform.childCount;
            return (root.transform, index, root);
        }

        public static (TMP_FontAsset? Regular, TMP_FontAsset? Bold, string From) Fonts()
        {
            TMP_FontAsset? regular = null;
            var from = "";
            var hudTexts = InstanceBehavior<UIs>.Instance?.tasksUI?.GetComponentsInChildren<TMP_Text>(true) ?? Array.Empty<TMP_Text>();
            regular = hudTexts.Select(t => t.font).FirstOrDefault(f => f != null && IsRubik(f) && !IsBold(f));
            if (regular != null) from = "the Objectives panel";
            var loaded = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Where(IsRubik).ToList();
            if (regular == null)
            {
                regular = loaded.FirstOrDefault(f => !IsBold(f) && !f.name.ToLowerInvariant().Contains("italic"));
                if (regular != null) from = "loaded fonts";
            }
            if (regular == null)
            {
                try { regular = InstanceBehavior<GlobalReferences>.Instance?.GetFontByName(FontFace.Rubik); }
                catch (Exception) { regular = null; }
                if (regular != null) from = "GlobalReferences (sign font)";
            }
            if (regular == null)
            {
                regular = hudTexts.Select(t => t.font).FirstOrDefault(f => f != null);
                if (regular != null) from = "the Objectives panel (not Rubik)";
            }
            var bold = loaded.FirstOrDefault(f => IsBold(f) && !f.name.ToLowerInvariant().Contains("italic") && !f.name.ToLowerInvariant().Contains("semi"));
            return (regular, bold, from);
        }

        private static bool IsRubik(TMP_FontAsset font) => font.name.IndexOf("rubik", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsBold(TMP_FontAsset font) => font.name.IndexOf("bold", StringComparison.OrdinalIgnoreCase) >= 0;

        public static void HoverSound()
        {
            try { if (GameManager.IsInFocus) UiSoundHelper.Play(UiSound.Hover); }
            catch (Exception) { }
        }

        public static void ClickSound()
        {
            try { UiSoundHelper.Play(UiSound.Click); }
            catch (Exception) { }
        }

        public static void Deselect()
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (events != null) events.SetSelectedGameObject(null);
        }
    }

    internal static class Loc
    {
        public static string Text(string key)
        {
            try { return LocalizorManager.GetLocalization(key, null); }
            catch (Exception) { return key; }
        }

        public static string Format(string key, params (string Key, string Value)[] values)
        {
            try { return LocalizorManager.GetLocalization(key, values.ToDictionary(v => v.Key, v => v.Value)); }
            catch (Exception) { return key; }
        }
    }
}
