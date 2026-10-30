using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Localization;
using ImageConverter.Core.Printing;
using ImageConverter.Models;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// Localisation: English must know every text, and language files from the languages folder
/// are read. The tests switch the language — hence they do not run in parallel with other
/// tests that check German messages.
/// </summary>
[Collection(nameof(LocalizationTests))]
[CollectionDefinition(nameof(LocalizationTests), DisableParallelization = true)]
public sealed partial class LocalizationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ic-lang-").FullName;

    public void Dispose()
    {
        Tr.Use(Language.German);
        Directory.Delete(_dir, recursive: true);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    [GeneratedRegex(@"\{l:T '([^']*)'\}")]
    private static partial Regex XamlAttribute();

    [GeneratedRegex(@"<l:T Text=""([^""]*)""")]
    private static partial Regex XamlElement();

    [GeneratedRegex(@"Tr\.T\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CsCall();

    /// <summary>Collects all German texts that the user interface displays translated.</summary>
    internal static SortedSet<string> AllTexts()
    {
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        var root = RepoRoot();

        foreach (var file in new[] { "ImageConverter.Ui", "ImageConverter.Viewer3D" }
                     .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.axaml", SearchOption.AllDirectories)))
        {
            var xaml = File.ReadAllText(file);
            foreach (Match m in XamlAttribute().Matches(xaml)) texts.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
            foreach (Match m in XamlElement().Matches(xaml)) texts.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
        }

        foreach (var project in new[] { "ImageConverter.Ui", "ImageConverter.Core", "ImageConverter.Viewer3D" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in CsCall().Matches(File.ReadAllText(file)))
                texts.Add(Regex.Unescape(m.Groups[1].Value));
        }

        // Lists whose values are German texts and are displayed translated
        var vm = new MainViewModel();
        texts.UnionWith(vm.MetadataModes);
        texts.UnionWith(vm.ColorModes);
        texts.UnionWith(vm.AspectPresets);
        texts.UnionWith(vm.SizeUnits);
        texts.UnionWith(vm.Tints);
        texts.UnionWith(vm.Ai.OrderOptions);
        foreach (var g in vm.Ai.ModelGroups) { texts.Add(g.Title); texts.Add(g.Hint); }
        texts.UnionWith(PrintDialogViewModel.OrientationLabels);
        texts.UnionWith(PrintDialogViewModel.ModeLabels);
        texts.UnionWith(PrintDialogViewModel.ColorModeLabels);
        texts.UnionWith(PrintDialogViewModel.DuplexLabels);
        texts.Add("Als PDF speichern");
        foreach (var f in FilterOption.All) { texts.Add(f.Description); texts.Add(f.BestFor); }
        foreach (var m in AiModelCatalog.All) { texts.Add(m.Name); texts.Add(m.Purpose); texts.Add(m.Description); texts.Add(m.Origin); }
        // Paper: only descriptive names ("Foto 10 × 15 cm"), not A4 or Letter
        foreach (var p in PaperSize.Common) if (p.Name.Contains(' ')) texts.Add(p.Name);

        // Texts without letters (symbols, numbers) need no translation
        texts.RemoveWhere(t => !t.Any(char.IsLetter));
        return texts;
    }

    [Fact]
    public void English_CoversEveryText()
    {
        var missing = AllTexts().Where(t => !English.Texts.ContainsKey(t)).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} Texte ohne englische Übersetzung (in English.cs ergänzen):\n" +
            string.Join("\n", missing.Select(t => $"[{Quote(t)}] = {Quote(t)},")));
    }

    /// <summary>
    /// No German text appears twice in English.cs: the table is filled through the indexer, so a second entry
    /// silently overwrites the first. For example, a second entry for "Ziel" (luminance slider) would turn the label
    /// of the export destination into "Target".
    /// </summary>
    [Fact]
    public void English_HasNoDuplicateKeys()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "ImageConverter.Core", "Localization", "English.cs"));
        var keys = Regex.Matches(source, @"^\s*\[(""(?:[^""\\]|\\.)*"")\]\s*=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.True(keys.Count > 500, $"nur {keys.Count} Einträge gefunden — Muster passt nicht mehr");
        var duplicates = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Doppelt in English.cs: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void English_HasNoLeftovers()
    {
        // Entries that no longer occur in the code — the text was changed or removed
        var used = AllTexts();
        var stale = English.Texts.Keys.Where(k => !used.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Nicht mehr benutzt:\n" + string.Join("\n", stale));
    }

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void English_KeepsPlaceholders()
    {
        foreach (var (german, english) in English.Texts)
        {
            var a = Placeholder().Matches(german).Select(m => m.Value).Order().ToList();
            var b = Placeholder().Matches(english).Select(m => m.Value).Order().ToList();
            Assert.True(a.SequenceEqual(b), $"Platzhalter weichen ab:\n{german}\n{english}");
        }
    }

    [Fact]
    public void Switching_ChangesTexts_AndFallsBackToEnglish()
    {
        File.WriteAllText(Path.Combine(_dir, "fr.json"), """
            {
              "code": "fr",
              "name": "Français",
              "flag": { "stripes": "vertical", "colors": ["#002395", "#FFFFFF", "#ED2939"] },
              "texts": { "Exportieren": "Exporter" }
            }
            """);
        var all = Languages.All(out var problems, _dir);
        var fr = all.Single(l => l.Code == "fr");

        Assert.Empty(problems);
        Assert.Equal(["de", "en", "fr"], all.Select(l => l.Code));
        Assert.Equal(FlagKind.Stripes, fr.Flag.Kind);
        Assert.True(fr.Flag.Vertical);

        Tr.Use(fr);
        Assert.Equal("Exporter", Tr.T("Exportieren"));
        Assert.Equal("Close", Tr.T("Schließen"));                 // missing in fr.json → English
        Assert.Equal("Unbekannter Text", Tr.T("Unbekannter Text"));  // missing everywhere → German

        Tr.Use(Language.German);
        Assert.Equal("Exportieren", Tr.T("Exportieren"));
    }

    [Fact]
    public void Switching_NotifiesBindings()
    {
        var changes = 0;
        void Count(object? s, System.ComponentModel.PropertyChangedEventArgs e) => changes++;
        Tr.Instance.PropertyChanged += Count;
        try
        {
            Tr.Use(English.Language);
            Tr.Use(Language.German);
        }
        finally { Tr.Instance.PropertyChanged -= Count; }

        Assert.Equal(2, changes);
    }

    [Fact]
    public void BrokenFiles_AreReported_NotLoaded()
    {
        File.WriteAllText(Path.Combine(_dir, "kaputt.json"), "{ \"code\": \"xx\", \"texts\": {");
        File.WriteAllText(Path.Combine(_dir, "de.json"), "{ \"code\": \"de\", \"name\": \"Deutsch 2\" }");

        var all = Languages.All(out var problems, _dir);

        Assert.Equal(["de", "en"], all.Select(l => l.Code));
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("kaputt.json"));
        Assert.Contains(problems, p => p.StartsWith("de.json"));
    }

    [Fact]
    public void FlagAsImageFile_IsFoundBesideTheJson()
    {
        File.WriteAllBytes(Path.Combine(_dir, "it.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(_dir, "it.json"), """{ "code": "it", "name": "Italiano", "flag": "it.png" }""");

        var it = Languages.All(out _, _dir).Single(l => l.Code == "it");

        Assert.Equal(FlagKind.Image, it.Flag.Kind);
        Assert.Equal(Path.Combine(_dir, "it.png"), it.Flag.ImagePath);
    }

    [Fact]
    public void Template_ContainsEveryText_AndLoadsAsLanguage()
    {
        var path = Languages.WriteTemplate(_dir);
        Assert.Equal("_vorlage.json", Path.GetFileName(path));

        // The template itself is not loaded as a language (leading underscore) …
        Assert.DoesNotContain(Languages.All(out _, _dir), l => l.Code == "xx");

        // … but it is once renamed, with all texts
        File.Copy(path, Path.Combine(_dir, "xx.json"));
        var xx = Languages.All(out var problems, _dir).Single(l => l.Code == "xx");
        Assert.Empty(problems);
        Assert.Equal(English.Texts.Count, xx.Texts.Count);
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]   // no French file → English
    public void FirstStart_FollowsTheSystem(string culture, string expected)
    {
        var all = Languages.All(out _, _dir);
        Assert.Equal(expected, Languages.ForSystem(all, new System.Globalization.CultureInfo(culture)).Code);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}
