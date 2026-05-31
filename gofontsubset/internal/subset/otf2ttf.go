package subset

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

// otf2ttfScript converts a single font face to a standalone TrueType (.ttf)
// file using fontTools. It accepts a font number (argv[3]) so a face can be
// pulled out of a collection (.ttc/.otc). CFF/OpenType outlines are converted
// to quadratic glyf via cu2qu; TrueType faces are simply re-saved as a clean,
// single-face .ttf (which is exactly what flattening a collection requires).
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


font_number = int(sys.argv[3]) if len(sys.argv) > 3 else 0
font = TTFont(sys.argv[1], fontNumber=font_number)
if font.sfntVersion == "OTTO" and "CFF " in font:
    otf_to_ttf(font, POST_FORMAT, MAX_ERR, REVERSE_DIRECTION)
# Drop any collection/woff wrapper so the result is a plain standalone .ttf.
font.flavor = None
font.save(sys.argv[2])
`

// convertToTtf produces a standalone TrueType (.ttf) copy of a single font face
// inside workDir, returning the produced .ttf path. faceIndex selects the face
// when src is a collection (.ttc/.otc); it is ignored for single-face files.
func convertToTtf(src string, faceIndex int, workDir, python string, logf func(string)) (string, error) {
	if err := os.MkdirAll(workDir, 0o755); err != nil {
		return "", err
	}
	scriptPath := filepath.Join(workDir, "otf2ttf.py")
	if _, err := os.Stat(scriptPath); err != nil {
		if err := os.WriteFile(scriptPath, []byte(otf2ttfScript), 0o644); err != nil {
			return "", err
		}
	}
	base := strings.TrimSuffix(filepath.Base(src), filepath.Ext(src))
	// Include the face index so distinct faces of one collection don't collide.
	ttfPath := filepath.Join(workDir, fmt.Sprintf("%s.%d.ttf", base, faceIndex))

	py := resolvePython(python)
	if logf != nil {
		logf(fmt.Sprintf("convert to TTF: %s (face %d, python: %s)", filepath.Base(src), faceIndex, py))
	}

	cmd := exec.Command(py, scriptPath, src, ttfPath, fmt.Sprintf("%d", faceIndex))
	out, err := cmd.CombinedOutput()
	if err != nil {
		return "", fmt.Errorf("TTF conversion failed for %s (need Python with fontTools): %v: %s",
			filepath.Base(src), err, strings.TrimSpace(string(out)))
	}
	if _, err := os.Stat(ttfPath); err != nil {
		return "", fmt.Errorf("TTF conversion produced no output for %s", filepath.Base(src))
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
