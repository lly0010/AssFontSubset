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

    private static void InvokeEmbedFontsToAss(AssData ass, List<AssEmbeddedFile> embeddedFonts)
    {
        var method = typeof(SubsetCore).GetMethod("EmbedFontsToAss", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(null, [ass, embeddedFonts]);
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
