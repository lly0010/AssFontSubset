//go:build windows

package ui

import "github.com/ncruces/zenity"

// On Windows these use the native (official Microsoft) IFileDialog pickers.

func nativeSelectFolder(title string) (string, error) {
	p, err := zenity.SelectFile(zenity.Directory(), zenity.Title(title))
	return p, mapZenityErr(err)
}

func nativeOpenFiles(title string, exts []string) ([]string, error) {
	opts := []zenity.Option{zenity.Title(title)}
	if len(exts) > 0 {
		pats := make([]string, 0, len(exts))
		for _, e := range exts {
			pats = append(pats, "*"+e)
		}
		opts = append(opts, zenity.FileFilters{{Name: "Subtitles", Patterns: pats, CaseFold: true}})
	}
	ps, err := zenity.SelectFileMultiple(opts...)
	return ps, mapZenityErr(err)
}

func nativeSaveFile(title, filename string) (string, error) {
	p, err := zenity.SelectFileSave(
		zenity.Title(title),
		zenity.ConfirmOverwrite(),
		zenity.Filename(filename),
	)
	return p, mapZenityErr(err)
}

func mapZenityErr(err error) error {
	if err == zenity.ErrCanceled {
		return errCanceled
	}
	return err
}
