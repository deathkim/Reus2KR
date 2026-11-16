using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using i18n;
using TMPro;

namespace Reus2KR
{
    [BepInPlugin("com.reus2kr.localization", "Reus 2 Korean Localization", "0.1.0")]
    public sealed class Reus2KRPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static string KoCsvPath
        {
            get
            {
                return Path.Combine(
                    Path.GetDirectoryName(typeof(Reus2KRPlugin).Assembly.Location),
                    "ko.csv");
            }
        }

        private void Awake()
        {
            Log = Logger;
            KoreanFontPicker.Initialize(Config);
            RuntimeUiFallback.Initialize();
            new Harmony("com.reus2kr.localization").PatchAll();
            // The plugin GameObject's Update() is not called in this game, so poll
            // the Esc reload check from the canvas render callback (once per frame).
            UnityEngine.Canvas.willRenderCanvases += OnCanvasRender;
            Log.LogInfo("Reus 2 Korean localization loaded. ko.csv: " + KoCsvPath);
            Log.LogInfo("ko.csv exists: " + File.Exists(KoCsvPath));
        }

        // Esc opens/closes the game's menu, which rebuilds the on-screen texts.
        // Before that happens, re-read ko.csv if it changed on disk: the game
        // keeps the table in MacroSettings.currentTranslation and skips the load
        // when the locale is unchanged, so clear the cached locale and set it
        // again; that runs ReadTranslations (our patch) and fires
        // TranslationsChanged.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private static bool escWasDown;
        private static int lastHotkeyFrame = -1;
        private static DateTime loadedWriteTimeUtc = DateTime.MinValue;

        private static void OnCanvasRender()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == lastHotkeyFrame) return;
            lastHotkeyFrame = frame;
            try
            {
                bool down = (GetAsyncKeyState(0x1B) & 0x8000) != 0 && UnityEngine.Application.isFocused;
                bool pressed = down && !escWasDown;
                escWasDown = down;
                if (pressed) ReloadKoCsvIfChanged();
            }
            catch (Exception error)
            {
                Log.LogError("Esc reload hook failed: " + error.Message);
            }
        }

        internal static void NoteLoaded(string path)
        {
            try { loadedWriteTimeUtc = File.GetLastWriteTimeUtc(path); }
            catch (Exception) { }
        }

        internal static void ReloadKoCsvIfChanged()
        {
            if (!RuntimeUiFallback.KoreanActive || !File.Exists(KoCsvPath)) return;
            DateTime writeTime = File.GetLastWriteTimeUtc(KoCsvPath);
            if (writeTime == loadedWriteTimeUtc) return;
            // Validate first: if the file is locked or broken the game must not
            // fall back to its built-in Chinese table.
            try
            {
                KoreanTranslationLoader.Load(KoCsvPath);
            }
            catch (Exception error)
            {
                Log.LogError("Esc reload skipped: ko.csv cannot be read now: " + error.Message);
                return;
            }

            string locale = MacroSettings.CurrentLocale;
            FieldInfo cached = typeof(MacroSettings).GetField(
                "currentLocale", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (cached == null)
            {
                Log.LogError("Esc reload failed: MacroSettings.currentLocale not found.");
                return;
            }
            try
            {
                cached.SetValue(null, null);
                MacroSettings.SetTranslationLocale(locale);
                loadedWriteTimeUtc = writeTime;
                Log.LogInfo("Esc: ko.csv changed on disk, reloaded.");
            }
            catch (Exception error)
            {
                cached.SetValue(null, locale);
                Log.LogError("Esc reload failed: " + error);
            }
        }
    }

    // The Japanese plugin also redirects zh_CN. Run this prefix first so the
    // Korean table wins when both plugins are installed.
    [HarmonyPatch(typeof(I18n_Translation), "ReadTranslations")]
    internal static class PatchReadTranslations
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(string locale, ref I18n_Translation.Translation __result)
        {
            if (locale != "zh_CN") return true;

            string path = Reus2KRPlugin.KoCsvPath;
            if (!File.Exists(path)) return true;

            try
            {
                __result = KoreanTranslationLoader.Load(path);
                Reus2KRPlugin.Log.LogInfo("Loaded Korean translation.");
                return false;
            }
            catch (Exception error)
            {
                // The CSV may be temporarily locked while an editor is saving it.
                // Let the game's original loader continue instead of breaking localization.
                Reus2KRPlugin.Log.LogError("Failed to read ko.csv: " + error);
                return true;
            }
        }

        // The Japanese plugin's prefix also handles zh_CN and runs after our
        // prefix, replacing __result with ja.csv. Restore ko.csv after all
        // prefixes have completed so the selected Japanese slot displays Korean.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(string locale, ref I18n_Translation.Translation __result)
        {
            if (locale != "zh_CN") return;

            string path = Reus2KRPlugin.KoCsvPath;
            if (!File.Exists(path)) return;

            try
            {
                __result = KoreanTranslationLoader.Load(path);
                Reus2KRPlugin.NoteLoaded(path);
                Reus2KRPlugin.Log.LogInfo("Restored Korean translation after other locale patches.");
                RuntimeUiFallback.SetTranslations(__result);
            }
            catch (Exception error)
            {
                // Avoid a crash if the user has the file open in an editor.
                Reus2KRPlugin.Log.LogError("Failed to restore ko.csv: " + error);
            }
        }
    }

    // HunterRule bypasses localization and returns the literal "Hunter".
    // Resolve its existing CSV key here so manual translation edits still apply.
    [HarmonyPatch(typeof(HunterRule), "GetName")]
    internal static class PatchHunterRuleName
    {
        internal static void Postfix(ref string __result)
        {
            string translated;
            if (__result == "Hunter" &&
                RuntimeUiFallback.TryTranslate("HUNTER_VISUALNAME", out translated))
                __result = translated;
        }
    }

    // Keep only the VSync label aligned with its sibling dropdown labels.
    // ToggleUI and DropdownUI ship with different label rectangle widths/offsets.
    [HarmonyPatch(typeof(VideoSettingsUI), "Render")]
    internal static class PatchVSyncLabelAlignment
    {
        private static readonly FieldInfo ToggleLabel = AccessTools.Field(typeof(ToggleUI), "label");
        private static readonly FieldInfo DropdownLabel = AccessTools.Field(typeof(DropdownUI), "label");

        private static void Postfix(ToggleUI ___vsyncToggle, DropdownUI ___windowModeDropdown)
        {
            if (!RuntimeUiFallback.KoreanActive || ___vsyncToggle == null ||
                ___windowModeDropdown == null || ToggleLabel == null || DropdownLabel == null) return;
            var target = ToggleLabel.GetValue(___vsyncToggle) as TextMeshProUGUI;
            var reference = DropdownLabel.GetValue(___windowModeDropdown) as TextMeshProUGUI;
            if (target == null || reference == null) return;
            var a = target.rectTransform;
            var b = reference.rectTransform;
            a.anchorMin = new UnityEngine.Vector2(b.anchorMin.x, a.anchorMin.y);
            a.anchorMax = new UnityEngine.Vector2(b.anchorMax.x, a.anchorMax.y);
            a.pivot = new UnityEngine.Vector2(b.pivot.x, a.pivot.y);
            a.sizeDelta = new UnityEngine.Vector2(b.sizeDelta.x, a.sizeDelta.y);
            a.anchoredPosition = new UnityEngine.Vector2(b.anchoredPosition.x, a.anchoredPosition.y);
            var margin = target.margin;
            margin.x = reference.margin.x;
            margin.z = reference.margin.z;
            target.margin = margin;
            target.horizontalAlignment = reference.horizontalAlignment;
        }
    }

    // The file is a GS-delimited translation table, not conventional comma CSV.
    // Record boundaries cannot be recognized by CRLF alone because keys and
    // translated values may themselves contain line breaks. The Steam 1.12 and
    // GOG 1.12.2 multi-line key union is embedded to recover record boundaries.
    // Users can freely edit translation values (including adding line breaks),
    // and CRLF/LF/CR conversions are normalized at load time. Do NOT edit keys
    // or delete GS separators: those identify the strings used by the game.
    internal static class KoreanTranslationLoader
    {
        // Compressed multiline KEY union, retaining historical key shapes.
        // These are keys with literal LF characters, NOT translations.
        // Kept inside the DLL so no additional files are needed.
        private const string MultiKeyData =
            "H4sIAAAAAAAC/+Vca3Mbx5X9jv/AqqFTu7YrMCxKdqqWpWgLJEGJWZFgQDCKw0KpBpgGMNZgBpkHKdjSf99z7u3uaYCUItl5bG38QbKAfty+fe+5z0an" +
            "07+N0yyeZiZK86hemujm8mX/YjB+ffVieDmJ7tJ6Gf188L631+ncvMZ/k05nr3NU5E11GP386P1e57zJ6nSdpaY85MDoK3waFbemjKZxZaI4T6Iph3+9" +
            "17k5upzonxH+GvePJvL/XObp9NnTahVn2SxeV8+el8Ud9v3P6KgoE1NWT79tv3v67fRZ5yyv1mZWW4JBaDe6Obu4GE5kv5tXg/7L8YtJVMxlxCytN135" +
            "M1rIyl0Zpovj3POiXMV1WuS9HTrO8luT84tKJgxW4NPq49TYMd1oZ+64jBMTjYqmNlU0K01cm0RZnlZC2u7elzh3kccZvvrohpz7ZRWt2+HdqC7jtNZ9" +
            "S5PJ0ap765fFj1hDR92c/3A1Hox+mPzNraKb6Ts3VbmNb9IyutqspmlRmaprP7THAml+8aip4oUBIbMiK8rf/+a/Hh//7vjo2cj8tUlLsKMuommDHfpX" +
            "l4Pj8dUEq8R11KwXwrtN0ZTRzdHZcHx23J/0IhznVZpl0RSExdVS2YmrhECMhlhhdDbGlrGSbkATpCGO1unsDYYOynhfzvdpCvDofe/pt0L1s49R31Dg" +
            "pykuIcUtZ+kbQ3ZhNkSxlP/97j1E9W6ZzpbRbBnnCyNbrrM4NzUJaPL0r42J7uJN1ftE2p58Om2yhCVI6dw4aszbNe6tCsnhPS7SW5wErI9ycxdVWVH/" +
            "MrpaqTpKi5WJTrBuWVGeBEtCset0fsB2i6JWIRLwkHtdxrdpvuBlREla1WkOsZxytUp3xFVnJq5qEZLXx++ARhCTMchbFaW5N4eHAlO6coBlulgCC2TH" +
            "0tzFZbIfEn0cZ/GK1B4vTVVvUTsyFQRzhgUXcZqD16A0K0DFvCxWERQxgUpgQasYoAIk4uy96AQXjYuZFau1qSEyt+B8aaqqKRU2yfEM/yaE4GAlFldi" +
            "ASIzMlEEGktD/nOqQRzx2tZFpYuFetCLXmKlqCqEk1zkNp3V6arXabXT0i90L9I4Bzasy6KGppPtHgBikpdhpThJMBxsMxAJAfGAYRx5Vcf1ztVu6WZp" +
            "5plA0LK4w4UXieq47IJ7SArs24vOAAEql1WzUkgvDf7BxQ87gv7R099vUX9zOhyeTKJNarKkYw3D7pDj69HZ0I9xFmN31J/6L6/7Ry8HV25k5wf+VQnL" +
            "izzbCPwUGc+BSZRDqAR0bKqWC4y3ZkYtj7uqVGFVQesuTRbUN4WquljrKcGCCpbC0CwFuK3KebUs1lusfYF7EJnmDlhckVms47tJV5jwbss84l8cvohr" +
            "XiHkNlf2W+23YAwNmTcZrspKeU/tN074gkZ+oqYeS6w4o8LF3C31IBtcoB19Tn2g3AAXBYxAosWfnqi7XDoxMynyL2vqD1Vg4wz4ziGIpVtnkOOqfqmV" +
            "CKXsTpZcFFj7Lu9FFwVsZbEGdlERoM1R3UwFQtSWLJuydrAG8ceRYTr7+A5HvhZ0nrRWSITlEC7RBwcoMqkvdDN+MRr0x5PoNs6g++FcqM/Pj9/bjwBc" +
            "e32RIpipiTUUITT3okEMzHYDljEUwd8LZuLT6I8NzjY5bPFPZANYk5naOAFTPJGvMRMqBYmVgcINUtPedx+z8ySlM4Fl5Upzg+usCEX+K5EbaKehRtBE" +
            "tAsM5nPxGg5Jn2oQIB22WT9Xa7802Zo3I9cASiAfA0jYRtDQ0qgkggOpwXQhFVikIzzjQYeiiSonSNvf61s2CMTKMYFyHJmAV2WxUaVTHv93Zyw+TJzn" +
            "MEU4UpNDOOG99CtdeJ7C3eJRul7tBBBmy4LiOzX1HXQ3+gJX+4WI5hewIitO+MKCMJdfQoENrlchVK/eW9epmcXObrcc9pN5zfhrZepDwd8jOxzkfFmK" +
            "rGyI3KeEy/MiMS2hWAygrvaHi+fmbR0ZLHe3NLmhRnMkHZj9TmeYz/yay3i93qhQB2DRVcMlS02bui6IcRlAnaqe3MZYoLcnhj66ggvhDnq0cQIpBqYp" +
            "c/69BuzXkMwiS5wZ7LYy2eRZMXsDdwU+bhSvzdvQJbQ3VpaEEo1B7C0onTC6/2PMOtzVfiEekl1axA1OQp3OmhXOL6vCVjoVcmBPR0RURj0lNRsMgUKy" +
            "rs8nvc6xzomo4qJs2I3jMbEg+vb2jlVoyEAuK1Ba15nadLgB2KEHQoya3zc5oIyDxMXEqlggYKQ6MX8lAOjNqjbTJ8AJAqg4wJ8no/7peBKcf0uN9u3Y" +
            "68t3j3VQ6I2LL1eBRcHuMRfOwMQs+m7iyQBFcUavNkpXIisgRgEbtLmvHIXWA6v2944lUOKa4/7Z875EdN7NI6PWcT1bCnonRk2JyLf4Jz8VsODC5IQu" +
            "k0otbMjl9fj1X4ZDXMw/YH3FU2ghNAksgKq+kgFX2KwpS4SEcAHnc4hWBi/HQp+q74mZx/gUkrHiGglNqTXqVD0PFWJBVzFuflo00GPa59APzlK4qiYJ" +
            "bHY0MhD5FYwkHRuBD0pbVhRveGX8JASikwJLEfXM21nWJGqKVyl8UWg2ltsb5AJPuOhxUxdlChwUc1PZkE3kTZCct0kcotNWNAvlDw/Y4+wh8TL2cEfw" +
            "UXwW64EgoZoVoo3CDY971ghOOp3dc1F2uR396z21Gwk2Fzkz5k0kro4Iowwt8QXu0J+9G1XLmL7QdKMwgONGi6yY4vuNhBIbNVk8xRxKXdyRf2UD1hyq" +
            "pox0Ses/c2NChGhGtU7LtNZhV/xQr8vOOBLAie3XuE0iTiUBQJSU8bzGfX5TYs9qd0jMo880qI2e0P6kxNvBbZHdmmSHVq/Zlt6907hcASivlDhijGKG" +
            "wFAaBErUkqPh6GQwej3482X/4upseDHptnGB2mEqxsxr1fQdl5/QtxTbEM/ewA9uyBXYAZ+fyWJslKiyacQ2x7Ro5VJLalnUa6KGYiOYcFmIZ9o7lePJ" +
            "oITIUzAb0mWGoD+d4s6eQ+AqhvskBJ9e1SZe8d/7GrgE89fNNEsriIlMP0X0XCGGTj4y+TlQK4/S+baDJbkgGlRe8WNrJyChhH8FvrT+UrVs25uA/u29" +
            "aFZxTp7Cs9MhcMhNPqO6i68uapC6DJOfUjGM12xNXIfWagvUQPcA0WSEtVfU7LjyfsHUzMU8OSG4GV+PLs4unl8Ozy5gJESUEZJvDoP0XYD9I0V7Bgn+" +
            "63Oj32355GOEcNUSFp7ZhFOwRY3uzcngT7pVFw6gHGjLdEGk1DSEhD5IY2/vzObWErMqIkk2aIAj5xRIatYEUvj4c0BbpCaLmZ7OUbNRrWnECV7ZMN26" +
            "TOr3lMWilNDgD4UNFkYG7s1jBa0yscLxkhaV8npzdjzoPx9MNFUyvoK3IucWP14+mkQ0v+5yYRC7ziLH4PGyAF5gQl98C7/Y88HQpzkrUqrHgDSq7y1x" +
            "id3Rpxs1BSMOCO7p+TtFCpjD/1PUigzT27m5vjj74/XgE+i/AKa/gIdq6Ege0s2M6JDtXdCW5WJ/bs77F/3Q3bEuV+vEYEPsdkUSNAcAwf2DzUdat00D" +
            "Jlmpt3eZYhWr/j82UDEXVDQ5EFd9WLHWsDkAZme9FH5twm7BMJ9JE2IdMwUYTICcS67I59EemEOPREySTgBzNWF2MwA6ix8pqQCBIbk+Qoxfr/M0fUZD" +
            "Elj3IOKxEVai8E1d8p4Llkif6dHlGxpKywN3/IpZnWI+d/5LHNUl1Gec1nG+v3WS38znv8N/chZ1HXggnuD18buDRxM1gsB0QVSbMpObEl1cFLA6vQdX" +
            "VO8WN0c5klsjQOb0Pl2MDzLXvY7jyEOLbF/LNK1pvO7Ca9ndlYuQVOvHyu6awNOLeGLtQZrjemPJTFsT0aZMP4u3kjyz+SMCFqT5153oH8ZM3nHVOj87" +
            "lxyw9CNrnCkzlqAbvk9WxGJgoFtTUy4kVvySHkKavwnYSfPHxHpaq4dh01yttFcxRf1uSWOg9rWScFfBRND80qL+VtjMzAZMA4I1OKL1xPkvVFyQ56tO" +
            "Ya5l7ddxzhMBYnvZYE3mQNp/qTiJeXSFHoHKaZMiYlajZm5loHxeSplBoCyFC0GnITUqRohaynSxrDX/0cyYNu5Frzgr2FDThIajiCWV8+aCklNYaQO1" +
            "2yxBVJAUzIRXVmFpIANv32tz7LBxL/TrQ9EHyKUSFnuXnfkGfCwKAOkTT2dlXQ5aMhC2tN4U3K0HoGIHRj8HPT+O4LvY/Sux9kpjoscuJO1FfTratzQp" +
            "AprgdbkQTXRJD1oeJsboyktkZINZK2aZmYvBgEZ0vmLeQr4nv+Ht+1y2iKfNZu9/3du7AvRoxl4iBJJ6cz64Gv5leHYMEw3u7l212WfHR5c20PE+NbLP" +
            "XIT7R5t0VG2jHsUr+rykUrJYN6PBaPjy5cRmCWZGiiZqGCB3LwliukddaFQkIzUnKaonsQlnSdRuU95zw5zqSCRUVX8bbkUydDq8QARhmlzSylmbcrTA" +
            "A+71hPiZDfjbQ+BYXU0RYXdJ+JrclFILVl/DF2OmNhl5rBr185P3+OM7/vG9ckYDBlUlCjku5M5g9zCH3xZRJMdu0wGHHc/eeVraRIdTwqRN3xz49E31" +
            "YAbJJpAuhuf9E3WOtnJWigXBwI9nmno20SjEqBq5UDstHyKqJbkbSRbNTdOEPxG9cNkmQzds08ZezqCcnn6H/55hdZk0ccoqcOestwBVkDJ8gJbe35eb" +
            "r49f9Ef0mD+Np69t6PP/l7NrAl0BYNb2imgtin4XV60o3xUlzB8LxrQdXA2+iBHPkTk6gVefHT0xrAVv9v1s8obBdprATZBUkmQv1trpoEYd4MCiHbNd" +
            "YmuxGmzhvGb0nzO5o4mjPK5dBRfTb7GiGhXwz1b3XQm2tZaXzquYGUb4+i38OPiQ1tgyRJmQVFwZWA8MM7xT0ixBh/hUJvE51UyLEd7TEFaLNRNGE5dt" +
            "PlNkQYo4itZiFtr4emNvjCkwuClJ7z4RKU82hYPLKK5rzY5k/UX+R5gLt39lCPenaZ5Wmga0N9zd3oxaASdGj419nExKV0nDtAUtRCVeyf7euI0HyYrW" +
            "UYzx3zPYVi93WmqyQeARS78unJTcqZYqmJXx1wPSsXKy3amw1VFikzy9zlkt2weNB7b5Rvov2i8ev2+35cRd+rfok0CHhsNVBQLC2v6J6T2qrC/2dyDM" +
            "rNbYQVJ1XCVm+utSVO+iSIzLidF/6NybIAkqLWibskzrotxYC4jRl1k804SiN/6MrVNFpVWQutZ16bdJ4iYvEObC7WNlxws39c6WvlKKapaqH2W9b60r" +
            "xkQG6IAt9ZkkSOcDDKAw81Y0jc3cMa0fQocRSixRTCPn7POKJQTiqX1vD2ubUvTON1K8iW76F2fn/ZdavNYMSvfvyKBfT4v4uJ9wxb92Jyx0xswnWFsR" +
            "G5nPxfDQzezSvtj8n/WQmQDl/fpzu8oCMZAdKCFxrRT+0za7o7MvR7bb+sqSoovUHYTFjOaLuc1cSJXAr7aUcFyIw9brQvqxitYp7EVa0pARZDb2mGk9" +
            "ydDRBEBjeOoCNYqsh1EETCUGIyCR+PRmeDzoX9Bfh9dfstKZxSXmS8oT5Pk9952F9XWXIEVD4T32kZi1zTY9MYttPbN6w/Fl0IpGgyLnxykR4OyrIrKZ" +
            "wBYl+i5b/ZwMCgtm7AyQVi0/cEWBO4D7bHhhLJC4gECHfKfVE80fCdLq5y3dLI3bA6q5t2AhPVbA2pdmEc82E/ctO5JEotaNoByLHGXxk8l9O6QUhPhV" +
            "i1aIu5gthWC8Civsau3Vt5NOgrXryvTZT2nykuwMDw8uAgnVSXLZgmY15ZdzbasII+qk69taaDql2wiz9EB+r97uB6F7aM85lxO24pTxdvQ7nGlciuQV" +
            "6zpdpT+ZdiFr2ReFwGfRntRablo1b4vYZtwZ+X8hOkrnbZWk3nKwXQrC5gvGheiCtMQcRG2PyKoJvFmNe5gPDofbxNsnj3/yCeOvt1LL0Xk6Kwtt69Fv" +
            "7lflmMbuW59pEqC+VQimsjsfmqvtsn4od7HRdlu9Zqrt+lKMiOIYT/6NMioIFJ5rE8NjGdxz69Dya24gdpvaqNqvBQJ3V9NiK6TjXHNbcUm9t2XOQK+6" +
            "4O2q8AY2sHhM7VSsL+tXEDvpwXOXL26GtF6Uce56DzSf+HjCRkv1njB6uzNIiNa8miYMVJ12BiS2WKx9GtH3j/4DFoNYo2rniKl6nYDjf4tTmp2jFP37" +
            "cutRwC0SFjQ0qqByf0JJkYdlRk0q2WZO3z8jTro2WB5xP2m5vD/LtXfapkaq0+4Y+gXO4Ltx2mWkwdKvIPqhpek56Opw5mmgPo1skFEpqNxcjs6uzvtj" +
            "Zt1o3Bfmq+prjrRGUAysUlRZV9e12cTaoirRQLjMbjge5xoLyJYH0pW0gjzexrVpG/wl3SzN41riV68qIgqbNfyJkoJBLwIL0GP63NZx2ksrY93IwupW" +
            "DrEXsWwIp3+RSlOKF8wHyoUPIPLnrh8nP8aM0neaQH/h0kN4gx9cmlr9obW3Q5e1CxesLQhjOeZY9WOfVqJfYd9M3HswAZtLc9toVCXmO3w1wQgTQQe3" +
            "TwTJLobjyVZwbSXFVYO9nLdncnEs5TnyKQjcOqZ2Bcc21h8M+hYevxf/QOSI9C+k7SKufDJJkjY+VpPOBt9PcDVRkn8xna4NUNypWaEEBWuKe2BNM5hy" +
            "Hr+1YjeK8zdS8/7+t/s+3tR+HlKog1wnhu3nqXzyeV/YYzuYjU7R4rNkCEigrOXMgS43NRvEDhaov/8sKuWoV2nQ8em6SEUzOYOCaB9xcGvfVEqpaQ+m" +
            "YY82Oy5xZN9OFJAD1ttWNV8nsK1D4SsUyw2E5riEFzBBt2zOsZuCL0HK8zzoUfvQLt1t05zmDPsZqdU1Q7lKIZTOnhSLxGQC3zLXbqLNax8qUBEM80W8" +
            "sMEeK1BuUrQy7OROZ5XKuLa6u1nyjCCvWNDLJU44SSvCZCVrtOWu1ScdceO7AnzLLL/0iOVR2Gbr/JslmZQXIuJ6dn3uIYbDz/ro+e0V8hmAxDn2Ht2Q" +
            "Tzqaq6gXUrx89qovHT6H0aUp5bEe5XOllcsSFthSsG5s+oZ9kUmsXcDz9ta8TWkxh4Y0AAlgwr9Fc5SLt4UBriNZpd4fj/goENKTrkDXN1k15TprhLEM" +
            "0R6oEuryaphkE3FfwFOu+DaMFhRB6LF1+pWEyo7fcjsuicHJVajyCjPhQtZRVpj/UvINWhyUQmfgt9r3mY5HYR2xfR1nnXMdKy+Q5B3cYceZ3g+011Ot" +
            "WXrnUrm7JMeCmcQFzTpMu9st1U1Tt7bYbi7Yvr/CArOaZ5FecU7FTMCBZ8u8dGC6ZjVL5P4/i2he8a8jEhscaa7iA+IiGUTtfGnH6puw7YHtc4eTj8tL" +
            "uVNHsxkVUn4XS4JnKrsECR7YSwctJFPtIQfDdJbsNJFS0AFI1X60ymeoHzKrNy+Go7O/DC/OhycDaZblPtZii2wmmt9qI/W/RbbN+uyQ7j5uWI5SIJfn" +
            "QbDquydqu3ndieRA/hh2qNoW1VlbgpIj/YMP7irrWqKxDkx0HK8loykpdHHXVobC3HbDPuB7aUjeuieRf+4CKmSwFUWBSX3x0A2cLbezNrhUeu33qLTx" +
            "omDQxGYN+Qw31KvQOzvv/zkK5+x/xoo/hBlAId2bnmh8D+ZlgW7rcYftde69in+EpRjqLr2NedX7tqkOVoaLW7PViaAy0a9dm+2T9147D6WxQc/X8Flk" +
            "rs519MPWY8bUv8prD8/we+JeEGqfvuYNKx9bBF0PxO57aQsxbL6hje9pbBZdK7q2z+rm5fWfr0dngyvu9jx4IBpHbbFEkyi37jGlxF85Pu/t2XpjtZUF" +
            "15jN9tHNYFJdVwrrU5I0Dl422bcM7glzEKSwCk+WbVcz4yTRQPPdbw8mkvoW2CulVU4xv/ZHF+fO5uVJD3thVdDkNx6+BePA1T3xWK7t3e8kQbrtWxXe" +
            "Wa/3QDZDXuq6N/6ua0SqRMejwZ/OjgfuWgdMJ7Bf+8Fcjs8A7fxWwI7gBZDhU6h7dvJ2t5r0/Qheu0fB1qlx/kznzEJgtfPbAnzPre8Nqm0RYZKr/UCY" +
            "HLYIbD8PbXeyGZjAFXcFYaHbvkCRBJ52BukraMgF4LLONurN2yfGNbNVictOwVmeam7wFsiOAKWS1UrbPKYlCcxKmnUGwwjlz5q3jbrQeXDbPd+x1RfA" +
            "pjJ6Dagm3sTYgodN2RMd/HOFan9PZeGD16DPtz/jFhwi8YvTwRWfprT3EH4mV9G4TJ4FrTLM5UhaYbqxHXJxoqUVqXSRzu0t2HXyRhsw9NcevDj60Eke" +
            "ybvHqK53ftHAT0/hy7pXHEVJFuwsbktQ2jJa1Ma2AgSveeVxBwT8j9fD8eDdS8NiyVo7LRdFYntQ4o1taAH2IFCG2yVog8uzB5O3W/sT6UT57cEjnOLV" +
            "YDLZe+hnVLbvKXgd/0suazzqnwxGw+txoDTTd/ceq2n07QvwiORY0V23v60isibVzsYWFrNYXnbbnfhSTHGLgyS97fNklTo5qwY2UZqFfE8zg5IsTHSL" +
            "6LQ1GH3PpsXFHgk/LWb2V3EmEij0gnSF9LZoSKRxa/t02nJXG61Un+uikBVfYGjG3PGHg20fPfPclcnmeul83yKaL8Ki79zSn+SXYHjRbRe2f7jIujS2" +
            "lOpqVauXLM83/JF9AZXGExEgO49n8tQDWHLGajSLjGrX5QV/ZQuo6rb7xuyFuC1g9bb7CunRbsjMJiVYiRamUCgsXZGna6JpOLVxjyfOT+nbPk3LF0kF" +
            "SpizZZ/ELYniJZvv3WNKVa2qDqrzhAInQ9G/6EI8++UybGn7X3ADj7+Rz55884ErOPj2n3YLF0UkujaxSdlZ+FRuxqetuTyKnNkfjtEClLXU2tHOojFW" +
            "sr9ahDXG8WJiX3rHCzDSulWS8fCD5FH7oQMqUXTtyX383nlMOEgdZ4eRNuzC46TfoT1M+gMgXMbeqLUyhFvXgyVAnW9139t5zLcAUzQ8kf6uYIyGDioQ" +
            "iOa66gpaS29juVntvdJg6v6ee41v/440+yt1d7GBzL5Lb0IdJMBzfRgc1uAZQhFO1UhNAdgSUE4Nb/9ptS6ZZc2B3r8v41uQlTzjD1LZhr3BwcF3j06f" +
            "+eYsl6pjA5JWSUSg5MmHBgPO0ZByqFoKQi2bXeUXSfheX18pyI993GkTyjpObYN4Eq/kx6gs2dM49069kmwDBP8bXf6BNPNBbemFfWhaHREnJ9DIzvaP" +
            "Z3lgkOfCLpgZVLN4jXjP5I0sIMfc+kkyH8+L3wd5lF9Zg4tYMtb5WpskXPTJ/dW5gnBBBvc1uHp4DUwXnP/kNbpCuxdcieK0MbBKF3k6h+O43e5+4BOE" +
            "XNQ5B9JAA3lYxeXGqmkqDR/MNCoISPcgG0ZmWpYWxzgxs9R1Nd25mN4Br7y5SG1DPzaGYDOZe7z7IFcfqrc/VmNK0/6ahj6kyBM7+ZKorb9pcq//oxsV" +
            "05rFOfvTUaGb02WrTvUmXbu0mE1k+l8ICH8kAzcM+OgEP94B4MV2iX1rHn+Vf32/56Xry/8WhdMw9yRiLF5FmS7koUD4m1i6obxQ4Ew5tM/ysD8h6Bal" +
            "TKoNYET9v5Lz+tcwUAAA";

        private static readonly Dictionary<string, List<string>> MultiKeysByTail = LoadMultiKeys();

        private static Dictionary<string, List<string>> LoadMultiKeys()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            byte[] compressed = Convert.FromBase64String(MultiKeyData);
            using (var source = new MemoryStream(compressed))
            using (var gzip = new System.IO.Compression.GZipStream(
                       source, System.IO.Compression.CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
            {
                foreach (string key in reader.ReadToEnd().Split('\x1d'))
                {
                    if (key.Length == 0) continue;
                    int lastBreak = key.LastIndexOf('\n');
                    string tail = key.Substring(lastBreak + 1);
                    List<string> candidates;
                    if (!result.TryGetValue(tail, out candidates))
                    {
                        candidates = new List<string>();
                        result.Add(tail, candidates);
                    }
                    candidates.Add(key);
                }
            }
            foreach (var list in result.Values)
                list.Sort((a, b) => b.Length.CompareTo(a.Length));
            return result;
        }

        private struct RecordBoundary
        {
            public int Start;
            public int Delimiter;
            public RecordBoundary(int start, int delimiter)
            {
                Start = start;
                Delimiter = delimiter;
            }
        }

        internal static I18n_Translation.Translation Load(string path)
        {
            string content = File.ReadAllText(path, Encoding.UTF8)
                .TrimStart('\uFEFF')
                .Replace("\r\n", "\n").Replace('\r', '\n');
            // The usual final line terminator is not part of the last value.
            if (content.EndsWith("\n", StringComparison.Ordinal))
                content = content.Substring(0, content.Length - 1);

            var boundaries = new List<RecordBoundary>();
            int scan = 0;
            while (true)
            {
                int delimiter = content.IndexOf('\x1d', scan);
                if (delimiter < 0) break;

                int lastLine = content.LastIndexOf('\n', delimiter - 1) + 1;
                string tail = content.Substring(lastLine, delimiter - lastLine);
                int start = lastLine;
                List<string> candidates;
                if (MultiKeysByTail.TryGetValue(tail, out candidates))
                {
                    foreach (string key in candidates)
                    {
                        int candidateStart = delimiter - key.Length;
                        if (candidateStart >= 0 &&
                            (candidateStart == 0 || content[candidateStart - 1] == '\n') &&
                            String.CompareOrdinal(content, candidateStart, key, 0, key.Length) == 0)
                        {
                            start = candidateStart;
                            break;
                        }
                    }
                }
                boundaries.Add(new RecordBoundary(start, delimiter));
                scan = delimiter + 1;
            }

            var translation = new I18n_Translation.Translation();
            int duplicates = 0;
            int invalidRecords = 0;
            int loaded = 0;
            for (int i = 0; i < boundaries.Count; i++)
            {
                RecordBoundary current = boundaries[i];
                int end = i + 1 < boundaries.Count
                    ? boundaries[i + 1].Start - 1 : content.Length;
                if (current.Start < 0 || current.Delimiter < current.Start ||
                    end < current.Delimiter + 1)
                {
                    invalidRecords++;
                    continue;
                }

                string key = content.Substring(current.Start,
                    current.Delimiter - current.Start);
                string value = content.Substring(current.Delimiter + 1,
                    end - current.Delimiter - 1);
                if (String.IsNullOrWhiteSpace(key) || value.Length == 0)
                {
                    invalidRecords++;
                    continue;
                }
                if (translation.keyToTranslation.ContainsKey(key))
                {
                    duplicates++;
                    continue;
                }
                translation.keyToTranslation[key] =
                    PatchKoreanParticles.PreparePlaceholders(value);
                loaded++;
            }
            if (Reus2KRPlugin.Log != null)
            {
                Reus2KRPlugin.Log.LogInfo("Korean multiline loader: " + loaded +
                    " entries; duplicate keys: " + duplicates +
                    "; invalid records: " + invalidRecords +
                    "; delimiters: " + boundaries.Count);
                if (duplicates > 0 || invalidRecords > 0)
                    Reus2KRPlugin.Log.LogWarning(
                        "Check the GS (U+001D) key/value separators in ko.csv.");
            }
            // A file with no valid translations is almost certainly a failed save.
            // Keep the existing game data rather than replacing it with an empty table.
            if (loaded == 0)
                throw new InvalidDataException("ko.csv contains no valid translation records.");
            return translation;
        }
    }

    // Some UI labels are serialized in prefabs or written through TMP SetText
    // overloads, bypassing both I18n and the TMP text property setter. Keep an
    // exact-key fallback for those labels and explicitly format dynamic dates.
    internal static class RuntimeUiFallback
    {
        internal static bool KoreanActive
        {
            get { return String.Equals(MacroSettings.CurrentLocale, "zh_CN", StringComparison.Ordinal); }
        }
        private static readonly Dictionary<string, string> Exact =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> RuntimeOverrides =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Diversity", "다양성" },
                { "Reus 2 on Console", "Reus 2 콘솔 출시" },
                {
                    "Reus 2 is out now on Switch and Xbox, along with the Ice Age DLC.",
                    "Reus 2가 빙하기 DLC와 함께 Nintendo Switch와 Xbox로 출시되었습니다."
                },
                { "Learn More", "자세히 보기" },
                { "Go to Store", "상점으로" },
                { "Available only when UI scale is set to 130%", "인터페이스 크기가 130%일 때만 사용할 수 있습니다." },
                { "Jurassic DLC release!", "쥐라기 DLC 출시!" },
                {
                    "Jump back in time to find dinosaurs, new leaders and a planet-wide threat!",
                    "과거로 돌아가 공룡과 새로운 지도자, 행성 전체를 위협하는 위험을 만나 보세요!"
                },
                {
                    "The new Mesozoic Wildlands biome shifts between three distinct modes depending on the biotica you place there. Each mode unlocks unique synergies and changes how the biome behave! Help the Paleontologist and three other new leaders to become Epic Sculptors and avert Armageddon.",
                    "새로운 생물군계인 중생대 황야는 배치한 바이오티카에 따라 서로 다른 세 가지 상태로 바뀝니다. 상태마다 고유한 공생 효과가 해금되고 생물군계의 작동 방식도 달라집니다! 고생물학자를 비롯한 새로운 지도자 네 명이 ‘위대한 조각가’가 되고 ‘세계의 종말’을 막도록 도와주세요."
                },
                { "Grasslands DLC released!", "대초원 DLC 출시!" },
                {
                    "A full new biome where you start fires to strengthen it, along with new Spirits and Turning Points!",
                    "불을 일으켜 땅을 강화하는 새로운 생물군계와 새로운 영혼, 전환점을 만나 보세요!"
                },
                {
                    "A dry and brittle land, the Grasslands sits in a balance of life and destruction. Stress increases on the land with every biotica you place. When the inevitable fire sparks and spreads across the land, utilising chain effects will upgrade the slots of land affected. Regrow the land with boosted and domesticated biotica! This DLC launched together with the Manchuria support update.",
                    "메마르고 부서지기 쉬운 초원은 생명과 파괴 사이에서 균형을 이룹니다. 바이오티카를 배치할 때마다 땅의 압력이 높아집니다. 피할 수 없는 불이 일어나 땅 전체로 번지면, 연쇄 효과를 활용해 불길이 닿은 슬롯을 강화할 수 있습니다. 강화하고 재배화한 바이오티카로 땅에 다시 생명을 불어넣으세요! 이 DLC는 만주 지원 업데이트와 함께 출시되었습니다."
                },
                { "Visit the Everglades!", "대습지로 떠나세요!" },
                {
                    "Explore the amphibian world in the Wetlands. More leaders, more eras.",
                    "습지에서 물과 땅을 넘나드는 생명의 세계를 탐험하세요. 더 많은 지도자와 시대가 기다립니다."
                },
                {
                    "The Everglades DLC introduces a whole new biome, the Wetlands, as well as new leaders and era options. The Wetlands make for a micro-heavy biome, where some patches are underwater and others are not! Use amphibious biotica, reptiles, birds, fish, and boost them with as many micros as you can.",
                    "대습지 DLC에는 새로운 생물군계인 습지와 새로운 지도자, 시대 선택지가 추가됩니다. 일부 토지는 물속에, 나머지는 물 밖에 있는 습지에서는 소형 창조물이 큰 역할을 합니다! 수륙 양용 바이오티카와 파충류, 새, 물고기를 활용하고, 최대한 많은 소형 창조물로 강화하세요."
                },
                { "The Ice Age DLC is out!", "빙하기 DLC 출시!" },
                {
                    "New Prehistoric Ice Age biome with unique mechanics & biotica. New leaders and era options",
                    "독특한 작동 방식과 바이오티카를 갖춘 새로운 선사시대 빙하기 생물군계, 새로운 지도자와 시대 선택지가 추가됩니다."
                },
                {
                    "The Ice Age DLC introduces a whole new biome, the Prehistoric Ice Age, adding unique biotica and a new look for the Frost Giant. With its own fresh strategic resource management challenges based on evolution, this DLC also brings new leaders and new Era options. Nature does not fear change or the cold as life always finds a way to adapt.",
                    "빙하기 DLC에는 새로운 생물군계인 선사시대 빙하기와 독특한 바이오티카, 서리 거인의 새로운 모습이 추가됩니다. 진화를 바탕으로 자원을 전략적으로 관리하는 새로운 도전과 함께, 새로운 지도자와 시대 선택지도 만나 볼 수 있습니다. 생명은 언제나 적응할 길을 찾아내기에 자연은 변화도 추위도 두려워하지 않습니다."
                },
                { "Join the Community", "커뮤니티에 참여하세요" },
                {
                    "Discuss strategy, meet the devs and get the latest news.",
                    "전략을 나누고 개발자들을 만나 최신 소식을 받아 보세요."
                }
            };
        private static readonly Regex EnglishDate = new Regex(
            @"(?m)^(?:<[^>\r\n]*>)*[ \t]*(?<month>January|February|March|April|May|June|July|August|September|October|November|December)[ \t]+" +
            @"(?<day>\d{1,2})(?:st|nd|rd|th)?,[ \t]+(?<year>\d{4})[ \t]*(?:</[^>\r\n]*>)*[ \t]*(?=\r?$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
        private static readonly Regex EnglishRemainingTime = new Regex(
            @"(?m)^(?:(?<days>\d+)[ \t]+days?(?:,[ \t]*)?)?" +
            @"(?:(?<hours>\d+)[ \t]+hours?(?:,[ \t]*)?)?" +
            @"(?:(?<minutes>\d+)[ \t]+minutes?)?[ \t]+left[ \t]*(?=\r?$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase |
            RegexOptions.Multiline);
        private static readonly Dictionary<string, int> MonthNumbers =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "January", 1 }, { "February", 2 }, { "March", 3 },
                { "April", 4 }, { "May", 5 }, { "June", 6 },
                { "July", 7 }, { "August", 8 }, { "September", 9 },
                { "October", 10 }, { "November", 11 }, { "December", 12 }
            };
        private static bool initialized;

        internal static void Initialize()
        {
            if (initialized) return;
            if (!File.Exists(Reus2KRPlugin.KoCsvPath)) return;
            try
            {
                I18n_Translation.Translation table =
                    KoreanTranslationLoader.Load(Reus2KRPlugin.KoCsvPath);
                SetTranslations(table);
                if (Reus2KRPlugin.Log != null)
                    Reus2KRPlugin.Log.LogInfo(
                        "Runtime UI fallback initialized: " + Exact.Count + " exact keys.");
            }
            catch (Exception error)
            {
                if (Reus2KRPlugin.Log != null)
                    Reus2KRPlugin.Log.LogError(
                        "Failed to initialize runtime UI fallback: " + error);
            }
        }

        internal static void SetTranslations(I18n_Translation.Translation table)
        {
            Exact.Clear();
            if (table != null)
            {
                foreach (KeyValuePair<string, string> pair in table.keyToTranslation)
                {
                    Exact[pair.Key] = pair.Value;
                }
            }
            initialized = true;
        }

        internal static bool TryTranslate(string input, out string output)
        {
            output = input;
            if (!KoreanActive) return false;
            if (String.IsNullOrEmpty(input)) return false;

            string exact;
            if (Exact.TryGetValue(input, out exact) && exact != input)
            {
                output = exact;
                return true;
            }

            if (RuntimeOverrides.TryGetValue(input, out exact) && exact != input)
            {
                output = exact;
                return true;
            }

            bool dateChanged = false;
            string dated = EnglishDate.Replace(input, delegate(Match date)
            {
                int month;
                if (MonthNumbers.TryGetValue(date.Groups["month"].Value, out month))
                {
                    dateChanged = true;
                    return date.Groups["year"].Value + "년 " + month + "월 " +
                        date.Groups["day"].Value + "일";
                }
                return date.Value;
            });
            bool timeChanged = false;
            string timed = EnglishRemainingTime.Replace(dated, delegate(Match time)
            {
                var parts = new List<string>();
                if (time.Groups["days"].Success)
                    parts.Add(time.Groups["days"].Value + "일");
                if (time.Groups["hours"].Success)
                    parts.Add(time.Groups["hours"].Value + "시간");
                if (time.Groups["minutes"].Success)
                    parts.Add(time.Groups["minutes"].Value + "분");
                if (parts.Count == 0) return time.Value;
                timeChanged = true;
                return String.Join(" ", parts.ToArray()) + " 남음";
            });
            if (dateChanged || timeChanged)
            {
                output = timed;
                return true;
            }
            return false;
        }

        internal static void Apply(TMP_Text text)
        {
            if (!KoreanActive) return;
            if (text == null) return;
            string before = text.text;
            string after;
            if (!TryTranslate(before, out after)) after = before;
            after = PatchKoreanParticles.ResolveAlternatives(after);
            if (after != before)
                text.text = after;
        }

        internal static void Apply(UnityEngine.UI.Text text)
        {
            if (!KoreanActive) return;
            if (text == null) return;
            string before = text.text;
            string after;
            if (!TryTranslate(before, out after)) after = before;
            after = PatchKoreanParticles.ResolveAlternatives(after);
            if (after != before)
                text.text = after;
        }
    }

    // The Collection screen's tag filter compares the *translated* tag name with
    // the English literals "Aquatic" and "Micro Haven", so those two filters match
    // nothing in any language except English. Compare with the same translated
    // names the tag getter returns instead.
    [HarmonyPatch]
    internal static class PatchCollectionTagFilter
    {
        private static MethodBase target;

        private static bool Prepare()
        {
            Type type = AccessTools.TypeByName("BioticumUnlockUI");
            if (type == null) return false;
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name == "RenderBiotica" && method.GetParameters().Length == 1)
                {
                    target = method;
                    return true;
                }
            }
            return false;
        }

        private static MethodBase TargetMethod()
        {
            return target;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo aquatic = AccessTools.Method(typeof(PatchCollectionTagFilter), "AquaticName");
            MethodInfo microHaven = AccessTools.Method(typeof(PatchCollectionTagFilter), "MicroHavenName");
            foreach (CodeInstruction instruction in instructions)
            {
                string literal = instruction.opcode == OpCodes.Ldstr ? instruction.operand as string : null;
                if (literal == "Aquatic")
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = aquatic;
                }
                else if (literal == "Micro Haven")
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = microHaven;
                }
                yield return instruction;
            }
        }

        private static string Name(string english)
        {
            try { return MacroSettings.L(english); }
            catch (Exception) { return english; }
        }

        internal static string AquaticName() { return Name("Aquatic"); }
        internal static string MicroHavenName() { return Name("Micro Haven"); }
    }

    [HarmonyPatch]
    internal static class PatchTmpOnEnableFallback
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in new[] { typeof(TextMeshProUGUI), typeof(TextMeshPro) })
            {
                MethodInfo method = type.GetMethod("OnEnable",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (method != null) yield return method;
            }
        }

        private static void Postfix(TMP_Text __instance)
        {
            KoreanFontSize.Apply(__instance);
            RuntimeUiFallback.Apply(__instance);
        }
    }

    [HarmonyPatch(typeof(UnityEngine.UI.Text), "set_text")]
    internal static class PatchLegacyTextSetterFallback
    {
        private static void Prefix(UnityEngine.UI.Text __instance, ref string __0)
        {
            string translated;
            if (RuntimeUiFallback.TryTranslate(__0, out translated))
                __0 = translated;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.UI.Text), "OnEnable")]
    internal static class PatchLegacyTextOnEnableFallback
    {
        private static void Postfix(UnityEngine.UI.Text __instance)
        {
            KoreanFontSize.ApplyLegacy(__instance);
            RuntimeUiFallback.Apply(__instance);
        }
    }

    // Default planet names are generated as
    // <UTC minutes>_<English animal name>_<Greek letter>. Keep that internal
    // session ID untouched and localize only MetaGameSession.VisualName.
    [HarmonyPatch(typeof(MetaGameSession), "get_VisualName")]
    internal static class PatchKoreanPlanetDisplayName
    {
        private static Dictionary<string, string> localizedAnimals;
        private static bool logged;
        private static readonly Dictionary<string, string> GreekLetters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Alpha", "알파" }, { "Beta", "베타" }, { "Gamma", "감마" },
                { "Delta", "델타" }, { "Epsilon", "엡실론" }, { "Zeta", "제타" },
                { "Eta", "에타" }, { "Theta", "세타" }, { "Iota", "이오타" },
                { "Kappa", "카파" }, { "Lambda", "람다" }, { "Mu", "뮤" },
                { "Nu", "뉴" }, { "Xi", "크시" }, { "Omicron", "오미크론" },
                { "Pi", "파이" }, { "Rho", "로" }, { "Sigma", "시그마" },
                { "Tau", "타우" }, { "Upsilon", "입실론" }, { "Phi", "피" },
                { "Chi", "카이" }, { "Psi", "프사이" }, { "Omega", "오메가" }
            };

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(string ___customName, ref string __result)
        {
            if (!RuntimeUiFallback.KoreanActive) return;
            // Never alter names explicitly chosen by the player.
            if (!String.IsNullOrWhiteSpace(___customName) ||
                String.IsNullOrWhiteSpace(__result) ||
                !File.Exists(Reus2KRPlugin.KoCsvPath))
                return;

            EnsureAnimalMap();
            if (localizedAnimals == null || localizedAnimals.Count == 0) return;
            __result = LocalizeDefaultName(__result, localizedAnimals);
        }

        private static void EnsureAnimalMap()
        {
            if (localizedAnimals != null && localizedAnimals.Count > 0) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DefinitionProvider provider = DefinitionProvider.Instance;
                if (provider == null) return;
                foreach (BioticumDefinition definition in
                    provider.GetAllDefinitions<BioticumDefinition>())
                {
                    if (definition == null || !definition.GetIsAnimal()) continue;
                    string english = definition.EnglishVisualName;
                    string localized = definition.VisualName;
                    if (String.IsNullOrWhiteSpace(english) ||
                        String.IsNullOrWhiteSpace(localized) ||
                        !Regex.IsMatch(localized, @"[가-힣]"))
                        continue;
                    map[NormalizeGeneratedAnimalName(english)] = localized;
                }
                localizedAnimals = map;
                if (!logged && map.Count > 0 && Reus2KRPlugin.Log != null)
                {
                    logged = true;
                    Reus2KRPlugin.Log.LogInfo(
                        "Korean planet display names enabled: " + map.Count +
                        " animal names; internal session IDs remain unchanged.");
                }
            }
            catch (Exception error)
            {
                if (Reus2KRPlugin.Log != null)
                    Reus2KRPlugin.Log.LogWarning(
                        "Could not build Korean planet-name map: " + error.Message);
            }
        }

        internal static string LocalizeDefaultName(string visualName,
            IDictionary<string, string> animalNames)
        {
            if (String.IsNullOrWhiteSpace(visualName) || animalNames == null)
                return visualName;
            int separator = visualName.LastIndexOf(' ');
            if (separator <= 0 || separator >= visualName.Length - 1)
                return visualName;

            string animal = visualName.Substring(0, separator);
            string greek = visualName.Substring(separator + 1);
            string localizedAnimal;
            string localizedGreek;
            if (!animalNames.TryGetValue(animal, out localizedAnimal) ||
                !GreekLetters.TryGetValue(greek, out localizedGreek))
                return visualName;
            return localizedAnimal + " " + localizedGreek;
        }

        private static string NormalizeGeneratedAnimalName(string englishName)
        {
            string identifier = englishName.Replace(" ", "_");
            foreach (char invalid in Path.GetInvalidFileNameChars())
                identifier = identifier.Replace(invalid.ToString(), String.Empty);
            return identifier.Replace("_", " ");
        }
    }

    // Only the event-log message body uses left alignment. Preserve vertical
    // alignment, city portrait buttons, filters, and unrelated notification UIs.
    [HarmonyPatch(typeof(ChatMessageComponent), "Render")]
    internal static class PatchChatLogLeftAlignment
    {
        private static readonly Type ChatLogType =
            typeof(ChatMessageComponent).Assembly.GetType("ChatLogUI");

        private static void Postfix(ChatMessageComponent __instance,
            TextMeshProUGUI ___text)
        {
            if (!RuntimeUiFallback.KoreanActive) return;
            if (__instance == null || ___text == null ||
                ChatLogType == null || __instance.GetComponentInParent(ChatLogType) == null)
                return;
            ___text.horizontalAlignment = HorizontalAlignmentOptions.Left;
        }
    }

    [HarmonyPatch(typeof(LanguageSelectionUI), "VisualizeOption")]
    internal static class PatchVisualizeOption
    {
        // The Japanese mod also renames the zh_CN option. Apply Korean last
        // so the shared language slot is visibly identified as Korean.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(string option, ref string __result)
        {
            if (option == "zh_CN" && File.Exists(Reus2KRPlugin.KoCsvPath))
                __result = "한국어 (MOD)";
        }
    }

    // Particle alternatives are resolved after game macros have replaced
    // placeholders, so {0}은(는) follows the final Korean word's batchim.
    [HarmonyPatch]
    internal static class PatchKoreanParticles
    {
        private static bool updating;

        // Keep Korean syllable runs together when TextMeshPro wraps text.
        // Ignore rich-text tags so their attributes and formatting stay intact.
        private static readonly Regex KoreanWordsOrTags = new Regex(
            @"<[^>]*>|[가-힣]{2,}", RegexOptions.Compiled);

        private static void Prefix(TMP_Text __instance, ref string __0)
        {
            if (updating || __instance == null || String.IsNullOrEmpty(__0)) return;
            if (!RuntimeUiFallback.KoreanActive) return;

            // Resolve exact CSV keys and dynamic English date labels even
            // when the game bypasses its normal I18n lookup.
            string translated;
            if (RuntimeUiFallback.TryTranslate(__0, out translated))
                __0 = translated;

            // Resolve visible particle alternatives before Korean word runs
            // are wrapped in <nobr>. Otherwise 거인을(를) can become
            // <nobr>거인을</nobr>(를), splitting the pattern in two.
            __0 = ResolveAlternatives(__0);

            if (!__instance.richText || __0.IndexOf('<') >= 0 &&
                __0.IndexOf("<nobr>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                KoreanFontSize.RewriteText(__instance, ref __0);
                return;
            }
            if (!Regex.IsMatch(__0, @"[가-힣]{2,}"))
            {
                KoreanFontSize.RewriteText(__instance, ref __0);
                return;
            }

            __instance.enableWordWrapping = !SettingsTextStyle.IsSingleLineLabel(__instance);
            __0 = KoreanWordsOrTags.Replace(__0, delegate(Match match)
            {
                string part = match.Value;
                if (part[0] == '<') return part;
                return "<nobr>" + part + "</nobr>";
            });
            KoreanFontSize.RewriteText(__instance, ref __0);
        }
        // Preserve the CSV; mark particles following runtime format arguments
        // for resolution after those arguments have become visible text.
        internal static string PreparePlaceholders(string value)
        {
            return Regex.Replace(value,
                @"(?<!\{)(\{\d+(?:,-?\d+)?(?::[^{}]+)?\})(?!\})([ \t\x22\x27‘’“”\)\]\}〉》」』】〕）]*(?:(?:</[A-Za-z][^>]*>)[ \t\x22\x27‘’“”\)\]\}〉》」』】〕）]*)*)(으로|로|은|는|을|를|이|가|과|와|이나|나|이랑|랑|이든가|든가|이든|든)(?=$|[\s.,!?;:…。！？<>\x22\x27’”\)\]〉》」』】〕）])",
                delegate(Match match)
                {
                    string particle = match.Groups[3].Value;
                    string alternatives;
                    switch (particle)
                    {
                        case "은": case "는": alternatives = "은(는)"; break;
                        case "을": case "를": alternatives = "을(를)"; break;
                        case "이": case "가": alternatives = "이(가)"; break;
                        case "과": case "와": alternatives = "과(와)"; break;
                        case "으로": case "로": alternatives = "(으)로"; break;
                        case "이나": case "나": alternatives = "이나(나)"; break;
                        case "이랑": case "랑": alternatives = "이랑(랑)"; break;
                        case "이든가": case "든가": alternatives = "이든가(든가)"; break;
                        default: alternatives = "이든(든)"; break;
                    }
                    return match.Groups[1].Value + match.Groups[2].Value + alternatives;
                });
        }
        private static readonly Regex Alternatives = new Regex(
            @"이\(가\)|은\(는\)|을\(를\)|과\(와\)|\(으\)로|으로\(로\)|이나\(나\)|이랑\(랑\)|이든가\(든가\)|이든\(든\)",
            RegexOptions.Compiled);

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var seen = new HashSet<MethodBase>();
            Type[] types = { typeof(TMP_Text), typeof(TextMeshProUGUI), typeof(TextMeshPro) };
            foreach (Type type in types)
            {
                PropertyInfo property = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo setter = property == null ? null : property.GetSetMethod();
                if (setter != null && !setter.IsAbstract && seen.Add(setter))
                    yield return setter;
            }

            // TMP SetText(string, float, ...) formats its arguments internally
            // and may never call the public text setter. Patch every string
            // overload so alternatives are resolved after {0}, {1}, ... have
            // become their final visible values.
            foreach (MethodInfo method in typeof(TMP_Text).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "SetText" || method.IsAbstract || !seen.Add(method))
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length > 0 && parameters[0].ParameterType == typeof(string))
                    yield return method;
            }
        }

        private static void Postfix(TMP_Text __instance)
        {
            if (updating || __instance == null) return;
            if (!RuntimeUiFallback.KoreanActive) return;
            string before = __instance.text;
            if (String.IsNullOrEmpty(before) || before.IndexOf("(", StringComparison.Ordinal) < 0)
                return;

            string after = ResolveAlternatives(before);

            if (after != before)
            {
                updating = true;
                try { __instance.text = after; }
                finally { updating = false; }
            }
        }

        internal static string ResolveAlternatives(string text)
        {
            if (String.IsNullOrEmpty(text)) return text;
            return Alternatives.Replace(text, delegate(Match match)
            {
                char last = PreviousHangul(text, match.Index);
                if (last == '\0') return match.Value;

                int syllable = last - 0xAC00;
                int jongseong = syllable % 28;
                bool hasBatchim = jongseong != 0;
                switch (match.Value)
                {
                    case "이(가)": return hasBatchim ? "이" : "가";
                    case "은(는)": return hasBatchim ? "은" : "는";
                    case "을(를)": return hasBatchim ? "을" : "를";
                    case "과(와)": return hasBatchim ? "과" : "와";
                    case "(으)로": return hasBatchim && jongseong != 8 ? "으로" : "로";
                    case "으로(로)": return hasBatchim && jongseong != 8 ? "으로" : "로";
                    case "이나(나)": return hasBatchim ? "이나" : "나";
                    case "이랑(랑)": return hasBatchim ? "이랑" : "랑";
                    case "이든가(든가)": return hasBatchim ? "이든가" : "든가";
                    case "이든(든)": return hasBatchim ? "이든" : "든";
                    default: return match.Value;
                }
            });
        }

        private static readonly Regex SpriteTag = new Regex(
            @"^<sprite\s+name\s*=\s*""?([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Last syllable of each icon's Korean name (MACROS_* display names).
        private static readonly Dictionary<string, char> SpriteReading =
            new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
            {
                { "food", '량' }, { "curio", '학' }, { "valuables", '금' }, { "wealth", '부' },
                { "pop", '구' }, { "inno", '술' }, { "culture", '화' }, { "mystery", '비' },
                { "prosperity", '영' }, { "eon", '간' }, { "awe", '외' }, { "bio", '성' },
                { "threat", '협' }, { "stress", '력' }, { "power", '힘' }, { "grand", '함' },
                { "shrouded", '함' }, { "savage", '성' }, { "rugged", '침' }, { "sacrifice", '물' },
                { "animal", '물' }, { "plant", '물' }, { "geo", '물' }, { "flora", '물' },
                { "border", '트' }, { "crevice", '트' }, { "free", '트' }, { "nomad", '트' },
                { "terraform", '트' }, { "up_charge", '트' }, { "aspect", '트' },
                { "prey", '감' }, { "score", '수' }, { "doom", '망' }, { "danger", '험' },
                { "reroll", '침' }, { "desert", '막' }, { "forest", '림' }, { "ocean", '다' },
                { "taiga", '림' }, { "rainforest", '림' }, { "savanna", '나' }, { "wetlands", '지' },
                { "grasslands", '원' }, { "hills", '덕' }, { "iceage", '라' }, { "mesozoic", '야' }
            };

        private static char SpriteLastSyllable(string tag)
        {
            Match match = SpriteTag.Match(tag);
            if (!match.Success) return ' ';
            char reading;
            // Unknown icons: choose the no-batchim form so the text never
            // shows the raw "이(가)" alternative.
            return SpriteReading.TryGetValue(match.Groups[1].Value, out reading) ? reading : '가';
        }

        private static char PreviousHangul(string text, int beforeIndex)
        {
            int index = beforeIndex - 1;
            while (index >= 0)
            {
                char current = text[index];
                // If a macro has not been expanded yet, do not infer the
                // particle from a Korean word earlier in the sentence.
                if (current == '}') return '\0';
                if (Char.IsWhiteSpace(current) || current == '\'' || current == '"' ||
                    current == '\u2019' || current == '\u201D' || current == '\u2018' ||
                    current == '\u201C' || current == ')' || current == ']' ||
                    "〉》」』】〕）".IndexOf(current) >= 0)
                {
                    index--;
                    continue;
                }

                if (current == '>')
                {
                    int tagStart = text.LastIndexOf('<', index);
                    if (tagStart >= 0)
                    {
                        // A variable made only of icons has no readable text.
                        // Use the Korean name of the LAST icon (e.g. valuables
                        // icon = 금) to choose the particle.
                        char iconChar = SpriteLastSyllable(
                            text.Substring(tagStart, index - tagStart + 1));
                        if (iconChar != ' ') return iconChar;
                        index = tagStart - 1;
                        continue;
                    }
                }

                if (current >= '\uAC00' && current <= '\uD7A3') return current;
                // Numeric results use the final digit's Korean reading.
                if (current >= '0' && current <= '9')
                    return "영일이삼사오육칠팔구"[current - '0'];
                if (Char.IsLetterOrDigit(current) || current == '.' || current == '!' ||
                    current == '?' || current == '\u2026' || current == ',')
                    return '\0';
                index--;
            }
            return '\0';
        }
    }
    // Reus 2: The move hint is kept in GiantMoveAbility's definition data.
    // The base GetTargetingHint() returns it without passing through localization.
    [HarmonyPatch(typeof(GiantAbilityDefinition), "GetTargetingHint")]
    internal static class PatchGiantMoveTargetingHint
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(GiantAbilityDefinition __instance, ref string __result)
        {
            if (!RuntimeUiFallback.KoreanActive) return;
            if (!(__instance is GiantMoveAbility)) return;
            if (String.IsNullOrEmpty(__result)) return;

            // Pass the known key directly. MacroSettings.L resolves it from the
            // current Korean translation table (ko.csv), including game macros.
            string translated = MacroSettings.L("MOVEABILITY_TARGETINGHINT");
            if (!String.IsNullOrEmpty(translated) &&
                translated != "MOVEABILITY_TARGETINGHINT")
                __result = translated;
        }
    }

}
