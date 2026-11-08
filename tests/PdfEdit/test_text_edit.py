"""Run with bundled Python. Own synthetic fixtures only; no private documents."""
import hashlib
import json
import subprocess
import sys
from pathlib import Path
from contextlib import closing
from reportlab.pdfgen.canvas import Canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from pypdf import PdfReader, PdfWriter
from pypdf.annotations import Text
from pypdf.generic import NameObject, NumberObject
import pypdfium2 as pdfium

root=Path('artifacts/pdf-text-test').resolve();root.mkdir(parents=True,exist_ok=True)
worker=Path('src/Topdf/engines/pdf_edit.py').resolve()
pdfmetrics.registerFont(TTFont('Korean','C:/Windows/Fonts/malgun.ttf'))
source=root/'text-source.pdf'
c=Canvas(str(source),pagesize=(595,842))
c.beginForm('shared');c.setFont('Korean',14)
c.drawString(60,600,'공유 본문 변경 전');c.drawString(60,560,'유지할 본문 ABC 7654321');c.endForm()
for i in range(2):
 c.setFillColorRGB(.85,.95,1);c.rect(40,700,450,50,fill=1,stroke=0)
 c.setFillColorRGB(0,0,0);c.setFont('Korean',14);c.drawString(60,720,'기존 금액 123,456원');c.doForm('shared');c.showPage()
c.setFont('Korean',14);c.drawString(60,720,'마지막 페이지');c.save()
w=PdfWriter();w.append(source);w.add_annotation(0,Text(rect=(20,20,42,42),text='keep original annotation'));w.add_outline_item('Keep bookmark',0)
with source.open('wb') as f:w.write(f)
original_hash=hashlib.sha256(source.read_bytes()).hexdigest()

def run(op,source,output,*extra,ok=True):
 result=subprocess.run([sys.executable,'-X','utf8',str(worker),op,str(source),str(output),*map(str,extra)],capture_output=True,encoding='utf-8')
 assert (result.returncode==0)==ok,result.stderr
 return result
def inspect(path):
 out=root/(path.stem+'-info.json');run('inspect',path,out);return json.loads(out.read_text(encoding='utf-8'))
def layer(path):
 out=root/(path.stem+'-text.json');run('text',path,out,'--page',0);return json.loads(out.read_text(encoding='utf-8'))
def replacement(t,text):
 return dict(kind='replace',sourceTextId=t['id'],sourceText=t['text'],text=text,x=t['editX'],y=t['editY'],width=t['editWidth']+15,height=t['editHeight']+14,fontSize=t['fontSize'],color=t['color'],rotation=t['rotation'])
def save(path,plan,suffix):
 pf=root/(suffix+'.json');pf.write_text(json.dumps(plan,ensure_ascii=False),encoding='utf-8');output=root/(suffix+'.pdf')
 if output.exists():output.unlink()
 run('export',path,output,'--plan',pf);return output,pf
items=layer(source);assert len(items)==3,items
assert any(t['text']=='공유 본문 변경 전' for t in items)
info=inspect(source)
elements=[replacement(next(t for t in items if '123,456' in t['text']),'수정 금액 987,654원'),replacement(next(t for t in items if t['text']=='공유 본문 변경 전'),'공유 본문 변경 후')]
plan=dict(sourceSha256=info['sourceSha256'],pages=[dict(sourceIndex=i,rotation=0,elements=elements if i==0 else []) for i in range(3)])
output,pf=save(source,plan,'text-replaced')
reader=PdfReader(output);texts=[p.extract_text() for p in reader.pages]
assert '123,456' not in texts[0] and '변경 전' not in texts[0],texts[0]
for token in ('987,654','변경 후','유지할 본문 ABC 7654321'):assert token in texts[0],texts[0]
assert '123,456' in texts[1] and '변경 전' in texts[1]
assert reader.outline[0].title=='Keep bookmark'
assert any(a.get_object().get('/Contents')=='keep original annotation' for a in reader.pages[0]['/Annots'])
assert hashlib.sha256(source.read_bytes()).hexdigest()==original_hash
assert any('987,654' in t['text'] for t in layer(output)), 'Saved replacement must remain selectable'
with pdfium.PdfDocument(str(output)) as d:
 with closing(d[0]) as page:
  bitmap=page.render(scale=1)
  try:
   image=bitmap.to_pil();image.save(root/'text-replaced.png');assert image.getpixel((45,100))[:3]!=(255,255,255)
  finally:bitmap.close()
preview=root/'clean-preview.png'
if preview.exists():preview.unlink()
run('render',source,preview,'--page',0,'--plan',pf)
# Rotation + CropBox + UserUnit: selected region and removed object agree.
for angle in (0,90,180,270):
 writer=PdfWriter();writer.append(source,pages=[0]);page=writer.pages[0];page.cropbox.lower_left=(20,20);page.cropbox.upper_right=(580,820);page.rotate(angle);page[NameObject('/UserUnit')]=NumberObject(2)
 rotated=root/f'rotated-{angle}.pdf'
 with rotated.open('wb') as f:writer.write(f)
 data=inspect(rotated);t=next(t for t in layer(rotated) if '123,456' in t['text'])
 p=dict(sourceSha256=data['sourceSha256'],pages=[dict(sourceIndex=0,rotation=0,elements=[replacement(t,'수정 금액 987,654원')])])
 edited,_=save(rotated,p,f'rotated-{angle}-edited');text=PdfReader(edited).pages[0].extract_text()
 assert '123,456' not in text and '987,654' in text
 with pdfium.PdfDocument(str(edited)) as d:
  with closing(d[0]) as page:
   bitmap=page.render(scale=.5)
   try:bitmap.to_pil().save(root/f'rotated-{angle}-edited.png')
   finally:bitmap.close()
# Empty replacement deletes selected text; invalid identity must fail before output.
delete=json.loads(json.dumps(plan));delete['pages'][0]['elements'][0]['text']=''
deleted,_=save(source,delete,'text-deleted');assert '123,456' not in PdfReader(deleted).pages[0].extract_text()
bad=json.loads(json.dumps(plan));bad['pages'][0]['elements'][0]['sourceText']='wrong source text'
badfile=root/'bad-selection.json';badfile.write_text(json.dumps(bad),encoding='utf-8');run('export',source,root/'bad-selection.pdf','--plan',badfile,ok=False)
print('PASS text selection/replacement: Korean, actual old-text removal, shared/nested forms isolated, untouched text/annotation/bookmark preserved, colored background, selectable result, preview, deletion, four rotations/CropBox/UserUnit, stale selection rejected, source unchanged')
