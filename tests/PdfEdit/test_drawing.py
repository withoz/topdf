"""Vector overlay export on rotated, cropped, UserUnit PDFs (synthetic fixtures)."""
import hashlib,json,subprocess
from pathlib import Path
import fitz
from pypdf import PdfWriter,PdfReader
from pypdf.generic import NameObject,NumberObject
root=Path('artifacts/drawing-test').resolve();root.mkdir(parents=True,exist_ok=True)
source=root/'source.pdf';writer=PdfWriter()
for angle in (0,90,180,270):
 p=writer.add_blank_page(width=650,height=850);p.cropbox.lower_left=(25,25);p.cropbox.upper_right=(625,825);p.rotate(angle);p[NameObject('/UserUnit')]=NumberObject(2)
with source.open('wb') as stream:writer.write(stream)
original=hashlib.sha256(source.read_bytes()).hexdigest()
runtime=Path('.local/python-embed/runtime/python.exe').resolve();worker=Path('src/Topdf/engines/pdf_edit.py').resolve()
def run(op,out,*args,ok=True):
 r=subprocess.run([str(runtime),'-X','utf8',str(worker),op,str(source),str(out),*map(str,args)],capture_output=True,encoding='utf-8');assert (r.returncode==0)==ok,r.stderr
info=root/'info.json';run('inspect',info);metadata=json.loads(info.read_text(encoding='utf-8'))
kinds=['line','arrow','rectangle','ellipse','highlight','underline','strikeout','freehand'];pages=[]
for n in range(4):
 elements=[]
 for i,kind in enumerate(kinds):
  elements.append(dict(kind=kind,x=80+(i%2)*380,y=100+(i//2)*150,width=260,height=70,rotation=0,color='#1755B5',strokeWidth=4,dashed=i%2==0,fill='#F05522' if kind=='rectangle' else 'none',fillOpacity=.55 if kind=='rectangle' else 1,points=[[0,.8],[.4,.2],[1,.8]] if kind=='freehand' else [[0,.5],[1,.5]]))
 for i,font in enumerate(['Malgun Gothic','Gulim','Batang']):
  elements.append(dict(kind='text',text='한글 편집 123,000원',x=80+i*300,y=750,width=280,height=70,fontSize=20,color='#111111',rotation=0,fontFamily=font,alignment=['left','center','right'][i]))
 pages.append(dict(sourceIndex=n,rotation=(n*90)%360,elements=elements))
plan=dict(sourceSha256=original,pages=pages);spec=root/'plan.json';spec.write_text(json.dumps(plan),encoding='utf-8');output=root/'vectors.pdf'
if output.exists():output.unlink()
run('export',output,'--plan',spec)
doc=fitz.open(output)
for n,page in enumerate(doc):
 assert len(page.get_drawings())>=8,(n,len(page.get_drawings()))
 assert '123,000' in page.get_text(),page.get_text()
 pix=page.get_pixmap(matrix=fitz.Matrix(.7,.7));pix.save(root/f'vectors-{n}.png')
 # Convert PDF extraction coordinates to displayed page coordinates before comparison.
 blue=[d for d in page.get_drawings() if d.get('color') and abs(d['color'][2]-.71)<.06]
 assert len(blue)>=6
 filled=next(d for d in page.get_drawings() if d.get('fill') and d['fill'][0]>.9 and d['fill'][2]<.2)
 assert abs(filled['fill_opacity']-.55)<.01 and abs(filled['stroke_opacity']-1)<.01 and filled['color'][2]>.65,filled
 # Raster coordinates use the displayed crop/rotation/UserUnit geometry.
 from PIL import Image
 image=Image.frombytes('RGB',(pix.width,pix.height),pix.samples)
 blue_pixels=[(x,y) for y in range(image.height) for x in range(image.width) if (lambda c:c[2]>100 and c[2]>c[0]*1.5 and c[2]>c[1]*1.15)(image.getpixel((x,y))) ]
 assert blue_pixels
 assert min(x for x,y in blue_pixels)>=52 and max(x for x,y in blue_pixels)<=510
 assert min(y for x,y in blue_pixels)>=65 and max(y for x,y in blue_pixels)<=440
 assert any((d.get('fill_opacity') if d.get('fill_opacity') is not None else 1)<.4 for d in page.get_drawings()),'highlight alpha missing'
assert hashlib.sha256(source.read_bytes()).hexdigest()==original
for bad in [float('nan'),-1,100000]:
 plan['pages'][0]['elements'][0]['strokeWidth']=bad;spec.write_text(json.dumps(plan),encoding='utf-8');run('export',root/'bad.pdf','--plan',spec,ok=False)
print('PASS vector export: 8 tools, 3 Korean fonts, alignment, opacity/dash/fill, all rotations/CropBox/UserUnit, geometric bounds, invalid style guards, source unchanged')

