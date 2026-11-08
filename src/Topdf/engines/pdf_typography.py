"""Preserve original PDF fonts, text matrices and glyph spacing during replacement."""
import ctypes, hashlib, io, math, re, os
from pathlib import Path
from collections import Counter
from pypdf import PdfReader,PdfWriter
from pypdf.generic import ContentStream,NameObject,ByteStringObject,FloatObject,NumberObject,DecodedStreamObject,ArrayObject
from pypdf._cmap import get_encoding
import pypdfium2 as pdfium

def resource_name(value):
    value=str(value).lstrip('/')
    try:value=value.encode('latin1').decode('utf8')
    except (UnicodeError,UnicodeEncodeError):pass
    return value

def name(value):
    return re.sub(r'^[A-Z]{6}\+','',resource_name(value))

def glyph(text,index,obj,value):
    m=obj.get_matrix();font=obj.get_font();x=ctypes.c_double();y=ctypes.c_double()
    if not pdfium.raw.FPDFText_GetCharOrigin(text,index,ctypes.byref(x),ctypes.byref(y)):return None
    width=ctypes.c_float();size=obj.get_font_size()
    pdfium.raw.FPDFFont_GetGlyphWidth(font,ord(value),size,ctypes.byref(width))
    fill=[ctypes.c_uint() for _ in range(4)];stroke=[ctypes.c_uint() for _ in range(4)];pen=ctypes.c_float()
    pdfium.raw.FPDFPageObj_GetFillColor(obj,*[ctypes.byref(v) for v in fill]);pdfium.raw.FPDFPageObj_GetStrokeColor(obj,*[ctypes.byref(v) for v in stroke]);pdfium.raw.FPDFPageObj_GetStrokeWidth(obj,ctypes.byref(pen))
    return dict(char=value,x=x.value,y=y.value,a=m.a,b=m.b,c=m.c,d=m.d,size=size,font=font.get_base_name(),width=max(0,width.value),fill=[v.value for v in fill],stroke=[v.value for v in stroke],pen=pen.value,mode=pdfium.raw.FPDFTextObj_GetTextRenderMode(obj))

def reverse_font(font):
    encoding,mapping=get_encoding(font);reverse={}
    for code,unicode in mapping.items():
        if not isinstance(code,str) or not isinstance(unicode,str) or len(unicode)!=1:continue
        try:reverse.setdefault(unicode,code.encode(encoding if isinstance(encoding,str) else 'charmap'))
        except UnicodeError:pass
    if isinstance(encoding,dict):
        for code,unicode in encoding.items():
            if isinstance(code,int) and 0<=code<256 and len(unicode)==1:reverse.setdefault(unicode,bytes([code]))
    elif not mapping:
        for code in range(256):
            try:reverse.setdefault(bytes([code]).decode(encoding),bytes([code]))
            except UnicodeError:pass
    return reverse

_installed={}
def installed_font(family):
    key=name(family).casefold()
    if key in _installed:return _installed[key]
    from fontTools.ttLib import TTFont
    target=None
    for path in (Path(os.environ.get('WINDIR','C:/Windows'))/'Fonts').glob('*'):
        if path.suffix.lower() not in ('.ttf','.ttc','.otf'):continue
        try:
            with TTFont(path,fontNumber=0,lazy=True) as font:
                names={name(n.toUnicode()).replace(' ','').casefold() for n in font['name'].names if n.nameID in (1,4,6,16)}
                if key.replace(' ','') in names:target=path;break
        except Exception:continue
    _installed[key]=target;return target

def preserved_page(source,groups,by_path,elements,pdf_page,geometry,rotation=0):
    """Remove only selected text-show operations; leave all other PDF operators intact."""
    reader=PdfReader(source);page=reader.pages[0];content=ContentStream(page.get_contents(),reader)
    shows=[]
    for i,(args,op) in enumerate(content.operations):
        if op in (b'Tj',b'TJ',b"'",b'"'):
            values=args[0] if op==b'TJ' else [args[-1]]
            if any(isinstance(v,(bytes,str)) and len(v)>0 for v in values):shows.append(i)
    objects=sorted(by_path,key=lambda p:int(p))
    if len(shows)!=len(objects):raise ValueError('이 PDF의 글자 명령 구조는 원문 모양 보존 편집을 지원하지 않습니다.')
    indices=dict(zip(objects,shows));remove=set();added=[];fallback=[];direct={}
    fonts=page['/Resources']['/Font'];fontcodes={key:reverse_font(value.get_object()) for key,value in fonts.items()}
    width,height,matrix=geometry(pdf_page,rotation);a,b,c,d,e,f=matrix;det=a*d-b*c
    def display(x,y):x,y=x-e,y-f;return ((d*x-c*y)/det,height-(-b*x+a*y)/det)
    def raw(x,y):return (a*x+c*(height-y)+e,b*x+d*(height-y)+f)
    def rotate(x,y,angle):angle=math.radians(angle);return x*math.cos(angle)-y*math.sin(angle),x*math.sin(angle)+y*math.cos(angle)
    ctm=(1.,0.,0.,1.,0.,0.);ctms={};fontbyop={};currentfont=None;matstack=[];showset=set(shows)
    for opindex,(args,op) in enumerate(content.operations):
        if op==b'q':matstack.append((ctm,currentfont))
        elif op==b'Q' and matstack:ctm,currentfont=matstack.pop()
        elif op==b'cm':ctm=compose(ctm,[float(v) for v in args])
        if op==b'Tf':currentfont=args[0]
        if opindex in showset:ctms[opindex]=ctm;fontbyop[opindex]=currentfont
    expected=Counter()
    for item in elements:
        g=groups.get(item.get('sourceTextId'))
        if g is None or g['text']!=item.get('sourceText'):raise ValueError('원본 글자 선택 정보가 다릅니다. PDF를 다시 열어 주세요.')
        if any(indices[p] in remove for p in g['paths']):raise ValueError('동일한 본문을 두 번 수정할 수 없습니다.')
        remove.update(indices[p] for p in g['paths'])
        if not item.get('preserveSourceStyle'):continue
        text=item.get('text','')
        if len(text)>5000:raise ValueError('본문은 5000자까지 수정할 수 있습니다.')
        expected.update(ch for ch in text if not ch.isspace())
        glyphs=g['_glyphs'];base=glyphs[0]
        # Logical box and baseline use the same center rotation as the WPF editor.
        gx,gy,gw,gh=g['editX'],g['editY'],g['editWidth'],g['editHeight'];pw,ph=geometry(pdf_page)[:2]
        for _ in range(rotation//90):cx,cy=gx+gw/2,gy+gh/2;gx,gy=ph-cy-gw/2,cx-gh/2;pw,ph=ph,pw
        baseline=display(base['x'],base['y']);canonical=(g['rotation']+rotation)%360
        ox,oy=rotate(baseline[0]-gx-gw/2,baseline[1]-gy-gh/2,-canonical)
        local=(ox+gw/2,oy+gh/2)
        ew,eh=float(item['width']),float(item['height']);angle=int(item.get('rotation',canonical))
        offset=rotate(local[0]-ew/2,local[1]-eh/2,angle)
        bx,by=float(item['x'])+ew/2+offset[0],float(item['y'])+eh/2+offset[1]
        factor=float(item.get('fontSize',g['fontSize']))/g['fontSize']
        # Same-length changes can retain every original paint operator, including CTM, stroke and kerning.
        if len(text)==len(glyphs) and abs(factor-1)<.0001 and angle==canonical and abs(bx-baseline[0])+abs(by-baseline[1])<.01 and item.get('color',g['color'])==g['color']:
            replacements={};valid=True
            for old,ch in zip(glyphs,text):
                key=old.get('path')
                if key is None:
                    if not ch.isspace():valid=False;break
                    continue
                resource=fontbyop.get(indices[key]);code=fontcodes.get(resource,{}).get(ch)
                if code is None:valid=False;break
                replacements[key]=replacements.get(key,b'')+code
            pending={}
            if valid:
                for key,data in replacements.items():
                    opindex=indices[key];args,op=content.operations[opindex];args=args.copy()
                    if op==b'TJ':
                        array=ArrayObject();offset=0
                        for value in args[0]:
                            if isinstance(value,(str,bytes)):
                                rawbytes=value.original_bytes if hasattr(value,'original_bytes') else bytes(value);array.append(ByteStringObject(data[offset:offset+len(rawbytes)]));offset+=len(rawbytes)
                            else:array.append(value)
                        if offset!=len(data):valid=False;break
                        args[0]=array
                    else:
                        oldbytes=args[-1].original_bytes if hasattr(args[-1],'original_bytes') else bytes(args[-1])
                        if len(oldbytes)!=len(data):valid=False;break
                        args[-1]=ByteStringObject(data)
                    pending[opindex]=(args,op)
            if valid:direct.update(pending);continue
        cursor=0.0;line=0;source_index=0
        for ch in text:
            if ch=='\n':cursor=0;line+=1;continue
            old=glyphs[min(source_index,len(glyphs)-1)];source_index+=1
            font_key=fontbyop.get(indices[old.get('path') or base['path']])
            if font_key not in fonts:raise ValueError('원본 글꼴 리소스를 찾을 수 없습니다.')
            font=fonts[font_key].get_object();codes=fontcodes[font_key];code=codes.get(ch)
            old_width=math.hypot(old['a'],old['b'])*old['width']
            old_advance=old.get('advance',old_width)
            raw_width=old['width'];full_font=None
            if ch.isspace():advance=old_advance if old['char'].isspace() else max(g['fontSize']*.25,old_width*.5)
            elif code is None:
                full_font=installed_font(old['font'])
                if full_font is None:raise ValueError('원본 글꼴에 없는 글자입니다. 같은 글꼴을 설치해야 모양을 유지할 수 있습니다: '+ch)
                from reportlab.pdfbase import pdfmetrics
                from reportlab.pdfbase.ttfonts import TTFont
                alias='TOPDFOriginal'+hashlib.sha256(str(full_font).encode()).hexdigest()[:12]
                if alias not in pdfmetrics.getRegisteredFontNames():pdfmetrics.registerFont(TTFont(alias,str(full_font),subfontIndex=0))
                if ord(ch) not in pdfmetrics.getFont(alias).face.charToGlyph:raise ValueError('같은 글꼴에서 지원하지 않는 글자입니다: '+ch)
                raw_width=pdfmetrics.stringWidth(ch,alias,old['size']);advance=math.hypot(old['a'],old['b'])*raw_width+(old_advance-old_width)
            else:
                # PDF widths are authoritative, including subset CID widths.
                raw_width=font_width(font,code,old['size'])
                advance=math.hypot(old['a'],old['b'])*raw_width+(old_advance-old_width)
            dx,dy=rotate(cursor,line*g['fontSize']*1.35*factor,angle);tx,ty=raw(bx+dx,by+dy)
            if not ch.isspace():
                delta=angle-canonical
                origin=display(old['x'],old['y']);vx=display(old['x']+old['a'],old['y']+old['b']);vy=display(old['x']+old['c'],old['y']+old['d'])
                ax,ay=rotate(vx[0]-origin[0],vx[1]-origin[1],delta);cx,cy=rotate(vy[0]-origin[0],vy[1]-origin[1],delta)
                rx,ry=raw(bx+ax,by+ay);zx,zy=raw(bx,by);sx,sy=raw(bx+cx,by+cy)
                tm=[rx-zx,ry-zy,sx-zx,sy-zy,tx,ty];graphics=ctms[indices[old.get('path') or base['path']]];tm=compose(inverse(graphics),tm);size=old['size']*factor
                color=item.get('color',g['color']);rgb=[int(color[i:i+2],16)/255 for i in (1,3,5)] if color!=g['color'] else [v/255 for v in old['fill'][:3]]
                if full_font:fallback.append((alias,ch,tm,size,rgb,old,graphics))
                else:added.extend([([],b'q'),([FloatObject(v) for v in graphics],b'cm'),([FloatObject(v) for v in rgb],b'rg'),([FloatObject(v/255) for v in old['stroke'][:3]],b'RG'),([FloatObject(old['pen'])],b'w'),([],b'BT'),([NameObject(font_key),FloatObject(size)],b'Tf'),([NumberObject(old['mode'])],b'Tr'),([FloatObject(v) for v in tm],b'Tm'),([ByteStringObject(code)],b'Tj'),([],b'ET'),([],b'Q')])
            cursor+=max(0,advance)*factor
            if cursor>ew+2 or (line+1)*g['fontSize']*factor>eh+2:raise ValueError('같은 글자 크기를 유지하려면 글자 영역을 넓히거나 내용을 줄여 주세요.')
    # Numeric TJ advances preserve the following text position without retaining deleted characters.
    state=dict(font=None,size=1.0,char=0.0,word=0.0);stack=[];operations=[]
    for i,(args,op) in enumerate(content.operations):
        if op==b'q':stack.append(state.copy())
        elif op==b'Q' and stack:state=stack.pop()
        elif op==b'Tf':state['font']=fonts[args[0]].get_object();state['size']=float(args[1])
        elif op==b'Tc':state['char']=float(args[0])
        elif op==b'Tw':state['word']=float(args[0])
        elif op==b'"':state['word']=float(args[0]);state['char']=float(args[1])
        if i in direct:operations.append(direct[i]);continue
        if i not in remove:operations.append((args,op));continue
        if op==b"'":operations.append(([],b'T*'))
        elif op==b'"':operations.extend([([args[0]],b'Tw'),([args[1]],b'Tc'),([],b'T*')])
        font=state['font'];size=state['size'];units=0.0
        values=args[0] if op==b'TJ' else [args[-1]]
        for value in values:
            if isinstance(value,(str,bytes)):
                data=value.original_bytes if hasattr(value,'original_bytes') else bytes(value)
                step=2 if '/DescendantFonts' in font else 1
                if len(data)%step:raise ValueError('원본 글자 인코딩을 보존할 수 없습니다.')
                for start in range(0,len(data),step):
                    code=data[start:start+step];units+=font_width(font,code,size)/size*1000+state['char']/size*1000
                    if code==b' ':units+=state['word']/size*1000
            else:units-=float(value)
        operations.append(([ArrayObject([FloatObject(-units)])],b'TJ'))
    content.operations=[([],b'q')]+operations+[([],b'Q')]+added;page.replace_contents(content)
    if fallback:
        from reportlab.pdfgen.canvas import Canvas
        from reportlab.lib.colors import Color
        stream=io.BytesIO();canvas=Canvas(stream,pagesize=(float(page.mediabox.width),float(page.mediabox.height)))
        for alias,ch,tm,size,rgb,old,graphics in fallback:
            canvas.saveState();canvas.transform(*graphics);canvas.setFillColor(Color(*rgb));canvas.setStrokeColor(Color(*[v/255 for v in old['stroke'][:3]]));canvas.setLineWidth(old['pen'])
            obj=canvas.beginText();obj.setTextTransform(*tm);obj.setFont(alias,size);obj.setTextRenderMode(old['mode']);obj.textOut(ch);canvas.drawText(obj);canvas.restoreState()
        canvas.save();stream.seek(0);page.merge_page(PdfReader(stream).pages[0])
    writer=PdfWriter();cloned=writer.add_page(page);stream=DecodedStreamObject();stream.set_data(cloned.get_contents().get_data());cloned[NameObject("/Contents")]=writer._add_object(stream.flate_encode());result=io.BytesIO();writer.write(result);result.seek(0);return result

def font_width(font,code,size):
    cid=int.from_bytes(code,'big')
    if '/DescendantFonts' in font:
        desc=font['/DescendantFonts'][0].get_object();values=desc.get('/W',[]);i=0
        while i<len(values):
            first=int(values[i]);next=values[i+1];i+=2
            if isinstance(next,list):
                if first<=cid<first+len(next):return float(next[cid-first])/1000*size
            else:
                width=values[i];i+=1
                if first<=cid<=int(next):return float(width)/1000*size
        return float(desc.get('/DW',1000))/1000*size
    widths=font.get('/Widths');first=int(font.get('/FirstChar',0))
    if widths and first<=cid<first+len(widths):return float(widths[cid-first])/1000*size
    from reportlab.pdfbase.pdfmetrics import stringWidth
    try:return stringWidth(code.decode('cp1252'),name(font.get('/BaseFont','Helvetica')),size)
    except Exception:return size*.5


def compose(left,right):
    a,b,c,d,e,f=left;g,h,i,j,k,l=right
    return (a*g+c*h,b*g+d*h,a*i+c*j,b*i+d*j,a*k+c*l+e,b*k+d*l+f)

def inverse(matrix):
    a,b,c,d,e,f=matrix;det=a*d-b*c
    if abs(det)<1e-12:raise ValueError('원본 글자 변환 행렬이 유효하지 않습니다.')
    return (d/det,-b/det,-c/det,a/det,(c*f-d*e)/det,(b*e-a*f)/det)
