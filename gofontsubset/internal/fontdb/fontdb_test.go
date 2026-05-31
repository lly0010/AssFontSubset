package fontdb

import (
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"

	"github.com/lly0010/AssFontSubset/gofontsubset/internal/opentype"
)

func sampleEntry(family, ps string, weight int, bold bool) Entry {
	return Entry{
		Families:    []string{family},
		FullNames:   []string{ps},
		PsNames:     []string{ps},
		Weight:      weight,
		Bold:        bold,
		Path:        ps + ".ttf",
		FamilyNames: map[int]string{opentype.LangEnUS: family},
	}
}

func TestEntryJSONShape(t *testing.T) {
	e := Entry{
		Families:      []string{"a-otf jun pro 501", "a-otf じゅん pro 501"},
		FullNames:     []string{"jun501pro-bold"},
		PsNames:       []string{"jun501pro-bold"},
		Weight:        600,
		Slant:         0,
		Path:          `D:\fonts\old\A-OTF Jun Pro 501.ttf`,
		Index:         0,
		LastWriteTime: "UTC 2025-04-15 01:03:05",
	}
	data, err := json.Marshal(e)
	if err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"families", "fullnames", "psnames", "weight", "slant", "path", "index", "last_write_time"} {
		if !strings.Contains(string(data), `"`+key+`"`) {
			t.Errorf("missing json key %q in %s", key, data)
		}
	}
}

func TestSaveLoad(t *testing.T) {
	path := filepath.Join(t.TempDir(), "db.json")
	db := &DB{Entries: []Entry{sampleEntry("Example Font", "ExampleFont-Bold", 700, true)}}
	if err := db.Save(path); err != nil {
		t.Fatal(err)
	}
	loaded, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if loaded.Count() != 1 || loaded.Entries[0].Weight != 700 || !loaded.Entries[0].Bold {
		t.Errorf("round-trip mismatch: %+v", loaded.Entries)
	}
}

func TestSelectForNames(t *testing.T) {
	db := &DB{Entries: []Entry{
		sampleEntry("Family A", "FamilyA-Regular", 400, false),
		sampleEntry("Family A", "FamilyA-Bold", 700, true),
		sampleEntry("Family B", "FamilyB-Regular", 400, false),
	}}

	// Referencing a face by its bold full name returns the whole family.
	got := db.SelectForNames([]string{"FamilyA-Bold"})
	if len(got) != 2 {
		t.Fatalf("want 2 (whole family), got %d", len(got))
	}
	for _, e := range got {
		if e.PrimaryFamily() != "Family A" {
			t.Errorf("unexpected family %q", e.PrimaryFamily())
		}
	}

	// Vertical prefix is stripped before matching.
	if len(db.SelectForNames([]string{"@Family B"})) != 1 {
		t.Errorf("vertical prefix not handled")
	}
}
