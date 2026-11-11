"""Check all basic dash arrays in exported PDF and legacy project compatibility."""
from pathlib import Path
import hashlib,json,subprocess,re
import fitz
from pypdf import PdfWriter
root=Path('artifacts/line-types-test').resolve();root.mkdir(parents=True,exist_ok=True)
source=root/'source.pdf';writer=PdfWriter();writer.add_blank_page(width=600,height=800)
with source.open('wb') as f:writer.write(f)
runtime=Path('.local/python-embed/runtime/python.exe').resolve();worker=Path('src/Topdf/engines/pdf_edit.py').resolve()
styles={'solid':[],'dotted':[1,2],'dashed':[4,3],'dashdot':[8,3,1,3],'dashdotdot':[8,3,1,3,1,3]}
elements=[]
for i,key in enumerate([*styles,'legacy','explicit-solid']):
 item=dict(kind='line',x=40,y=40+i*70,width=450,height=20,color='#FF6500',strokeWidth=2,points=[[0,.5],[1,.5]])
 if key=='legacy':item['dashed']=True
 elif key=='explicit-solid':item.update(lineStyle='solid',dashed=True)
 else:item['lineStyle']=key
 elements.append(item)
# Two padded segments meet at (190, 610), including after PDF rendering.
elements.extend([
 dict(kind='line', x=38, y=608, width=154, height=4, strokeWidth=2, points=[[2/154,.5],[152/154,.5]]),
 dict(kind='line', x=188, y=608, width=4, height=84, strokeWidth=2, points=[[.5,2/84],[.5,82/84]]),
])
plan=dict(sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),pages=[dict(sourceIndex=0,elements=elements)])
spec=root/'plan.json';output=root/'lines.pdf'
def export(ok=True):
 spec.write_text(json.dumps(plan),encoding='utf-8')
 if output.exists():output.unlink()
 r=subprocess.run([str(runtime),'-X','utf8',str(worker),'export',str(source),str(output),'--plan',str(spec)],capture_output=True,encoding='utf-8')
 assert (r.returncode==0)==ok,r.stderr
export()
with fitz.open(output) as doc:
 drawings=doc[0].get_drawings();assert len(drawings)==9
 for drawing,key in zip(drawings,[*styles,'dashed','solid']):
  dash=drawing['dashes'];actual=[float(x) for x in re.findall(r'[\d.]+',dash.split(']')[0])]
  assert actual==[2*v for v in styles[key]],(key,dash)
 a,b=drawings[-2:]
 assert (a['items'][0][2]-b['items'][0][1]).norm()<.001
 assert a['lineCap']==b['lineCap']==(1,1,1)
 doc[0].get_pixmap().save(root/'lines.png')
for value in ['invalid',['dashed'],42]:
 plan['pages'][0]['elements'][0]['lineStyle']=value;export(False)
assert hashlib.sha256(source.read_bytes()).hexdigest()==plan['sourceSha256']
print('PASS line types: five PDF dash arrays, legacy dashed, explicit solid precedence, invalid pattern rejection, original preserved')
