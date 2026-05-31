// Package ui implements the Fyne GUI for the ASS font subsetter.
package ui

import (
	"fmt"
	"path/filepath"
	"sort"
	"strings"

	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/app"
	"fyne.io/fyne/v2/container"
	"fyne.io/fyne/v2/dialog"
	"fyne.io/fyne/v2/storage"
	"fyne.io/fyne/v2/widget"

	"github.com/lly0010/AssFontSubset/gofontsubset/internal/config"
	"github.com/lly0010/AssFontSubset/gofontsubset/internal/fontdb"
	"github.com/lly0010/AssFontSubset/gofontsubset/internal/subset"
)

type ui struct {
	app fyne.App
	win fyne.Window

	settings *config.Settings
	db       *fontdb.DB

	assFiles []string
	libDirs  []string

	// widgets
	assList    *widget.List
	libList    *widget.List
	dbList     *widget.List
	output     *widget.Entry
	fontFolder *widget.Entry
	dbPath     *widget.Entry
	hbPath     *widget.Entry
	pyPath     *widget.Entry
	useDB      *widget.Check
	convertOtf *widget.Check
	debug      *widget.Check
	logBox     *widget.Entry
	progress   *widget.ProgressBarInfinite
	summary    *widget.Label

	assSel int
	libSel int
	logBuf strings.Builder
}

// Run starts the GUI event loop.
func Run() {
	u := &ui{assSel: -1, libSel: -1}
	u.settings = config.Load()
	db, err := fontdb.Load(u.settings.DatabasePath)
	if err != nil {
		db = &fontdb.DB{}
	}
	u.db = db

	u.app = app.New()
	u.win = u.app.NewWindow("gofontsubset")
	u.win.Resize(fyne.NewSize(960, 720))

	u.libDirs = append([]string(nil), u.settings.LibraryFolders...)

	tabs := container.NewAppTabs(
		container.NewTabItem("子集化 Subset", u.buildSubsetTab()),
		container.NewTabItem("字体库 Font Library", u.buildLibraryTab()),
	)

	u.win.SetContent(tabs)
	u.win.SetOnDropped(func(_ fyne.Position, uris []fyne.URI) {
		var paths []string
		for _, uri := range uris {
			paths = append(paths, uri.Path())
		}
		u.addAssFiles(paths)
	})
	u.win.SetOnClosed(u.persist)

	u.win.ShowAndRun()
}

// ---------------------------------------------------------------- Subset tab

func (u *ui) buildSubsetTab() fyne.CanvasObject {
	u.assList = widget.NewList(
		func() int { return len(u.assFiles) },
		func() fyne.CanvasObject { return widget.NewLabel("") },
		func(i widget.ListItemID, o fyne.CanvasObject) { o.(*widget.Label).SetText(u.assFiles[i]) },
	)
	u.assList.OnSelected = func(id widget.ListItemID) { u.assSel = id }

	addBtn := widget.NewButton("添加文件 Add Files", u.openAssFiles)
	addDirBtn := widget.NewButton("添加目录 Add Folder", u.openAssFolder)
	clearBtn := widget.NewButton("清空 Clear", func() {
		u.assFiles = nil
		u.assSel = -1
		u.assList.Refresh()
	})
	assButtons := container.NewHBox(addBtn, addDirBtn, clearBtn)

	u.output = widget.NewEntry()
	u.output.SetText(u.settings.OutputFolder)
	outBrowse := widget.NewButton("浏览…", func() { u.pickFolder(u.output) })

	u.fontFolder = widget.NewEntry()
	u.fontFolder.SetText(u.settings.FontFolder)
	fontBrowse := widget.NewButton("浏览…", func() { u.pickFolder(u.fontFolder) })

	u.useDB = widget.NewCheck("使用字体数据库 Use font database", func(checked bool) {
		if checked {
			u.fontFolder.Disable()
		} else {
			u.fontFolder.Enable()
		}
	})
	u.useDB.SetChecked(u.settings.UseDatabase)

	u.convertOtf = widget.NewCheck("OTF/TTC 转 TTF (需 Python+fontTools)", nil)
	u.convertOtf.SetChecked(u.settings.ConvertOtf)
	u.debug = widget.NewCheck("调试 Debug (保留临时文件)", nil)
	u.debug.SetChecked(u.settings.Debug)

	u.hbPath = widget.NewEntry()
	u.hbPath.SetPlaceHolder("hb-subset (留空则用 PATH 中的)")
	u.hbPath.SetText(u.settings.HbSubsetPath)
	u.pyPath = widget.NewEntry()
	u.pyPath.SetPlaceHolder("python (留空则用 PATH 中的)")
	u.pyPath.SetText(u.settings.PythonPath)

	u.progress = widget.NewProgressBarInfinite()
	u.progress.Hide()

	startBtn := widget.NewButton("开始子集化 Start", u.startSubset)
	startBtn.Importance = widget.HighImportance

	u.logBox = widget.NewMultiLineEntry()
	u.logBox.Wrapping = fyne.TextWrapWord
	clearLog := widget.NewButton("清空日志 Clear Log", func() {
		u.logBuf.Reset()
		u.logBox.SetText("")
	})

	form := container.New(layout4(),
		widget.NewLabel("字幕文件 ASS"), assButtons,
		widget.NewLabel("输出目录 Output"), withBrowse(u.output, outBrowse),
		widget.NewLabel(""), u.useDB,
		widget.NewLabel("字体目录 Fonts"), withBrowse(u.fontFolder, fontBrowse),
		widget.NewLabel("hb-subset"), u.hbPath,
		widget.NewLabel("python"), u.pyPath,
		widget.NewLabel("选项 Options"), container.NewHBox(u.convertOtf, u.debug),
	)

	top := container.NewBorder(nil, form, nil, nil, u.assList)
	logHeader := container.NewBorder(nil, nil, widget.NewLabel("日志 Log"), clearLog)
	logArea := container.NewBorder(logHeader, nil, nil, nil, container.NewVScroll(u.logBox))

	center := container.NewVSplit(top, logArea)
	center.Offset = 0.5

	bottom := container.NewVBox(u.progress, startBtn)
	return container.NewBorder(nil, bottom, nil, nil, center)
}

// --------------------------------------------------------------- Library tab

func (u *ui) buildLibraryTab() fyne.CanvasObject {
	u.libList = widget.NewList(
		func() int { return len(u.libDirs) },
		func() fyne.CanvasObject { return widget.NewLabel("") },
		func(i widget.ListItemID, o fyne.CanvasObject) { o.(*widget.Label).SetText(u.libDirs[i]) },
	)
	u.libList.OnSelected = func(id widget.ListItemID) { u.libSel = id }

	addBtn := widget.NewButton("添加目录 Add Folder", func() {
		dialog.ShowFolderOpen(func(uri fyne.ListableURI, err error) {
			if err != nil || uri == nil {
				return
			}
			u.addLibDir(uri.Path())
		}, u.win)
	})
	removeBtn := widget.NewButton("移除 Remove", func() {
		if u.libSel >= 0 && u.libSel < len(u.libDirs) {
			u.libDirs = append(u.libDirs[:u.libSel], u.libDirs[u.libSel+1:]...)
			u.libSel = -1
			u.libList.Refresh()
			u.persist()
		}
	})

	u.dbPath = widget.NewEntry()
	u.dbPath.SetText(u.settings.DatabasePath)
	dbBrowse := widget.NewButton("浏览…", func() {
		dialog.ShowFileSave(func(w fyne.URIWriteCloser, err error) {
			if err != nil || w == nil {
				return
			}
			path := w.URI().Path()
			_ = w.Close()
			u.dbPath.SetText(path)
		}, u.win)
	})

	buildBtn := widget.NewButton("建立数据库 Build Database", u.buildDatabase)
	buildBtn.Importance = widget.HighImportance

	u.summary = widget.NewLabel(u.dbSummary())

	u.dbList = widget.NewList(
		func() int { return len(u.db.Entries) },
		func() fyne.CanvasObject {
			return container.NewVBox(widget.NewLabel(""), widget.NewLabel(""))
		},
		func(i widget.ListItemID, o fyne.CanvasObject) {
			e := u.db.Entries[i]
			box := o.(*fyne.Container)
			box.Objects[0].(*widget.Label).SetText(strings.Join(e.Families, " / ") + "   " + entryDetail(e))
			box.Objects[1].(*widget.Label).SetText(e.Path)
		},
	)

	libButtons := container.NewHBox(addBtn, removeBtn)
	dbRow := withBrowse(u.dbPath, dbBrowse)
	header := container.NewVBox(
		widget.NewLabel("字体库目录 Library Folders"),
		libButtons,
	)
	mid := container.New(layout4(),
		widget.NewLabel("数据库文件 Database"), dbRow,
	)
	actions := container.NewBorder(nil, nil, buildBtn, nil, u.summary)

	topPart := container.NewBorder(header, nil, nil, nil, u.libList)
	bottomPart := container.NewBorder(
		container.NewVBox(mid, actions, widget.NewLabel("已索引字体 Indexed Fonts")),
		nil, nil, nil, u.dbList,
	)
	split := container.NewVSplit(topPart, bottomPart)
	split.Offset = 0.35
	return split
}

// ------------------------------------------------------------------- actions

func (u *ui) startSubset() {
	if len(u.assFiles) == 0 {
		dialog.ShowError(fmt.Errorf("没有 ASS 文件，请先添加"), u.win)
		return
	}
	output := strings.TrimSpace(u.output.Text)
	if output == "" {
		if dir := filepath.Dir(u.assFiles[0]); dir != "" {
			output = filepath.Join(dir, "output")
			u.output.SetText(output)
		}
	}
	u.persist()

	cfg := subset.Config{
		HbSubset:   strings.TrimSpace(u.hbPath.Text),
		OutputDir:  output,
		Debug:      u.debug.Checked,
		ConvertOtf: u.convertOtf.Checked,
		Python:     strings.TrimSpace(u.pyPath.Text),
		Log:        u.appendLog,
	}

	var candidates []subset.Candidate
	if u.useDB.Checked {
		if u.db.Count() == 0 {
			dialog.ShowError(fmt.Errorf("字体数据库为空，请先在字体库标签页建立数据库"), u.win)
			return
		}
		candidates = subset.CandidatesFromEntries(u.db.Entries)
	} else {
		folder := strings.TrimSpace(u.fontFolder.Text)
		if folder == "" {
			dialog.ShowError(fmt.Errorf("未设置字体目录，请选择字体目录或启用字体数据库"), u.win)
			return
		}
		c, err := subset.CandidatesFromFolder(folder, u.appendLog)
		if err != nil {
			dialog.ShowError(err, u.win)
			return
		}
		candidates = c
	}

	files := append([]string(nil), u.assFiles...)
	u.setBusy(true)
	go func() {
		err := subset.Run(files, candidates, cfg)
		fyne.Do(func() {
			u.setBusy(false)
			if err != nil {
				u.appendLog("ERROR: " + err.Error())
				dialog.ShowError(err, u.win)
			} else {
				dialog.ShowInformation("完成", "子集化完成，请检查输出目录。", u.win)
			}
		})
	}()
}

func (u *ui) buildDatabase() {
	if len(u.libDirs) == 0 {
		dialog.ShowError(fmt.Errorf("未添加字体库目录，请先至少添加一个目录"), u.win)
		return
	}
	u.persist()
	dirs := append([]string(nil), u.libDirs...)
	dbPath := strings.TrimSpace(u.dbPath.Text)
	if dbPath == "" {
		dbPath = config.DefaultDatabasePath()
		u.dbPath.SetText(dbPath)
	}

	u.setBusy(true)
	go func() {
		u.db.Build(dirs, u.appendLog)
		err := u.db.Save(dbPath)
		fyne.Do(func() {
			u.setBusy(false)
			u.dbList.Refresh()
			u.summary.SetText(u.dbSummary())
			if err != nil {
				dialog.ShowError(err, u.win)
				return
			}
			u.appendLog(fmt.Sprintf("数据库已建立：共索引 %d 个字体", u.db.Count()))
		})
	}()
}

// -------------------------------------------------------------------- helpers

func (u *ui) openAssFiles() {
	d := dialog.NewFileOpen(func(rc fyne.URIReadCloser, err error) {
		if err != nil || rc == nil {
			return
		}
		path := rc.URI().Path()
		_ = rc.Close()
		u.addAssFiles([]string{path})
	}, u.win)
	d.SetFilter(storage.NewExtensionFileFilter([]string{".ass"}))
	d.Show()
}

func (u *ui) openAssFolder() {
	dialog.ShowFolderOpen(func(uri fyne.ListableURI, err error) {
		if err != nil || uri == nil {
			return
		}
		list, err := uri.List()
		if err != nil {
			return
		}
		var paths []string
		for _, child := range list {
			paths = append(paths, child.Path())
		}
		u.addAssFiles(paths)
	}, u.win)
}

func (u *ui) addAssFiles(paths []string) {
	existing := map[string]bool{}
	for _, f := range u.assFiles {
		existing[f] = true
	}
	for _, p := range paths {
		if strings.EqualFold(filepath.Ext(p), ".ass") && !existing[p] {
			u.assFiles = append(u.assFiles, p)
			existing[p] = true
		}
	}
	sort.Strings(u.assFiles)
	if u.assList != nil {
		fyne.Do(u.assList.Refresh)
	}
	if u.output != nil && strings.TrimSpace(u.output.Text) == "" && len(u.assFiles) > 0 {
		dir := filepath.Dir(u.assFiles[0])
		u.output.SetText(filepath.Join(dir, "output"))
		if u.fontFolder != nil && strings.TrimSpace(u.fontFolder.Text) == "" {
			u.fontFolder.SetText(filepath.Join(dir, "fonts"))
		}
	}
}

func (u *ui) addLibDir(path string) {
	for _, d := range u.libDirs {
		if d == path {
			return
		}
	}
	u.libDirs = append(u.libDirs, path)
	u.libList.Refresh()
	u.persist()
}

func (u *ui) pickFolder(target *widget.Entry) {
	dialog.ShowFolderOpen(func(uri fyne.ListableURI, err error) {
		if err != nil || uri == nil {
			return
		}
		target.SetText(uri.Path())
	}, u.win)
}

func (u *ui) setBusy(busy bool) {
	if busy {
		u.progress.Show()
		u.progress.Start()
	} else {
		u.progress.Stop()
		u.progress.Hide()
	}
}

func (u *ui) appendLog(line string) {
	write := func() {
		u.logBuf.WriteString(line)
		u.logBuf.WriteString("\n")
		u.logBox.SetText(u.logBuf.String())
		u.logBox.CursorRow = strings.Count(u.logBuf.String(), "\n")
		u.logBox.Refresh()
	}
	if u.logBox == nil {
		return
	}
	fyne.Do(write)
}

func (u *ui) dbSummary() string {
	return fmt.Sprintf("已索引 %d 个字体 / %d faces indexed", u.db.Count(), u.db.Count())
}

func (u *ui) persist() {
	u.settings.LibraryFolders = append([]string(nil), u.libDirs...)
	u.settings.OutputFolder = u.output.Text
	u.settings.FontFolder = u.fontFolder.Text
	u.settings.DatabasePath = u.dbPath.Text
	u.settings.HbSubsetPath = u.hbPath.Text
	u.settings.PythonPath = u.pyPath.Text
	u.settings.UseDatabase = u.useDB.Checked
	u.settings.ConvertOtf = u.convertOtf.Checked
	u.settings.Debug = u.debug.Checked
	_ = u.settings.Save()
}

func entryDetail(e fontdb.Entry) string {
	parts := []string{fmt.Sprintf("weight %d", e.Weight)}
	if e.Slant != 0 {
		parts = append(parts, "italic")
	} else {
		parts = append(parts, "upright")
	}
	if e.Bold {
		parts = append(parts, "bold")
	}
	if e.Index > 0 {
		parts = append(parts, fmt.Sprintf("index %d", e.Index))
	}
	return "(" + strings.Join(parts, ", ") + ")"
}
