//go:build windows

package ui

import (
	"os"
	"path/filepath"
)

// systemCJKFontBytes returns the bytes of a Windows system font that covers CJK,
// preferring Microsoft YaHei. The first candidate that exists is returned; the
// caller flattens a .ttc to a single face.
func systemCJKFontBytes() (string, []byte) {
	winDir := os.Getenv("WINDIR")
	if winDir == "" {
		winDir = os.Getenv("SystemRoot")
	}
	if winDir == "" {
		winDir = `C:\Windows`
	}
	fontsDir := filepath.Join(winDir, "Fonts")

	candidates := []string{
		"msyh.ttc",    // Microsoft YaHei (Win 8+)
		"msyh.ttf",    // Microsoft YaHei (Win 7)
		"Deng.ttf",    // DengXian
		"simhei.ttf",  // SimHei
		"simsun.ttc",  // SimSun / NSimSun
		"simkai.ttf",  // KaiTi
		"meiryo.ttc",  // Meiryo (Japanese)
		"YuGothM.ttc", // Yu Gothic (Japanese)
		"YuGothR.ttc",
		"msgothic.ttc", // MS Gothic (Japanese)
		"malgun.ttf",   // Malgun Gothic (Korean)
	}
	for _, name := range candidates {
		p := filepath.Join(fontsDir, name)
		if b, err := os.ReadFile(p); err == nil && len(b) > 0 {
			return name, b
		}
	}
	return "", nil
}
