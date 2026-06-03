using System.Buffers;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Mobsub.SubtitleParse.AssText;
using Mobsub.SubtitleParse.AssTypes;
using ZLogger;

namespace AssFontSubset.Core;

public class SubsetCore(ILogger? logger = null)
{
    private static readonly Stopwatch _stopwatch = new();

    public async Task SubsetAsync(FileInfo[] path, DirectoryInfo? fontPath, DirectoryInfo? outputPath, DirectoryInfo? binPath, SubsetConfig subsetConfig)
    {
        var baseDir = path[0].Directory!.FullName;
        fontPath ??= new DirectoryInfo(Path.Combine(baseDir, "fonts"));
        outputPath ??= new DirectoryInfo(Path.Combine(baseDir, "output"));

        foreach (var file in path)
        {
            if (!file.Exists)
            {
                throw new Exception($"Please check if file {file} exists");
            }
        }
        var useDatabase = !string.IsNullOrWhiteSpace(subsetConfig.FontDatabasePath);
        // Re-embed forces embedding (the whole point is to re-embed) and sources the fonts from
        // the ass' own existing embedded fonts.
        var embedToAss = subsetConfig.EmbedFontToAss || subsetConfig.ReembedFonts;
        if (!useDatabase && !subsetConfig.ReembedFonts && !fontPath.Exists) { throw new Exception($"Please check if directory {fontPath} exists"); }

        // Replace original: subset to a temp dir, then mirror the result over the input files.
        // This avoids ever wiping the input directory.
        var replaceOriginal = subsetConfig.ReplaceOriginal;
        if (replaceOriginal)
        {
            outputPath = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "AssFontSubset_inplace_" + Guid.NewGuid().ToString("N")));
        }

        if (outputPath.Exists) { outputPath.Delete(true); }
        var fontDir = fontPath.FullName;
        var optDir = outputPath.FullName;

        await Task.Run(() =>
        {
            try
            {
                // Re-embed: each ass already carries only its own glyphs, so process every file
                // independently (pooling embedded fonts across files would mix partial subsets).
                if (subsetConfig.ReembedFonts)
                {
                    Directory.CreateDirectory(optDir);
                    foreach (var assFile in path)
                    {
                        ReembedOneFile(assFile, optDir, binPath, subsetConfig);
                    }
                }
                else
                {
                    var assFonts = GetAssFontInfoFromFiles(path, optDir, out var assMulti);
                    var fontInfos = useDatabase
                        ? GetFontInfoFromDatabase(subsetConfig.FontDatabasePath!, assFonts.Keys)
                        : GetFontInfoFromFiles(fontDir);

                    var subsetFonts = GetSubsetFonts(fontInfos, assFonts, out var fontMap);
                    RunSubsetBackend(subsetFonts, optDir, binPath, subsetConfig, out var nameMap);

                    var embeddedFonts = embedToAss ? EncodeSubsetFonts(optDir) : [];

                    foreach (var kv in assMulti)
                    {
                        ChangeAssFontName(kv.Value, nameMap, fontMap);
                        if (embeddedFonts.Count > 0)
                        {
                            EmbedFontsToAss(kv.Value, embeddedFonts);
                        }
                        kv.Value.WriteAssFile(kv.Key);
                    }

                    if (embedToAss && subsetConfig.EmbedOnly)
                    {
                        // Fonts are embedded; drop the loose subset font files.
                        DeleteSubsetFontFiles(optDir);
                    }
                    else if (subsetConfig.SeparateFontFolder)
                    {
                        MoveFontsToAssFolders(optDir, assMulti.Keys);
                    }
                }

                if (replaceOriginal)
                {
                    MirrorOutputOverOriginals(optDir, path);
                }
            }
            finally
            {
                if (replaceOriginal && !subsetConfig.DebugMode)
                {
                    TryDeleteDirectory(optDir);
                }
            }
        });
    }

    /// <summary>
    /// Overwrite the original ass files with the subset results from <paramref name="tempOut"/>,
    /// and copy each ass' font folder / loose fonts next to the original file.
    /// </summary>
    private void MirrorOutputOverOriginals(string tempOut, FileInfo[] path)
    {
        logger?.ZLogInformation($"Replace original files in place");
        foreach (var assFile in path)
        {
            var producedAss = Path.Combine(tempOut, assFile.Name);
            if (File.Exists(producedAss))
            {
                File.Copy(producedAss, assFile.FullName, true);
            }

            var assName = Path.GetFileNameWithoutExtension(assFile.Name);
            var producedFolder = Path.Combine(tempOut, assName);
            if (Directory.Exists(producedFolder))
            {
                var destFolder = Path.Combine(assFile.Directory!.FullName, assName);
                Directory.CreateDirectory(destFolder);
                foreach (var f in Directory.EnumerateFiles(producedFolder))
                {
                    File.Copy(f, Path.Combine(destFolder, Path.GetFileName(f)), true);
                }
            }
        }

        // Loose fonts at the temp root (when not using same-named folders) go next to the first ass.
        var baseDir = path[0].Directory!.FullName;
        foreach (var f in EnumerateSubsetFontFiles(tempOut))
        {
            File.Copy(f, Path.Combine(baseDir, Path.GetFileName(f)), true);
        }
    }

    private void RunSubsetBackend(Dictionary<string, List<SubsetFont>> subsetFonts, string outDir, DirectoryInfo? binPath, SubsetConfig config, out Dictionary<string, string> nameMap)
    {
        nameMap = [];
        switch (config.Backend)
        {
            case SubsetBackend.PyFontTools:
                var pyftsubset = binPath is null ? "pyftsubset" : Path.Combine(binPath.FullName, "pyftsubset");
                var ttx = binPath is null ? "ttx" : Path.Combine(binPath.FullName, "ttx");
                var pyFT = new PyFontTools(pyftsubset, ttx, logger) { Config = config, sw = _stopwatch };
                pyFT.SubsetFonts(subsetFonts, outDir, out nameMap);
                break;
            case SubsetBackend.HarfBuzzSubset:
                var hbss = new HarfBuzzSubset(logger) { Config = config, sw = _stopwatch };
                hbss.SubsetFonts(subsetFonts, outDir, out nameMap);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// Re-subset and re-embed a single already-embedded ass file: extract its embedded fonts,
    /// subset them to this file's glyphs in an isolated temp dir, then embed the result back.
    /// The output is the self-contained ass at optDir/&lt;name&gt; (no loose font files).
    /// </summary>
    private void ReembedOneFile(FileInfo assFile, string optDir, DirectoryInfo? binPath, SubsetConfig config)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), "AssFontSubset_extract_" + Guid.NewGuid().ToString("N"));
        var subsetDir = Path.Combine(Path.GetTempPath(), "AssFontSubset_subset_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(subsetDir);

            var assFonts = GetAssFontInfoFromFiles([assFile], optDir, out var assMulti);
            var fontInfos = GetFontInfoFromExtractedFonts([assFile], extractDir);
            var subsetFonts = GetSubsetFonts(fontInfos, assFonts, out var fontMap);
            RunSubsetBackend(subsetFonts, subsetDir, binPath, config, out var nameMap);

            var embeddedFonts = EncodeSubsetFonts(subsetDir);
            foreach (var kv in assMulti)
            {
                ChangeAssFontName(kv.Value, nameMap, fontMap);
                EmbedFontsToAss(kv.Value, embeddedFonts);
                kv.Value.WriteAssFile(kv.Key);
            }

            if (!config.EmbedOnly)
            {
                // Also output the loose subset fonts, by default into a folder named after the ass.
                var destDir = config.SeparateFontFolder
                    ? Path.Combine(optDir, Path.GetFileNameWithoutExtension(assFile.Name))
                    : optDir;
                Directory.CreateDirectory(destDir);
                foreach (var fontFile in EnumerateSubsetFontFiles(subsetDir))
                {
                    File.Copy(fontFile, Path.Combine(destDir, Path.GetFileName(fontFile)), true);
                }
            }
        }
        finally
        {
            if (config.DebugMode)
            {
                logger?.ZLogInformation($"Reembed temp dirs kept: {extractDir} , {subsetDir}");
            }
            else
            {
                TryDeleteDirectory(extractDir);
                TryDeleteDirectory(subsetDir);
            }
        }
    }

    private static readonly string[] FontExtensions = [".ttf", ".otf"];

    private static IEnumerable<string> EnumerateSubsetFontFiles(string optDir) =>
        Directory.EnumerateFiles(optDir)
            .Where(p => FontExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));

    private void DeleteSubsetFontFiles(string optDir)
    {
        foreach (var fontFile in EnumerateSubsetFontFiles(optDir).ToList())
        {
            File.Delete(fontFile);
        }
        logger?.ZLogInformation($"Removed loose subset font files (embed only)");
    }

    /// <summary>
    /// Move the subsetted fonts into a sub-folder named after the ass file
    /// (i.e. output/&lt;assname&gt;/&lt;fonts&gt;). When there are multiple ass files,
    /// the fonts are placed under each ass file's folder.
    /// </summary>
    private void MoveFontsToAssFolders(string optDir, IEnumerable<string> assOutputPaths)
    {
        var fontFiles = EnumerateSubsetFontFiles(optDir).ToList();
        if (fontFiles.Count == 0) { return; }

        var assNames = assOutputPaths.Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();
        if (assNames.Count == 0) { return; }

        logger?.ZLogInformation($"Move subset fonts into ass-named folders");
        foreach (var assName in assNames)
        {
            var subDir = Path.Combine(optDir, assName!);
            Directory.CreateDirectory(subDir);
            foreach (var fontFile in fontFiles)
            {
                var dest = Path.Combine(subDir, Path.GetFileName(fontFile));
                if (File.Exists(dest)) { File.Delete(dest); }
                File.Copy(fontFile, dest);
            }
            logger?.ZLogDebug($"Placed {fontFiles.Count} subset fonts into {subDir}");
        }

        // The fonts now live under the ass folder(s); drop the originals at the top level.
        foreach (var fontFile in fontFiles)
        {
            File.Delete(fontFile);
        }
    }

    /// <summary>
    /// Read the subsetted font files in the output directory and UU-encode them
    /// into the ASS embedded-font representation. Subsetting is global, so the same
    /// set of fonts is embedded into each output ass file.
    /// </summary>
    private List<AssEmbeddedFile> EncodeSubsetFonts(string optDir)
    {
        List<AssEmbeddedFile> embeddedFonts = [];

        logger?.ZLogInformation($"Start embed subset fonts into ass");
        _stopwatch.Start();

        foreach (var fontFile in EnumerateSubsetFontFiles(optDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(fontFile);
            var embeddedFont = new AssEmbeddedFile(name, name, AssEmbeddedFileType.Font);
            embeddedFont.Encode(File.ReadAllBytes(fontFile));
            embeddedFonts.Add(embeddedFont);
            logger?.ZLogDebug($"Encoded font for embedding: {name}");
        }

        _stopwatch.Stop();
        logger?.ZLogInformation($"Embed {embeddedFonts.Count} subset fonts completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();
        return embeddedFonts;
    }

    private static void EmbedFontsToAss(AssData ass, List<AssEmbeddedFile> embeddedFonts)
    {
        // Drop any fonts the ass already embedded so re-embedding replaces them instead of
        // piling new fonts on top of the old ones.
        ass.Fonts.Files.Clear();
        foreach (var embeddedFont in embeddedFonts)
        {
            ass.Fonts.Files.Add(embeddedFont);
        }
        // Ensure the [Fonts] section is emitted even if the source ass had none.
        ass.Sections.Add(AssSection.Fonts);
    }

    /// <summary>
    /// Extract the fonts already embedded in the ass files to a temp directory, then parse them
    /// as the font source (deduplicating identical faces). Lets an embedded ass be re-subset
    /// without external font files.
    /// </summary>
    private IEnumerable<IGrouping<string, FontInfo>> GetFontInfoFromExtractedFonts(FileInfo[] assFiles, string extractDir)
    {
        logger?.ZLogInformation($"Extract embedded fonts from ass to re-subset");
        _stopwatch.Start();

        Directory.CreateDirectory(extractDir);
        HashSet<string> seenHashes = [];
        var count = 0;
        foreach (var assFile in assFiles)
        {
            foreach (var bytes in ExtractEmbeddedFonts(assFile.FullName))
            {
                if (bytes.Length == 0) { continue; }

                var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes));
                if (!seenHashes.Add(hash)) { continue; } // same font embedded in multiple ass

                var dest = Path.Combine(extractDir, $"embedded_{count}{FontExtensionFromBytes(bytes)}");
                File.WriteAllBytes(dest, SanitizeFontBytes(bytes));
                count++;
            }
        }

        logger?.ZLogInformation($"Extracted {count} embedded fonts");
        var fontInfos = FontParse.GetFontInfos(new DirectoryInfo(extractDir));

        // Mobsub.Font keeps each parsed font file open until GC. Release the handles now so the
        // subset tools can read them, and the temp dir can be deleted, on Windows (which locks
        // files with open handles).
        GC.Collect();
        GC.WaitForPendingFinalizers();

        _stopwatch.Stop();
        logger?.ZLogDebug($"Embedded font extraction completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();

        var deduped = DeduplicateFontInfos(fontInfos);
        return deduped.GroupBy(fontInfo => fontInfo.FamilyNames[FontConstant.LanguageIdEnUs]);
    }

    private static readonly string[] KnownSectionNames =
    [
        "Script Info", "V4 Styles", "V4+ Styles", "V4++ Styles", "Events",
        "Fonts", "Graphics", "Aegisub Project Garbage", "Aegisub Extradata",
    ];

    /// <summary>
    /// Decode the fonts embedded in an ass file's [Fonts] section directly from the raw text.
    /// UU-encoded data lines may start with '[', so the section only ends on a *known* section
    /// header — this avoids the truncation that happens when such lines are mistaken for headers.
    /// </summary>
    public static IEnumerable<byte[]> ExtractEmbeddedFonts(string assPath)
    {
        var inFonts = false;
        AssEmbeddedFile? current = null;
        List<byte[]> results = [];

        void Flush()
        {
            if (current.HasValue) { results.Add(current.Value.GetDecodedData()); }
            current = null;
        }

        foreach (var line in File.ReadLines(assPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' &&
                KnownSectionNames.Contains(trimmed[1..^1], StringComparer.OrdinalIgnoreCase))
            {
                Flush();
                inFonts = trimmed[1..^1].Equals("Fonts", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inFonts) { continue; }

            if (line.StartsWith("fontname:", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                var name = line["fontname:".Length..].Trim();
                current = new AssEmbeddedFile(name, name, AssEmbeddedFileType.Font);
            }
            else if (current.HasValue && trimmed.Length > 0)
            {
                current.Value.Data.Add(Encoding.UTF8.GetBytes(line));
            }
        }
        Flush();

        return results;
    }

    /// <summary>Delete a directory, retrying after GC in case a font handle is briefly still open (Windows).</summary>
    private void TryDeleteDirectory(string dir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.ZLogDebug($"Temp dir cleanup retry ({attempt + 1}): {ex.Message}");
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    private static string FontExtensionFromBytes(byte[] bytes)
    {
        if (bytes.Length < 4) { return ".ttf"; }
        // 'OTTO' => CFF/OpenType, 'ttcf' => TrueType Collection
        if (bytes[0] == 'O' && bytes[1] == 'T' && bytes[2] == 'T' && bytes[3] == 'O') { return ".otf"; }
        if (bytes[0] == 't' && bytes[1] == 't' && bytes[2] == 'c' && bytes[3] == 'f') { return ".ttc"; }
        return ".ttf";
    }

    /// <summary>
    /// Repair an embedded font whose OS/2 table version claims more fields than the table
    /// actually contains (some subsetters write a too-high version with a truncated table).
    /// fontTools/pyftsubset crash on this, so downgrade the version to fit the real length.
    /// </summary>
    private byte[] SanitizeFontBytes(byte[] bytes)
    {
        // Only handle single-font sfnt (TrueType 0x00010000 or OpenType 'OTTO').
        if (bytes.Length < 12) { return bytes; }
        var isSfnt = (bytes[0] == 0x00 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
                     || (bytes[0] == 'O' && bytes[1] == 'T' && bytes[2] == 'T' && bytes[3] == 'O');
        if (!isSfnt) { return bytes; }

        var numTables = (bytes[4] << 8) | bytes[5];
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            if (rec + 16 > bytes.Length) { break; }
            if (!(bytes[rec] == 'O' && bytes[rec + 1] == 'S' && bytes[rec + 2] == '/' && bytes[rec + 3] == '2')) { continue; }

            var tableOffset = ReadUInt32BE(bytes, rec + 8);
            var tableLength = ReadUInt32BE(bytes, rec + 12);
            if (tableOffset + 2 > bytes.Length) { break; }

            var version = (bytes[(int)tableOffset] << 8) | bytes[(int)tableOffset + 1];
            var required = version switch { 0 => 78u, 1 => 86u, 2 or 3 or 4 => 96u, 5 => 100u, _ => 78u };
            if (tableLength < required)
            {
                var newVersion = tableLength >= 100 ? 5 : tableLength >= 96 ? 4 : tableLength >= 86 ? 1 : 0;
                logger?.ZLogDebug($"Fix malformed OS/2 table: version {version} -> {newVersion} (length {tableLength})");
                bytes[(int)tableOffset] = (byte)(newVersion >> 8);
                bytes[(int)tableOffset + 1] = (byte)(newVersion & 0xFF);
            }
            break;
        }
        return bytes;
    }

    private static uint ReadUInt32BE(byte[] b, int offset) =>
        (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);

    private IEnumerable<IGrouping<string, FontInfo>> GetFontInfoFromFiles(string dir)
    {
        string[] supportFonts = [".ttf", ".otf", ".ttc", "otc"];

        logger?.ZLogInformation($"Start scan valid font files in {dir}");
        logger?.ZLogInformation($"Support font file extension: {string.Join(", ", supportFonts)}");
        _stopwatch.Start();

        var dirInfo = new DirectoryInfo(dir);
        var fontInfos = FontParse.GetFontInfos(dirInfo);

        _stopwatch.Stop();
        logger?.ZLogDebug($"Font file scanning completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();

        if (TryCheckDuplicatFonts(fontInfos, out var fontInfoGroup))
        {
            throw new Exception($"Maybe have duplicate fonts in fonts directory");
        }

        return fontInfoGroup;
    }

    /// <summary>
    /// Resolve the fonts needed by the ass files from a font database, parse only those files,
    /// then feed them into the unchanged matching/subset pipeline (so the output is identical
    /// to scanning a fonts folder that contains the same fonts).
    /// </summary>
    private IEnumerable<IGrouping<string, FontInfo>> GetFontInfoFromDatabase(string dbPath, IEnumerable<AssFontInfo> requiredAssFonts)
    {
        logger?.ZLogInformation($"Use font database {dbPath} to locate fonts");
        _stopwatch.Start();

        var entries = FontDatabase.Read(dbPath);
        var requiredNames = requiredAssFonts.Select(afi => afi.Name.StartsWith('@') ? afi.Name[1..] : afi.Name);
        var files = FontDatabase.ResolveFontFiles(entries, requiredNames)
            .Select(p => new FileInfo(p))
            .Where(f => f.Exists)
            .ToArray();

        logger?.ZLogInformation($"Font database matched {files.Length} font files");
        var fontInfos = FontParse.GetFontInfos(files);

        _stopwatch.Stop();
        logger?.ZLogDebug($"Font database lookup completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();

        // A large font library commonly contains the same font more than once (e.g. both .otf
        // and .ttf, or a romanized and a CJK file name). Unlike a curated fonts folder, these
        // are expected here, so collapse identical faces to one instead of failing.
        var deduped = DeduplicateFontInfos(fontInfos);
        return deduped.GroupBy(fontInfo => fontInfo.FamilyNames[FontConstant.LanguageIdEnUs]);
    }

    private List<FontInfo> DeduplicateFontInfos(List<FontInfo> fontInfos)
    {
        HashSet<(string, bool, bool, int, uint, ushort)> seen = [];
        List<FontInfo> result = [];
        foreach (var fi in fontInfos.OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            var key = (fi.FamilyNames[FontConstant.LanguageIdEnUs], fi.Bold, fi.Italic, fi.Weight, fi.Index, fi.MaxpNumGlyphs);
            if (seen.Add(key))
            {
                result.Add(fi);
            }
            else
            {
                logger?.ZLogDebug($"Skip duplicate font from database: {fi.FileName}");
            }
        }
        return result;
    }

    private bool TryCheckDuplicatFonts(List<FontInfo> fontInfos, out IEnumerable<IGrouping<string, FontInfo>> fontInfoGroup)
    {
        var dupFonts = false;
        fontInfoGroup = fontInfos.GroupBy(fontInfo => fontInfo.FamilyNames[FontConstant.LanguageIdEnUs]);
        foreach (var group in fontInfoGroup)
        {
            if (group.Count() <= 1) continue;
            var groupWithoutFileNames = group.GroupBy(fi => new
            {
                fi.Bold,
                fi.Italic,
                fi.Weight,
                fi.Index,
                fi.MaxpNumGlyphs,
            });

            foreach (var g in groupWithoutFileNames)
            {
                if (g.Count() <= 1) continue;
                logger?.ZLogError($"Duplicate fonts: {string.Join('、', g.Select(x => x.FileName))}");
                dupFonts = true;
            }
        }

        return dupFonts;
    }

    private Dictionary<AssFontInfo, HashSet<Rune>> GetAssFontInfoFromFiles(FileInfo[] assFiles, string optDir, out Dictionary<string, AssData> assDataWithOutputName)
    {
        assDataWithOutputName = [];
        Dictionary<AssFontInfo, HashSet<Rune>> multiAssFonts = [];

        logger?.ZLogInformation($"Start parse font info from ass files");
        _stopwatch.Start();

        foreach (var assFile in assFiles)
        {
            var assFileNew = Path.Combine(optDir, assFile.Name);

            // The parser only accepts standard centisecond times (H:MM:SS.cc). If the ass uses
            // e.g. millisecond precision, normalize a temp copy first so parsing doesn't fail.
            var parsePath = assFile.FullName;
            string? tempPath = null;
            try
            {
                var content = File.ReadAllText(assFile.FullName);
                // The parser requires the very first line to be a section header, so drop any
                // leading blank lines / whitespace (some tools prepend an empty line).
                var leadingStripped = StripLeadingBlankLines(content, out var deblanked);
                var timesChanged = TryNormalizeAssTimeFields(deblanked, out var normalized);
                if (leadingStripped || timesChanged)
                {
                    if (timesChanged) { logger?.ZLogWarning($"Normalized non-standard event time precision in {assFile.Name}"); }
                    if (leadingStripped) { logger?.ZLogWarning($"Stripped leading blank line(s) in {assFile.Name}"); }
                    tempPath = Path.Combine(Path.GetTempPath(), "AssFontSubset_" + Guid.NewGuid().ToString("N") + ".ass");
                    File.WriteAllText(tempPath, normalized);
                    parsePath = tempPath;
                }

                var assFonts = AssFont.GetAssFonts(parsePath, out var ass, logger);

                foreach (var kv in assFonts)
                {
                    if (multiAssFonts.Count > 0 && multiAssFonts.TryGetValue(kv.Key, out var value))
                    {
                        value.UnionWith(kv.Value);
                    }
                    else
                    {
                        multiAssFonts.Add(kv.Key, kv.Value);
                    }
                }
                assDataWithOutputName.Add(assFileNew, ass);
            }
            finally
            {
                if (tempPath is not null && File.Exists(tempPath)) { File.Delete(tempPath); }
            }
        }

        _stopwatch.Stop();
        logger?.ZLogInformation($"Ass font info parsing completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();
        return multiAssFonts;
    }

    /// <summary>Remove leading blank lines / whitespace / BOM so the first line is a section header.</summary>
    internal static bool StripLeadingBlankLines(string content, out string result)
    {
        var trimmed = content.TrimStart('\uFEFF', '\n', '\r', ' ', '\t');
        if (trimmed.Length == content.Length)
        {
            result = content;
            return false;
        }
        result = trimmed;
        return true;
    }

    internal static bool TryNormalizeAssTimeFields(string content, out string normalized)
    {
        var lines = content.Split('\n');
        var changed = false;
        var inEvents = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var head = line.TrimStart();
            if (head.StartsWith('['))
            {
                inEvents = head.TrimEnd('\r', ' ', '\t').Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents) { continue; }
            if (!head.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) &&
                !head.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cr = line.EndsWith('\r') ? "\r" : string.Empty;
            var body = cr.Length > 0 ? line[..^1] : line;
            var colon = body.IndexOf(':');
            if (colon < 0) { continue; }

            var prefix = body[..(colon + 1)];
            // Fields: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text (Text may hold commas).
            var parts = body[(colon + 1)..].Split(',', 10);
            if (parts.Length < 3) { continue; }

            var start = NormalizeTimeField(parts[1]);
            var end = NormalizeTimeField(parts[2]);
            if (start is null && end is null) { continue; }

            if (start is not null) { parts[1] = start; }
            if (end is not null) { parts[2] = end; }
            lines[i] = prefix + string.Join(',', parts) + cr;
            changed = true;
        }

        normalized = changed ? string.Join('\n', lines) : content;
        return changed;
    }

    /// <summary>Returns the centisecond-normalized time, or null when the field is already fine / not a time.</summary>
    private static string? NormalizeTimeField(string field)
    {
        var t = field.Trim();
        var c1 = t.IndexOf(':');
        if (c1 <= 0) { return null; }
        var c2 = t.IndexOf(':', c1 + 1);
        if (c2 < 0) { return null; }
        var dot = t.IndexOf('.', c2 + 1);
        if (dot < 0) { return null; }

        var h = t[..c1];
        var mm = t[(c1 + 1)..c2];
        var ss = t[(c2 + 1)..dot];
        var frac = t[(dot + 1)..];
        if (!AllAsciiDigits(h) || !AllAsciiDigits(mm) || !AllAsciiDigits(ss) || !AllAsciiDigits(frac) || frac.Length == 0)
        {
            return null;
        }

        var cc = frac.Length >= 2 ? frac[..2] : frac.PadRight(2, '0');
        var result = $"{h}:{mm.PadLeft(2, '0')}:{ss.PadLeft(2, '0')}.{cc}";
        return result == field ? null : result;
    }

    private static bool AllAsciiDigits(ReadOnlySpan<char> s)
    {
        foreach (var ch in s)
        {
            if (ch is < '0' or > '9') { return false; }
        }
        return s.Length > 0;
    }

    Dictionary<string, List<SubsetFont>> GetSubsetFonts(IEnumerable<IGrouping<string, FontInfo>> fontInfos, Dictionary<AssFontInfo, HashSet<Rune>> assFonts, out Dictionary<FontInfo, List<AssFontInfo>> fontMap)
    {
        logger?.ZLogInformation($"Start generate subset font info");
        _stopwatch.Start();

        logger?.ZLogDebug($"Start match font file info and ass font info");
        fontMap = [];
        List<AssFontInfo> matchedAssFontInfos = [];

        foreach (var fig in fontInfos)
        {
            foreach (var afi in assFonts.Keys)
            {
                if (matchedAssFontInfos.Contains(afi)) { continue; }
                var _fontInfo = AssFont.GetMatchedFontInfo(afi, fig, logger);
                if (_fontInfo == null) { continue; }
                var fontInfo = (FontInfo)_fontInfo;

                if (!fontMap.TryGetValue(fontInfo, out var _))
                {
                    fontMap.Add(fontInfo, []);
                }
                fontMap[fontInfo].Add(afi);

                matchedAssFontInfos.Add(afi);
                logger?.ZLogDebug($"{afi.ToString()} match {fontInfo.FileName} index {fontInfo.Index}");
            }
        }
        logger?.ZLogDebug($"Match completed");

        if (matchedAssFontInfos.Count != assFonts.Keys.Count)
        {
            var notFound = assFonts.Keys.Except(matchedAssFontInfos).ToList();
            throw new Exception($"Not found font file: {string.Join("、", notFound.Select(x => x.ToString()))}");
        }

        logger?.ZLogDebug($"Start convert font file info to subset font info");
        Dictionary<string, List<SubsetFont>> subsetFonts = [];
        foreach (var kv in fontMap)
        {
            HashSet<Rune> horRunes = [];
            HashSet<Rune> vertRunes = [];
            foreach (var afi in kv.Value)
            {
                if (afi.Name.StartsWith('@'))
                {
                    vertRunes.UnionWith(assFonts[afi]);
                }
                else
                {
                    horRunes.UnionWith(assFonts[afi]);
                }
            }

            var familyName = kv.Key.FamilyNames[FontConstant.LanguageIdEnUs];
            if (!subsetFonts.TryGetValue(familyName, out var _))
            {
                subsetFonts.Add(familyName, []);
            }
            subsetFonts[familyName].Add(new SubsetFont(new FileInfo(kv.Key.FileName), kv.Key.Index, horRunes, vertRunes));
        }
        logger?.ZLogDebug($"Convert completed");

        _stopwatch.Stop();
        logger?.ZLogInformation($"Generate completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();
        return subsetFonts;
    }

    static void ChangeAssFontName(AssData ass, Dictionary<string, string> nameMap, Dictionary<FontInfo, List<AssFontInfo>> fontMap)
    {
        Dictionary<string, string> assFontNameMap = [];
        foreach (var (kv, kv2) in from kv in nameMap
                                  from kv2 in fontMap
                                  where kv2.Key.FamilyNames.ContainsValue(kv.Key)
                                  select (kv, kv2))
        {
            foreach (var afi in kv2.Value)
            {
                assFontNameMap.TryAdd(afi.Name.StartsWith('@') ? afi.Name[1..] : afi.Name, kv.Value);
            }
        }

        var styleChanged = false;
        for (var i = 0; i < ass.Styles.Collection.Count; i++)
        {
            var style = ass.Styles.Collection[i];
            if (style.Fontname.StartsWith('@'))
            {
                if (assFontNameMap.TryGetValue(style.Fontname[1..], out var newFn))
                {
                    style.Fontname = '@' + newFn;
                    styleChanged = true;
                }
            }
            else
            {
                if (assFontNameMap.TryGetValue(style.Fontname, out var newFn))
                {
                    style.Fontname = newFn;
                    styleChanged = true;
                }
            }

            ass.Styles.Collection[i] = style;
        }

        if (styleChanged)
        {
            ass.Styles.InvalidateStyleMap();
        }

        var assFontNameMapSort = assFontNameMap.OrderByDescending(d => d.Key).ToDictionary();

        if (ass.Events is not null)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < ass.Events.Collection.Count; i++)
            {
                var evt = ass.Events.Collection[i];
                if (!evt.IsDialogue) { continue; }
                var text = evt.Text.AsSpan();
                if (text.IsEmpty) { continue; }

                if (evt.TextRanges.Length == 0)
                {
                    evt.UpdateTextRanges();
                }

                if (evt.TextRanges.Length == 1)
                {
                    continue;
                }

                var lineChanged = false;
                foreach (var range in evt.TextRanges)
                {
                    var block = text[range];
                    Debug.WriteLine($"{range.Start}:{range.End}:{block}");
                    if (AssEvent.IsOverrideBlock(block))
                    {
                        if (ReplaceFontName(block, assFontNameMapSort, sb))
                        {
                            lineChanged = true;
                        }
                    }
                    else
                    {
                        sb.Append(block);
                    }
                    Debug.WriteLine(sb.ToString());
                }

                if (lineChanged)
                {
                    evt.Text = sb.ToString();
                    ass.Events.Collection[i] = evt;
                }

                sb.Clear();
            }
        }

        List<string> subsetList = [];
        foreach (var kv in assFontNameMapSort)
        {
            subsetList.Add($"Font Subset: {kv.Value} - {kv.Key}");
        }
        subsetList.AddRange(ass.ScriptInfo.Comment);
        ass.ScriptInfo.Comment = subsetList;
    }

    private static bool ReplaceFontName(ReadOnlySpan<char> block, Dictionary<string, string> nameMap, StringBuilder sb)
    {
        const string fontNameTag = @"\fn";

        var changed = false;
        var start = 0;
        var tagIndex = block.IndexOf(fontNameTag);
        while (tagIndex != -1)
        {
            tagIndex += fontNameTag.Length;
            sb.Append(block.Slice(start, tagIndex));
            start += tagIndex;

            var sepValues = SearchValues.Create(@"\}");
            var nextTag = block[start..].IndexOfAny(sepValues);

            var tagValue = nextTag == -1 ? block[start..] : block.Slice(start, nextTag);

            var matched = false;
            foreach (var (oldValue, newValue) in nameMap)
            {
                var vertical = tagValue.Length > 1 && tagValue[0] == '@';
                if (tagValue[(vertical ? 1 : 0)..].SequenceEqual(oldValue))
                {
                    if (vertical)
                    {
                        sb.Append('@');
                    }
                    sb.Append(newValue);
                    changed = true;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                sb.Append(tagValue);
            }

            start += tagValue.Length;
            if (nextTag == -1)
            {
                break;
            }

            tagIndex = block[start..].IndexOf(fontNameTag);
        }

        sb.Append(block[start..]);

        return changed;
    }
}
