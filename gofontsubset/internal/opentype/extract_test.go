package opentype

import (
	"encoding/binary"
	"testing"
)

// makeCollection packs standalone sfnt faces into a .ttc, rebasing each face's
// table-directory offsets so they are absolute within the collection file.
func makeCollection(faces [][]byte) []byte {
	header := make([]byte, 12+4*len(faces))
	copy(header[0:4], "ttcf")
	binary.BigEndian.PutUint32(header[4:8], 0x00010000)
	binary.BigEndian.PutUint32(header[8:12], uint32(len(faces)))

	out := append([]byte(nil), header...)
	for fi, face := range faces {
		for len(out)%4 != 0 {
			out = append(out, 0)
		}
		base := uint32(len(out))
		binary.BigEndian.PutUint32(out[12+fi*4:12+fi*4+4], base)
		block := append([]byte(nil), face...)
		numTables := int(binary.BigEndian.Uint16(block[4:6]))
		for i := 0; i < numTables; i++ {
			r := 12 + i*16
			off := binary.BigEndian.Uint32(block[r+8 : r+12])
			binary.BigEndian.PutUint32(block[r+8:r+12], off+base)
		}
		out = append(out, block...)
	}
	return out
}

func faceWith(t *testing.T, family string, weight uint16, glyphs uint16) []byte {
	t.Helper()
	os2 := make([]byte, 96)
	binary.BigEndian.PutUint16(os2[4:6], weight)
	maxp := make([]byte, 32)
	binary.BigEndian.PutUint32(maxp[0:4], 0x00010000)
	binary.BigEndian.PutUint16(maxp[4:6], glyphs)
	head := make([]byte, 54)
	name := makeNameTable([]nameRec{{3, 1, 0x0409, 1, family}})
	return buildSFNT(t, map[string][]byte{"OS/2": os2, "maxp": maxp, "head": head, "name": name})
}

func TestExtractFaceFromCollection(t *testing.T) {
	ttc := makeCollection([][]byte{
		faceWith(t, "First Face", 400, 100),
		faceWith(t, "Second Face", 700, 200),
	})

	for idx, want := range map[int]struct {
		family string
		weight int
		glyphs int
	}{
		0: {"First Face", 400, 100},
		1: {"Second Face", 700, 200},
	} {
		single, err := ExtractFace(ttc, idx)
		if err != nil {
			t.Fatalf("extract %d: %v", idx, err)
		}
		if string(single[0:4]) == "ttcf" {
			t.Fatalf("extract %d: result is still a collection", idx)
		}
		faces, err := Parse(single)
		if err != nil {
			t.Fatalf("parse extracted %d: %v", idx, err)
		}
		if len(faces) != 1 {
			t.Fatalf("extract %d: want 1 face, got %d", idx, len(faces))
		}
		f := faces[0]
		if f.FamilyByLang[LangEnUS] != want.family {
			t.Errorf("extract %d: family = %q, want %q", idx, f.FamilyByLang[LangEnUS], want.family)
		}
		if f.Weight != want.weight || f.NumGlyphs != want.glyphs {
			t.Errorf("extract %d: weight=%d glyphs=%d", idx, f.Weight, f.NumGlyphs)
		}
	}

	if _, err := ExtractFace(ttc, 5); err == nil {
		t.Errorf("expected error for out-of-range index")
	}
}

func TestExtractFaceSingleFile(t *testing.T) {
	single := faceWith(t, "Solo", 400, 10)
	out, err := ExtractFace(single, 0)
	if err != nil {
		t.Fatalf("extract: %v", err)
	}
	if faces, err := Parse(out); err != nil || faces[0].FamilyByLang[LangEnUS] != "Solo" {
		t.Fatalf("roundtrip failed: %v", err)
	}
	if _, err := ExtractFace(single, 1); err == nil {
		t.Errorf("expected error for index 1 on single-face file")
	}
}
