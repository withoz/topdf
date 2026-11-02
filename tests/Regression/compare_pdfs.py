"""Compare PDF page count, extracted amounts and raster output against approved baseline.
Usage: python compare_pdfs.py BASELINE CURRENT OUTPUT
Read-only: never changes input PDFs. JSON/images may contain private data; keep local.
"""
import json
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path
import fitz
from PIL import Image, ImageChops, ImageStat, ImageOps, ImageDraw

base, current, output = map(Path, sys.argv[1:4]); output.mkdir(parents=True,exist_ok=True)
results=[]; contacts=[]
for index,pdf in enumerate(sorted(current.glob('*.pdf'))):
    old=base/pdf.name
    if not old.exists(): continue
    a=fitz.open(old); b=fitz.open(pdf)
    def amounts(doc): return Counter(re.findall(r'(?<!\d)\d{1,3}(?:,\d{3})+(?!\d)', ''.join(p.get_text() for p in doc)))
    missing=amounts(a)-amounts(b)
    diffs=[]
    for page in range(min(len(a),len(b))):
        paths=[]
        for tag,source in [('old',old),('new',pdf)]:
            prefix=output/f'{index}-{page}-{tag}'
            subprocess.run(['pdftoppm','-f',str(page+1),'-l',str(page+1),'-singlefile','-scale-to','900','-png',str(source),str(prefix)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            paths.append(prefix.with_suffix('.png'))
        im1=Image.open(paths[0]).convert('RGB'); im2=Image.open(paths[1]).convert('RGB')
        if im1.size != im2.size: diffs.append(None)
        else: diffs.append(round(sum(ImageStat.Stat(ImageChops.difference(im1,im2)).mean)/3,5))
        if page in [0,len(b)-1]:
            thumbs=[ImageOps.contain(im,(450,600)) for im in [im1,im2]]
            row=Image.new('RGB',(920,640),'#e8edf2'); draw=ImageDraw.Draw(row)
            draw.text((10,8),f'{index+1} / page {page+1}: baseline | current',fill='black')
            row.paste(thumbs[0],(10,30)); row.paste(thumbs[1],(470,30)); contacts.append(row)
    size_equal=len(a)==len(b) and all(x.rect==y.rect for x,y in zip(a,b))
    passed=size_equal and not missing and all(d is not None and d<=0.1 for d in diffs)
    results.append(dict(name=pdf.name,baselinePages=len(a),currentPages=len(b),pageSizesEqual=size_equal,missingAmountTokens=dict(missing),pixelMeanAbsoluteDifference=diffs,status='PASS' if passed else 'REVIEW'))
for i,row in enumerate(contacts): row.save(output/f'comparison-{i+1:02}.png')
(output/'comparison.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k!='name'} for r in results],indent=2))
if not results or any(r['status']!='PASS' for r in results): sys.exit(1)
