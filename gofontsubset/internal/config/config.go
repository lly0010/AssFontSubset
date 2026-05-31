// Package config persists the GUI's settings (font library folders, database
// path and the last used subset options) under the user's config directory.
package config

import (
	"encoding/json"
	"os"
	"path/filepath"
)

// Settings is the persisted application state.
type Settings struct {
	LibraryFolders []string `json:"library_folders"`
	DatabasePath   string   `json:"database_path"`
	OutputFolder   string   `json:"output_folder"`
	FontFolder     string   `json:"font_folder"`
	UseDatabase    bool     `json:"use_database"`
	ConvertOtf     bool     `json:"convert_otf_to_ttf"`
	Debug          bool     `json:"debug"`
	HbSubsetPath   string   `json:"hb_subset_path"`
	PythonPath     string   `json:"python_path"`
}

const appDirName = "gofontsubset"

// Dir returns the application's config directory, creating nothing.
func Dir() string {
	base, err := os.UserConfigDir()
	if err != nil || base == "" {
		base, _ = os.UserHomeDir()
	}
	return filepath.Join(base, appDirName)
}

func settingsPath() string { return filepath.Join(Dir(), "settings.json") }

// DefaultDatabasePath is where the font database lives unless overridden.
func DefaultDatabasePath() string { return filepath.Join(Dir(), "fontdb.json") }

// Load reads the settings, falling back to defaults on any error.
func Load() *Settings {
	s := &Settings{DatabasePath: DefaultDatabasePath()}
	data, err := os.ReadFile(settingsPath())
	if err != nil || len(data) == 0 {
		return s
	}
	if err := json.Unmarshal(data, s); err != nil {
		return &Settings{DatabasePath: DefaultDatabasePath()}
	}
	if s.DatabasePath == "" {
		s.DatabasePath = DefaultDatabasePath()
	}
	return s
}

// Save writes the settings (best effort).
func (s *Settings) Save() error {
	if err := os.MkdirAll(Dir(), 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(settingsPath(), data, 0o644)
}
