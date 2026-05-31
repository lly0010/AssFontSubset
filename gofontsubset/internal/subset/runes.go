package subset

// vertMapping maps a character to its vertical presentation form, mirroring the
// .NET FontConstant.VertMapping table (Unicode 15.1).
var vertMapping = map[rune]rune{
	',': '︐', '、': '︑', '。': '︒', ':': '︓',
	';': '︔', '!': '︕', '?': '︖', '〖': '︗',
	'〗': '︘', '…': '︙', '—': '︱', '–': '︲',
	'(': '︵', ')': '︶', '{': '︷', '}': '︸',
	'〔': '︹', '〕': '︺', '【': '︻', '】': '︼',
	'《': '︽', '》': '︾', '〈': '︿', '〉': '﹀',
	'「': '﹁', '」': '﹂', '『': '﹃', '』': '﹄',
	'[': '﹇', ']': '﹈',
}

// layoutFeatures are the OpenType features kept during subsetting (vertical
// layout related), mirroring FontConstant.SubsetKeepFeatures.
var layoutFeatures = []string{"vert", "vrtr", "vrt2", "vkna"}

// appendNecessaryRunes adds half- and full-width Latin letters and digits, which
// fixes font fallback issues (e.g. on ellipses), mirroring the .NET behaviour.
func appendNecessaryRunes(set map[rune]bool) {
	for c := rune(0x0041); c <= 0x005A; c++ { // A-Z
		set[c] = true
		set[c+65248] = true
	}
	for c := rune(0x0061); c <= 0x007A; c++ { // a-z
		set[c] = true
		set[c+65248] = true
	}
	for c := rune(0x0030); c <= 0x0039; c++ { // 0-9
		set[c] = true
		set[c+65248] = true
	}
	set[0xFF1F] = true
	set[0xFF20] = true
}
