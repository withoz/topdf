"""Synthetic original-font/size/position regression, including scaled text and relative Tj."""
from pathlib import Path
import sys,subprocess,json,hashlib
root=Path('artifacts/typography-test').resolve();root.mkdir(parents=True,exist_ok=True)
if '--fixture' in sys.argv:
 from reportlab.pdfgen.canvas import Canvas
 from reportlab.pdfbase import pdfmetrics
 from reportlab.pdfbase.ttfonts import TTFont
 pdfmetrics.registerFont(TTFont('Original','C:/Windows/Fonts/malgun.ttf'))
 c=Canvas(str(root/'source.pdf'),pagesize=(600,800));c.setFillColorRGB(.85,.94,1);c.rect(50,620,480,100,fill=1,stroke=0)
 c.saveState();c.translate(80,650);c.scale(.75,.75);t=c.beginText();t.setFont('Original',22.6666667);t.setCharSpace(-.5);t.setFillColorRGB(0,0,0);t.setStrokeColorRGB(0,0,0);t.setTextRenderMode(2);c.setLineWidth(.2);t.textOut('원문 제목 123,000원');c.drawText(t);c.restoreState()
 c.setFont('Original',14);c.setFillColorRGB(0,0,0);c.drawString(60,550,'수정하지 않는 본문 9876543210');c.showPage();c.setFont('Original',14);c.drawString(60,550,'다른 페이지 보존');c.showPage();c.setFont('Original',14);c.drawString(60,550,'세 번째 페이지');c.save();sys.exit()
import fitz
from PIL import Image,ImageChops
runtime=Path('.local/python-embed/runtime/python.exe').resolve();worker=Path('src/Topdf/engines/pdf_edit.py').resolve()
subprocess.run([str(runtime),'-X','utf8',__file__,'--fixture'],check=True)
def run(op,source,out,*args):
 result=subprocess.run([str(runtime),'-X','utf8',str(worker),op,str(source),str(out),*map(str,args)],capture_output=True,encoding='utf8');assert result.returncode==0,result.stderr
source=root/'source.pdf';original=hashlib.sha256(source.read_bytes()).hexdigest();run('inspect',source,root/'info.json');run('text',source,root/'text.json');info=json.loads((root/'info.json').read_text(encoding='utf8'));groups=json.loads((root/'text.json').read_text(encoding='utf8'));g=next(x for x in groups if '123,000' in x['text']);assert abs(g['fontSize']-17)<.001,g
for n,text in enumerate([g['text'],g['text'].replace('123,000','923,000'),g['text']+' 수정',g['text'].replace('원문','글꼴')]):
 element=dict(kind='replace',text=text,sourceTextId=g['id'],sourceText=g['text'],x=g['editX'],y=g['editY'],width=430,height=g['editHeight'],fontSize=g['fontSize'],color=g['color'],rotation=g['rotation'],preserveSourceStyle=True)
 plan=dict(sourceSha256=info['sourceSha256'],pages=[dict(sourceIndex=0,rotation=0,elements=[element]),dict(sourceIndex=1,rotation=0,elements=[])])
 spec=root/f'plan-{n}.json';spec.write_text(json.dumps(plan),encoding='utf8');out=root/f'result-{n}.pdf'
 if out.exists():out.unlink()
 run('export',source,out,'--plan',spec);doc=fitz.open(out);old=fitz.open(source)
 a=old[0].get_pixmap(matrix=fitz.Matrix(2,2));b=doc[0].get_pixmap(matrix=fitz.Matrix(2,2));imga=Image.frombytes('RGB',(a.width,a.height),a.samples);imgb=Image.frombytes('RGB',(b.width,b.height),b.samples)
 diff=ImageChops.difference(imga,imgb)
 if n==0:assert diff.getbbox() is None,'Round trip changed original pixels: '+str(diff.getbbox())
 assert doc[1].get_text()==old[1].get_text();assert imgb.getpixel((110,130))==imga.getpixel((110,130)),'Background changed'
 spans=[s for block in doc[0].get_text('dict')['blocks'] for line in block.get('lines',[]) for s in line['spans'] if s['bbox'][1]<180]
 assert spans and all(abs(s['size']-17)<.002 for s in spans),spans
 assert any(abs(s['origin'][0]-80)<.01 and abs(s['origin'][1]-150)<.01 for s in spans),spans
 b.save(root/f'result-{n}.png')
 assert hashlib.sha256(source.read_bytes()).hexdigest()==original
print('PASS typography: scaled font 17pt, identical unchanged pixels, original baseline/font/stroke/spacing, same-length and longer edits, original-font fallback, colored background/other page/source preserved')
