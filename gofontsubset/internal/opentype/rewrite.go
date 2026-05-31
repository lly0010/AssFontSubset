package opentype

import (
	"encoding/binary"
	"errors"
	"sort"
	"unicode/utf16"
)

// RenameFont rewrites the `name` table of a single-face sfnt so that the family,
// full and PostScript names all become newName. This is needed because hb-subset
// cannot assign a custom font name itself; ASS subsetting requires a unique name
// per output font. The table directory and checksums are rebuilt.
func RenameFont(data []byte, newName, note string) ([]byte, error) {
	if len(data) < 12 {
		return nil, errors.New("font too small")
	}
	if string(data[0:4]) == "ttcf" {
		return nil, errors.New("cannot rename a font collection")
	}

	sfntVersion := data[0:4]
	numTables := int(binary.BigEndian.Uint16(data[4:6]))

	type table struct {
		tag  string
		data []byte
	}
	tables := make([]table, 0, numTables+1)
	hasName := false

	for i := 0; i < numTables; i++ {
		r := 12 + i*16 // offset table is 12 bytes; records follow
		if r+16 > len(data) {
			return nil, errors.New("truncated table directory")
		}
		tag := string(data[r : r+4])
		off := binary.BigEndian.Uint32(data[r+8 : r+12])
		length := binary.BigEndian.Uint32(data[r+12 : r+16])
		if int(off)+int(length) > len(data) {
			return nil, errors.New("table out of bounds: " + tag)
		}
		body := make([]byte, length)
		copy(body, data[off:off+length])
		if tag == "name" {
			body = buildNameTable(newName, note)
			hasName = true
		}
		tables = append(tables, table{tag: tag, data: body})
	}
	if !hasName {
		tables = append(tables, table{tag: "name", data: buildNameTable(newName, note)})
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
	// Compute offsets, padding each table to 4 bytes.
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

	// head.checkSumAdjustment must be zero while computing checksums.
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

func buildNameTable(newName, note string) []byte {
	type entry struct {
		nameID uint16
		value  string
	}
	entries := []entry{
		{0, note},
		{1, newName},
		{2, "Regular"},
		{3, newName},
		{4, newName},
		{6, newName},
	}

	var storage []byte
	header := make([]byte, 6+len(entries)*12)
	binary.BigEndian.PutUint16(header[0:2], 0) // format 0
	binary.BigEndian.PutUint16(header[2:4], uint16(len(entries)))
	binary.BigEndian.PutUint16(header[4:6], uint16(6+len(entries)*12)) // string storage offset

	for i, e := range entries {
		enc := encodeUTF16BE(e.value)
		r := 6 + i*12
		binary.BigEndian.PutUint16(header[r:r+2], 3)          // platformID Windows
		binary.BigEndian.PutUint16(header[r+2:r+4], 1)        // encoding UCS-2
		binary.BigEndian.PutUint16(header[r+4:r+6], 0x0409)   // language en-US
		binary.BigEndian.PutUint16(header[r+6:r+8], e.nameID) // name id
		binary.BigEndian.PutUint16(header[r+8:r+10], uint16(len(enc)))
		binary.BigEndian.PutUint16(header[r+10:r+12], uint16(len(storage)))
		storage = append(storage, enc...)
	}

	return append(header, storage...)
}

func encodeUTF16BE(s string) []byte {
	u16 := utf16.Encode([]rune(s))
	b := make([]byte, len(u16)*2)
	for i, v := range u16 {
		binary.BigEndian.PutUint16(b[i*2:i*2+2], v)
	}
	return b
}

func calcChecksum(b []byte) uint32 {
	var sum uint32
	i := 0
	for ; i+4 <= len(b); i += 4 {
		sum += binary.BigEndian.Uint32(b[i : i+4])
	}
	if rem := len(b) - i; rem > 0 {
		var last [4]byte
		copy(last[:], b[i:])
		sum += binary.BigEndian.Uint32(last[:])
	}
	return sum
}
