// Package fontdb builds and queries a persistent index of fonts found in one or
// more library folders, so the font files don't have to be gathered for every job.
package fontdb

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/lly0010/AssFontSubset/gofontsubset/internal/opentype"
)

// Entry is one indexed font face. The JSON shape intentionally matches the
// requested font-library format, with a few extra fields needed for matching.
type Entry struct {
	Families      []string       `json:"families"`
	FullNames     []string       `json:"fullnames"`
	PsNames       []string       `json:"psnames"`
	Weight        int            `json:"weight"`
	Slant         int            `json:"slant"` // 0 = upright, 1 = italic
	Path          string         `json:"path"`
	Index         int            `json:"index"`
	LastWriteTime string         `json:"last_write_time"`
	Bold          bool           `json:"bold"`
	MaxpNumGlyphs int            `json:"maxp_num_glyphs"`
	FamilyNames   map[int]string `json:"family_names"`
}

// SupportedExtensions are the font file extensions that get indexed.
var SupportedExtensions = []string{".ttf", ".otf", ".ttc", ".otc"}

// IsCFF reports whether the entry's source file uses PostScript/CFF outlines.
func (e Entry) IsCFF() bool {
	return strings.EqualFold(filepath.Ext(e.Path), ".otf") || strings.EqualFold(filepath.Ext(e.Path), ".otc")
}

// PrimaryFamily returns the canonical (en-US) family name for grouping.
func (e Entry) PrimaryFamily() string {
	if n, ok := e.FamilyNames[opentype.LangEnUS]; ok && n != "" {
		return n
	}
	if len(e.Families) > 0 {
		return e.Families[0]
	}
	return ""
}

// matchNames returns the set of names this entry can be referenced by.
func (e Entry) matchNames() map[string]bool {
	m := make(map[string]bool)
	for _, n := range e.Families {
		m[n] = true
	}
	for _, n := range e.FullNames {
		m[n] = true
	}
	for _, n := range e.PsNames {
		m[n] = true
	}
	for _, n := range e.FamilyNames {
		m[n] = true
	}
	return m
}

// DB is the in-memory font index.
type DB struct {
	Entries []Entry `json:"-"`
}

// Load reads a database from disk. A missing file yields an empty database.
func Load(path string) (*DB, error) {
	db := &DB{}
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return db, nil
		}
		return nil, err
	}
	if len(data) == 0 {
		return db, nil
	}
	if err := json.Unmarshal(data, &db.Entries); err != nil {
		return nil, err
	}
	return db, nil
}

// Save writes the database to disk as a JSON array of entries.
func (db *DB) Save(path string) error {
	if dir := filepath.Dir(path); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	data, err := json.MarshalIndent(db.Entries, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0o644)
}

// Count returns the number of indexed faces.
func (db *DB) Count() int { return len(db.Entries) }

// Build scans the given library folders recursively and rebuilds the index.
// logf, if non-nil, receives progress messages.
func (db *DB) Build(folders []string, logf func(string)) {
	log := func(s string) {
		if logf != nil {
			logf(s)
		}
	}

	seen := make(map[string]bool)
	var entries []Entry

	for _, folder := range folders {
		if folder == "" {
			continue
		}
		info, err := os.Stat(folder)
		if err != nil || !info.IsDir() {
			log("skip missing library folder: " + folder)
			continue
		}
		_ = filepath.WalkDir(folder, func(path string, d os.DirEntry, err error) error {
			if err != nil || d.IsDir() {
				return nil
			}
			if !supported(path) || seen[strings.ToLower(path)] {
				return nil
			}
			seen[strings.ToLower(path)] = true

			faces, err := opentype.ParseFile(path)
			if err != nil {
				log("skip " + filepath.Base(path) + ": " + err.Error())
				return nil
			}
			lwt := lastWriteTime(path)
			for _, face := range faces {
				entries = append(entries, faceToEntry(face, path, lwt))
			}
			return nil
		})
	}

	db.Entries = entries
	log("indexed " + strconv.Itoa(len(entries)) + " font faces")
}

// SelectForNames returns the faces required by the given ass font names, expanded
// to whole families so that weight/italic selection still works.
func (db *DB) SelectForNames(requiredNames []string) []Entry {
	required := make(map[string]bool)
	for _, n := range requiredNames {
		required[strings.TrimPrefix(n, "@")] = true
	}

	groups := map[string][]Entry{}
	var order []string
	for _, e := range db.Entries {
		key := e.PrimaryFamily()
		if _, ok := groups[key]; !ok {
			order = append(order, key)
		}
		groups[key] = append(groups[key], e)
	}

	var selected []Entry
	for _, key := range order {
		group := groups[key]
		matched := false
		for _, e := range group {
			for name := range e.matchNames() {
				if required[name] {
					matched = true
					break
				}
			}
			if matched {
				break
			}
		}
		if matched {
			selected = append(selected, group...)
		}
	}
	return selected
}

func faceToEntry(face opentype.Face, path, lwt string) Entry {
	slant := 0
	if face.Italic {
		slant = 1
	}
	ps := []string{}
	if face.PostScript != "" {
		ps = []string{face.PostScript}
	}
	fams := append([]string(nil), face.Families...)
	full := append([]string(nil), face.FullNames...)
	famByLang := map[int]string{}
	for k, v := range face.FamilyByLang {
		famByLang[k] = v
	}
	return Entry{
		Families:      fams,
		FullNames:     full,
		PsNames:       ps,
		Weight:        face.Weight,
		Slant:         slant,
		Path:          path,
		Index:         face.Index,
		LastWriteTime: lwt,
		Bold:          face.Bold,
		MaxpNumGlyphs: face.NumGlyphs,
		FamilyNames:   famByLang,
	}
}

func supported(path string) bool {
	ext := strings.ToLower(filepath.Ext(path))
	for _, e := range SupportedExtensions {
		if ext == e {
			return true
		}
	}
	return false
}

func lastWriteTime(path string) string {
	info, err := os.Stat(path)
	if err != nil {
		return ""
	}
	return "UTC " + info.ModTime().UTC().Format("2006-01-02 15:04:05")
}
