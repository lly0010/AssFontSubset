# AssFontSubset

使用 fonttools 或 harfbuzz-subset 生成 ASS 字幕文件的字体子集，并自动修改字体名称及 ASS 文件中对应的字体名称

## 依赖

如果使用 fonttools 进行子集化，需要：
1. [fonttools](https://github.com/fonttools/fonttools)，推荐使用最新版本
2. Path 环境变量中存在 pyftsubset 和 ttx，也可以通过选项指定所在目录，此二者是 fonttools 的一部分

## AssFontSubset.Console

```
Usage:
  AssFontSubset.Console [<path>...] [options]

Arguments:
  <path>  要子集化的 ASS 字幕文件路径，可以输入多个同目录的字幕文件

Options:
  -?, -h, --help                                 Show help and usage information
  --version                                      Show version information
  --fonts                                        ASS 字幕文件需要的字体所在目录，默认为 ASS 同目录的 fonts 文件夹
  --output                                       子集化后成品所在目录，默认为 ASS 同目录的 output 文件夹
  --subset-backend <HarfBuzzSubset|PyFontTools>  子集化使用的后端 [default: PyFontTools]
  --bin-path                                     指定 pyftsubset 和 ttx 所在目录。若未指定，会使用环境变量中的
  --source-han-ellipsis                          使思源黑体和宋体的省略号居中对齐 [default: True]
  --debug                                        保留子集化期间的各种临时文件，位于 --output-dir 指定的文件夹；同时打印 出所有运行的命令 [default: False]
  --embed-font-to-ass                            将子集化生成的字体内嵌到输出 ASS 字幕文件的 [Fonts] 段中 [default: False]
  --separate-font-folder                         将子集化字体放入与 ASS 文件同名的子文件夹中 [default: False]
```

> `--separate-font-folder` 会把子集字体放入与 ASS 文件同名的子文件夹中，即 `output/<ass 文件名>/...`；输出的字幕文件仍位于输出目录顶层。若有多个字幕文件，会在每个字幕同名文件夹下各放一份字体。

> 内嵌字体（`--embed-font-to-ass`）会把生成的子集字体以 UUEncode 编码写入每个输出 ASS 的 `[Fonts]` 段，使字幕文件自带字体、便于分发。子集字体的内部名称已被改写为随机名并在字幕中引用，因此最适合 libass 系播放器（mpv 等）。同时输出目录下仍会保留独立的子集字体文件。

## AssFontSubset.Avalonia

跨平台图形前端，是对 `AssFontSubset.Console` 的图形化封装：它在后台调用命令行程序进行子集化，并实时显示其运行日志，因此与命令行后端能力一致（同时支持 PyFontTools 和 HarfBuzz-Subset 后端）。

使用方法：

1. 将 GUI 与 `AssFontSubset.Console`（win64 等版本的可执行文件）放在同一目录，程序会自动检测；也可在「命令行程序」一栏手动指定其路径。
2. 拖入或选择需要子集化的 ASS 字幕文件，「字体目录」「输出目录」会自动填入字幕同目录下的 `fonts`、`output`，也可自行修改。
3. 选择子集化后端，按需勾选「居中思源省略号」「调试选项」「内嵌字体」（内嵌进输出字幕）「字体放入同名文件夹」（字体放入与 ass 同名的子文件夹）。
4. 点击「开始」，子集化日志会实时显示在下方；完成后请检查输出目录。

## 注意

1. 每次生成时会自动删除 ass 同目录下的 output 文件夹。
2. 目前，子集化后只保留了必要的 OpenType features，可参照[此 issue](https://github.com/AmusementClub/AssFontSubset/issues/13) 和 [保留的 features](https://github.com/AmusementClub/AssFontSubset/blob/b9e872b2ae450001eada6e84f47a32198a3c11a7/AssFontSubset.Core/src/FontConstant.cs#L49-L63)。

## Todo

1. 考虑增加对 fontations subset (klippa) 的支持
3. 不确定是否要支持可变字体（variable fonts）
4. 不确定是否要恢复检查更新的功能

## FAQ 常见问题和故障排除

1. 如果弹出的错误信息中有提到`请尝试使用 FontForge 重新生成字体。`： 请下载并安装 [Fontforge](https://fontforge.org/en-US/)，然后使用 Fontforge 打开有问题的字体，不需要改动任何信息，直接点文件——生成字体（File - Generate Font），然后生成一个新的字体文件，无视中途弹出的警告。再使用新生成的字体进行子集化操作。

2. 如果 Fontforge 无法解决问题，或出现奇怪的错误，且没有有用的错误信息，请尝试更新 fonttools:

```
pip3 install --upgrade fonttools
```

3. 其他已知问题
  
    - [部分特殊字体 hb-subset 丢弃而不是重生成可用的 cmap 表](https://github.com/harfbuzz/harfbuzz/issues/4980)，目前只能切换到 fonttools 进行子集化
    - [方正锐正黑_GBK Bold 名称匹配错误](https://github.com/AmusementClub/AssFontSubset/issues/21)

4. 若有无法解决的问题，欢迎回报 issue
