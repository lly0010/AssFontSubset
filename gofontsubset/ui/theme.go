package ui

import (
	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/theme"

	"github.com/lly0010/AssFontSubset/gofontsubset/internal/opentype"
)

// cjkTheme wraps the default theme but serves a CJK-capable font so that font
// names in Japanese/Chinese/Korean render correctly instead of as tofu boxes.
type cjkTheme struct {
	fyne.Theme
	font fyne.Resource
}

func (t *cjkTheme) Font(s fyne.TextStyle) fyne.Resource {
	// Keep the monospace face for code-like text; everything else uses the CJK
	// font, which also covers Latin so the UI stays consistent.
	if t.font != nil && !s.Monospace {
		return t.font
	}
	return t.Theme.Font(s)
}

// loadCJKFont locates a system font that can render CJK text and returns it as a
// single-face Fyne resource (Fyne can only parse single-face sfnt files, so a
// .ttc is flattened to its first face). Returns nil when none is found.
func loadCJKFont() fyne.Resource {
	name, data := systemCJKFontBytes()
	if len(data) == 0 {
		return nil
	}
	single, err := opentype.ExtractFace(data, 0)
	if err != nil {
		single = data
	}
	return fyne.NewStaticResource(name, single)
}

// applyCJKTheme installs the CJK theme on the app if a suitable font was found.
func applyCJKTheme(app fyne.App) {
	if res := loadCJKFont(); res != nil {
		app.Settings().SetTheme(&cjkTheme{Theme: theme.DefaultTheme(), font: res})
	}
}
