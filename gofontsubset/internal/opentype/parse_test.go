package opentype

import (
	"encoding/binary"
	"sort"
	"strings"
	"testing"

	"golang.org/x/text/encoding/japanese"
)

type rawRec struct {
	platform, enc, lang, nameID uint16
	raw                         []byte
}

// makeNameTableRaw builds a name table from records whose string bytes are
// already encoded (so non-UTF-16 encodings can be tested).
func makeNameTableRaw(recs []rawRec) []byte {
	header := make([]byte, 6+len(recs)*12)
	binary.BigEndian.PutUint16(header[0:2], 0)
	binary.BigEndian.PutUint16(header[2:4], uint16(len(recs)))
	binary.BigEndian.PutUint16(header[4:6], uint16(6+len(recs)*12))
	var storage []byte
	for i, r := range recs {
		off := 6 + i*12
		binary.BigEndian.PutUint16(header[off:off+2], r.platform)
		binary.BigEndian.PutUint16(header[off+2:off+4], r.enc)
		binary.BigEndian.PutUint16(header[off+4:off+6], r.lang)
		binary.BigEndian.PutUint16(header[off+6:off+8], r.nameID)
		binary.BigEndian.PutUint16(header[off+8:off+10], uint16(len(r.raw)))
		binary.BigEndian.PutUint16(header[off+10:off+12], uint16(len(storage)))
		storage = append(storage, r.raw...)
	}
	return append(header, storage...)
}

type nameRec struct {
	platform, enc, lang, nameID uint16
	value                       string
}

func makeNameTable(recs []nameRec) []byte {
	header := make([]byte, 6+len(recs)*12)
	binary.BigEndian.PutUint16(header[0:2], 0)
	binary.BigEndian.PutUint16(header[2:4], uint16(len(recs)))
	binary.BigEndian.PutUint16(header[4:6], uint16(6+len(recs)*12))
	var storage []byte
	for i, r := range recs {
		enc := encodeUTF16BE(r.value)
		off := 6 + i*12
		binary.BigEndian.PutUint16(header[off:off+2], r.platform)
		binary.BigEndian.PutUint16(header[off+2:off+4], r.enc)
		binary.BigEndian.PutUint16(header[off+4:off+6], r.lang)
		binary.BigEndian.PutUint16(header[off+6:off+8], r.nameID)
		binary.BigEndian.PutUint16(header[off+8:off+10], uint16(len(enc)))
		binary.BigEndian.PutUint16(header[off+10:off+12], uint16(len(storage)))
		storage = append(storage, enc...)
	}
	return append(header, storage...)
}

func buildSFNT(t *testing.T, tables map[string][]byte) []byte {
	t.Helper()
	tags := make([]string, 0, len(tables))
	for tag := range tables {
		tags = append(tags, tag)
	}
	sort.Strings(tags)
	n := len(tags)
	headerLen := 12 + n*16
	offsets := make([]uint32, n)
	cur := headerLen
	for i, tag := range tags {
		offsets[i] = uint32(cur)
		cur += len(tables[tag])
		for cur%4 != 0 {
			cur++
		}
	}
	out := make([]byte, cur)
	binary.BigEndian.PutUint32(out[0:4], 0x00010000)
	binary.BigEndian.PutUint16(out[4:6], uint16(n))
	for i, tag := range tags {
		r := 12 + i*16
		copy(out[r:r+4], tag)
		binary.BigEndian.PutUint32(out[r+8:r+12], offsets[i])
		binary.BigEndian.PutUint32(out[r+12:r+16], uint32(len(tables[tag])))
		copy(out[offsets[i]:], tables[tag])
	}
	return out
}

func sampleFont(t *testing.T) []byte {
	os2 := make([]byte, 96)
	binary.BigEndian.PutUint16(os2[4:6], 700)    // usWeightClass
	binary.BigEndian.PutUint16(os2[62:64], 0x21) // fsSelection: italic(bit0)+bold(bit5)

	maxp := make([]byte, 32)
	binary.BigEndian.PutUint32(maxp[0:4], 0x00010000)
	binary.BigEndian.PutUint16(maxp[4:6], 1234)

	head := make([]byte, 54)

	name := makeNameTable([]nameRec{
		{3, 1, 0x0409, 1, "Test Font"},
		{3, 1, 0x0411, 1, "テストフォント"},
		{3, 1, 0x0409, 4, "Test Font Regular"},
		{3, 1, 0x0409, 6, "TestFont-Regular"},
	})

	return buildSFNT(t, map[string][]byte{
		"OS/2": os2,
		"maxp": maxp,
		"head": head,
		"name": name,
	})
}

func TestParse(t *testing.T) {
	faces, err := Parse(sampleFont(t))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if len(faces) != 1 {
		t.Fatalf("want 1 face, got %d", len(faces))
	}
	f := faces[0]
	if f.FamilyByLang[LangEnUS] != "Test Font" {
		t.Errorf("en-US family = %q", f.FamilyByLang[LangEnUS])
	}
	if f.FamilyByLang[0x0411] != "テストフォント" {
		t.Errorf("ja family = %q", f.FamilyByLang[0x0411])
	}
	if f.Weight != 700 {
		t.Errorf("weight = %d", f.Weight)
	}
	if !f.Bold || !f.Italic {
		t.Errorf("bold=%v italic=%v", f.Bold, f.Italic)
	}
	if f.NumGlyphs != 1234 {
		t.Errorf("numGlyphs = %d", f.NumGlyphs)
	}
	if f.PostScript != "TestFont-Regular" {
		t.Errorf("ps = %q", f.PostScript)
	}
	if len(f.FullNames) != 1 || f.FullNames[0] != "Test Font Regular" {
		t.Errorf("full = %v", f.FullNames)
	}
}

// TestMacJapaneseName guards against the regression where a Macintosh-platform
// name record stored in Shift-JIS (encodingID 1) was decoded byte-per-rune as
// Latin-1, turning "FOT-ハミング ProN" into garbage like "FOT-ƒnƒ~ƒ"O ProN".
func TestMacJapaneseName(t *testing.T) {
	const want = "FOT-ハミング ProN"
	sjis, err := japanese.ShiftJIS.NewEncoder().Bytes([]byte(want))
	if err != nil {
		t.Fatalf("encode shift-jis: %v", err)
	}

	os2 := make([]byte, 96)
	binary.BigEndian.PutUint16(os2[4:6], 600)
	maxp := make([]byte, 32)
	binary.BigEndian.PutUint32(maxp[0:4], 0x00010000)
	head := make([]byte, 54)
	name := makeNameTableRaw([]rawRec{
		{platform: 1, enc: 1, lang: 11, nameID: 1, raw: sjis}, // Mac / Japanese / ja
	})
	font := buildSFNT(t, map[string][]byte{"OS/2": os2, "maxp": maxp, "head": head, "name": name})

	faces, err := Parse(font)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	f := faces[0]
	found := false
	for _, fam := range f.Families {
		if fam == want {
			found = true
		}
		if strings.ContainsRune(fam, '�') {
			t.Errorf("family contains replacement char (garbled): %q", fam)
		}
	}
	if !found {
		t.Errorf("decoded families = %v, want to contain %q", f.Families, want)
	}
}

func TestRenameRoundTrip(t *testing.T) {
	renamed, err := RenameFont(sampleFont(t), "ABCD1234", "note")
	if err != nil {
		t.Fatalf("rename: %v", err)
	}
	faces, err := Parse(renamed)
	if err != nil {
		t.Fatalf("reparse: %v", err)
	}
	f := faces[0]
	if f.FamilyByLang[LangEnUS] != "ABCD1234" {
		t.Errorf("renamed family = %q", f.FamilyByLang[LangEnUS])
	}
	if f.PostScript != "ABCD1234" {
		t.Errorf("renamed ps = %q", f.PostScript)
	}
	// Weight/glyph metadata must survive the rename.
	if f.Weight != 700 || f.NumGlyphs != 1234 {
		t.Errorf("metadata lost: weight=%d glyphs=%d", f.Weight, f.NumGlyphs)
	}
}
