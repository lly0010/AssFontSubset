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

    /// <summary>
    /// Subset using fonts gathered from a single fonts folder.
    /// </summary>
    public async Task SubsetAsync(FileInfo[] path, DirectoryInfo? fontPath, DirectoryInfo? outputPath, DirectoryInfo? binPath, SubsetConfig subsetConfig)
    {
        var baseDir = path[0].Directory!.FullName;
        fontPath ??= new DirectoryInfo(Path.Combine(baseDir, "fonts"));
        outputPath ??= new DirectoryInfo(Path.Combine(baseDir, "output"));

        ValidateInputs(path);
        if (!fontPath.Exists) { throw new Exception($"Please check if directory {fontPath} exists"); }
        if (outputPath.Exists) { outputPath.Delete(true); }
        var fontDir = fontPath.FullName;
        var optDir = outputPath.FullName;

        await Task.Run(() => RunPipeline(_ => GetFontInfoFromFiles(fontDir), path, optDir, binPath, subsetConfig));
    }

    /// <summary>
    /// Subset using a pre-built font database, so font files don't have to be gathered
    /// for every job. Only the fonts referenced by the ass files are used.
    /// </summary>
    public async Task SubsetWithDatabaseAsync(FileInfo[] path, FontDatabase database, DirectoryInfo? outputPath, DirectoryInfo? binPath, SubsetConfig subsetConfig)
    {
        var baseDir = path[0].Directory!.FullName;
        outputPath ??= new DirectoryInfo(Path.Combine(baseDir, "output"));

        ValidateInputs(path);
        if (database.Count == 0) { throw new Exception("Font database is empty. Please build the database first."); }
        if (outputPath.Exists) { outputPath.Delete(true); }
        var optDir = outputPath.FullName;

        await Task.Run(() => RunPipeline(
            assFonts => ResolveFontInfosFromDatabase(database, assFonts),
            path, optDir, binPath, subsetConfig));
    }

    private static void ValidateInputs(FileInfo[] path)
    {
        foreach (var file in path)
        {
            if (!file.Exists)
            {
                throw new Exception($"Please check if file {file} exists");
            }
        }
    }

    private void RunPipeline(
        Func<Dictionary<AssFontInfo, HashSet<Rune>>, IEnumerable<IGrouping<string, FontInfo>>> fontProvider,
        FileInfo[] path, string optDir, DirectoryInfo? binPath, SubsetConfig subsetConfig)
    {
        var assFonts = GetAssFontInfoFromFiles(path, optDir, out var assMulti);
        var fontInfos = fontProvider(assFonts);
        var subsetFonts = GetSubsetFonts(fontInfos, assFonts, out var fontMap, subsetConfig, optDir);
        Dictionary<string, string> nameMap = [];

        try
        {
            switch (subsetConfig.Backend)
            {
                case SubsetBackend.PyFontTools:
                    var pyftsubset = binPath is null ? "pyftsubset" : Path.Combine(binPath.FullName, "pyftsubset");
                    var ttx = binPath is null ? "ttx" : Path.Combine(binPath.FullName, "ttx");
                    var pyFT = new PyFontTools(pyftsubset, ttx, logger) { Config = subsetConfig, sw = _stopwatch };
                    pyFT.SubsetFonts(subsetFonts, optDir, out nameMap);
                    break;
                case SubsetBackend.HarfBuzzSubset:
                    var hbss = new HarfBuzzSubset(logger) { Config = subsetConfig, sw = _stopwatch };
                    hbss.SubsetFonts(subsetFonts, optDir, out nameMap);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            foreach (var kv in assMulti)
            {
                ChangeAssFontName(kv.Value, nameMap, fontMap);
                kv.Value.WriteAssFile(kv.Key);
            }
        }
        finally
        {
            CleanupOtfWorkDir(optDir, subsetConfig);
        }
    }

    private IEnumerable<IGrouping<string, FontInfo>> ResolveFontInfosFromDatabase(FontDatabase database, Dictionary<AssFontInfo, HashSet<Rune>> assFonts)
    {
        logger?.ZLogInformation($"Resolving fonts from database ({database.Count} indexed faces)");
        var selected = database.SelectForNames(assFonts.Keys.Select(afi => afi.Name));
        if (TryCheckDuplicatFonts(selected, out var fontInfoGroup))
        {
            throw new Exception("Maybe have duplicate fonts in font database");
        }
        return fontInfoGroup;
    }

    private static string GetOtfWorkDir(string optDir) => Path.Combine(optDir, "_otf2ttf_");

    private void CleanupOtfWorkDir(string optDir, SubsetConfig config)
    {
        if (config.DebugMode) { return; }
        var workDir = GetOtfWorkDir(optDir);
        if (Directory.Exists(workDir))
        {
            try { Directory.Delete(workDir, true); }
            catch (Exception ex) { logger?.ZLogWarning($"Failed to clean OTF→TTF temp dir: {ex.Message}"); }
        }
    }

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
            var assFonts = AssFont.GetAssFonts(assFile.FullName, out var ass, logger);

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

        _stopwatch.Stop();
        logger?.ZLogInformation($"Ass font info parsing completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();
        return multiAssFonts;
    }

    Dictionary<string, List<SubsetFont>> GetSubsetFonts(IEnumerable<IGrouping<string, FontInfo>> fontInfos, Dictionary<AssFontInfo, HashSet<Rune>> assFonts, out Dictionary<FontInfo, List<AssFontInfo>> fontMap, SubsetConfig config, string optDir)
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
        Dictionary<string, string> otfConvertCache = [];
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

            var fontFile = ResolveFontFile(kv.Key, config, optDir, otfConvertCache);

            var familyName = kv.Key.FamilyNames[FontConstant.LanguageIdEnUs];
            if (!subsetFonts.TryGetValue(familyName, out var _))
            {
                subsetFonts.Add(familyName, []);
            }
            subsetFonts[familyName].Add(new SubsetFont(fontFile, kv.Key.Index, horRunes, vertRunes));
        }
        logger?.ZLogDebug($"Convert completed");

        _stopwatch.Stop();
        logger?.ZLogInformation($"Generate completed, use {_stopwatch.ElapsedMilliseconds} ms");
        _stopwatch.Reset();
        return subsetFonts;
    }

    private FileInfo ResolveFontFile(FontInfo fontInfo, SubsetConfig config, string optDir, Dictionary<string, string> cache)
    {
        if (!config.ConvertOtfToTtf || !OtfToTtfConverter.IsCffFont(fontInfo.FileName))
        {
            return new FileInfo(fontInfo.FileName);
        }

        if (!cache.TryGetValue(fontInfo.FileName, out var converted))
        {
            converted = OtfToTtfConverter.Convert(fontInfo.FileName, GetOtfWorkDir(optDir), config.PythonPath, logger);
            cache[fontInfo.FileName] = converted;
        }
        return new FileInfo(converted);
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
