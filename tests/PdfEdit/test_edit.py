"""PDF edit integration tests. Synthetic inputs only; never touches user documents."""
import argparse, hashlib, json, subprocess
from pathlib import Path
from pypdf import PdfReader, PdfWriter
from pypdf.generic import NameObject, NumberObject, DictionaryObject, ArrayObject, TextStringObject
from pypdf.annotations import Text
from PIL import Image, ImageDraw, ImageFont
import fitz

parser=argparse.ArgumentParser();parser.add_argument('--python',default='.local/python-embed/runtime/python.exe');args=parser.parse_args()
root=Path('artifacts/pdf-edit-test').resolve();root.mkdir(parents=True,exist_ok=True)
worker=Path('src/Topdf/engines/pdf_edit.py').resolve();runtime=Path(args.python).resolve()
def run(op,source,output,*extra,ok=True):
 r=subprocess.run([str(runtime),"-X","utf8",str(worker),op,str(source),str(output),*map(str,extra)],capture_output=True,encoding='utf-8')
 if ok and r.returncode: raise AssertionError(r.stderr)
 if not ok: assert r.returncode!=0,r.stdout
 return r
source=root/'source.pdf';writer=PdfWriter();base=PdfReader('artifacts/smart-test/scan.pdf')
writer.add_page(base.pages[0]);writer.add_annotation(0,Text(rect=(20,20,42,42),text='Original note'))
writer.add_page(base.pages[0]);page=writer.pages[1];page.cropbox.lower_left=(50,100);page.cropbox.upper_right=(500,700);page.rotate(90);page[NameObject('/UserUnit')]=NumberObject(2)
writer.add_blank_page(width=400,height=400)
for p in writer.pages[:2]:
 p[NameObject('/Resources')]=writer._add_object(p['/Resources'])
writer.add_outline_item('First page',0);writer.add_outline_item('Second page',1);writer.add_outline_item('Removed page',2)
with source.open('wb') as f:writer.write(f)
original=hashlib.sha256(source.read_bytes()).hexdigest()
image=Image.new('RGBA',(240,240),(0,0,0,0));draw=ImageDraw.Draw(image);draw.ellipse((8,8,232,232),outline='#C92127',width=12)
font=ImageFont.truetype('C:/Windows/Fonts/malgun.ttf',55);draw.text((63,79),'승인',font=font,fill='#C92127')
stamp=root/'도장.png';image.save(stamp)
metadata=root/'metadata.json';run('inspect',source,metadata);info=json.loads(metadata.read_text(encoding='utf-8'))
assert len(info['pages'])==3
plan={'sourceSha256':info['sourceSha256'],'pages':[
 {'sourceIndex':1,'rotation':90,'elements':[{'kind':'text','text':'TOPDF_EDIT_TOP\n회전 쪽 테스트','x':40,'y':40,'width':260,'height':75,'fontSize':18,'rotation':0,'color':'#C92127'}]},
 {'sourceIndex':0,'rotation':0,'elements':[
  {'kind':'text','text':'추가 문구 123,000원','x':60,'y':350,'width':260,'height':55,'fontSize':18,'rotation':0,'color':'#1755B5'},
  {'kind':'note','text':'검토 메모\n승인 필요','x':60,'y':460,'width':200,'height':90,'fontSize':16,'rotation':0,'color':'#111111'},
  {'kind':'stamp','imagePath':str(stamp),'x':350,'y':460,'width':95,'height':95,'rotation':0}
 ]}
]}
planfile=root/'plan.json';planfile.write_text(json.dumps(plan,ensure_ascii=False),encoding='utf-8')
target=root/'edited.pdf'
if target.exists():target.unlink()
run('export',source,target,'--plan',planfile)
result=PdfReader(target);assert len(result.pages)==2;assert result.pages[0].rotation==180
text=''.join(p.extract_text() or '' for p in result.pages)
assert '추가 문구' not in result.pages[0].extract_text()
assert 'TOPDF_EDIT_TOP' not in result.pages[1].extract_text()
assert len(result.pages[1]['/Resources'].get('/XObject',{}))>=2
for token in ['TOPDF_EDIT_TOP','회전 쪽 테스트','추가 문구 123,000원','검토 메모','승인 필요']:assert token in text,token
assert any(a.get_object().get('/Contents')=='Original note' for a in result.pages[1]['/Annots'])
for item in result.outline:
 if hasattr(item,'title'):
  expected={'First page':1,'Second page':0}
  assert item.title in expected,item.title
  assert result.get_destination_page_number(item)==expected[item.title],item.title
with fitz.open(target) as document:
 page=document[0];rect=page.search_for('TOPDF_EDIT_TOP')[0]
 # Text coordinates already include UserUnit; apply displayed 180-degree rotation in physical points.
 visual=fitz.Rect(page.rect.width-rect.x1,page.rect.height-rect.y1,page.rect.width-rect.x0,page.rect.height-rect.y0)
 assert abs(visual.x0-41)<8 and abs(visual.y0-40)<20,(visual,page.rect)
 for i,p in enumerate(document):p.get_pixmap(matrix=fitz.Matrix(1,1),alpha=False).save(root/f'edited-page-{i+1}.png')
assert hashlib.sha256(source.read_bytes()).hexdigest()==original
run('export',source,target,'--plan',planfile,ok=False)
for title,changes in [('empty',[]),('duplicate',[plan['pages'][0],plan['pages'][0]])]:
 bad=dict(plan,pages=changes);p=root/f'{title}.json';p.write_text(json.dumps(bad),encoding='utf-8');run('export',source,root/f'{title}.pdf','--plan',p,ok=False)
stale=dict(plan,sourceSha256='0'*64);p=root/'stale.json';p.write_text(json.dumps(stale),encoding='utf-8');run('export',source,root/'stale.pdf','--plan',p,ok=False)
form=PdfWriter();form.add_blank_page(width=400,height=400)
field=form._add_object(DictionaryObject({NameObject('/FT'):NameObject('/Tx'),NameObject('/T'):TextStringObject('field'),NameObject('/V'):TextStringObject('value')}))
form._root_object[NameObject('/AcroForm')]=form._add_object(DictionaryObject({NameObject('/Fields'):ArrayObject([field])}))
with (root/'form.pdf').open('wb') as f:form.write(f)
run('inspect',root/'form.pdf',root/'form.json',ok=False)
print('PASS: Korean text, visible memo, transparent stamp, rotate/crop/UserUnit coordinates, reorder/delete, original annotation/bookmark preservation, source unchanged, overwrite/stale/form guards')
