package ui

import (
	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/widget"
)

// tappableRow is a list row that shows a label and, on right-click (secondary
// tap), pops up a context menu to delete the corresponding item. The current
// row index is rebound by the list's update callback.
type tappableRow struct {
	widget.BaseWidget
	label    *widget.Label
	id       widget.ListItemID
	win      fyne.Window
	onDelete func(widget.ListItemID)
}

func newTappableRow(win fyne.Window, onDelete func(widget.ListItemID)) *tappableRow {
	r := &tappableRow{
		label:    widget.NewLabel(""),
		win:      win,
		onDelete: onDelete,
	}
	r.label.Truncation = fyne.TextTruncateEllipsis
	r.ExtendBaseWidget(r)
	return r
}

func (r *tappableRow) CreateRenderer() fyne.WidgetRenderer {
	return widget.NewSimpleRenderer(r.label)
}

func (r *tappableRow) setItem(id widget.ListItemID, text string) {
	r.id = id
	r.label.SetText(text)
}

// TappedSecondary shows the right-click delete menu.
func (r *tappableRow) TappedSecondary(e *fyne.PointEvent) {
	if r.onDelete == nil || r.win == nil {
		return
	}
	id := r.id
	menu := fyne.NewMenu("", fyne.NewMenuItem("删除 Remove", func() { r.onDelete(id) }))
	widget.ShowPopUpMenuAtPosition(menu, r.win.Canvas(), e.AbsolutePosition)
}
