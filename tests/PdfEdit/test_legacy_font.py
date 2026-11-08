"""Regression for legacy Johab cmaps and Korean PostScript names. Synthetic font copy only."""
from pathlib import Path
import sys,hashlib
sys.path.insert(0,str(Path('src/Topdf/engines').resolve()))
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._c_m_a_p import CmapSubtable
from reportlab.pdfbase import pdfmetrics
from pdf_typography import register_original_font
root=Path('artifacts/legacy-font-test');root.mkdir(parents=True,exist_ok=True)
source=Path('C:/Windows/Fonts/malgun.ttf');source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
fixture=root/'legacy-korean.ttf'
with TTFont(source) as font:
    mapping=font.getBestCmap()
    legacy=CmapSubtable.newSubtable(4);legacy.platformID=3;legacy.platEncID=5;legacy.language=0;legacy.cmap={32:mapping[32],65:mapping[65]}
    font['cmap'].tables.append(legacy)
    font['name'].names=[n for n in font['name'].names if n.nameID!=6]
    font['name'].setName('한글포스트스크립트',6,3,1,0x409)
    font.save(fixture)
before=hashlib.sha256(fixture.read_bytes()).hexdigest()
alias=register_original_font(fixture);face=pdfmetrics.getFont(alias).face
assert all(ord(ch) in face.charToGlyph for ch in '델리모한글'), 'Unicode cmap must override legacy Johab'
assert all(byte<128 for byte in face.name), 'Embedded PostScript name must be ASCII'
assert before==hashlib.sha256(fixture.read_bytes()).hexdigest()
assert source_hash==hashlib.sha256(source.read_bytes()).hexdigest()
print('PASS legacy font: Unicode cmap, Korean PostScript name normalization, glyph coverage, source font unchanged')

