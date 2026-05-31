package opentype

import (
	"encoding/binary"
	"errors"
	"sort"
)

// ExtractFace returns a standalone single-face sfnt for face `index`. If data is
// not a collection ("ttcf"), a copy of data is returned unchanged (index must be
// 0). For a collection, the referenced tables are copied out and a fresh offset
// table / table directory is written with recomputed checksums, so the result is
// a valid standalone .ttf/.otf — useful e.g. for loaders that cannot open a .ttc.
func ExtractFace(data []byte, index int) ([]byte, error) {
	if len(data) < 12 {
		return nil, errors.New("font too small")
	}
	if string(data[0:4]) != "ttcf" {
		if index != 0 {
			return nil, errors.New("not a collection: only face 0 exists")
		}
		out := make([]byte, len(data))
		copy(out, data)
		return out, nil
	}

	numFonts := int(binary.BigEndian.Uint32(data[8:12]))
	if index < 0 || index >= numFonts {
		return nil, errors.New("face index out of range")
	}
	off := 12 + index*4
	if off+4 > len(data) {
		return nil, errors.New("truncated collection header")
	}
	base := binary.BigEndian.Uint32(data[off : off+4])
	if int(base)+12 > len(data) {
		return nil, errors.New("invalid offset table")
	}

	sfntVersion := data[base : base+4]
	numTables := int(binary.BigEndian.Uint16(data[base+4 : base+6]))

	type table struct {
		tag  string
		data []byte
	}
	tables := make([]table, 0, numTables)
	rec := base + 12
	for i := 0; i < numTables; i++ {
		if int(rec)+16 > len(data) {
			return nil, errors.New("truncated table directory")
		}
		tag := string(data[rec : rec+4])
		tOff := binary.BigEndian.Uint32(data[rec+8 : rec+12])
		length := binary.BigEndian.Uint32(data[rec+12 : rec+16])
		if int(tOff)+int(length) > len(data) {
			return nil, errors.New("table out of bounds: " + tag)
		}
		body := make([]byte, length)
		copy(body, data[tOff:tOff+length])
		tables = append(tables, table{tag: tag, data: body})
		rec += 16
	}
	if len(tables) == 0 {
		return nil, errors.New("no tables in face")
	}

	sort.Slice(tables, func(i, j int) bool { return tables[i].tag < tables[j].tag })

	n := len(tables)
	entrySelector := 0
	for (1 << (entrySelector + 1)) <= n {
		entrySelector++
	}
	searchRange := (1 << entrySelector) * 16
	rangeShift := n*16 - searchRange

	headerLen := 12 + n*16
	offsets := make([]uint32, n)
	cur := headerLen
	for i := range tables {
		offsets[i] = uint32(cur)
		cur += len(tables[i].data)
		for cur%4 != 0 {
			cur++
		}
	}
	total := cur

	out := make([]byte, total)
	copy(out[0:4], sfntVersion)
	binary.BigEndian.PutUint16(out[4:6], uint16(n))
	binary.BigEndian.PutUint16(out[6:8], uint16(searchRange))
	binary.BigEndian.PutUint16(out[8:10], uint16(entrySelector))
	binary.BigEndian.PutUint16(out[10:12], uint16(rangeShift))

	headDataOffset := -1
	for i, t := range tables {
		r := 12 + i*16
		copy(out[r:r+4], t.tag)
		copy(out[offsets[i]:], t.data)
		binary.BigEndian.PutUint32(out[r+8:r+12], offsets[i])
		binary.BigEndian.PutUint32(out[r+12:r+16], uint32(len(t.data)))
		if t.tag == "head" {
			headDataOffset = int(offsets[i])
		}
	}

	if headDataOffset >= 0 && headDataOffset+12 <= len(out) {
		binary.BigEndian.PutUint32(out[headDataOffset+8:headDataOffset+12], 0)
	}
	for i, t := range tables {
		r := 12 + i*16
		binary.BigEndian.PutUint32(out[r+4:r+8], calcChecksum(out[offsets[i]:offsets[i]+uint32(len(t.data))]))
	}
	if headDataOffset >= 0 {
		adjustment := 0xB1B0AFBA - calcChecksum(out)
		binary.BigEndian.PutUint32(out[headDataOffset+8:headDataOffset+12], adjustment)
	}

	return out, nil
}
