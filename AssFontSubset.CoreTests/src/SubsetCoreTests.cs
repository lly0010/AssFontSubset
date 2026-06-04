using Mobsub.SubtitleParse.AssText;
using System.Reflection;
using System.Text;
using Mobsub.SubtitleParse.AssTypes;

namespace AssFontSubset.Core.Tests;

[TestClass]
public class SubsetCoreTests
{
    [TestMethod]
    public void ChangeAssFontName_ReplacesVerticalStyleAndOverrideFontNames()
    {
        var assPath = CreateTempAssFile(
            """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1920
            PlayResY: 1080

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Example Font,50,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
            Style: Vertical,@Example Font,50,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\fnExample Font}hello
            Dialogue: 0,0:00:01.00,0:00:02.00,Vertical,,0,0,0,,{\fn@Example Font}world
            """
        );

        try
        {
            var ass = new AssData();
            ass.ReadAssFile(assPath);

            var fontInfo = new FontInfo
            {
                FamilyNames = new Dictionary<int, string> { [1033] = "Example Font" },
                FileName = "Example.ttf",
            };

            var fontMap = new Dictionary<FontInfo, List<AssFontInfo>>
            {
                [fontInfo] =
                [
                    CreateAssFontInfo("Example Font", 0, false),
                    CreateAssFontInfo("@Example Font", 0, false),
                ]
            };

            var nameMap = new Dictionary<string, string>
            {
                ["Example Font"] = "SUBSET123",
            };

            InvokeChangeAssFontName(ass, nameMap, fontMap);

            Assert.AreEqual("SUBSET123", ass.Styles.Collection.Single(x => x.Name == "Default").Fontname);
            Assert.AreEqual("@SUBSET123", ass.Styles.Collection.Single(x => x.Name == "Vertical").Fontname);
            Assert.AreEqual(@"{\fnSUBSET123}hello", ass.Events!.Collection[0].Text);
            Assert.AreEqual(@"{\fn@SUBSET123}world", ass.Events!.Collection[1].Text);
            CollectionAssert.Contains(ass.ScriptInfo.Comment, "Font Subset: SUBSET123 - Example Font");
        }
        finally
        {
            if (File.Exists(assPath))
            {
                File.Delete(assPath);
            }
        }
    }

    [TestMethod]
    public void EmbedFontToAss_EncodesFontsAndRoundTrips()
    {
        var optDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(optDir);
        var assPath = Path.Combine(optDir, "sample.ass");
        File.WriteAllText(assPath,
            """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,SUBSET123,50,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,hello
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // A subsetted font file in the output directory (content does not need to be a real font).
        var fontBytes = new byte[512];
        for (var i = 0; i < fontBytes.Length; i++)
        {
            fontBytes[i] = (byte)((i * 37 + 11) & 0xFF);
        }
        var fontName = "sample.0.SUBSET123.ttf";
        File.WriteAllBytes(Path.Combine(optDir, fontName), fontBytes);

        try
        {
            var ass = new AssData();
            ass.ReadAssFile(assPath);
            Assert.IsFalse(ass.Sections.Contains(AssSection.Fonts));

            var embeddedFonts = InvokeEncodeSubsetFonts(optDir);
            Assert.AreEqual(1, embeddedFonts.Count);

            InvokeEmbedFontsToAss(ass, embeddedFonts);
            Assert.IsTrue(ass.Sections.Contains(AssSection.Fonts));

            var outPath = Path.Combine(optDir, "out.ass");
            ass.WriteAssFile(outPath);

            var written = File.ReadAllText(outPath);
            StringAssert.Contains(written, "[Fonts]");
            StringAssert.Contains(written, $"fontname: {fontName}");

            // Re-parse the written ass and confirm the embedded font decodes back to the original bytes.
            var reread = new AssData();
            reread.ReadAssFile(outPath);
            Assert.AreEqual(1, reread.Fonts.Files.Count);
            CollectionAssert.AreEqual(fontBytes, reread.Fonts.Files[0].GetDecodedData());
        }
        finally
        {
            Directory.Delete(optDir, true);
        }
    }

    [TestMethod]
    public void SeparateFontFolder_MovesFontsIntoAssNamedFolder()
    {
        var optDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(optDir);
        var fontNames = new[] { "sample.0.SUBSET123.ttf", "another.0.SUBSET456.otf" };
        foreach (var name in fontNames)
        {
            File.WriteAllBytes(Path.Combine(optDir, name), [1, 2, 3, 4]);
        }
        var assPath = Path.Combine(optDir, "Episode 01.ass");
        File.WriteAllText(assPath, "x");

        try
        {
            InvokeMoveFontsToAssFolders(optDir, [assPath]);

            var assFolder = Path.Combine(optDir, "Episode 01");
            foreach (var name in fontNames)
            {
                Assert.IsTrue(File.Exists(Path.Combine(assFolder, name)), $"expected {name} under ass folder");
                Assert.IsFalse(File.Exists(Path.Combine(optDir, name)), $"{name} should have left the top level");
            }
            // The ass file itself stays at the top level.
            Assert.IsTrue(File.Exists(assPath));
        }
        finally
        {
            Directory.Delete(optDir, true);
        }
    }

    [TestMethod]
    public void FontDatabase_WriteRead_UsesExpectedSchemaAndRoundTrips()
    {
        var entry = new FontDatabaseEntry
        {
            Families = ["a-otf jun pro 501", "a-otf じゅん pro 501"],
            Fullnames = ["jun501pro-bold"],
            Psnames = ["jun501pro-bold"],
            Weight = 600,
            Slant = 0,
            Path = @"C:\fonts\A-OTF Jun Pro 501.ttf",
            Index = 0,
            LastWriteTime = "UTC 2025-04-15 01:03:05",
        };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            FontDatabase.Write([entry], path);

            var json = File.ReadAllText(path);
            StringAssert.Contains(json, "\"families\"");
            StringAssert.Contains(json, "\"fullnames\"");
            StringAssert.Contains(json, "\"psnames\"");
            StringAssert.Contains(json, "\"last_write_time\"");
            StringAssert.Contains(json, "じゅん"); // non-ASCII written literally, not \uXXXX

            var read = FontDatabase.Read(path);
            Assert.AreEqual(1, read.Count);
            CollectionAssert.AreEqual(entry.Families, read[0].Families);
            Assert.AreEqual(600, read[0].Weight);
            Assert.AreEqual("jun501pro-bold", read[0].Psnames.Single());
            Assert.AreEqual(entry.LastWriteTime, read[0].LastWriteTime);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void DeduplicateFontInfos_CollapsesIdenticalFacesButKeepsDistinctOnes()
    {
        static FontInfo Make(string family, bool bold, int weight, string file) => new()
        {
            FamilyNames = new Dictionary<int, string> { [1033] = family },
            Bold = bold,
            Italic = false,
            Weight = weight,
            Index = 0,
            MaxpNumGlyphs = 1000,
            FileName = file,
        };

        List<FontInfo> list =
        [
            Make("Foo", false, 400, "/a/Foo.otf"),
            Make("Foo", false, 400, "/a/Foo.ttf"),       // same logical face as above
            Make("Foo", true, 700, "/a/Foo-Bold.ttf"),   // distinct (bold)
        ];

        var method = typeof(SubsetCore).GetMethod("DeduplicateFontInfos", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var result = (List<FontInfo>)method.Invoke(new SubsetCore(), [list])!;

        Assert.AreEqual(2, result.Count);
        CollectionAssert.Contains(result.Select(f => f.FileName).ToList(), "/a/Foo.otf");
        CollectionAssert.Contains(result.Select(f => f.FileName).ToList(), "/a/Foo-Bold.ttf");
        CollectionAssert.DoesNotContain(result.Select(f => f.FileName).ToList(), "/a/Foo.ttf");
    }

    [TestMethod]
    public void FontDatabase_ResolveFontFiles_MatchesNamesAndFamilySiblingsCaseInsensitively()
    {
        var regular = new FontDatabaseEntry
        {
            Families = ["myfont"], Fullnames = ["myfont regular"], Psnames = ["myfont-regular"], Path = "/f/regular.ttf",
        };
        var bold = new FontDatabaseEntry
        {
            Families = ["myfont"], Fullnames = ["myfont bold"], Psnames = ["myfont-bold"], Path = "/f/bold.ttf",
        };
        var other = new FontDatabaseEntry
        {
            Families = ["unrelated"], Fullnames = ["unrelated"], Psnames = ["unrelated"], Path = "/f/other.ttf",
        };
        List<FontDatabaseEntry> entries = [regular, bold, other];

        // Family match (case-insensitive) pulls all sibling faces, but not unrelated fonts.
        CollectionAssert.AreEquivalent(
            new[] { "/f/regular.ttf", "/f/bold.ttf" },
            FontDatabase.ResolveFontFiles(entries, ["MyFont"]));

        // Matching by a face full name still pulls the whole family.
        CollectionAssert.AreEquivalent(
            new[] { "/f/regular.ttf", "/f/bold.ttf" },
            FontDatabase.ResolveFontFiles(entries, ["MyFont Bold"]));

        // Unrelated request returns only the unrelated file.
        CollectionAssert.AreEquivalent(
            new[] { "/f/other.ttf" },
            FontDatabase.ResolveFontFiles(entries, ["Unrelated"]));
    }

    private static void InvokeMoveFontsToAssFolders(string optDir, IEnumerable<string> assOutputPaths)
    {
        var method = typeof(SubsetCore).GetMethod("MoveFontsToAssFolders", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(new SubsetCore(), [optDir, assOutputPaths]);
    }

    private static List<AssEmbeddedFile> InvokeEncodeSubsetFonts(string optDir)
    {
        var method = typeof(SubsetCore).GetMethod("EncodeSubsetFonts", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (List<AssEmbeddedFile>)method.Invoke(new SubsetCore(), [optDir])!;
    }

    [TestMethod]
    public void TryNormalizeAssTimeFields_FixesNonStandardTimesAndLeavesStandardAlone()
    {
        static (bool changed, string output) Normalize(string input)
        {
            var method = typeof(SubsetCore).GetMethod("TryNormalizeAssTimeFields", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            var args = new object?[] { input, null };
            var changed = (bool)method.Invoke(null, args)!;
            return (changed, (string)args[1]!);
        }

        // Millisecond precision is normalized to centiseconds; text (with commas) is preserved.
        var ms = "[Events]\n" +
                 "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
                 "Dialogue: 0,0:00:00.000,0:00:01.234,Default,,0,0,0,,hi, there\n";
        var (changedMs, outMs) = Normalize(ms);
        Assert.IsTrue(changedMs);
        StringAssert.Contains(outMs, "Dialogue: 0,0:00:00.00,0:00:01.23,Default,,0,0,0,,hi, there");

        // Two-digit hours stay valid and are left as-is.
        var hh = "[Events]\nDialogue: 0,0:00:00.00,0:00:01.23,Default,,0,0,0,,x\n";
        var (changedHh, outHh) = Normalize(hh);
        Assert.IsFalse(changedHh);
        Assert.AreEqual(hh, outHh);

        // Time-like text outside [Events] is never touched.
        var nonEvent = "[Script Info]\nTitle: clip 0:00:00.000\n";
        var (changedNon, _) = Normalize(nonEvent);
        Assert.IsFalse(changedNon);
    }

    [TestMethod]
    public void HasEmbeddedFonts_DetectsFontsSection()
    {
        var method = typeof(SubsetCore).GetMethod("HasEmbeddedFonts", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var withFonts = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ass");
        var without = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ass");
        File.WriteAllText(withFonts, "[Script Info]\n\n[Fonts]\nfontname: x.ttf\n!!!!\n\n[Events]\n");
        File.WriteAllText(without, "[Script Info]\n\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,D,,0,0,0,,hi\n");
        try
        {
            Assert.IsTrue((bool)method.Invoke(null, [withFonts])!);
            Assert.IsFalse((bool)method.Invoke(null, [without])!);
        }
        finally
        {
            File.Delete(withFonts);
            File.Delete(without);
        }
    }

    [TestMethod]
    public void StripLeadingBlankLines_RemovesLeadingBlankLinesOnly()
    {
        var method = typeof(SubsetCore).GetMethod("StripLeadingBlankLines", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var withBlank = new object?[] { "\n\n[Script Info]\nx\n", null };
        Assert.IsTrue((bool)method.Invoke(null, withBlank)!);
        Assert.IsTrue(((string)withBlank[1]!).StartsWith("[Script Info]"));

        var clean = new object?[] { "[Script Info]\nx\n", null };
        Assert.IsFalse((bool)method.Invoke(null, clean)!);
    }

    [TestMethod]
    public void SanitizeFontBytes_DowngradesTruncatedOs2Version()
    {
        // Minimal sfnt: 1 table "OS/2" at offset 32, length 78, but version claims 3 (needs 96).
        var font = new byte[110];
        font[1] = 0x01; // sfnt version 0x00010000
        font[5] = 0x01; // numTables = 1
        font[12] = (byte)'O'; font[13] = (byte)'S'; font[14] = (byte)'/'; font[15] = (byte)'2';
        font[23] = 32;  // table offset = 32
        font[27] = 78;  // table length = 78
        font[32] = 0x00; font[33] = 0x03; // OS/2 version = 3

        var method = typeof(SubsetCore).GetMethod("SanitizeFontBytes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var result = (byte[])method.Invoke(new SubsetCore(), [font])!;

        Assert.AreEqual(0, (result[32] << 8) | result[33]); // version downgraded to fit 78 bytes
    }

    [TestMethod]
    public void ExtractEmbeddedFonts_DecodesFullDataFromRawAss()
    {
        var original = new byte[5000];
        new Random(123).NextBytes(original);
        var ef = new AssEmbeddedFile("x.ttf", "x.ttf", AssEmbeddedFileType.Font);
        ef.Encode(original);

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine();
        sb.AppendLine("[Fonts]");
        sb.AppendLine("fontname: x.ttf");
        foreach (var d in ef.Data) sb.AppendLine(Encoding.UTF8.GetString(d.Span));

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ass");
        File.WriteAllText(path, sb.ToString());
        try
        {
            var extracted = SubsetCore.ExtractEmbeddedFonts(path).ToList();
            Assert.AreEqual(1, extracted.Count);
            CollectionAssert.AreEqual(original, extracted[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EmbedFontsToAss_ReplacesExistingEmbeddedFonts()
    {
        var ass = new AssData();
        // Pre-existing embedded font in the ass.
        var old = new AssEmbeddedFile("old.ttf", "old.ttf", AssEmbeddedFileType.Font);
        old.Encode([9, 9, 9, 9]);
        ass.Fonts.Files.Add(old);

        var fresh = new AssEmbeddedFile("new.ttf", "new.ttf", AssEmbeddedFileType.Font);
        fresh.Encode([1, 2, 3, 4]);

        InvokeEmbedFontsToAss(ass, [fresh]);

        // The old embedded font is gone; only the freshly embedded one remains.
        Assert.AreEqual(1, ass.Fonts.Files.Count);
        Assert.AreEqual("new.ttf", ass.Fonts.Files[0].Name);
        Assert.IsTrue(ass.Sections.Contains(AssSection.Fonts));
    }

    private static void InvokeEmbedFontsToAss(AssData ass, List<AssEmbeddedFile> embeddedFonts)
    {
        var method = typeof(SubsetCore).GetMethod("EmbedFontsToAss", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(null, [ass, embeddedFonts]);
    }

    [TestMethod]
    public void GetSubsetFonts_FontFallback_RemapsMissingToMainFont()
    {
        var fnDict = new Dictionary<int, string> { [1033] = "Main" };
        var mainFi = new FontInfo { FamilyNames = fnDict, MatchNames = ["Main"], Bold = false, Italic = false, Weight = 400, FileName = "main.ttf", Index = 0, MaxpNumGlyphs = 3000 };
        var fontInfos = new List<FontInfo> { mainFi }.GroupBy(fi => fi.FamilyNames[1033]);

        var mainAfi = CreateAssFontInfo("Main", 0, false);     // matches mainFi
        var missingAfi = CreateAssFontInfo("Missing", 0, false); // no font available
        var assFonts = new Dictionary<AssFontInfo, HashSet<Rune>>
        {
            [mainAfi] = [new Rune('A'), new Rune('B'), new Rune('C')], // main has more text -> chosen as main
            [missingAfi] = [new Rune('X')],
        };

        var method = typeof(SubsetCore).GetMethod("GetSubsetFonts", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        // Without fallback -> throws "Not found font file".
        var noFallback = new object?[] { fontInfos, assFonts, false, null };
        try
        {
            method.Invoke(new SubsetCore(), noFallback);
            Assert.Fail("expected a 'Not found font file' exception");
        }
        catch (TargetInvocationException ex)
        {
            StringAssert.Contains(ex.InnerException!.Message, "Not found font file");
        }

        // With fallback -> missing font is merged into the main font (no throw).
        var withFallback = new object?[] { fontInfos, assFonts, true, null };
        method.Invoke(new SubsetCore(), withFallback);
        var fontMap = (Dictionary<FontInfo, List<AssFontInfo>>)withFallback[3]!;
        CollectionAssert.Contains(fontMap[mainFi], missingAfi);
    }

    private static void InvokeChangeAssFontName(AssData ass, Dictionary<string, string> nameMap, Dictionary<FontInfo, List<AssFontInfo>> fontMap)
    {
        var method = typeof(SubsetCore).GetMethod("ChangeAssFontName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(null, [ass, nameMap, fontMap]);
    }

    private static string CreateTempAssFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ass");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static AssFontInfo CreateAssFontInfo(string name, int weight, bool italic, int encoding = 1)
    {
        return new($"{name},{weight},{(italic ? 1 : 0)},{encoding}");
    }
}
