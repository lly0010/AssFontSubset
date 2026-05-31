# gofontsubset

一个用 Go + [Fyne](https://fyne.io/) 编写的 **ASS 字幕字体子集化** 桌面程序（面向 Windows），
是 AssFontSubset 的 Go 版本实现。子集化引擎调用 **hb-subset**，字体库索引/数据库为纯 Go 实现。

## 功能

与 .NET GUI 对应，分为两个标签页：

### 子集化（Subset）
- **批量子集化**：拖拽、「添加文件」或「添加目录」选入多个 ASS 文件一次处理；在列表项上**右键**可单独删除该文件，或「清空」全部移除。
- **设置输出位置**：可指定输出目录（留空则用第一个字幕同目录的 `output`）。
- **字体来源**：默认从单个「字体目录」查找；勾选「使用字体数据库」后改用已建立索引的字体库。
- **OTF/TTC 转 TTF**：勾选后在子集化前把 `.otf`（CFF 轮廓）转换为 `.ttf`，并把字体集合（`.ttc`/`.otc`）按所用的那一个字面拆分、扁平化为独立的 `.ttf`，依赖带 fontTools 的 Python。
- 运行日志实时显示。

### 字体库（Font Library）
- **添加字体库**：加入一个或多个目录（递归扫描 ttf/otf/ttc/otc）。
- **建立数据库**：扫描全部库目录，把每个字体面写入 JSON 数据库，记录结构为：

```json
{
  "families": ["a-otf jun pro 501", "a-otf じゅん pro 501"],
  "fullnames": ["jun501pro-bold"],
  "psnames": ["jun501pro-bold"],
  "weight": 600,
  "slant": 0,
  "path": "D:\\fonts\\old\\A-OTF Jun Pro 501.ttf",
  "index": 0,
  "last_write_time": "UTC 2025-04-15 01:03:05",
  "bold": true,
  "maxp_num_glyphs": 12345,
  "family_names": { "1033": "...", "1041": "..." }
}
```

其中 `families`/`fullnames`/`psnames`/`weight`/`slant`/`path`/`index`/`last_write_time` 为要求的字段，
`bold`/`maxp_num_glyphs`/`family_names` 是字体匹配所需的额外字段。

设置（字体库目录、数据库路径、各选项）保存在用户配置目录下的 `gofontsubset/settings.json`，
数据库默认在 `gofontsubset/fontdb.json`（Windows 即 `%AppData%\gofontsubset\`）。

### 界面说明
- **原生文件对话框**：在 Windows 上「浏览/添加」均调用系统官方的文件/文件夹选择框（其它平台回退到 Fyne 自带对话框）。
- **右键删除**：字幕列表与字体库目录列表中的条目，右键弹出菜单即可删除对应的文件/文件夹。
- **多国语言字体名**：按平台/编码正确解码 `name` 表，包含 Macintosh 平台的旧式 CJK 编码（Shift-JIS / Big5 / EUC-KR / GBK），避免把日文等字体名解析成乱码写进数据库；界面启动时自动加载系统 CJK 字体（Windows 优先「微软雅黑」等）使其正常显示。

## 运行期依赖

- **hb-subset**：HarfBuzz 自带的子集化工具。请将 `hb-subset(.exe)` 放进 PATH，或在界面中指定其路径。
- **Python + fontTools**（仅当勾选「OTF/TTC 转 TTF」时）：用于 CFF→TrueType 轮廓转换，以及把 `.ttc`/`.otc` 集合拆分为独立 `.ttf`。

> 说明：hb-subset 自身不能重命名字体，本程序在子集化后用纯 Go 重写 `name` 表，
> 为每个字族生成唯一的随机名，并重算表目录与校验和。

## 构建

需要 Go 1.24+，且 Fyne 依赖 CGO 与系统图形库。

Windows（推荐安装 [MSYS2](https://www.msys2.org/) 或 TDM-GCC 提供 gcc）：

```sh
cd gofontsubset
go build -o gofontsubset.exe .
# 或使用 fyne 打包工具生成带图标的程序：
#   go install fyne.io/tools/cmd/fyne@latest
#   fyne package -os windows
```

Linux 构建需安装 `libgl1-mesa-dev xorg-dev`。

## 测试

```sh
cd gofontsubset
go test ./...
```

纯 Go 部分（字体元数据解析、`name` 表重写往返、数据库读写与选择、ASS 解析/改写）均有单元测试覆盖。
实际子集化效果需在装有 hb-subset 与真实字体的环境中验证。
