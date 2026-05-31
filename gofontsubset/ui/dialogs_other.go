//go:build !windows

package ui

// Non-Windows builds have no bundled native picker; callers fall back to Fyne's
// built-in dialogs.

func nativeSelectFolder(string) (string, error)          { return "", errUseFallback }
func nativeOpenFiles(string, []string) ([]string, error) { return nil, errUseFallback }
func nativeSaveFile(string, string) (string, error)      { return "", errUseFallback }
