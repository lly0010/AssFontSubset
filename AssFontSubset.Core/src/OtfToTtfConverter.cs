using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace AssFontSubset.Core;

/// <summary>
/// Converts fonts with PostScript/CFF outlines (.otf) into TrueType outlines (.ttf)
/// by running a small fontTools (cu2qu) script through Python. The original font is
/// left untouched; a converted copy is written to <c>workDir</c>.
/// </summary>
public static class OtfToTtfConverter
{
    public static bool IsCffFont(string path)
        => string.Equals(Path.GetExtension(path), ".otf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Convert <paramref name="otfPath"/> to a ttf inside <paramref name="workDir"/> and
    /// return the path of the produced ttf.
    /// </summary>
    public static string Convert(string otfPath, string workDir, string? pythonPath, ILogger? logger = null)
    {
        Directory.CreateDirectory(workDir);
        var ttfPath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(otfPath) + ".ttf");

        var scriptPath = Path.Combine(workDir, "otf2ttf.py");
        if (!File.Exists(scriptPath))
        {
            File.WriteAllText(scriptPath, ScriptContent);
        }

        var python = ResolvePython(pythonPath);
        logger?.ZLogInformation($"Convert OTF to TTF: {Path.GetFileName(otfPath)} (python: {python})");

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(otfPath);
        startInfo.ArgumentList.Add(ttfPath);
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(startInfo)
            ?? throw new Exception($"Failed to start python for OTF→TTF conversion ({python})");

        var error = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(ttfPath))
        {
            throw new Exception(
                $"OTF→TTF conversion failed for {Path.GetFileName(otfPath)}. " +
                $"Ensure Python with fontTools is installed. Detail: {error.TrimEnd()}");
        }

        return ttfPath;
    }

    private static string ResolvePython(string? pythonPath)
    {
        if (!string.IsNullOrWhiteSpace(pythonPath))
        {
            return pythonPath;
        }
        // Prefer python3 on unix-like systems, fall back to python.
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    // Standard otf2ttf (cu2qu) conversion, adapted from the widely used fontTools recipe.
    private const string ScriptContent = """
import sys
from fontTools.ttLib import TTFont, newTable
from fontTools.pens.cu2quPen import Cu2QuPen
from fontTools.pens.ttGlyphPen import TTGlyphPen

MAX_ERR = 1.0
POST_FORMAT = 2.0
REVERSE_DIRECTION = True


def glyphs_to_quadratic(glyphs, max_err, reverse_direction):
    quad_glyphs = {}
    for name in glyphs.keys():
        glyph = glyphs[name]
        tt_pen = TTGlyphPen(glyphs)
        cu2qu_pen = Cu2QuPen(tt_pen, max_err, reverse_direction=reverse_direction)
        glyph.draw(cu2qu_pen)
        quad_glyphs[name] = tt_pen.glyph()
    return quad_glyphs


def update_hmtx(font, glyf):
    hmtx = font["hmtx"]
    for name, glyph in glyf.glyphs.items():
        if hasattr(glyph, "xMin"):
            hmtx[name] = (hmtx[name][0], glyph.xMin)


def otf_to_ttf(font, post_format, max_err, reverse_direction):
    assert font.sfntVersion == "OTTO"
    assert "CFF " in font

    glyph_order = font.getGlyphOrder()

    font["loca"] = newTable("loca")
    font["glyf"] = glyf = newTable("glyf")
    glyf.glyphOrder = glyph_order
    glyf.glyphs = glyphs_to_quadratic(font.getGlyphSet(), max_err, reverse_direction)
    del font["CFF "]
    glyf.compile(font)
    update_hmtx(font, glyf)

    font["maxp"] = maxp = newTable("maxp")
    maxp.tableVersion = 0x00010000
    maxp.maxZones = 1
    maxp.maxTwilightPoints = 0
    maxp.maxStorage = 0
    maxp.maxFunctionDefs = 0
    maxp.maxInstructionDefs = 0
    maxp.maxStackElements = 0
    maxp.maxSizeOfInstructions = 0
    maxp.maxComponentElements = max(
        (len(getattr(g, "components", [])) for g in glyf.glyphs.values()),
        default=0,
    )
    maxp.compile(font)

    post = font["post"]
    post.formatType = post_format
    post.extraNames = []
    post.mapping = {}
    post.glyphOrder = glyph_order
    try:
        post.compile(font)
    except OverflowError:
        post.formatType = 3.0

    font.sfntVersion = "\000\001\000\000"


def main():
    in_path = sys.argv[1]
    out_path = sys.argv[2]
    font = TTFont(in_path)
    otf_to_ttf(font, POST_FORMAT, MAX_ERR, REVERSE_DIRECTION)
    font.save(out_path)


if __name__ == "__main__":
    main()
""";
}
