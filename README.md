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
  --separate-font-folder                         将子集化字体放入与 ASS 文件同名的子文件夹中 [default: True]
  --embed-only                                   内嵌字体到 ASS 后，不再额外输出独立的子集化字体文件 [default: False]
  --build-font-database                          扫描 --fonts 目录（递归）生成字体索引 JSON 后退出，不进行子集化
  --font-database                                使用字体索引 JSON 按名查找所需字体（替代扫描 --fonts 目录），输出不变
  --reembed-fonts                                以 ASS 已内嵌的字体为字体源：删除原有内嵌，重新子集化并重新内嵌 [default: False]
```

> `--reembed-fonts`：针对**已内嵌字体的 ASS**。直接从其 `[Fonts]` 段解码取出内嵌字体作为字体源，删除原有内嵌,重新子集化后再内嵌一次。多个文件会**各自独立处理**。该选项仅适用于已内嵌字体的字幕；若选中的文件中有**未内嵌**的，会直接报错并指明是哪些文件（请对这些文件改用普通子集化）。
>
> 对一些非标准/有瑕疵的输入会自动容错：事件时间为非标准格式（如毫秒精度 `0:00:01.234`）会规整为标准厘秒；首行为空行的字幕会自动跳过空行；重新内嵌时若内嵌字体的 OS/2 表版本号与长度不符（部分子集化工具的产物会导致 pyftsubset 崩溃），会自动修正。这些都不影响正常字幕。

> `--build-font-database <db.json>` 配合 `--fonts <目录>` 使用：递归扫描该目录下的字体，将每个字体的 families / fullnames / psnames / weight / slant / path / index / last_write_time 写入 JSON 索引，便于快速查找字体。
>
> `--font-database <db.json>`：子集化时改用该索引按名定位字体文件（无需再准备 fonts 目录）。数据库仅用于"按名找到字体文件"，随后仍以相同方式解析这些字体并走相同的子集化流程，**输出与扫描 fonts 目录完全一致**。

> `--separate-font-folder` 会把子集字体放入与 ASS 文件同名的子文件夹中，即 `output/<ass 文件名>/...`；输出的字幕文件仍位于输出目录顶层。若有多个字幕文件，会在每个字幕同名文件夹下各放一份字体。

> 内嵌字体（`--embed-font-to-ass`）会把生成的子集字体以 UUEncode 编码写入每个输出 ASS 的 `[Fonts]` 段，使字幕文件自带字体、便于分发。子集字体的内部名称已被改写为随机名并在字幕中引用，因此最适合 libass 系播放器（mpv 等）。同时输出目录下仍会保留独立的子集字体文件。

## AssFontSubset.Avalonia

跨平台图形前端，是对 `AssFontSubset.Console` 的图形化封装：它在后台调用命令行程序进行子集化，并实时显示其运行日志，因此与命令行后端能力一致（同时支持 PyFontTools 和 HarfBuzz-Subset 后端）。

使用方法：

1. 将 GUI 与 `AssFontSubset.Console`（win64 等版本的可执行文件）放在同一目录，程序会自动检测；也可在「命令行程序」一栏手动指定其路径。
2. 添加需要子集化的 ASS 字幕文件：**拖入文件/文件夹**或点**「添加文件夹」**会用新内容**替换整个列表**；点**「浏览」**选择文件则是**追加累计**到现有列表。列表中可多选后用「删除所选」或 Delete 键单独移除。「字体目录」是会跨重启保留的配置，应用不会自动改写它（留空时命令行会回退到「字幕目录/fonts」）；「输出目录」在为空时会自动填入第一个字幕同目录下的 `output`。「清空」只清空字幕列表与输出目录，不会动字体目录。
3. 选择子集化后端，按需勾选各选项：「居中思源省略号」「调试选项」「内嵌字体」（内嵌进输出字幕）「仅内嵌」（内嵌后不再输出独立子集字体文件，只留自带字体的 ass）「重新内嵌」（对已内嵌字体的 ass：以其内嵌字体为源，删除旧内嵌并重新子集化内嵌，无需外部字体）。子集字体默认放入与 ass 同名的子文件夹（已成为固定默认行为，无需勾选）。
4. 点击「开始」，子集化日志会实时显示在下方，完成或失败的状态会显示在底部状态栏；完成后窗口不会自动关闭，可继续查看日志或再次子集化。
5. 各项选项（字体目录、后端、各勾选项、命令行程序路径、字体数据库路径）会在退出时自动保存，下次打开时恢复。
6. 「字体数据库」一栏可指定索引 JSON 路径，点「构建数据库」会扫描「字体目录」并生成该索引。若该路径指向一个已存在的索引文件，子集化时会自动改用它按名定位字体（无需再准备 fonts 目录），输出不变。

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
