"""PDFium text-object selection/removal. No raster cover-up and no new runtime."""
from contextlib import closing
from collections import Counter
import ctypes
import hashlib
import io
import math
import pypdfium2 as pdfium
from pypdf import PdfReader, PdfWriter
from pypdf.generic import ContentStream, DictionaryObject, NameObject, FloatObject


def editable_page(path,index):
    """Expand ordinary Form XObjects, retaining their matrix and BBox clipping.

    PDFium's bundled version cannot reliably serialize nested form edits. Work
    on isolated, expanded page content so all removals use its page-object API.
    Transparency groups retain their special compositing semantics: refuse them.
    """
    reader=PdfReader(path);writer=PdfWriter();writer.append(reader,pages=[index],import_outline=False)
    page=writer.pages[0];resources=DictionaryObject();serial=0;op_count=0
    categories=('/Font','/XObject','/ExtGState','/ColorSpace','/Pattern','/Shading','/Properties')
    def expand(stream,local,depth=0):
        nonlocal serial,op_count
        if depth>15:raise ValueError('본문의 중첩 구조가 너무 깊습니다.')
        if hasattr(local,'get_object'):local=local.get_object()
        renames={};serial+=1;prefix=f'/TOPDF{serial}_'
        for category in categories:
            values=local.get(category,{})
            if hasattr(values,'get_object'):values=values.get_object()
            mapping={};renames[category]=mapping
            target=resources.setdefault(NameObject(category),DictionaryObject())
            for name,value in values.items():
                if category=='/XObject' and value.get_object().get('/Subtype')=='/Form':continue
                renamed=NameObject(prefix+str(name)[1:]);mapping[name]=renamed;target[renamed]=value
        result=[]
        for operands,operator in ContentStream(stream,writer).operations:
            op_count+=1
            if op_count>500000:raise ValueError('본문 구조 크기 한도를 초과했습니다.')
            if operator==b'Do':
                obj=local['/XObject'][operands[0]].get_object()
                if obj.get('/Subtype')=='/Form':
                    if obj.get('/Group'):raise ValueError('투명도 그룹 본문은 아직 직접 수정할 수 없습니다.')
                    matrix=obj.get('/Matrix',[1,0,0,1,0,0]);bbox=obj['/BBox']
                    result.extend([([],b'q'),([FloatObject(v) for v in matrix],b'cm'),
                        ([FloatObject(bbox[0]),FloatObject(bbox[1]),FloatObject(bbox[2]-bbox[0]),FloatObject(bbox[3]-bbox[1])],b're'),([],b'W'),([],b'n')])
                    result.extend(expand(obj,obj.get('/Resources',local),depth+1));result.append(([],b'Q'));continue
            operands=operands.copy()
            category={b'Tf':'/Font',b'Do':'/XObject',b'gs':'/ExtGState',b'CS':'/ColorSpace',b'cs':'/ColorSpace',b'sh':'/Shading'}.get(operator)
            if category and operands:operands[0]=renames[category].get(operands[0],operands[0])
            if operator in (b'SCN',b'scn') and operands and isinstance(operands[-1],NameObject):operands[-1]=renames['/Pattern'].get(operands[-1],operands[-1])
            if operator in (b'BDC',b'DP') and len(operands)>1 and isinstance(operands[1],NameObject):operands[1]=renames['/Properties'].get(operands[1],operands[1])
            if operator==b'INLINE IMAGE':
                settings=operands['settings'].copy()
                for key in ('/CS','/ColorSpace'):
                    if key in settings and isinstance(settings[key],NameObject):settings[NameObject(key)]=renames['/ColorSpace'].get(settings[key],settings[key])
                operands['settings']=settings
            result.append((operands,operator))
        return result
    content=ContentStream(None,writer);content.operations=expand(page.get_contents(),page['/Resources'])
    page[NameObject('/Resources')]=resources;page.replace_contents(content)
    output=io.BytesIO();writer.write(output);output.seek(0);return output


def objects(page):
    result = {}
    def walk(form=None, prefix='', depth=0):
        if depth > 15: return
        for i, obj in enumerate(page.get_objects(form=form, max_depth=1, level=depth)):
            path = prefix+str(i)
            if obj.type == pdfium.raw.FPDF_PAGEOBJ_TEXT:
                result[ctypes.cast(obj.raw, ctypes.c_void_p).value] = (path, obj)
            elif obj.type == pdfium.raw.FPDF_PAGEOBJ_FORM:
                walk(obj, path+'/', depth+1)
    walk()
    return result


def groups(page, pdf_page, geometry):
    handles = objects(page); chunks = {}
    with closing(page.get_textpage()) as text:
        if text.count_chars() > 200000: raise ValueError('페이지의 글자가 너무 많습니다.')
        for i in range(text.count_chars()):
            obj = text.get_textobj(i)
            if obj is None: continue
            key = ctypes.cast(obj.raw, ctypes.c_void_p).value
            if key not in handles: continue
            value = text.get_text_range(i, 1)
            box = text.get_charbox(i)
            if not all(math.isfinite(v) for v in box): continue
            record = chunks.setdefault(key, dict(path=handles[key][0], obj=handles[key][1], text='', boxes=[], glyphs=[], size=0, angled=False))
            if value in ('\r','\n'):continue
            record['text'] += value
            from pdf_typography import glyph
            detail=glyph(text,i,handles[key][1],value)
            if detail:detail['path']=record['path'];record['glyphs'].append(detail)
            matrix=handles[key][1].get_matrix()
            record['size'] = handles[key][1].get_font_size()*math.hypot(matrix.c,matrix.d)
            angle=pdfium.raw.FPDFText_GetCharAngle(text,i)
            if min(abs(angle),abs(angle-2*math.pi))>.02:record['angled']=True
            if value.strip() and box[2] > box[0] and box[3] > box[1]: record['boxes'].append(box)
    records = []
    for c in chunks.values():
        if any(g['mode'] not in (0,1,2) for g in c['glyphs']) or c['angled'] or not c['text'].strip() or '\ufffd' in c['text'] or not c['boxes']: continue
        boxes = c.pop('boxes')
        c['box'] = (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))
        l,b,r,t = c['box']
        # Do not guess at vertical/skewed text or objects spanning multiple lines.
        if t-b > max(c['size']*2.3, 40) or r-l < .1: continue
        records.append(c)
    lines = []
    for c in sorted(records, key=lambda c: (-((c['box'][1]+c['box'][3])/2), c['box'][0])):
        l,b,r,t = c['box']; cy=(b+t)/2
        match = next((line for line in lines[-12:] if abs(line['cy']-cy) < max(1.5,min(t-b,line['height'])*.35)), None)
        if match is None:
            match=dict(cy=cy,height=t-b,items=[]); lines.append(match)
        match['items'].append(c)
    runs=[]
    for line in lines:
        current=[]; end=None
        for c in sorted(line['items'], key=lambda c:c['box'][0]):
            if current and c['box'][0]-end > max(8,c['size']*1.4): runs.append(current);current=[]
            current.append(c);end=c['box'][2]
        if current:runs.append(current)
    width,height,matrix=geometry(pdf_page)
    a,b,c,d,e,f=matrix; det=a*d-b*c
    def display(x,y):
        x,y=x-e,y-f
        return ((d*x-c*y)/det, height-(-b*x+a*y)/det)
    unit=float(pdf_page.get('/UserUnit',1)); output=[]
    for run in runs:
        text='';end=None;glyphs=[]
        for chunk in run:
            if end is not None and chunk['box'][0]-end > chunk['size']*.22 and not text.endswith(' ') and not chunk['text'].startswith(' '):
                text+=' '
                if glyphs:
                    previous=glyphs[-1];space=dict(previous);space.update(char=' ',path=None,x=previous['x']+previous['a']*previous['width'],y=previous['y']+previous['b']*previous['width']);glyphs.append(space)
            text+=chunk['text'];glyphs.extend(chunk['glyphs']);end=chunk['box'][2]
        if len(text)>5000:continue
        paths=[c['path'] for c in run]
        l=min(c['box'][0] for c in run); bot=min(c['box'][1] for c in run)
        r=max(c['box'][2] for c in run); top=max(c['box'][3] for c in run)
        corners=[display(x,y) for x in (l,r) for y in (bot,top)]
        x=min(p[0] for p in corners);y=min(p[1] for p in corners)
        w=max(p[0] for p in corners)-x;h=max(p[1] for p in corners)-y
        if x<-.5 or y<-.5 or x+w>width+.5 or y+h>height+.5:continue
        if not glyphs:continue
        size=min(300,max(.1,max(c['size'] for c in run)*unit))
        first=glyphs[0];baseline=display(first['x'],first['y'])
        ascent=size*.86;ew=max((r-first['x'])*unit,size)+2;eh=size*1.35+2
        center=display(first['x']+ew/2/unit,first['y']+(ascent-eh/2)/unit)
        for i,glyph in enumerate(glyphs):
            glyph['advance']=(glyphs[i+1]['x']-glyph['x']) if i+1<len(glyphs) else math.hypot(glyph['a'],glyph['b'])*glyph['width']
        rgba=[ctypes.c_uint() for _ in range(4)]
        ok=pdfium.raw.FPDFPageObj_GetFillColor(run[0]['obj'],*[ctypes.byref(v) for v in rgba])
        color=('#%02X%02X%02X'%tuple(v.value for v in rgba[:3])) if ok else '#111111'
        # Public caret stops come from PDF glyph origins, not substitute UI font metrics.
        carets=[]
        for glyph in glyphs:
            point=display(glyph['x'],glyph['y'])
            carets.extend([list(point)]*(len(glyph['char'].encode('utf-16-le'))//2))
        last=glyphs[-1]
        carets.append(list(display(last['x']+last['a']*last['width'],last['y']+last['b']*last['width'])))
        output.append(dict(id=hashlib.sha256('|'.join(paths).encode()).hexdigest()[:24],text=text,paths=paths,x=x,y=y,width=w,height=h,
                           editX=center[0]-ew/2,editY=center[1]-eh/2,editWidth=ew,editHeight=eh,fontSize=size,color=color,rotation=pdf_page.rotation%360,baselineX=baseline[0],baselineY=baseline[1],fontFamily=__import__('pdf_typography').name(first['font']),caretStops=carets,textStrokeWidth=first['pen']*math.hypot(first['a'],first['b'])*unit,textRenderMode=first['mode'],_glyphs=glyphs))
    return output


def text_layer(path,index,geometry):
    reader=PdfReader(path)
    try: source=editable_page(path,index)
    except ValueError:return []
    with pdfium.PdfDocument(source) as document:
        with closing(document[0]) as page:
            return [{k:v for k,v in g.items() if not k.startswith('_')} for g in groups(page,reader.pages[index],geometry)]


def cleaned_page(path,index,elements,geometry,rotation=0):
    replacements=[e for e in elements if e['kind']=='replace']
    if not replacements:return None
    reader=PdfReader(path);source=editable_page(path,index)
    with pdfium.PdfDocument(source) as document:
        with closing(document[0]) as page:
            with closing(page.get_textpage()) as text:expected=Counter(c for c in text.get_text_range() if not c.isspace())
            available={g['id']:g for g in groups(page,reader.pages[index],geometry)}
            by_path={entry[0]:entry[1] for entry in objects(page).values()}
            for item in replacements:
                group=available.get(item.get('sourceTextId'))
                if group is None or group['text']!=item.get('sourceText'):raise ValueError('원본 글자 선택 정보가 다릅니다. PDF를 다시 열어 주세요.')
                expected.subtract(c for c in group['text'] if not c.isspace())
                if item.get('preserveSourceStyle'):expected.update(c for c in item.get('text','') if not c.isspace())
            from pdf_typography import preserved_page
            source.seek(0);output=preserved_page(source,available,by_path,replacements,reader.pages[index],geometry,rotation)
    with pdfium.PdfDocument(output) as check:
        with closing(check[0]) as page,closing(page.get_textpage()) as text:actual=Counter(c for c in text.get_text_range() if not c.isspace())
    if +expected!=actual:raise ValueError('원문 글꼴의 문자 지원 또는 수정하지 않은 본문 보존 검사를 통과하지 못했습니다.')
    output.seek(0);return output
