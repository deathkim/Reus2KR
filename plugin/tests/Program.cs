using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using i18n;

namespace Reus2KR
{
    internal static class Program
    {
        private static int ExpectedRecords = 6533;
        private const string MultilineKey = "All {0}{2} gain:\n{1}";

        private static int CheckVersionUpdate(string csvPath, string reportPath)
        {
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath, Encoding.UTF8));
            var data = report.RootElement;
            string gameRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csvPath), "..", "..", ".."));
            string backup = Path.Combine(gameRoot, data.GetProperty("backup").GetString(), "ko.csv");
            var before = KoreanTranslationLoader.Load(backup).keyToTranslation;
            var actual = KoreanTranslationLoader.Load(csvPath).keyToTranslation;
            var expected = new Dictionary<string, string>(before, StringComparer.Ordinal);
            foreach (var change in data.GetProperty("changes").EnumerateArray())
                expected[change.GetProperty("key").GetString()] =
                    PatchKoreanParticles.PreparePlaceholders(change.GetProperty("after").GetString());
            Assert(before.Count == data.GetProperty("beforeCount").GetInt32(), "Backup record boundaries changed.");
            Assert(actual.Count == data.GetProperty("afterCount").GetInt32(), "Updated record count mismatch.");
            Assert(actual.Count == expected.Count, "Unexpected added or lost keys.");
            foreach (var pair in expected)
                Assert(actual.TryGetValue(pair.Key, out var value) && value == pair.Value,
                    "Unexpected translation or missing key: " + pair.Key);
            string content = File.ReadAllText(csvPath, Encoding.UTF8);
            Assert(content.Count(c => c == '\x1d') == actual.Count, "Duplicate or invalid record.");
            string temp = Path.Combine(Path.GetDirectoryName(reportPath), "version-update-loader-temp.csv");
            try
            {
                string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
                foreach (string newline in new[] { "\n", "\r\n", "\r" })
                {
                    File.WriteAllText(temp, normalized.Replace("\n", newline), new UTF8Encoding(true));
                    var variant = KoreanTranslationLoader.Load(temp).keyToTranslation;
                    Assert(variant.Count == actual.Count && actual.All(p => variant.TryGetValue(p.Key, out var v) && v == p.Value),
                        "Multiline/newline regression in updated table.");
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            Console.WriteLine("PASS VERSION_UPDATE: " + actual.Count + " records, old keys and unrelated manual edits preserved; LF/CRLF/CR/BOM passed.");
            return 0;
        }

        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--font-size-check")
            {
                string path = Path.Combine(Path.GetFullPath(args[1]), "font-size-check.cfg");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    var config = new BepInEx.Configuration.ConfigFile(path, false);
                    KoreanFontSize.Initialize(config);
                    Assert(KoreanFontSize.Percent == 100, "Font size default must be 100%.");
                    KoreanFontSize.SetPercent(0.5f);
                    Assert(KoreanFontSize.Percent == 100, "Slider midpoint must be 100%.");
                    KoreanFontSize.SetPercent(0.75f);
                    Assert(KoreanFontSize.Percent == 110, "Slider three-quarter value must be 110%.");
                    Assert(KoreanFontSize.NeedsRefresh, "A live size change must request a pre-render rebuild.");
                    Assert(config[new BepInEx.Configuration.ConfigDefinition("KoreanFont", "SizePercent")].BoxedValue.Equals(100), "Dragging must not save each step.");
                    KoreanFontSize.Save();
                    Assert(config[new BepInEx.Configuration.ConfigDefinition("KoreanFont", "SizePercent")].BoxedValue.Equals(110), "Release must save selection.");
                    KoreanFontSize.SetPercent(-10); Assert(KoreanFontSize.Percent == 80, "Minimum must be 80%.");
                    KoreanFontSize.SetPercent(10); Assert(KoreanFontSize.Percent == 120, "Maximum must be 120%.");
                    for (int n = 80; n <= 120; n++)
                    {
                        KoreanFontSize.SetPercent((n - 80) / 40f);
                        Assert(KoreanFontSize.Percent == n, "1% tick roundtrip failed.");
                    }
                    Assert(KoreanFontSize.ClampPercent(116) == 116 && KoreanFontSize.ClampPercent(114) == 114, "1% precision failed.");
                    Assert(KoreanFontSize.ScaleSizeMarkup("<size=20>A</size><size=80%>B</size><sprite name=food>", 2f) ==
                        "<size=40>A</size><size=80%>B</size><sprite name=food>", "Absolute text sizes must scale, relative sizes and sprite tags must not change.");
                    Assert(typeof(Program).Assembly.GetType("Reus2KR.PatchTmpSetTextFallback") == null, "Duplicate SetText patch remains.");
                    var targets = (IEnumerable<MethodBase>)typeof(PatchKoreanParticles).GetMethod("TargetMethods", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                    Assert(targets.Any(t => t.Name == "SetText"), "SetText translation coverage was removed.");
                    var meshTargets = ((IEnumerable<MethodBase>)typeof(PatchFirstTextMeshScale).GetMethod("TargetMethods", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null)).ToArray();
                    Assert(meshTargets.Length == 2 && meshTargets.All(t => t != null && t.Name == "GenerateTextMesh"),
                        "Both TMP first-render hooks must resolve in this game's assemblies.");
                    Console.WriteLine("PASS FONT_SIZE: default/range, live preview, deferred save, SetText patch consolidation.");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--font-picker-check")
            {
                var files = KoreanFontPicker.GetFontFiles(args[1]);
                Assert(files.Length > 0, "No test TTF files found.");
                foreach (var file in files) Assert(KoreanFontPicker.IsSafeFileName(file), "Invalid discovered TTF.");
                foreach (var file in new[] { "../bad.ttf", "..\\bad.ttf", "C:\\bad.ttf", "font.otf", "" })
                    Assert(!KoreanFontPicker.IsSafeFileName(file), "Unsafe font path accepted.");
                RuntimeUiFallback.SetTranslations(new I18n_Translation.Translation());
                var field = typeof(MacroSettings).GetField("currentLocale", BindingFlags.Static | BindingFlags.NonPublic);
                string input = "Available only when UI scale is set to 130%";
                field.SetValue(null, "zh_CN");
                Assert(RuntimeUiFallback.TryTranslate(input, out var result) && result == "인터페이스 크기가 130%일 때만 사용할 수 있습니다.", "Tooltip translation failed.");
                field.SetValue(null, "en");
                Assert(!RuntimeUiFallback.TryTranslate(input, out result) && result == input, "English tooltip changed.");
                Console.WriteLine("PASS FONT_PICKER: " + files.Length + " TTF files, safe names, bilingual tooltip.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--font-api")
            {
                foreach (var method in typeof(TMPro.TMP_FontAsset).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    if (method.Name.Contains("CreateFontAsset")) Console.WriteLine(method);
                foreach (var method in typeof(UnityEngine.TextCore.LowLevel.FontEngine).GetMethods())
                    if (method.Name == "LoadFontFace") Console.WriteLine(method);
                foreach (var ctor in typeof(UnityEngine.Font).GetConstructors()) Console.WriteLine(ctor);
                foreach (var field in typeof(TMPro.TMP_FontAsset).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0) Console.WriteLine(field);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--community-ads-check")
            {
                RuntimeUiFallback.SetTranslations(new I18n_Translation.Translation());
                FieldInfo locale = typeof(MacroSettings).GetField("currentLocale",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                using var report = JsonDocument.Parse(File.ReadAllText(args[1], Encoding.UTF8));
                int fields = 0;
                foreach (var ad in report.RootElement.GetProperty("ads").EnumerateArray())
                    foreach (var field in ad.GetProperty("fields").EnumerateObject())
                    {
                        string english = field.Value.GetProperty("english").GetString();
                        string korean = field.Value.GetProperty("korean").GetString();
                        locale.SetValue(null, "zh_CN");
                        Assert(RuntimeUiFallback.TryTranslate(english, out var result) && result == korean,
                            "Advertisement translation mismatch: " + english);
                        Assert(!RuntimeUiFallback.TryTranslate(english + " extra", out _),
                            "Advertisement must not use substring replacement.");
                        locale.SetValue(null, "en");
                        Assert(!RuntimeUiFallback.TryTranslate(english, out result) && result == english,
                            "Advertisement changed while English was selected.");
                        fields++;
                    }
                Assert(fields == 18, "Expected all five ads and their button fields.");
                Console.WriteLine("PASS COMMUNITY_ADS: five ads, 18 fields; Korean/English gating and exact matching.");
                return 0;
            }
            if (args.Length == 3 && args[0] == "--version-update-check")
                return CheckVersionUpdate(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--smoke-current")
            {
                ExpectedRecords = File.ReadAllText(args[1], Encoding.UTF8).Count(c => c == '\x1d');
                Assert(ExpectedRecords > 0, "No GS-delimited records.");
                args = new[] { args[1] };
            }
            if (args.Length == 3 && args[0] == "--phase-context")
                return ExportPhaseContext(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--raw-export")
                return ExportRaw(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--review-init")
                return InitializeReview(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--particle-audit")
                return RunParticleAudit(args[1]);
            if (args.Length == 3 && args[0] == "--particle-fix")
                return RunParticleFix(args[1], args[2]);
            if (args.Length == 4 && args[0] == "--placeholder-audit")
                return RunPlaceholderAudit(args[1], args[2], args[3]);
            if (args.Length == 2 && args[0] == "--quote-audit")
                return RunQuoteAudit(args[1]);
            if (args.Length == 1 && args[0] == "--name-generator-audit")
                return RunNameGeneratorAudit();
            if (args.Length >= 2 && args[0] == "--type-audit")
                return RunTypeAudit(args.Skip(1).ToArray());
            if (args.Length >= 2 && args[0] == "--method-search")
                return RunMethodSearch(args.Skip(1).ToArray());
            if (args.Length == 3 && args[0] == "--il-audit")
                return RunIlAudit(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--static-field")
                return RunStaticField(args[1], args[2]);

            if (args.Length < 1 || args.Length > 4)
            {
                Console.Error.WriteLine(
                    "Usage: Reus2KRLoaderSmokeTests <ko.csv> " +
                    "[key-reference.csv] [english-keys.csv] [english-locale.csv]");
                return 2;
            }

            string source = File.ReadAllText(args[0], Encoding.UTF8)
                .TrimStart('\uFEFF')
                .Replace("\r\n", "\n").Replace('\r', '\n');

            int separators = 0;
            foreach (char c in source)
                if (c == '\x1d') separators++;
            Assert(separators == ExpectedRecords,
                "GS separator count was " + separators + ", expected " + ExpectedRecords + ".");

            string extraValueBreak = source.Replace(
                "Unknown\x1d알 수 없음", "Unknown\x1d알 수\n없음");
            Assert(extraValueBreak != source, "Could not prepare the multiline-value test case.");

            var variants = new Dictionary<string, string>
            {
                { "LF", source },
                { "CRLF", source.Replace("\n", "\r\n") },
                { "CR", source.Replace("\n", "\r") },
                { "UTF8_BOM_LF", "\uFEFF" + source },
                { "EXTRA_VALUE_LINE", extraValueBreak }
            };

            string tempRoot = Path.Combine(AppContext.BaseDirectory, "test-artifacts",
                "Reus2KR-loader-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            foreach (KeyValuePair<string, string> variant in variants)
            {
                string path = Path.Combine(tempRoot, variant.Key + ".csv");
                File.WriteAllText(path, variant.Value, new UTF8Encoding(false));
                I18n_Translation.Translation table = KoreanTranslationLoader.Load(path);

                Assert(table.keyToTranslation.Count == ExpectedRecords,
                    variant.Key + ": loaded " + table.keyToTranslation.Count + " records.");
                Assert(table.keyToTranslation.ContainsKey(MultilineKey),
                    variant.Key + ": multiline key was not recovered.");
                Assert(table.keyToTranslation.ContainsKey("MOVEABILITY_TARGETINGHINT"),
                    variant.Key + ": move-hint key is missing.");
                Assert(table.keyToTranslation.ContainsKey("Unknown"),
                    variant.Key + ": Unknown key is missing.");

                string expectedUnknown = variant.Key == "EXTRA_VALUE_LINE"
                    ? "알 수\n없음" : "알 수 없음";
                Assert(table.keyToTranslation["Unknown"] == expectedUnknown,
                    variant.Key + ": translation value line breaks were not preserved.");

                Console.WriteLine("PASS " + variant.Key + ": " +
                    table.keyToTranslation.Count + " records");
            }

            Assert(PatchKoreanParticles.PreparePlaceholders("{0}은") ==
                "{0}은(는)", "Basic particle placeholder conversion failed.");
            Assert(PatchKoreanParticles.PreparePlaceholders("{1}</b>로 이동") ==
                "{1}</b>(으)로 이동", "Rich-text particle placeholder conversion failed.");
            Assert(PatchKoreanParticles.PreparePlaceholders("{{0}}은") ==
                "{{0}}은", "Escaped format braces must remain unchanged.");
            Assert(PatchKoreanParticles.ResolveAlternatives(
                "태양을(를) 숭배합니다.") == "태양을 숭배합니다.",
                "Batchim particle resolution failed.");
            Assert(PatchKoreanParticles.ResolveAlternatives(
                "<b>바다</b>을(를) 숭배합니다.") == "<b>바다</b>를 숭배합니다.",
                "Rich-text particle resolution failed.");
            Assert(PatchKoreanParticles.ResolveAlternatives(
                "2을(를) 선택하세요.") == "2를 선택하세요.",
                "Numeric particle resolution failed.");
            Assert(PatchKoreanParticles.ResolveAlternatives(
                "{1}을(를) 숭배합니다.") == "{1}을(를) 숭배합니다.",
                "Unexpanded placeholders must remain unresolved.");
            Assert(PatchKoreanParticles.ResolveAlternatives(
                "<b><sprite name=\"food\"><sprite name=\"curio\"><sprite name=\"valuables\"></b>이(가) 필요합니다.") ==
                "<b><sprite name=\"food\"><sprite name=\"curio\"><sprite name=\"valuables\"></b>이 필요합니다.",
                "Icon-only variable (last icon = valuables 금) particle failed.");
            Assert(PatchKoreanParticles.ResolveAlternatives(
                "<sprite name=wealth>을(를) 얻습니다.") == "<sprite name=wealth>를 얻습니다.",
                "Icon-only variable (wealth 부) particle failed.");
            Console.WriteLine("PASS PARTICLE_PLACEHOLDERS");

            I18n_Translation.Translation runtimeTable =
                KoreanTranslationLoader.Load(args[0]);
            RuntimeUiFallback.SetTranslations(runtimeTable);
            FieldInfo localeField = typeof(MacroSettings).GetField("currentLocale",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert(localeField != null, "Current locale field is missing.");
            localeField.SetValue(null, "zh_CN");
            string hunterName = "Hunter";
            PatchHunterRuleName.Postfix(ref hunterName);
            Assert(hunterName == runtimeTable.keyToTranslation["HUNTER_VISUALNAME"],
                "HunterRule must resolve its existing translation key.");
            string customRuleName = "Custom Hunter";
            string koreanHunterName = hunterName;
            PatchHunterRuleName.Postfix(ref customRuleName);
            Assert(customRuleName == "Custom Hunter", "Custom rule names must be preserved.");
            runtimeTable.keyToTranslation["HUNTER_VISUALNAME"] = "手動編集テスト";
            RuntimeUiFallback.SetTranslations(runtimeTable);
            hunterName = "Hunter";
            PatchHunterRuleName.Postfix(ref hunterName);
            Assert(hunterName == "手動編集テスト", "HunterRule must follow manual CSV edits.");
            RuntimeUiFallback.SetTranslations(KoreanTranslationLoader.Load(args[0]));
            Console.WriteLine("PASS HUNTER_RULE_NAME");
            string runtimeText;
            Assert(RuntimeUiFallback.TryTranslate("Continue", out runtimeText) &&
                runtimeText == "계속", "Runtime Continue fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate("October 26, 2026", out runtimeText) &&
                runtimeText == "2026년 10월 26일", "October date fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate("November 1, 2026", out runtimeText) &&
                runtimeText == "2026년 11월 1일", "November date fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate("<i>November 1, 2026</i>", out runtimeText) &&
                runtimeText == "2026년 11월 1일", "Rich-text date fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate(
                "November 1, 2026\n다음 항목까지 15시간", out runtimeText) &&
                runtimeText == "2026년 11월 1일\n다음 항목까지 15시간",
                "Multiline date fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate(
                "October 17th, 2023\n2 days, 4 hours left", out runtimeText) &&
                runtimeText == "2023년 10월 17일\n2일 4시간 남음",
                "Ordinal date fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate("4 hours left", out runtimeText) &&
                runtimeText == "4시간 남음", "English remaining-time fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate("Reus 2 on Console", out runtimeText) &&
                runtimeText == "Reus 2 콘솔 출시", "News-title fallback failed.");
            Assert(RuntimeUiFallback.TryTranslate("Learn More", out runtimeText) &&
                runtimeText == "자세히 보기", "News-button fallback failed.");
            Assert(!RuntimeUiFallback.TryTranslate("Continue playing", out runtimeText),
                "Runtime fallback must not replace English substrings.");
            Console.WriteLine("PASS RUNTIME_UI_FALLBACK");
            foreach (string locale in new[] { "en", "FR", "", null, "zh_CN", "en", "zh_CN" })
            {
                localeField.SetValue(null, locale);
                bool korean = locale == "zh_CN";
                Assert(RuntimeUiFallback.KoreanActive == korean, "Locale switch state is stale.");
                bool translated = RuntimeUiFallback.TryTranslate("Continue", out runtimeText);
                Assert(translated == korean && runtimeText == (korean ? "계속" : "Continue"),
                    "Continue must only translate in Korean mode.");
                translated = RuntimeUiFallback.TryTranslate("October 26, 2026", out runtimeText);
                Assert(translated == korean, "Date translation must follow the selected language.");
                hunterName = "Hunter";
                PatchHunterRuleName.Postfix(ref hunterName);
                Assert(hunterName == (korean ? koreanHunterName : "Hunter"),
                    "Hunter must follow the selected language.");
            }
            Console.WriteLine("PASS LIVE_LOCALE_SWITCHING");

            var planetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Cyclotosaurus", "키클로토사우루스" },
                { "Blue Whale", "흰긴수염고래" }
            };
            Assert(PatchKoreanPlanetDisplayName.LocalizeDefaultName(
                "Cyclotosaurus Rho", planetNames) == "키클로토사우루스 로",
                "Generated planet name localization failed.");
            Assert(PatchKoreanPlanetDisplayName.LocalizeDefaultName(
                "Blue Whale Omega", planetNames) == "흰긴수염고래 오메가",
                "Multiword generated planet name localization failed.");
            Assert(PatchKoreanPlanetDisplayName.LocalizeDefaultName(
                "My Custom Planet", planetNames) == "My Custom Planet",
                "Unknown/custom planet names must remain unchanged.");
            Console.WriteLine("PASS PLANET_DISPLAY_NAMES");

            if (args.Length >= 2)
            {
                I18n_Translation.Translation korean = KoreanTranslationLoader.Load(args[0]);
                I18n_Translation.Translation reference = KoreanTranslationLoader.Load(args[1]);
                Assert(reference.keyToTranslation.Count == ExpectedRecords,
                    "Reference loaded " + reference.keyToTranslation.Count + " records.");

                foreach (string key in reference.keyToTranslation.Keys)
                    Assert(korean.keyToTranslation.ContainsKey(key),
                        "Korean table is missing reference key: " + Escape(key));
                foreach (string key in korean.keyToTranslation.Keys)
                    Assert(reference.keyToTranslation.ContainsKey(key),
                        "Korean table contains an unknown key: " + Escape(key));
                Console.WriteLine("PASS REFERENCE_KEYSET: " + ExpectedRecords +
                    " keys exactly match");

            }

            if (args.Length >= 3)
            {
                I18n_Translation.Translation korean = KoreanTranslationLoader.Load(args[0]);
                I18n_Translation.Translation english = KoreanTranslationLoader.Load(args[2]);
                I18n_Translation.Translation englishLocale = args.Length == 4
                    ? KoreanTranslationLoader.Load(args[3]) : null;
                int tokenMismatches = 0;
                int checkedRecords = 0;
                foreach (string key in korean.keyToTranslation.Keys)
                {
                    string sourceText;
                    if (Regex.IsMatch(key, @"^[A-Z0-9_]+$") &&
                        englishLocale != null &&
                        englishLocale.keyToTranslation.TryGetValue(key, out string localeValue))
                        sourceText = localeValue;
                    else if (Regex.IsMatch(key, @"^[A-Z0-9_]+$") &&
                        english.keyToTranslation.TryGetValue(key, out string keyValue))
                        sourceText = keyValue;
                    else
                        sourceText = key;

                    HashSet<string> required = TokenSet(sourceText);
                    if (required.Count == 0) continue;
                    checkedRecords++;
                    HashSet<string> actual = TokenSet(korean.keyToTranslation[key]);
                    // Reviewed translation-only repair: the original [BIO|C]
                    // passes literal C to FMACROS_BIO; the sentence needs the
                    // unparameterized resource name. Keep its source KEY intact.
                    if (key == "You already have another {0} that has the required [BIO|C]." &&
                        actual.Contains("MACRO:BIO") && !actual.Contains("MACRO:BIO|C"))
                    {
                        required.Remove("MACRO:BIO|C");
                        required.Add("MACRO:BIO");
                        Console.WriteLine("PASS REVIEWED_MACRO_REPAIR: BIO|C -> BIO (exact KEY)");
                    }
                    var missing = new List<string>();
                    foreach (string token in required)
                        if (!actual.Contains(token)) missing.Add(token);
                    if (missing.Count == 0) continue;

                    tokenMismatches++;
                    if (tokenMismatches <= 30)
                    {
                        Console.Error.WriteLine("TOKEN MISMATCH " + Escape(key));
                        Console.Error.WriteLine("  missing: " + String.Join(", ", missing));
                    }
                }
                Assert(tokenMismatches == 0,
                    "Found " + tokenMismatches + " records with missing source tokens.");
                Console.WriteLine("PASS SOURCE_TOKENS: " + checkedRecords +
                    " token-bearing records");
            }

            Console.WriteLine("Temporary test files: " + tempRoot);
            return 0;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        private static int ExportPhaseContext(string phasePath, string outputPath)
        {
            using JsonDocument phase = JsonDocument.Parse(File.ReadAllText(phasePath));
            var keys = phase.RootElement.EnumerateArray().Select(x =>
                x.GetProperty("key").GetString()).ToHashSet(StringComparer.Ordinal);
            var refs = keys.ToDictionary(k => k, k => new HashSet<string>(), StringComparer.Ordinal);
            Assembly assembly = typeof(LanguageSelectionUI).Assembly;
            var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (OpCode)f.GetValue(null)).ToDictionary(op => unchecked((ushort)op.Value));
            foreach (Type type in assembly.GetTypes())
            foreach (MethodBase method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
            {
                byte[] bytes;
                try { bytes = method.GetMethodBody()?.GetILAsByteArray(); }
                catch { continue; }
                if (bytes == null) continue;
                for (int i = 0; i < bytes.Length;)
                {
                    ushort code = bytes[i++];
                    if (code == 0xfe) code = (ushort)(0xfe00 | bytes[i++]);
                    if (!opCodes.TryGetValue(code, out OpCode op)) break;
                    if (op.OperandType == OperandType.InlineString)
                    {
                        try
                        {
                            string value = method.Module.ResolveString(BitConverter.ToInt32(bytes, i));
                            if (refs.TryGetValue(value, out HashSet<string> sites))
                                sites.Add(type.FullName + "::" + method.Name);
                        }
                        catch { }
                    }
                    int size = op.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
                            OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                        _ => 4
                    };
                    i += size;
                }
            }
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(phasePath), ".."));
            string refRoot = Path.Combine(root, "Reus2KR_Codex_Handoff", "reference");
            var english = KoreanTranslationLoader.Load(Path.Combine(refRoot, "en.bytes")).keyToTranslation;
            var englishKeys = KoreanTranslationLoader.Load(Path.Combine(refRoot, "translation_keys.bytes")).keyToTranslation;
            var chinese = KoreanTranslationLoader.Load(Path.Combine(refRoot, "0140_zh_CN.bytes")).keyToTranslation;
            var output = phase.RootElement.EnumerateArray().Select(x =>
            {
                string key = x.GetProperty("key").GetString();
                return new
                {
                    ordinal = x.GetProperty("ordinal").GetInt32(), key,
                    english = Regex.IsMatch(key, @"^[A-Z0-9_]+$") ?
                        (english.TryGetValue(key, out string en) ? en :
                            englishKeys.TryGetValue(key, out string keyText) ? keyText : key) : key,
                    chinese = chinese.TryGetValue(key, out string zh) ? zh : null,
                    callSites = refs[key].OrderBy(s => s).ToArray()
                };
            });
            File.WriteAllText(outputPath, JsonSerializer.Serialize(output,
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("CONTEXT " + keys.Count + "; WITH_CALLS=" + refs.Count(x => x.Value.Count > 0));
            return 0;
        }

        private static int ExportRaw(string csvPath, string outputPath)
        {
            string content = File.ReadAllText(csvPath, Encoding.UTF8).TrimStart('\uFEFF')
                .Replace("\r\n", "\n").Replace('\r', '\n');
            if (content.EndsWith("\n", StringComparison.Ordinal)) content = content[..^1];
            var multi = (Dictionary<string, List<string>>)typeof(KoreanTranslationLoader)
                .GetField("MultiKeysByTail", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var boundaries = new List<(int start, int delimiter)>();
            for (int scan = 0;;)
            {
                int delimiter = content.IndexOf('\x1d', scan);
                if (delimiter < 0) break;
                int line = content.LastIndexOf('\n', delimiter - 1) + 1;
                string tail = content.Substring(line, delimiter - line);
                int start = line;
                if (multi.TryGetValue(tail, out List<string> candidates))
                    foreach (string key in candidates)
                    {
                        int candidate = delimiter - key.Length;
                        if (candidate >= 0 && (candidate == 0 || content[candidate - 1] == '\n') &&
                            String.CompareOrdinal(content, candidate, key, 0, key.Length) == 0)
                        { start = candidate; break; }
                    }
                boundaries.Add((start, delimiter));
                scan = delimiter + 1;
            }
            var records = boundaries.Select((b, i) => new
            {
                ordinal = i + 1,
                key = content.Substring(b.start, b.delimiter - b.start),
                value = content.Substring(b.delimiter + 1,
                    (i + 1 < boundaries.Count ? boundaries[i + 1].start - 1 : content.Length) - b.delimiter - 1)
            }).ToArray();
            Assert(records.Length == ExpectedRecords && records.Select(r => r.key).Distinct().Count() == ExpectedRecords,
                "Raw record export failed.");
            File.WriteAllText(outputPath, JsonSerializer.Serialize(records,
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("RAW_RECORDS=" + records.Length);
            return 0;
        }

        private static int InitializeReview(string csvPath, string outputDirectory)
        {
            string manifestPath = Path.Combine(outputDirectory, "manifest.json");
            if (File.Exists(manifestPath))
                throw new InvalidOperationException("Review manifest already exists; do not reset review progress.");
            string raw = File.ReadAllText(csvPath, Encoding.UTF8).TrimStart('\uFEFF')
                .Replace("\r\n", "\n").Replace('\r', '\n');
            var table = KoreanTranslationLoader.Load(csvPath);
            var records = table.keyToTranslation.Keys.Select(key => new
            {
                key,
                start = raw.IndexOf(key + "\x1d", StringComparison.Ordinal)
            }).OrderBy(item => item.start).ToArray();
            Assert(records.Length == ExpectedRecords && records.All(item => item.start >= 0),
                "Could not identify every raw record.");
            Directory.CreateDirectory(outputDirectory);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csvPath)));
            var phases = new List<object>();
            int begin = 0;
            for (int phase = 1; phase <= 10; phase++)
            {
                int count = ExpectedRecords / 10 + (phase <= ExpectedRecords % 10 ? 1 : 0);
                var slice = records.Skip(begin).Take(count).ToArray();
                var entries = slice.Select((item, offset) => new
                {
                    ordinal = begin + offset + 1,
                    key = item.key,
                    baselineTranslation = table.keyToTranslation[item.key]
                }).ToArray();
                File.WriteAllText(Path.Combine(outputDirectory, "phase-" + phase.ToString("D2") + ".json"),
                    JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                phases.Add(new
                {
                    phase, startOrdinal = begin + 1, endOrdinal = begin + count, count,
                    firstKey = slice.First().key, lastKey = slice.Last().key,
                    status = "not_started", reviewed = 0, changed = 0,
                    holds = 0, gameVerification = "not_tested"
                });
                Console.WriteLine("PHASE " + phase + ": " + (begin + 1) + "-" +
                    (begin + count) + " (" + count + ")");
                begin += count;
            }
            File.Copy(csvPath, Path.Combine(outputDirectory, "ko_baseline.csv"), false);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                version = 1, recordCount = ExpectedRecords, baselineSha256 = hash,
                partition = "fixed original record order; keys in phase files are authoritative",
                phases
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return 0;
        }

        private static int RunQuoteAudit(string path)
        {
            I18n_Translation.Translation table = KoreanTranslationLoader.Load(path);
            int records = 0;
            int singles = 0;
            int doubles = 0;
            foreach (KeyValuePair<string, string> pair in table.keyToTranslation)
            {
                string visible = Regex.Replace(pair.Value,
                    @"<[^>]*>|\[[^\]]*\]|\{[^}]*\}", delegate(Match match)
                    {
                        return new string(' ', match.Length);
                    });
                int singleCount = visible.Count(c => c == '\'');
                int doubleCount = visible.Count(c => c == '"');
                if (singleCount == 0 && doubleCount == 0) continue;
                records++;
                singles += singleCount;
                doubles += doubleCount;
                Console.WriteLine(Escape(pair.Key) + "\tS=" + singleCount +
                    "\tD=" + doubleCount + "\t" + Escape(pair.Value));
            }
            Console.Error.WriteLine("QUOTE_RECORDS=" + records +
                "; SINGLE=" + singles + "; DOUBLE=" + doubles);
            return 0;
        }

        private static int RunNameGeneratorAudit()
        {
            Assembly assembly = typeof(LanguageSelectionUI).Assembly;
            foreach (Type type in assembly.GetTypes().OrderBy(t => t.FullName))
            {
                MemberInfo[] members = type.GetMembers(BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                bool relevant = type.FullName.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.FullName.IndexOf("Planet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    members.Any(m => m.Name.IndexOf("GenerateRandomName",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("PlanetName", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("apocalypsePlanetName", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("eldritchPlanetName", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!relevant) continue;
                MemberInfo[] selected = members.Where(m =>
                    m.Name.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Name.IndexOf("Planet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Name.IndexOf("Random", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(m => m.MemberType).ThenBy(m => m.Name).ToArray();
                if (selected.Length == 0) continue;
                Console.WriteLine("TYPE " + type.FullName);
                foreach (MemberInfo member in selected)
                    Console.WriteLine("  " + member.MemberType + " " + member);
            }
            return 0;
        }

        private static int RunTypeAudit(string[] typeNames)
        {
            Assembly assembly = typeof(LanguageSelectionUI).Assembly;
            foreach (string name in typeNames)
            {
                Type type = assembly.GetType(name, false, true) ??
                    assembly.GetTypes().FirstOrDefault(t =>
                        String.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (type == null)
                {
                    Console.WriteLine("NOT_FOUND " + name);
                    continue;
                }
                Console.WriteLine("TYPE " + type.FullName);
                foreach (MemberInfo member in type.GetMembers(BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderBy(m => m.MemberType).ThenBy(m => m.Name))
                    Console.WriteLine("  " + member.MemberType + " " + member);
            }
            return 0;
        }

        private static int RunMethodSearch(string[] names)
        {
            Assembly assembly = typeof(LanguageSelectionUI).Assembly;
            foreach (Type type in assembly.GetTypes())
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
                if (names.Any(name => method.Name.IndexOf(name,
                    StringComparison.OrdinalIgnoreCase) >= 0))
                    Console.WriteLine(type.FullName + " :: " + method);
            return 0;
        }

        private static int RunIlAudit(string typeName, string methodName)
        {
            Assembly assembly = typeof(LanguageSelectionUI).Assembly;
            Type type = assembly.GetType(typeName, false, true) ??
                assembly.GetTypes().FirstOrDefault(t =>
                    String.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (type == null) return 3;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly).Where(m => m.Name == methodName))
                DumpIL(method);
            return 0;
        }

        private static void DumpIL(MethodInfo method)
        {
            Console.WriteLine("METHOD " + method.DeclaringType.FullName + " :: " + method);
            MethodBody body = method.GetMethodBody();
            if (body == null) return;
            byte[] bytes = body.GetILAsByteArray();
            var one = new Dictionary<byte, OpCode>();
            var two = new Dictionary<byte, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(
                BindingFlags.Public | BindingFlags.Static))
            {
                OpCode op = (OpCode)field.GetValue(null);
                ushort value = unchecked((ushort)op.Value);
                if (value < 0x100) one[(byte)value] = op;
                else if ((value & 0xff00) == 0xfe00) two[(byte)(value & 0xff)] = op;
            }
            int position = 0;
            while (position < bytes.Length)
            {
                int offset = position;
                byte first = bytes[position++];
                OpCode op = first == 0xfe ? two[bytes[position++]] : one[first];
                object operand = null;
                int token;
                switch (op.OperandType)
                {
                    case OperandType.ShortInlineI: operand = unchecked((sbyte)bytes[position++]); break;
                    case OperandType.ShortInlineVar: operand = bytes[position++]; break;
                    case OperandType.InlineVar: operand = BitConverter.ToUInt16(bytes, position); position += 2; break;
                    case OperandType.InlineI: operand = BitConverter.ToInt32(bytes, position); position += 4; break;
                    case OperandType.InlineI8: operand = BitConverter.ToInt64(bytes, position); position += 8; break;
                    case OperandType.ShortInlineR: operand = BitConverter.ToSingle(bytes, position); position += 4; break;
                    case OperandType.InlineR: operand = BitConverter.ToDouble(bytes, position); position += 8; break;
                    case OperandType.ShortInlineBrTarget:
                        operand = position + 1 + unchecked((sbyte)bytes[position]); position++; break;
                    case OperandType.InlineBrTarget:
                        operand = position + 4 + BitConverter.ToInt32(bytes, position); position += 4; break;
                    case OperandType.InlineString:
                        token = BitConverter.ToInt32(bytes, position); position += 4;
                        operand = method.Module.ResolveString(token); break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        token = BitConverter.ToInt32(bytes, position); position += 4;
                        try { operand = method.Module.ResolveMember(token); }
                        catch { operand = "token 0x" + token.ToString("X8"); }
                        break;
                    case OperandType.InlineSig:
                        token = BitConverter.ToInt32(bytes, position); position += 4;
                        operand = "sig 0x" + token.ToString("X8"); break;
                    case OperandType.InlineSwitch:
                        int count = BitConverter.ToInt32(bytes, position); position += 4;
                        int baseOffset = position + count * 4;
                        var targets = new int[count];
                        for (int i = 0; i < count; i++)
                        {
                            targets[i] = baseOffset + BitConverter.ToInt32(bytes, position);
                            position += 4;
                        }
                        operand = String.Join(",", targets); break;
                }
                Console.WriteLine(offset.ToString("X4") + " " + op.Name +
                    (operand == null ? "" : " " + operand));
            }
        }

        private static int RunStaticField(string typeName, string fieldName)
        {
            Assembly assembly = typeof(LanguageSelectionUI).Assembly;
            Type type = assembly.GetType(typeName, false, true) ??
                assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
            FieldInfo field = type == null ? null : type.GetField(fieldName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return 3;
            object value = field.GetValue(null);
            System.Collections.IEnumerable items = value as System.Collections.IEnumerable;
            if (items != null && !(value is string))
                foreach (object item in items) Console.WriteLine(item);
            else Console.WriteLine(value);
            return 0;
        }

        private static int RunParticleAudit(string path)
        {
            I18n_Translation.Translation table = KoreanTranslationLoader.Load(path);
            var displays = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in table.keyToTranslation)
            {
                if (!pair.Key.StartsWith("MACROS_", StringComparison.Ordinal)) continue;
                string name = pair.Key.Substring("MACROS_".Length);
                string plain = Regex.Replace(pair.Value, @"<[^>]*>", "");
                displays[name] = plain.Trim();
            }

            var pattern = new Regex(
                @"\[(?<body>[A-Za-z][A-Za-z0-9_]*(?:\|[^\]]*)?)\]" +
                @"(?<between>(?:</[A-Za-z][^>]*>)*[ \t]*)" +
                @"(?<particle>이든가\(든가\)|이랑\(랑\)|이나\(나\)|으로\(로\)|" +
                @"이\(가\)|은\(는\)|을\(를\)|과\(와\)|\(으\)로|이든\(든\)|" +
                @"이든가|든가|이랑|랑|이나|은|는|을|를|이|가|과|와|으로|로|이든|든)(?![가-힣])",
                RegexOptions.Compiled);

            int suggestions = 0;
            foreach (KeyValuePair<string, string> pair in table.keyToTranslation)
            {
                foreach (Match match in pattern.Matches(pair.Value))
                {
                    string body = match.Groups["body"].Value;
                    string visible = ResolveVisible(body, displays);
                    if (String.IsNullOrEmpty(visible)) continue;
                    string current = match.Groups["particle"].Value;
                    string desired = DesiredParticle(current, visible);
                    if (String.IsNullOrEmpty(desired) || current == desired) continue;
                    suggestions++;
                    int start = Math.Max(0, match.Index - 45);
                    int length = Math.Min(pair.Value.Length - start, match.Length + 90);
                    string context = pair.Value.Substring(start, length)
                        .Replace("\r", "\\r").Replace("\n", "\\n");
                    Console.WriteLine(Escape(pair.Key) + "\t" +
                        match.Value + "\t" +
                        match.Groups["body"].Value + match.Groups["between"].Value + desired +
                        "\t" + context);
                }
            }
            Console.Error.WriteLine("PARTICLE_SUGGESTIONS=" + suggestions);
            return 0;
        }

        private static int RunParticleFix(string input, string output)
        {
            string raw = File.ReadAllText(input, Encoding.UTF8);
            var displays = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match macro in Regex.Matches(raw,
                @"(?m)^MACROS_(?<name>[A-Z0-9_]+)\x1d(?<value>[^\r\n]*)\r?$"))
            {
                string plain = Regex.Replace(macro.Groups["value"].Value,
                    @"<[^>]*>", "");
                displays[macro.Groups["name"].Value] = plain.Trim();
            }
            Regex pattern = ParticlePattern();
            int changes = 0;
            string fixedText = pattern.Replace(raw, delegate(Match match)
            {
                string visible = ResolveVisible(match.Groups["body"].Value, displays);
                if (String.IsNullOrEmpty(visible)) return match.Value;
                string desired = DesiredParticle(match.Groups["particle"].Value, visible);
                if (String.IsNullOrEmpty(desired) ||
                    desired == match.Groups["particle"].Value) return match.Value;
                changes++;
                return "[" + match.Groups["body"].Value + "]" +
                    match.Groups["between"].Value + desired;
            });
            // Persist the same runtime-safe alternatives that the loader would
            // otherwise insert only in memory after format placeholders.
            fixedText = PatchKoreanParticles.PreparePlaceholders(fixedText);
            File.WriteAllText(output, fixedText, new UTF8Encoding(false));
            Console.WriteLine("PARTICLE_CHANGES=" + changes);
            return 0;
        }

        private static int RunPlaceholderAudit(string koreanPath,
            string englishKeysPath, string englishLocalePath)
        {
            I18n_Translation.Translation korean = KoreanTranslationLoader.Load(koreanPath);
            I18n_Translation.Translation englishKeys = KoreanTranslationLoader.Load(englishKeysPath);
            I18n_Translation.Translation englishLocale = KoreanTranslationLoader.Load(englishLocalePath);
            Regex placeholder = new Regex(
                @"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}(?!\})");
            int checkedCount = 0;
            int mismatches = 0;
            foreach (KeyValuePair<string, string> pair in korean.keyToTranslation)
            {
                string source;
                if (Regex.IsMatch(pair.Key, @"^[A-Z0-9_]+$") &&
                    englishLocale.keyToTranslation.TryGetValue(pair.Key, out string localeValue))
                    source = localeValue;
                else if (Regex.IsMatch(pair.Key, @"^[A-Z0-9_]+$") &&
                    englishKeys.keyToTranslation.TryGetValue(pair.Key, out string keyValue))
                    source = keyValue;
                else
                    source = pair.Key;
                string[] expected = placeholder.Matches(source).Cast<Match>()
                    .Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();
                if (expected.Length == 0) continue;
                checkedCount++;
                string[] actual = placeholder.Matches(pair.Value).Cast<Match>()
                    .Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();
                if (expected.SequenceEqual(actual)) continue;
                mismatches++;
                Console.WriteLine(Escape(pair.Key) + "\texpected=" +
                    String.Join(",", expected) + "\tactual=" + String.Join(",", actual));
            }
            Console.Error.WriteLine("PLACEHOLDER_RECORDS=" + checkedCount +
                "; MISMATCHES=" + mismatches);
            return mismatches == 0 ? 0 : 1;
        }

        private static Regex ParticlePattern()
        {
            return new Regex(
                @"\[(?<body>[A-Za-z][A-Za-z0-9_]*(?:\|[^\]]*)?)\]" +
                @"(?<between>(?:</[A-Za-z][^>]*>)*[ \t]*)" +
                @"(?<particle>이든가\(든가\)|이랑\(랑\)|이나\(나\)|으로\(로\)|" +
                @"이\(가\)|은\(는\)|을\(를\)|과\(와\)|\(으\)로|이든\(든\)|" +
                @"이든가|든가|이랑|랑|이나|은|는|을|를|이|가|과|와|으로|로|이든|든)(?![가-힣])",
                RegexOptions.Compiled);
        }

        private static string ResolveVisible(string body,
            Dictionary<string, string> displays)
        {
            int separator = body.IndexOf('|');
            string name = separator < 0 ? body : body.Substring(0, separator);
            string argument = separator < 0 ? null : body.Substring(separator + 1);
            if (argument != null)
            {
                Match variable = Regex.Match(argument, @"\{\d+\}");
                if (variable.Success)
                {
                    string suffix = argument.Substring(variable.Index + variable.Length);
                    if (Regex.IsMatch(suffix, @"[가-힣]")) return suffix;
                    return "{variable}";
                }
                if (Regex.IsMatch(argument, @"[가-힣]"))
                    return Regex.Replace(argument, @"<[^>]*>", "");
                if (argument.TrimEnd().EndsWith("%", StringComparison.Ordinal))
                    return "퍼센트";
                Match digit = Regex.Match(argument, @"\d(?!.*\d)");
                if (digit.Success) return "영일이삼사오육칠팔구"[digit.Value[0] - '0'].ToString();
                if (argument.Length > 0 && argument != "C")
                    return Regex.Replace(argument, @"<[^>]*>", "");
            }
            string value;
            return displays.TryGetValue(name, out value) ? value : null;
        }

        private static string DesiredParticle(string current, string visible)
        {
            string family;
            if (current.Contains("은") || current == "는") family = "topic";
            else if (current.Contains("을") || current == "를") family = "object";
            else if (current.Contains("과") || current == "와") family = "and";
            else if (current.Contains("으로") || current.Contains("으)로") || current == "로") family = "direction";
            else if (current.Contains("이나") || current == "나") family = "or";
            else if (current.Contains("이랑") || current == "랑") family = "with";
            else if (current.Contains("이든가") || current == "든가") family = "either_long";
            else if (current.Contains("이든") || current == "든") family = "either";
            else if (current.Contains("이") || current == "가") family = "subject";
            else return null;

            if (visible == "{variable}")
            {
                switch (family)
                {
                    case "topic": return "은(는)";
                    case "object": return "을(를)";
                    case "and": return "과(와)";
                    case "direction": return "(으)로";
                    case "or": return "이나(나)";
                    case "with": return "이랑(랑)";
                    case "either_long": return "이든가(든가)";
                    case "either": return "이든(든)";
                    default: return "이(가)";
                }
            }

            char last = '\0';
            for (int i = visible.Length - 1; i >= 0; i--)
                if (visible[i] >= '\uAC00' && visible[i] <= '\uD7A3') { last = visible[i]; break; }
            if (last == '\0') return null;
            int jong = (last - 0xAC00) % 28;
            bool batchim = jong != 0;
            switch (family)
            {
                case "topic": return batchim ? "은" : "는";
                case "object": return batchim ? "을" : "를";
                case "and": return batchim ? "과" : "와";
                case "direction": return batchim && jong != 8 ? "으로" : "로";
                case "or": return batchim ? "이나" : "나";
                case "with": return batchim ? "이랑" : "랑";
                case "either_long": return batchim ? "이든가" : "든가";
                case "either": return batchim ? "이든" : "든";
                default: return batchim ? "이" : "가";
            }
        }

        private static string Escape(string value)
        {
            return value.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static HashSet<string> TokenSet(string value)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(value,
                @"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}(?!\})"))
                tokens.Add("FORMAT:" + match.Value);

            foreach (Match match in Regex.Matches(value,
                @"\[([A-Z][A-Z0-9_]*)(?:\|([^\]]*))?\]"))
            {
                string name = match.Groups[1].Value;
                string argument = match.Groups[2].Success ? match.Groups[2].Value : null;
                if (argument == null)
                {
                    tokens.Add("MACRO:" + name);
                }
                else if (Regex.IsMatch(argument,
                    @"^(?:[+-]?\d+|\{\d+\}|[A-Z_]+)$"))
                {
                    tokens.Add("MACRO:" + name + "|" + argument);
                }
                else
                {
                    // Text labels such as [b|Sludge] are intentionally translated.
                    tokens.Add("MACRO:" + name + "|TEXT");
                }
            }
            return tokens;
        }
    }
}
