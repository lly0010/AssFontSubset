package ass

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const sample = `[Script Info]
Title: x

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Example Font,50,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
Style: Vert,@Example Font,50,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\fnOther Font\b1}AB
Dialogue: 0,0:00:01.00,0:00:02.00,Vert,,0,0,0,,世界
`

func writeSample(t *testing.T) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "sample.ass")
	if err := os.WriteFile(path, []byte(sample), 0o644); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestCollect(t *testing.T) {
	doc, err := Parse(writeSample(t))
	if err != nil {
		t.Fatal(err)
	}
	col := map[FontKey]*Usage{}
	doc.Collect(col)

	other := col[FontKey{Name: "Other Font", Bold: true}]
	if other == nil {
		t.Fatalf("missing Other Font (bold) key; got %v", keys(col))
	}
	if !other.Runes['A'] || !other.Runes['B'] {
		t.Errorf("Other Font runes missing A/B")
	}

	ex := col[FontKey{Name: "Example Font"}]
	if ex == nil {
		t.Fatalf("missing Example Font key; got %v", keys(col))
	}
	if !ex.Runes['世'] || !ex.Runes['界'] {
		t.Errorf("Example Font runes missing 世/界")
	}
	if !ex.Vertical {
		t.Errorf("Example Font should be flagged vertical (used via @ style)")
	}
}

func TestRewrite(t *testing.T) {
	doc, err := Parse(writeSample(t))
	if err != nil {
		t.Fatal(err)
	}
	out := filepath.Join(t.TempDir(), "out.ass")
	nameMap := map[string]string{"Example Font": "NEWFAM", "Other Font": "NEWOTH"}
	if err := doc.Rewrite(nameMap, out); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(out)
	s := string(data)

	checks := []string{
		"Style: Default,NEWFAM,",
		"Style: Vert,@NEWFAM,",
		`{\fnNEWOTH\b1}AB`,
		"; Font Subset: NEWFAM - Example Font",
		"; Font Subset: NEWOTH - Other Font",
	}
	for _, c := range checks {
		if !strings.Contains(s, c) {
			t.Errorf("output missing %q\n---\n%s", c, s)
		}
	}
}

func keys(col map[FontKey]*Usage) []FontKey {
	var ks []FontKey
	for k := range col {
		ks = append(ks, k)
	}
	return ks
}
