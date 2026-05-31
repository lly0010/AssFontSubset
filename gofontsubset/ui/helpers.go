package ui

import (
	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/container"
	"fyne.io/fyne/v2/layout"
	"fyne.io/fyne/v2/widget"
)

// layout4 lays out (label, field) pairs in two columns.
func layout4() fyne.Layout { return layout.NewFormLayout() }

// withBrowse places an entry next to a trailing "browse" button.
func withBrowse(e *widget.Entry, b *widget.Button) fyne.CanvasObject {
	return container.NewBorder(nil, nil, nil, b, e)
}
