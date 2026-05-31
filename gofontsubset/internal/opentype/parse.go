// Package opentype provides a small reader for the font metadata needed by the
// subsetter: family / full / PostScript names, weight, italic & bold flags,
// glyph count and whether the font uses CFF outlines.
//
// It understands single-face sfnt files (.ttf/.otf) and font collections
// (.ttc/.otc), reading the name, OS/2, head and maxp tables directly. Name
// records are decoded per platform/encoding, including legacy Macintosh CJK
// encodings (Shift-JIS, Big5, EUC-KR, GBK) so multi-language names aren't garbled.
package opentype

import (
	"encoding/binary"
	"errors"
	"os"
	"unicode/utf16"

	"golang.org/x/text/encoding"
	"golang.org/x/text/encoding/charmap"
	"golang.org/x/text/encoding/japanese"
	"golang.org/x/text/encoding/korean"
	"golang.org/x/text/encoding/simplifiedchinese"
	"golang.org/x/text/encoding/traditionalchinese"
)

// LangEnUS is the Windows language id for English (United States). It is used as
// the canonical key for grouping families, mirroring the .NET implementation.
const LangEnUS = 1033

// Face holds the metadata of a single font face.
type Face struct {
	Index        int
	Families     []string       // distinct family names (name IDs 1 and 16), all languages
	FullNames    []string       // name ID 4
	PostScript   string         // name ID 6
	FamilyByLang map[int]string // name ID 1 keyed by Windows language id
	Weight       int            // OS/2 usWeightClass
	Italic       bool
	Bold         bool
	NumGlyphs    int
	IsCFF        bool
}

// ParseFile reads every face contained in the given font file.
func ParseFile(path string) ([]Face, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return Parse(data)
}

// Parse reads every face from an in-memory font file.
func Parse(data []byte) ([]Face, error) {
	if len(data) < 12 {
		return nil, errors.New("font file too small")
	}

	tag := string(data[0:4])
	if tag == "ttcf" {
		numFonts := binary.BigEndian.Uint32(data[8:12])
		faces := make([]Face, 0, numFonts)
		for i := 0; i < int(numFonts); i++ {
			off := 12 + i*4
			if off+4 > len(data) {
				break
			}
			base := binary.BigEndian.Uint32(data[off : off+4])
			face, err := parseFace(data, base, i)
			if err != nil {
				continue
			}
			faces = append(faces, face)
		}
		if len(faces) == 0 {
			return nil, errors.New("no readable faces in collection")
		}
		return faces, nil
	}

	face, err := parseFace(data, 0, 0)
	if err != nil {
		return nil, err
	}
	return []Face{face}, nil
}

type tableRec struct {
	offset uint32
	length uint32
}

func parseFace(data []byte, base uint32, index int) (Face, error) {
	var f Face
	f.Index = index
	f.Weight = 400
	f.FamilyByLang = map[int]string{}

	if int(base)+12 > len(data) {
		return f, errors.New("invalid offset table")
	}
	sfntVersion := string(data[base : base+4])
	numTables := int(binary.BigEndian.Uint16(data[base+4 : base+6]))

	tables := make(map[string]tableRec, numTables)
	rec := base + 12
	for i := 0; i < numTables; i++ {
		if int(rec)+16 > len(data) {
			break
		}
		tag := string(data[rec : rec+4])
		off := binary.BigEndian.Uint32(data[rec+8 : rec+12])
		length := binary.BigEndian.Uint32(data[rec+12 : rec+16])
		tables[tag] = tableRec{offset: off, length: length}
		rec += 16
	}

	_, hasCFF := tables["CFF "]
	f.IsCFF = sfntVersion == "OTTO" || hasCFF

	if t, ok := tables["maxp"]; ok && int(t.offset)+6 <= len(data) {
		f.NumGlyphs = int(binary.BigEndian.Uint16(data[t.offset+4 : t.offset+6]))
	}

	if t, ok := tables["OS/2"]; ok && int(t.offset)+64 <= len(data) {
		f.Weight = int(binary.BigEndian.Uint16(data[t.offset+4 : t.offset+6]))
		fsSel := binary.BigEndian.Uint16(data[t.offset+62 : t.offset+64])
		f.Italic = fsSel&0x01 != 0
		f.Bold = fsSel&0x20 != 0
	} else if t, ok := tables["head"]; ok && int(t.offset)+46 <= len(data) {
		macStyle := binary.BigEndian.Uint16(data[t.offset+44 : t.offset+46])
		f.Bold = macStyle&0x01 != 0
		f.Italic = macStyle&0x02 != 0
	}

	if t, ok := tables["name"]; ok {
		parseName(data, t, &f)
	}

	if len(f.Families) == 0 {
		return f, errors.New("no family name")
	}
	if _, ok := f.FamilyByLang[LangEnUS]; !ok {
		// Fall back to any available family for the canonical key.
		for _, v := range f.FamilyByLang {
			f.FamilyByLang[LangEnUS] = v
			break
		}
		if _, ok := f.FamilyByLang[LangEnUS]; !ok {
			f.FamilyByLang[LangEnUS] = f.Families[0]
		}
	}
	return f, nil
}

func parseName(data []byte, t tableRec, f *Face) {
	no := t.offset
	if int(no)+6 > len(data) {
		return
	}
	count := int(binary.BigEndian.Uint16(data[no+2 : no+4]))
	storage := no + uint32(binary.BigEndian.Uint16(data[no+4:no+6]))

	familySeen := map[string]bool{}
	fullSeen := map[string]bool{}

	addFamily := func(s string) {
		if s != "" && !familySeen[s] {
			familySeen[s] = true
			f.Families = append(f.Families, s)
		}
	}

	recBase := no + 6
	for i := 0; i < count; i++ {
		r := recBase + uint32(i*12)
		if int(r)+12 > len(data) {
			break
		}
		platformID := binary.BigEndian.Uint16(data[r : r+2])
		encodingID := binary.BigEndian.Uint16(data[r+2 : r+4])
		langID := int(binary.BigEndian.Uint16(data[r+4 : r+6]))
		nameID := binary.BigEndian.Uint16(data[r+6 : r+8])
		length := uint32(binary.BigEndian.Uint16(data[r+8 : r+10]))
		strOff := storage + uint32(binary.BigEndian.Uint16(data[r+10:r+12]))
		if int(strOff)+int(length) > len(data) {
			continue
		}
		raw := data[strOff : strOff+length]
		s := decodeName(platformID, encodingID, raw)
		if s == "" {
			continue
		}

		// Normalize the language key: Windows uses LCIDs; map Unicode/Mac
		// English records onto the canonical en-US key.
		lang := langID
		if platformID == 0 || (platformID == 1 && langID == 0) {
			lang = LangEnUS
		}

		switch nameID {
		case 1, 16: // legacy family + typographic family
			addFamily(s)
			if nameID == 1 {
				if _, exists := f.FamilyByLang[lang]; !exists || platformID == 3 {
					f.FamilyByLang[lang] = s
				}
			}
		case 4: // full name
			if !fullSeen[s] {
				fullSeen[s] = true
				f.FullNames = append(f.FullNames, s)
			}
		case 6: // PostScript name
			if f.PostScript == "" || platformID == 3 {
				f.PostScript = s
			}
		}
	}
}

func decodeName(platformID, encodingID uint16, raw []byte) string {
	switch platformID {
	case 3, 0: // Windows / Unicode: UTF-16BE (always, regardless of encodingID)
		if len(raw)%2 != 0 {
			raw = raw[:len(raw)-1]
		}
		u16 := make([]uint16, len(raw)/2)
		for i := range u16 {
			u16[i] = binary.BigEndian.Uint16(raw[i*2 : i*2+2])
		}
		return string(utf16.Decode(u16))
	case 1: // Macintosh: encodingID selects a legacy codec
		if enc := macEncoding(encodingID); enc != nil {
			if out, err := enc.NewDecoder().Bytes(raw); err == nil {
				return string(out)
			}
		}
		return decodeLatin1(raw)
	default: // ISO / unknown: best-effort Latin-1
		return decodeLatin1(raw)
	}
}

// macEncoding maps a Macintosh name-record encoding id to a text codec. Only the
// common scripts are handled; others fall back to Mac Roman.
func macEncoding(encodingID uint16) encoding.Encoding {
	switch encodingID {
	case 0: // Roman
		return charmap.Macintosh
	case 1: // Japanese (MacJapanese ≈ Shift-JIS)
		return japanese.ShiftJIS
	case 2: // Traditional Chinese
		return traditionalchinese.Big5
	case 3: // Korean
		return korean.EUCKR
	case 25: // Simplified Chinese
		return simplifiedchinese.GBK
	default:
		return charmap.Macintosh
	}
}

func decodeLatin1(raw []byte) string {
	runes := make([]rune, len(raw))
	for i, b := range raw {
		runes[i] = rune(b)
	}
	return string(runes)
}
