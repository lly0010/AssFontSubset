namespace AssFontSubset.Core;

public struct SubsetConfig
{
    public bool SourceHanEllipsis;
    public bool DebugMode;
    public SubsetBackend Backend;

    /// <summary>
    /// When enabled, fonts with PostScript/CFF outlines (.otf) are converted to
    /// TrueType outlines (.ttf) before being subset. Requires Python with fontTools.
    /// </summary>
    public bool ConvertOtfToTtf;

    /// <summary>
    /// Optional path to the python executable used for OTF→TTF conversion.
    /// When null, "python3" / "python" on PATH is used.
    /// </summary>
    public string? PythonPath;
}

public enum SubsetBackend
{
    PyFontTools = 1,
    HarfBuzzSubset = 2,
}