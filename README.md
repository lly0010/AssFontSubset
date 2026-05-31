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
```

## AssFontSubset.Avalonia

为 Windows 设计的图形界面，包含两个标签页：

### 子集化（Subset）

- **批量子集化**：可拖拽或通过「添加文件」选择多个同目录的 ASS 文件一次性处理。
- **设置输出位置**：通过「浏览…」或直接编辑文本框指定 output 目录。
- **字体来源**：
  - 默认从单个「字体目录」查找字体；
  - 勾选「使用字体数据库」后，改为从已建立索引的字体库中自动查找所需字体，无需每次手动收集字体文件。
- **OTF 转 TTF**：勾选后会在子集化前先将 PostScript/CFF 轮廓字体（.otf）转换为 TrueType（.ttf）再进行子集化。该功能依赖带 [fontTools](https://github.com/fonttools/fonttools) 的 Python（可在「Python 路径」中指定 python 可执行文件，留空则使用 PATH 中的 `python`）。
- 其余选项（居中思源省略号、HarfBuzz-Subset 后端、调试）与命令行一致，运行日志会显示在下方的日志面板中。

### 字体库（Font Library）

- **添加字体库**：通过「添加目录」加入一个或多个字体库文件夹（会递归扫描其中的 ttf/otf/ttc/otc）。
- **建立数据库**：点击「建立数据库」按钮后，会扫描所有字体库目录并将每个字体的信息（families / fullnames / psnames / weight / slant / path / index / last_write_time 等）写入数据库文件（默认位于 `%APPDATA%\AssFontSubset\fontdb.json`，可通过「数据库文件」更改）。
- 建立完成后可在「已索引字体」列表中查看索引结果；之后在子集化标签页勾选「使用字体数据库」即可直接使用，无需每次寻找字体。

字体库目录、数据库路径与各选项会自动保存到 `%APPDATA%\AssFontSubset\settings.json`，下次启动时恢复。

## 注意

1. 每次生成时会自动删除 ass 同目录下的 output 文件夹。
2. 目前，子集化后只保留了必要的 OpenType features，可参照[此 issue](https://github.com/AmusementClub/AssFontSubset/issues/13) 和 [保留的 features](https://github.com/AmusementClub/AssFontSubset/blob/b9e872b2ae450001eada6e84f47a32198a3c11a7/AssFontSubset.Core/src/FontConstant.cs#L49-L63)。

## Todo

1. 考虑移除 gui 支持
2. 考虑增加对 fontations subset (klippa) 的支持
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
