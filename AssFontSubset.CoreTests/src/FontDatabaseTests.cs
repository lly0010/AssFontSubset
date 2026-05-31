using System.Text.Json;

namespace AssFontSubset.Core.Tests;

[TestClass]
public class FontDatabaseTests
{
    [TestMethod]
    public void Entry_SerializesWithRequestedFieldNames()
    {
        var entry = new FontDatabaseEntry
        {
            Families = ["a-otf jun pro 501", "a-otf じゅん pro 501"],
            FullNames = ["jun501pro-bold"],
            PsNames = ["jun501pro-bold"],
            Weight = 600,
            Slant = 0,
            Path = @"D:\fonts\old\A-OTF Jun Pro 501.ttf",
            Index = 0,
            LastWriteTime = "UTC 2025-04-15 01:03:05",
        };

        var json = JsonSerializer.Serialize(entry);

        foreach (var key in new[] { "families", "fullnames", "psnames", "weight", "slant", "path", "index", "last_write_time" })
        {
            StringAssert.Contains(json, $"\"{key}\"");
        }
    }

    [TestMethod]
    public void SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var db = new FontDatabase();
            db.Entries.Add(new FontDatabaseEntry
            {
                Families = ["Example Font"],
                FullNames = ["Example Font Bold"],
                PsNames = ["ExampleFont-Bold"],
                Weight = 700,
                Slant = 1,
                Bold = true,
                Path = "Example.otf",
                Index = 0,
                MaxpNumGlyphs = 1000,
                FamilyNamesByLang = new Dictionary<int, string> { [1033] = "Example Font" },
            });
            db.Save(path);

            var loaded = FontDatabase.Load(path);

            Assert.AreEqual(1, loaded.Count);
            var e = loaded.Entries[0];
            Assert.AreEqual("Example Font", e.Families[0]);
            Assert.AreEqual(700, e.Weight);
            Assert.AreEqual(1, e.Slant);
            Assert.IsTrue(e.Bold);
            Assert.AreEqual(1000, e.MaxpNumGlyphs);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [TestMethod]
    public void ToFontInfo_MapsSlantToItalicAndKeepsMatchNames()
    {
        var entry = new FontDatabaseEntry
        {
            Families = ["Example Font"],
            FullNames = ["Example Font Bold"],
            PsNames = ["ExampleFont-Bold"],
            Weight = 700,
            Slant = 1,
            Bold = true,
            Path = "Example.otf",
            FamilyNamesByLang = new Dictionary<int, string> { [1033] = "Example Font" },
        };

        var fi = entry.ToFontInfo();

        Assert.IsTrue(fi.Italic);
        Assert.IsTrue(fi.Bold);
        Assert.AreEqual(700, fi.Weight);
        Assert.AreEqual("Example.otf", fi.FileName);
        Assert.IsNotNull(fi.MatchNames);
        Assert.IsTrue(fi.MatchNames!.Contains("Example Font"));
        Assert.IsTrue(fi.MatchNames.Contains("ExampleFont-Bold"));
    }

    [TestMethod]
    public void SelectForNames_ReturnsWholeFamilyForMatchedFace()
    {
        var db = new FontDatabase();
        db.Entries.Add(MakeEntry("Family A", "FamilyA-Regular", 400, false));
        db.Entries.Add(MakeEntry("Family A", "FamilyA-Bold", 700, true));
        db.Entries.Add(MakeEntry("Family B", "FamilyB-Regular", 400, false));

        // Reference Family A via its bold full name; the whole family should come back.
        var selected = db.SelectForNames(["FamilyA-Bold"]);

        Assert.AreEqual(2, selected.Count);
        Assert.IsTrue(selected.All(fi => fi.FamilyNames[1033] == "Family A"));
    }

    [TestMethod]
    public void SelectForNames_StripsVerticalPrefix()
    {
        var db = new FontDatabase();
        db.Entries.Add(MakeEntry("Family A", "FamilyA-Regular", 400, false));

        var selected = db.SelectForNames(["@Family A"]);

        Assert.AreEqual(1, selected.Count);
    }

    private static FontDatabaseEntry MakeEntry(string family, string psName, int weight, bool bold) => new()
    {
        Families = [family],
        FullNames = [psName],
        PsNames = [psName],
        Weight = weight,
        Slant = 0,
        Bold = bold,
        Path = $"{psName}.ttf",
        FamilyNamesByLang = new Dictionary<int, string> { [1033] = family },
    };
}
