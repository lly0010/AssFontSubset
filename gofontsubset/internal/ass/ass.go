// Package ass parses ASS subtitle files to collect the fonts and characters they
// use, and rewrites the font names after subsetting. It is a pragmatic
// implementation covering styles, \fn / \b / \i / \r / \p override tags and
// vertical (@) fonts; it is not a full ASS engine.
package ass

import (
	"os"
	"sort"
	"strconv"
	"strings"
)

var bom = []byte{0xEF, 0xBB, 0xBF}

// Style is a subtitle style's font-relevant fields.
type Style struct {
	Name     string
	Font     string // without a leading '@'
	Bold     bool
	Italic   bool
	Vertical bool // the style's Fontname had a leading '@'
}

// FontKey identifies a distinct font request (family name + bold + italic).
type FontKey struct {
	Name   string
	Bold   bool
	Italic bool
}

// Usage records the characters seen for a font request and whether it was ever
// used vertically.
type Usage struct {
	Runes    map[rune]bool
	Vertical bool
}

// Doc is a parsed subtitle document with its original lines preserved.
type Doc struct {
	Path     string
	hadBOM   bool
	sep      string
	lines    []string
	Styles   map[string]Style
	styleIdx struct{ name, font, bold, italic int }
	eventIdx struct{ style, text, count int }
}

type state struct {
	name     string
	bold     bool
	italic   bool
	vertical bool
	drawing  int
}

// Parse reads and parses an ASS file.
func Parse(path string) (*Doc, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	d := &Doc{Path: path, Styles: map[string]Style{}}
	if strings.HasPrefix(string(raw), string(bom)) {
		d.hadBOM = true
		raw = raw[len(bom):]
	}
	text := string(raw)
	if strings.Contains(text, "\r\n") {
		d.sep = "\r\n"
	} else {
		d.sep = "\n"
	}
	d.lines = strings.Split(strings.ReplaceAll(text, "\r\n", "\n"), "\n")

	// Sensible defaults for the standard V4+ layout.
	d.styleIdx.name, d.styleIdx.font, d.styleIdx.bold, d.styleIdx.italic = 0, 1, 7, 8
	d.eventIdx.style, d.eventIdx.text, d.eventIdx.count = 3, 9, 10

	section := ""
	for _, line := range d.lines {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			section = strings.ToLower(trimmed)
			continue
		}
		switch {
		case strings.HasPrefix(trimmed, "Format:"):
			fields := splitTrim(strings.TrimPrefix(trimmed, "Format:"))
			if strings.Contains(section, "styles") {
				d.styleIdx.name = indexOf(fields, "Name")
				d.styleIdx.font = indexOf(fields, "Fontname")
				d.styleIdx.bold = indexOf(fields, "Bold")
				d.styleIdx.italic = indexOf(fields, "Italic")
			} else if strings.Contains(section, "events") {
				d.eventIdx.style = indexOf(fields, "Style")
				d.eventIdx.text = indexOf(fields, "Text")
				d.eventIdx.count = len(fields)
			}
		case strings.HasPrefix(trimmed, "Style:") && strings.Contains(section, "styles"):
			fields := splitTrim(strings.TrimPrefix(trimmed, "Style:"))
			s := Style{}
			s.Name = at(fields, d.styleIdx.name)
			rawFont := at(fields, d.styleIdx.font)
			s.Vertical = strings.HasPrefix(rawFont, "@")
			s.Font = strings.TrimPrefix(rawFont, "@")
			s.Bold = parseBoolFlag(at(fields, d.styleIdx.bold))
			s.Italic = parseBoolFlag(at(fields, d.styleIdx.italic))
			if s.Name != "" {
				d.Styles[s.Name] = s
			}
		}
	}
	return d, nil
}

// Collect accumulates font usage from this document into the shared map.
func (d *Doc) Collect(col map[FontKey]*Usage) {
	section := ""
	for _, line := range d.lines {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			section = strings.ToLower(trimmed)
			continue
		}
		if !strings.Contains(section, "events") || !strings.HasPrefix(trimmed, "Dialogue:") {
			continue
		}
		fields := strings.SplitN(strings.TrimPrefix(line, "Dialogue:"), ",", d.eventIdx.count)
		for i := range fields {
			fields[i] = strings.TrimSpace(fields[i])
		}
		if d.eventIdx.text >= len(fields) {
			continue
		}
		styleName := strings.TrimPrefix(at(fields, d.eventIdx.style), "*")
		base := d.Styles[styleName]
		d.collectText(fields[d.eventIdx.text], base, col)
	}
}

func (d *Doc) collectText(text string, base Style, col map[FontKey]*Usage) {
	cur := state{name: base.Font, bold: base.Bold, italic: base.Italic, vertical: base.Vertical}
	runes := []rune(text)
	for i := 0; i < len(runes); i++ {
		r := runes[i]
		switch {
		case r == '{':
			end := indexRune(runes, i+1, '}')
			if end < 0 {
				// Unterminated block: treat the rest as literal text.
				for _, rr := range runes[i+1:] {
					d.add(col, cur, rr)
				}
				return
			}
			d.applyTags(string(runes[i+1:end]), base, &cur)
			i = end
		case r == '\\' && i+1 < len(runes) && (runes[i+1] == 'n' || runes[i+1] == 'N' || runes[i+1] == 'h'):
			i++ // escape sequence, not a glyph
		default:
			d.add(col, cur, r)
		}
	}
}

func (d *Doc) add(col map[FontKey]*Usage, cur state, r rune) {
	if cur.drawing != 0 || cur.name == "" {
		return
	}
	if r == '\n' || r == '\r' {
		return
	}
	key := FontKey{Name: cur.name, Bold: cur.bold, Italic: cur.italic}
	u := col[key]
	if u == nil {
		u = &Usage{Runes: map[rune]bool{}}
		col[key] = u
	}
	u.Runes[r] = true
	if cur.vertical {
		u.Vertical = true
	}
}

func (d *Doc) applyTags(block string, base Style, cur *state) {
	for _, tok := range strings.Split(block, "\\") {
		if tok == "" {
			continue
		}
		switch {
		case strings.HasPrefix(tok, "fn"):
			name := tok[2:]
			if name == "" {
				cur.name, cur.vertical = base.Font, false
			} else if strings.HasPrefix(name, "@") {
				cur.name, cur.vertical = name[1:], true
			} else {
				cur.name, cur.vertical = name, false
			}
		case tok[0] == 'r':
			styleName := tok[1:]
			s := base
			if styleName != "" {
				if st, ok := lookupStyle(d, styleName); ok {
					s = st
				}
			}
			cur.name, cur.bold, cur.italic, cur.vertical = s.Font, s.Bold, s.Italic, s.Vertical
		case tok[0] == 'b' && allDigits(tok[1:]):
			v, _ := strconv.Atoi(tok[1:])
			cur.bold = v == 1 || v >= 550
		case tok[0] == 'i' && allDigits(tok[1:]):
			cur.italic = tok[1:] == "1"
		case tok[0] == 'p' && allDigits(tok[1:]):
			cur.drawing, _ = strconv.Atoi(tok[1:])
		}
	}
}

// Rewrite writes a copy of the document to outPath with font names replaced
// according to nameMap (original ass font name -> new subset name).
func (d *Doc) Rewrite(nameMap map[string]string, outPath string) error {
	// Replace longest names first to avoid partial-prefix collisions.
	keys := make([]string, 0, len(nameMap))
	for k := range nameMap {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { return len(keys[i]) > len(keys[j]) })

	out := make([]string, 0, len(d.lines)+len(nameMap)+1)
	section := ""
	insertedComments := false
	for _, line := range d.lines {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			section = strings.ToLower(trimmed)
			out = append(out, line)
			if section == "[script info]" && !insertedComments {
				for _, k := range keys {
					out = append(out, "; Font Subset: "+nameMap[k]+" - "+k)
				}
				insertedComments = true
			}
			continue
		}

		switch {
		case strings.HasPrefix(trimmed, "Style:") && strings.Contains(section, "styles"):
			out = append(out, d.rewriteStyleLine(line, nameMap))
		case strings.HasPrefix(trimmed, "Dialogue:") && strings.Contains(section, "events"):
			out = append(out, d.rewriteDialogueLine(line, keys, nameMap))
		default:
			out = append(out, line)
		}
	}

	var sb strings.Builder
	if d.hadBOM {
		sb.Write(bom)
	}
	sb.WriteString(strings.Join(out, d.sep))
	return os.WriteFile(outPath, []byte(sb.String()), 0o644)
}

func (d *Doc) rewriteStyleLine(line string, nameMap map[string]string) string {
	prefix := line[:strings.Index(line, ":")+1]
	fields := strings.Split(line[len(prefix):], ",")
	fi := d.styleIdx.font
	// Account for leading whitespace in the first field count.
	if fi >= 0 && fi < len(fields) {
		raw := fields[fi]
		lead := raw[:len(raw)-len(strings.TrimLeft(raw, " "))]
		val := strings.TrimSpace(raw)
		vertical := strings.HasPrefix(val, "@")
		bare := strings.TrimPrefix(val, "@")
		if nn, ok := nameMap[bare]; ok {
			if vertical {
				nn = "@" + nn
			}
			fields[fi] = lead + nn
		}
	}
	return prefix + strings.Join(fields, ",")
}

func (d *Doc) rewriteDialogueLine(line string, keys []string, nameMap map[string]string) string {
	prefix := line[:strings.Index(line, ":")+1]
	fields := strings.SplitN(line[len(prefix):], ",", d.eventIdx.count)
	ti := d.eventIdx.text
	if ti >= 0 && ti < len(fields) {
		fields[ti] = replaceFnTags(fields[ti], nameMap)
	}
	return prefix + strings.Join(fields, ",")
}

// replaceFnTags rewrites \fn override values found in an event's text.
func replaceFnTags(text string, nameMap map[string]string) string {
	const tag = `\fn`
	var sb strings.Builder
	for {
		idx := strings.Index(text, tag)
		if idx < 0 {
			sb.WriteString(text)
			break
		}
		sb.WriteString(text[:idx+len(tag)])
		text = text[idx+len(tag):]
		end := strings.IndexAny(text, `\}`)
		var val string
		if end < 0 {
			val = text
		} else {
			val = text[:end]
		}
		vertical := strings.HasPrefix(val, "@")
		bare := strings.TrimPrefix(val, "@")
		if nn, ok := nameMap[bare]; ok {
			if vertical {
				sb.WriteString("@")
			}
			sb.WriteString(nn)
		} else {
			sb.WriteString(val)
		}
		if end < 0 {
			break
		}
		text = text[end:]
	}
	return sb.String()
}

func lookupStyle(d *Doc, name string) (Style, bool) {
	s, ok := d.Styles[name]
	return s, ok
}

func splitTrim(s string) []string {
	parts := strings.Split(s, ",")
	for i := range parts {
		parts[i] = strings.TrimSpace(parts[i])
	}
	return parts
}

func indexOf(fields []string, name string) int {
	for i, f := range fields {
		if strings.EqualFold(f, name) {
			return i
		}
	}
	return -1
}

func at(fields []string, i int) string {
	if i >= 0 && i < len(fields) {
		return strings.TrimSpace(fields[i])
	}
	return ""
}

func parseBoolFlag(s string) bool {
	s = strings.TrimSpace(s)
	return s == "1" || s == "-1"
}

func allDigits(s string) bool {
	if s == "" {
		return false
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

func indexRune(runes []rune, from int, target rune) int {
	for i := from; i < len(runes); i++ {
		if runes[i] == target {
			return i
		}
	}
	return -1
}
