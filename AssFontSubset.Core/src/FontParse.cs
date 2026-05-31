using Microsoft.Extensions.Logging;
using Mobsub.Font;
using ZLogger;

namespace AssFontSubset.Core;

public struct FontInfo
{
    public Dictionary<int, string> FamilyNames;
    public HashSet<string>? MatchNames;
    //public bool Regular;
    public bool Bold;
    public bool Italic;
    public int Weight;
    //public bool MaybeHasTrueBoldOrItalic;
    public string FileName;
    public uint Index;
    public ushort MaxpNumGlyphs;

    public override bool Equals(object? obj)
    {
        return obj is FontInfo info &&
               FamilyNames == info.FamilyNames &&
               //Regular == info.Regular &&
               Bold == info.Bold &&
               Italic == info.Italic &&
               Weight == info.Weight &&
               //MaybeHasTrueBoldOrItalic == info.MaybeHasTrueBoldOrItalic &&
               FileName == info.FileName &&
               Index == info.Index &&
               MaxpNumGlyphs == info.MaxpNumGlyphs;
    }

    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        hash.Add(FamilyNames);
        //hash.Add(Regular);
        hash.Add(Bold);
        hash.Add(Italic);
        hash.Add(Weight);
        //hash.Add(MaybeHasTrueBoldOrItalic);
        hash.Add(FileName);
        hash.Add(Index);
        hash.Add(MaxpNumGlyphs);
        return hash.ToHashCode();
    }

    public static bool operator ==(FontInfo lhs, FontInfo rhs) => lhs.Equals(rhs);
    public static bool operator !=(FontInfo lhs, FontInfo rhs) => !lhs.Equals(rhs);
}

public static class FontParse
{
    public static List<FontInfo> GetFontInfos(DirectoryInfo dirInfo)
    {
        List<FontInfo> fontInfos = [];
        var fileInfos = dirInfo.GetFiles();
        var faceInfos = OpenType.GetLocalFontsInfo(fileInfos);

        foreach (var faceInfo in faceInfos)
        {
            fontInfos.Add(ConvertToFontInfo(faceInfo));
        }

        return fontInfos;
    }

    /// <summary>
    /// Build font database entries from the given font files. Faulty files are skipped
    /// with a warning instead of aborting the whole index.
    /// </summary>
    public static List<FontDatabaseEntry> BuildDatabaseEntries(IEnumerable<FileInfo> files, ILogger? logger = null)
    {
        var entries = new List<FontDatabaseEntry>();
        var fileArray = files as FileInfo[] ?? files.ToArray();
        if (fileArray.Length == 0)
        {
            return entries;
        }

        IEnumerable<FontFaceInfoBase> faceInfos;
        try
        {
            faceInfos = OpenType.GetLocalFontsInfo(fileArray);
        }
        catch (Exception ex)
        {
            logger?.ZLogError($"Failed to read fonts: {ex.Message}");
            return entries;
        }

        foreach (var faceInfo in faceInfos)
        {
            try
            {
                entries.Add(ConvertToDatabaseEntry((FontFaceInfoOpenType)faceInfo));
            }
            catch (Exception ex)
            {
                logger?.ZLogWarning($"Skip font face ({faceInfo.FileInfo?.FilePath}): {ex.Message}");
            }
        }

        return entries;
    }

    private static FontDatabaseEntry ConvertToDatabaseEntry(FontFaceInfoOpenType info)
    {
        var familyNames = info.FamilyNamesGdi?.Count > 0
            ? new Dictionary<int, string>(info.FamilyNamesGdi)
            : info.FamilyNames?.Count > 0
                ? new Dictionary<int, string>(info.FamilyNames)
                : [];

        if (familyNames.Count == 0)
        {
            throw new InvalidDataException("No family names found");
        }

        if (!familyNames.ContainsKey(FontConstant.LanguageIdEnUs))
        {
            familyNames.Add(FontConstant.LanguageIdEnUs, familyNames.FirstOrDefault().Value);
        }

        var families = new List<string>();
        void AddDistinct(List<string> target, IEnumerable<string>? names)
        {
            if (names is null) return;
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name) && !target.Contains(name))
                {
                    target.Add(name);
                }
            }
        }

        AddDistinct(families, familyNames.Values);
        AddDistinct(families, info.FamilyNames?.Values);

        var fullNames = new List<string>();
        AddDistinct(fullNames, info.FullNames?.Values);

        var psNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.PostScriptName))
        {
            psNames.Add(info.PostScriptName);
        }

        var fsSel = info.fsSelection;
        var path = info.FileInfo!.FilePath!;
        var lastWrite = "UTC " + File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd HH:mm:ss");

        return new FontDatabaseEntry
        {
            Families = families,
            FullNames = fullNames,
            PsNames = psNames,
            Weight = info.Weight,
            Slant = (fsSel & 0b_1) == 1 ? 1 : 0,
            Bold = ((fsSel & 0b_0010_0000) >> 5) == 1,
            Path = path,
            Index = (int)info.FaceIndex,
            MaxpNumGlyphs = info.MaxpNumGlyphs,
            FamilyNamesByLang = familyNames,
            LastWriteTime = lastWrite,
        };
    }

    private static FontInfo ConvertToFontInfo(FontFaceInfoBase faceInfo)
    {
        var info = (FontFaceInfoOpenType)faceInfo;
        var fsSel = info.fsSelection;
        var familyNamesNew = info.FamilyNamesGdi?.Count > 0
            ? new Dictionary<int, string>(info.FamilyNamesGdi)
            : info.FamilyNames?.Count > 0
                ? new Dictionary<int, string>(info.FamilyNames)
                : [];

        if (familyNamesNew.Count == 0)
        {
            throw new InvalidDataException($"No family names found for font: {info.FileInfo?.FilePath}");
        }

        if (!familyNamesNew.ContainsKey(FontConstant.LanguageIdEnUs))
        {
            familyNamesNew.Add(FontConstant.LanguageIdEnUs, familyNamesNew.FirstOrDefault().Value);
        }

        var matchNames = new HashSet<string>(StringComparer.Ordinal);

        static void AddNames(HashSet<string> target, IEnumerable<string>? names)
        {
            if (names is null)
            {
                return;
            }

            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    target.Add(name);
                }
            }
        }

        AddNames(matchNames, familyNamesNew.Values);
        AddNames(matchNames, info.FamilyNames?.Values);
        AddNames(matchNames, info.FullNames?.Values);
        AddNames(matchNames, string.IsNullOrWhiteSpace(info.PostScriptName) ? null : [info.PostScriptName]);

        return new FontInfo
        {
            FamilyNames = familyNamesNew,
            MatchNames = matchNames,
            //Regular = ((fsSel & 0b_0100_0000) >> 6) == 1,   // bit 6
            Bold = ((fsSel & 0b_0010_0000) >> 5) == 1, // bit 5
            Italic = (fsSel & 0b_1) == 1, // bit 0
            Weight = info.Weight,
            //MaybeHasTrueBoldOrItalic = false,
            FileName = info.FileInfo!.FilePath!,
            Index = info.FaceIndex,
            MaxpNumGlyphs = info.MaxpNumGlyphs,
        };
    }
}
