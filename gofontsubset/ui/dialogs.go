package ui

import (
	"errors"
	"os"
	"path/filepath"
	"strings"

	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/dialog"
	"fyne.io/fyne/v2/storage"
	"fyne.io/fyne/v2/widget"
)

// errUseFallback is returned by the platform dialog helpers when no native
// dialog is available, so the caller falls back to Fyne's built-in dialogs.
var errUseFallback = errors.New("native dialog unavailable")

// errCanceled is returned when the user dismisses a native dialog.
var errCanceled = errors.New("dialog canceled")

// pickFolder lets the user choose a directory and writes it into target. It uses
// the native OS picker (the official Microsoft dialog on Windows) and falls back
// to Fyne's dialog elsewhere.
func (u *ui) pickFolder(target *widget.Entry) {
	go func() {
		path, err := nativeSelectFolder("选择目录 Select Folder")
		switch {
		case errors.Is(err, errCanceled):
			return
		case errors.Is(err, errUseFallback):
			fyne.Do(func() {
				dialog.ShowFolderOpen(func(uri fyne.ListableURI, e error) {
					if e != nil || uri == nil {
						return
					}
					target.SetText(uri.Path())
					u.persist()
				}, u.win)
			})
			return
		case err != nil:
			fyne.Do(func() { dialog.ShowError(err, u.win) })
			return
		}
		if path == "" {
			return
		}
		fyne.Do(func() {
			target.SetText(path)
			u.persist()
		})
	}()
}

// openAssFiles lets the user pick one or more .ass files to add.
func (u *ui) openAssFiles() {
	go func() {
		paths, err := nativeOpenFiles("选择字幕 Select Subtitles", []string{".ass"})
		switch {
		case errors.Is(err, errCanceled):
			return
		case errors.Is(err, errUseFallback):
			fyne.Do(func() {
				d := dialog.NewFileOpen(func(rc fyne.URIReadCloser, e error) {
					if e != nil || rc == nil {
						return
					}
					p := rc.URI().Path()
					_ = rc.Close()
					u.addAssFiles([]string{p})
				}, u.win)
				d.SetFilter(storage.NewExtensionFileFilter([]string{".ass"}))
				d.Show()
			})
			return
		case err != nil:
			fyne.Do(func() { dialog.ShowError(err, u.win) })
			return
		}
		if len(paths) > 0 {
			u.addAssFiles(paths)
		}
	}()
}

// openAssFolder lets the user pick a folder; all .ass files within are added.
func (u *ui) openAssFolder() {
	go func() {
		dir, err := nativeSelectFolder("选择字幕目录 Select Folder")
		switch {
		case errors.Is(err, errCanceled):
			return
		case errors.Is(err, errUseFallback):
			fyne.Do(func() {
				dialog.ShowFolderOpen(func(uri fyne.ListableURI, e error) {
					if e != nil || uri == nil {
						return
					}
					u.addAssFromDir(uri.Path())
				}, u.win)
			})
			return
		case err != nil:
			fyne.Do(func() { dialog.ShowError(err, u.win) })
			return
		}
		if dir != "" {
			u.addAssFromDir(dir)
		}
	}()
}

// addAssFromDir scans dir (non-recursive) and adds every .ass file found.
func (u *ui) addAssFromDir(dir string) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	var paths []string
	for _, e := range entries {
		if !e.IsDir() && strings.EqualFold(filepath.Ext(e.Name()), ".ass") {
			paths = append(paths, filepath.Join(dir, e.Name()))
		}
	}
	if len(paths) > 0 {
		u.addAssFiles(paths)
	}
}

// pickLibFolder lets the user add a font library directory.
func (u *ui) pickLibFolder() {
	go func() {
		dir, err := nativeSelectFolder("选择字体库目录 Select Library Folder")
		switch {
		case errors.Is(err, errCanceled):
			return
		case errors.Is(err, errUseFallback):
			fyne.Do(func() {
				dialog.ShowFolderOpen(func(uri fyne.ListableURI, e error) {
					if e != nil || uri == nil {
						return
					}
					u.addLibDir(uri.Path())
				}, u.win)
			})
			return
		case err != nil:
			fyne.Do(func() { dialog.ShowError(err, u.win) })
			return
		}
		if dir != "" {
			fyne.Do(func() { u.addLibDir(dir) })
		}
	}()
}

// pickDbPath lets the user choose where to save the font database file.
func (u *ui) pickDbPath() {
	go func() {
		path, err := nativeSaveFile("保存数据库 Save Database", "fontdb.json")
		switch {
		case errors.Is(err, errCanceled):
			return
		case errors.Is(err, errUseFallback):
			fyne.Do(func() {
				dialog.ShowFileSave(func(w fyne.URIWriteCloser, e error) {
					if e != nil || w == nil {
						return
					}
					p := w.URI().Path()
					_ = w.Close()
					u.dbPath.SetText(p)
				}, u.win)
			})
			return
		case err != nil:
			fyne.Do(func() { dialog.ShowError(err, u.win) })
			return
		}
		if path != "" {
			fyne.Do(func() { u.dbPath.SetText(path) })
		}
	}()
}
