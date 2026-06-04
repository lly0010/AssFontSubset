namespace AssFontSubset.Core;

public struct SubsetConfig
{
    public bool SourceHanEllipsis;
    public bool DebugMode;
    public SubsetBackend Backend;
    public bool EmbedFontToAss;
    public bool SeparateFontFolder;
    public string? FontDatabasePath;
    public bool ReembedFonts;
    public bool EmbedOnly;
    public bool FontFallback;
}

public enum SubsetBackend
{
    PyFontTools = 1,
    HarfBuzzSubset = 2,
}