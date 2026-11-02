"""Check semantic anchors and readable numeric text in synthetic conversion outputs."""
from pathlib import Path
import json
import sys
import fitz
root=Path(sys.argv[1]); results=[]
checks={
 'amounts-and-sheets.xlsx.pdf':(2,['123,456,789','(9,876,543)','12.5%','3000','SECOND_SHEET_END','2,468,000']),
 'merged-amounts.xls.pdf':(1,['123,456,789','987,654,321','(12,345,678)','금액 병합 셀 회귀 시험']),
 'table-and-wrap.docx.pdf':(2,['123,456,789','987,654,321','SECOND_PAGE_END 2026','표와 긴 문장 회귀 시험']),
 'drawing.dxf.pdf':(1,[]),
}
for name,(count,tokens) in checks.items():
 doc=fitz.open(root/name);text=''.join(p.get_text() for p in doc)
 assert len(doc)==count,(name,'page count')
 assert all(t in text for t in tokens),(name,'missing', [t for t in tokens if t not in text])
 number_sizes=[s['size'] for p in doc for b in p.get_text('dict')['blocks'] if 'lines' in b for l in b['lines'] for s in l['spans'] if ',' in s['text'] and any(c.isdigit() for c in s['text'])]
 if number_sizes: assert min(number_sizes)>=8,(name,'unreadable amount',number_sizes)
 for p in doc:
  pix=p.get_pixmap(matrix=fitz.Matrix(.5,.5),colorspace=fitz.csGRAY)
  assert sum(v<245 for v in pix.samples)>20,(name,'blank page')
 results.append(dict(fixture=name,pages=len(doc),expectedTokensPresent=True,minAmountFont=min(number_sizes) if number_sizes else None,status='PASS'))
(root/'semantic-checks.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(results,ensure_ascii=False,indent=2))
