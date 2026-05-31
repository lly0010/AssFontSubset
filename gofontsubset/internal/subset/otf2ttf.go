package subset

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

// otf2ttfScript is the standard fontTools (cu2qu) OTF->TTF conversion recipe.
const otf2ttfScript = `import sys
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


font = TTFont(sys.argv[1])
otf_to_ttf(font, POST_FORMAT, MAX_ERR, REVERSE_DIRECTION)
font.save(sys.argv[2])
`

// convertOtfToTtf converts a CFF/OTF font to a TrueType (.ttf) copy inside
// workDir using fontTools via Python, returning the produced .ttf path.
func convertOtfToTtf(otfPath, workDir, python string, logf func(string)) (string, error) {
	if err := os.MkdirAll(workDir, 0o755); err != nil {
		return "", err
	}
	scriptPath := filepath.Join(workDir, "otf2ttf.py")
	if _, err := os.Stat(scriptPath); err != nil {
		if err := os.WriteFile(scriptPath, []byte(otf2ttfScript), 0o644); err != nil {
			return "", err
		}
	}
	ttfPath := filepath.Join(workDir, strings.TrimSuffix(filepath.Base(otfPath), filepath.Ext(otfPath))+".ttf")

	py := resolvePython(python)
	if logf != nil {
		logf(fmt.Sprintf("convert OTF to TTF: %s (python: %s)", filepath.Base(otfPath), py))
	}

	cmd := exec.Command(py, scriptPath, otfPath, ttfPath)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return "", fmt.Errorf("OTF->TTF conversion failed for %s (need Python with fontTools): %v: %s",
			filepath.Base(otfPath), err, strings.TrimSpace(string(out)))
	}
	if _, err := os.Stat(ttfPath); err != nil {
		return "", fmt.Errorf("OTF->TTF produced no output for %s", filepath.Base(otfPath))
	}
	return ttfPath, nil
}

func resolvePython(python string) string {
	if strings.TrimSpace(python) != "" {
		return python
	}
	if runtime.GOOS == "windows" {
		return "python"
	}
	return "python3"
}
