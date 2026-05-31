// Package subset orchestrates ASS font subsetting: it matches the fonts used by
// the subtitles to available font faces, runs hb-subset for each one, renames the
// output to a unique name, and rewrites the ASS files accordingly.
package subset

import (
	"fmt"
	"math/rand"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"

	"github.com/lly0010/AssFontSubset/gofontsubset/internal/ass"
	"github.com/lly0010/AssFontSubset/gofontsubset/internal/fontdb"
	"github.com/lly0010/AssFontSubset/gofontsubset/internal/opentype"
)

// Candidate is an available font face that ass fonts can be matched against.
type Candidate struct {
	Path       string
	Index      int
	Weight     int
	Bold       bool
	Italic     bool
	NumGlyphs  int
	IsCFF      bool
	Family     string // canonical (en-US) family name
	matchNames map[string]bool
}

// Config controls a subsetting run.
type Config struct {
	HbSubset   string // path to hb-subset (defaults to "hb-subset" on PATH)
	OutputDir  string
	Debug      bool
	ConvertOtf bool
	Python     string
	Log        func(string)
}

func (c Config) logf(format string, a ...any) {
	if c.Log != nil {
		c.Log(fmt.Sprintf(format, a...))
	}
}

// CandidatesFromFolder reads every font face from a folder (non-recursive).
func CandidatesFromFolder(dir string, logf func(string)) ([]Candidate, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	var cands []Candidate
	for _, e := range entries {
		if e.IsDir() || !isFontFile(e.Name()) {
			continue
		}
		path := filepath.Join(dir, e.Name())
		faces, err := opentype.ParseFile(path)
		if err != nil {
			if logf != nil {
				logf("skip " + e.Name() + ": " + err.Error())
			}
			continue
		}
		for _, f := range faces {
			cands = append(cands, candidateFromFace(f, path))
		}
	}
	return cands, nil
}

// CandidatesFromEntries converts selected database entries into candidates.
func CandidatesFromEntries(entries []fontdb.Entry) []Candidate {
	cands := make([]Candidate, 0, len(entries))
	for _, e := range entries {
		m := map[string]bool{}
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
		cands = append(cands, Candidate{
			Path:       e.Path,
			Index:      e.Index,
			Weight:     e.Weight,
			Bold:       e.Bold,
			Italic:     e.Slant != 0,
			NumGlyphs:  e.MaxpNumGlyphs,
			IsCFF:      e.IsCFF(),
			Family:     e.PrimaryFamily(),
			matchNames: m,
		})
	}
	return cands
}

func candidateFromFace(f opentype.Face, path string) Candidate {
	m := map[string]bool{}
	for _, n := range f.Families {
		m[n] = true
	}
	for _, n := range f.FullNames {
		m[n] = true
	}
	if f.PostScript != "" {
		m[f.PostScript] = true
	}
	for _, n := range f.FamilyByLang {
		m[n] = true
	}
	return Candidate{
		Path:       path,
		Index:      f.Index,
		Weight:     f.Weight,
		Bold:       f.Bold,
		Italic:     f.Italic,
		NumGlyphs:  f.NumGlyphs,
		IsCFF:      f.IsCFF,
		Family:     f.FamilyByLang[opentype.LangEnUS],
		matchNames: m,
	}
}

// Run subsets the fonts used by the given ass files and writes the results
// (subset fonts + rewritten ass files) to cfg.OutputDir.
func Run(assFiles []string, candidates []Candidate, cfg Config) error {
	if len(assFiles) == 0 {
		return fmt.Errorf("no ass files provided")
	}
	if len(candidates) == 0 {
		return fmt.Errorf("no fonts available")
	}

	// Parse and collect font usage from every subtitle.
	col := map[ass.FontKey]*ass.Usage{}
	docs := make([]*ass.Doc, 0, len(assFiles))
	for _, f := range assFiles {
		doc, err := ass.Parse(f)
		if err != nil {
			return fmt.Errorf("parse %s: %w", filepath.Base(f), err)
		}
		doc.Collect(col)
		docs = append(docs, doc)
	}
	if len(col) == 0 {
		return fmt.Errorf("no fonts referenced by the subtitles")
	}

	// Match each requested font to a candidate face.
	chosen := map[ass.FontKey]int{} // FontKey -> candidate index
	var unmatched []string
	for key := range col {
		idx, ok := matchKey(key, candidates)
		if !ok {
			unmatched = append(unmatched, key.Name)
			continue
		}
		chosen[key] = idx
	}
	if len(chosen) == 0 {
		return fmt.Errorf("no fonts matched. Missing: %s", strings.Join(dedupe(unmatched), ", "))
	}
	for _, u := range dedupe(unmatched) {
		cfg.logf("warning: no font matched for %q", u)
	}

	// Assign one new name per matched family (faces of a family share the name,
	// keeping their own weight/italic so \b and \i still select correctly).
	familyNew := map[string]string{}
	for key := range chosen {
		fam := candidates[chosen[key]].Family
		familyNew[fam] = ""
	}
	assignNames(familyNew)

	// Build per-candidate rune sets.
	type job struct {
		cand     Candidate
		runes    map[rune]bool
		vertical bool
		newName  string
	}
	jobs := map[int]*job{}
	for key, idx := range chosen {
		j := jobs[idx]
		if j == nil {
			j = &job{cand: candidates[idx], runes: map[rune]bool{}, newName: familyNew[candidates[idx].Family]}
			jobs[idx] = j
		}
		u := col[key]
		for r := range u.Runes {
			j.runes[r] = true
		}
		if u.Vertical {
			j.vertical = true
		}
	}

	// Prepare a clean output directory.
	if err := os.RemoveAll(cfg.OutputDir); err != nil {
		return err
	}
	if err := os.MkdirAll(cfg.OutputDir, 0o755); err != nil {
		return err
	}
	workDir := filepath.Join(cfg.OutputDir, "_work_")

	// Subset each matched face.
	for _, j := range jobs {
		if err := cfg.subsetOne(j.cand, j.runes, j.vertical, j.newName, workDir); err != nil {
			return err
		}
	}

	// Rewrite the ass files into the output directory.
	nameMap := map[string]string{}
	for key, idx := range chosen {
		nameMap[key.Name] = familyNew[candidates[idx].Family]
	}
	for _, doc := range docs {
		out := filepath.Join(cfg.OutputDir, filepath.Base(doc.Path))
		if err := doc.Rewrite(nameMap, out); err != nil {
			return err
		}
	}

	if !cfg.Debug {
		_ = os.RemoveAll(workDir)
	}
	cfg.logf("done: %d font(s) subset, %d subtitle(s) rewritten", len(jobs), len(docs))
	return nil
}

func (cfg Config) subsetOne(cand Candidate, runeSet map[rune]bool, vertical bool, newName, workDir string) error {
	if vertical {
		for r := range runeSet {
			if v, ok := vertMapping[r]; ok {
				runeSet[v] = true
			}
		}
	}
	appendNecessaryRunes(runeSet)

	inputPath := cand.Path
	faceIndex := cand.Index
	ext := ".ttf"
	if cand.IsCFF {
		ext = ".otf"
	}

	// Convert to a standalone .ttf when requested for CFF/OTF outlines or for any
	// collection face (.ttc/.otc), flattening it into its own TrueType file.
	if cfg.ConvertOtf && (cand.IsCFF || isCollectionFile(cand.Path)) {
		ttf, err := convertToTtf(cand.Path, cand.Index, workDir, cfg.Python, cfg.Log)
		if err != nil {
			return err
		}
		inputPath, faceIndex, ext = ttf, 0, ".ttf"
	}

	if err := os.MkdirAll(workDir, 0o755); err != nil {
		return err
	}

	base := strings.TrimSuffix(filepath.Base(cand.Path), filepath.Ext(cand.Path))
	charsFile := filepath.Join(workDir, fmt.Sprintf("%s.%d.%s.txt", base, cand.Index, newName))
	if err := writeRunes(charsFile, runeSet); err != nil {
		return err
	}

	hbOut := filepath.Join(workDir, fmt.Sprintf("%s.%d.%s.subset%s", base, cand.Index, newName, ext))
	hb := cfg.HbSubset
	if strings.TrimSpace(hb) == "" {
		hb = "hb-subset"
	}
	args := []string{
		inputPath,
		"--text-file=" + charsFile,
		"--output-file=" + hbOut,
		fmt.Sprintf("--face-index=%d", faceIndex),
		"--layout-features=" + strings.Join(layoutFeatures, ","),
		"--name-languages=*",
	}
	cfg.logf("subset %s -> %s", filepath.Base(cand.Path), newName)
	cmd := exec.Command(hb, args...)
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("hb-subset failed for %s: %v: %s", filepath.Base(cand.Path), err, strings.TrimSpace(string(out)))
	}

	// hb-subset cannot rename the font; rename the name table ourselves.
	data, err := os.ReadFile(hbOut)
	if err != nil {
		return err
	}
	renamed, err := opentype.RenameFont(data, newName, "Processed by gofontsubset; harfbuzz-subset")
	if err != nil {
		return fmt.Errorf("rename %s: %w", filepath.Base(cand.Path), err)
	}
	finalPath := filepath.Join(cfg.OutputDir, fmt.Sprintf("%s.%d.%s%s", base, cand.Index, newName, ext))
	return os.WriteFile(finalPath, renamed, 0o644)
}

// matchKey finds the best candidate for a font request.
func matchKey(key ass.FontKey, candidates []Candidate) (int, bool) {
	var idxs []int
	for i, c := range candidates {
		if c.matchNames[key.Name] {
			idxs = append(idxs, i)
		}
	}
	if len(idxs) == 0 {
		return 0, false
	}
	target := 400
	if key.Bold {
		target = 700
	}
	sort.SliceStable(idxs, func(a, b int) bool {
		ca, cb := candidates[idxs[a]], candidates[idxs[b]]
		ia, ib := boolMismatch(ca.Italic, key.Italic), boolMismatch(cb.Italic, key.Italic)
		if ia != ib {
			return ia < ib
		}
		return abs(ca.Weight-target) < abs(cb.Weight-target)
	})
	return idxs[0], true
}

func assignNames(familyNew map[string]string) {
	used := map[string]bool{}
	for fam := range familyNew {
		var name string
		for {
			name = randomName(8)
			if !used[name] {
				break
			}
		}
		used[name] = true
		familyNew[fam] = name
	}
}

const nameChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"

func randomName(n int) string {
	b := make([]byte, n)
	for i := range b {
		b[i] = nameChars[rand.Intn(len(nameChars))]
	}
	return string(b)
}

func writeRunes(path string, set map[rune]bool) error {
	runes := make([]rune, 0, len(set))
	for r := range set {
		runes = append(runes, r)
	}
	sort.Slice(runes, func(i, j int) bool { return runes[i] < runes[j] })
	return os.WriteFile(path, []byte(string(runes)), 0o644)
}

// isCollectionFile reports whether path is a TrueType/OpenType collection
// (.ttc/.otc), detected by the "ttcf" magic at the start of the file.
func isCollectionFile(path string) bool {
	f, err := os.Open(path)
	if err != nil {
		return false
	}
	defer f.Close()
	var magic [4]byte
	if _, err := f.Read(magic[:]); err != nil {
		return false
	}
	return string(magic[:]) == "ttcf"
}

func isFontFile(name string) bool {
	ext := strings.ToLower(filepath.Ext(name))
	for _, e := range fontdb.SupportedExtensions {
		if ext == e {
			return true
		}
	}
	return false
}

func dedupe(in []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, s := range in {
		if !seen[s] {
			seen[s] = true
			out = append(out, s)
		}
	}
	return out
}

func boolMismatch(a, b bool) int {
	if a == b {
		return 0
	}
	return 1
}

func abs(x int) int {
	if x < 0 {
		return -x
	}
	return x
}
