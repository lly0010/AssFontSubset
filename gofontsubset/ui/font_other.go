//go:build !windows

package ui

import (
	"os"
	"path/filepath"
)

// systemCJKFontBytes looks for a few common CJK fonts on non-Windows systems
// (mainly for development); returns no data when none is installed, in which
// case the default Fyne font is kept.
func systemCJKFontBytes() (string, []byte) {
	candidates := []string{
		"/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
		"/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
		"/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
		"/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
		"/System/Library/Fonts/PingFang.ttc",
		"/System/Library/Fonts/Hiragino Sans GB.ttc",
	}
	for _, p := range candidates {
		if b, err := os.ReadFile(p); err == nil && len(b) > 0 {
			return filepath.Base(p), b
		}
	}
	return "", nil
}
