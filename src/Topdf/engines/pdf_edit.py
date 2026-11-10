"""Snapshot-based PDF overlay editing. Includes PDFium text-object replacement."""
import argparse
import hashlib
import io
import json
import math
import re
import sys
from pathlib import Path
from xml.sax.saxutils import escape
sys.path.insert(0, str(Path(__file__).resolve().parent))

from pypdf import PdfReader, PdfWriter, Transformation
from pypdf.generic import NameObject, DictionaryObject, ArrayObject, DecodedStreamObject
import pypdfium2 as pdfium


def source_hash(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()


def open_source(path):
    if path.stat().st_size > 200_000_000: raise ValueError('편집은 200MB 이하 PDF를 지원합니다.')
    reader = PdfReader(path)
    if reader.is_encrypted: raise ValueError('암호 PDF 편집은 아직 지원하지 않습니다.')
    if not 1 <= len(reader.pages) <= 500: raise ValueError('편집은 1~500쪽 PDF를 지원합니다.')
    root = reader.trailer['/Root']
    form = root.get('/AcroForm')
    if form:
        form = form.get_object()
        if form.get('/Fields') or form.get('/XFA'): raise ValueError('입력 양식·전자서명이 포함된 PDF 편집은 아직 지원하지 않습니다.')
    if root.get('/Perms') or root.get('/Collection'): raise ValueError('전자서명·PDF 포트폴리오 편집은 아직 지원하지 않습니다.')
    names = root.get('/Names')
    if names and names.get_object().get('/EmbeddedFiles'): raise ValueError('첨부 파일이 있는 PDF 편집은 아직 지원하지 않습니다.')
    return reader


def geometry(page, extra_rotation=0):
    box = page.cropbox; media = page.mediabox
    x = max(float(box.left), float(media.left)); y = max(float(box.bottom), float(media.bottom))
    w = min(float(box.right), float(media.right))-x; h = min(float(box.top), float(media.top))-y
    unit = float(page.get('/UserUnit', 1))
    angle = (page.rotation+extra_rotation) % 360
    if angle not in (0, 90, 180, 270) or min(w, h, unit) <= 0: raise ValueError('지원하지 않는 PDF 페이지 크기·회전입니다.')
    width, height = (h*unit, w*unit) if angle in (90, 270) else (w*unit, h*unit)
    matrices = {
        0: (1/unit, 0, 0, 1/unit, x, y),
        90: (0, 1/unit, -1/unit, 0, x+w, y),
        180: (-1/unit, 0, 0, -1/unit, x+w, y+h),
        270: (0, -1/unit, 1/unit, 0, x, y+h),
    }
    return width, height, matrices[angle]


def inspect(path):
    reader = open_source(path)
    return dict(sourceSha256=source_hash(path), pages=[dict(sourceIndex=i, width=geometry(page)[0], height=geometry(page)[1]) for i, page in enumerate(reader.pages)])


def render(path, index, rotation, target, plan_path=None, max_size=1800):
    reader = open_source(path)
    if not 0 <= index < len(reader.pages) or rotation not in (0, 90, 180, 270): raise ValueError('잘못된 페이지 또는 회전입니다.')
    if target.exists(): raise ValueError('새 미리보기 파일을 지정하세요.')
    cleaned = None
    if plan_path:
        plan = json.loads(plan_path.read_text(encoding='utf-8-sig'))
        if plan.get('sourceSha256') != source_hash(path): raise ValueError('원본 PDF가 변경됐습니다.')
        specification = next((p for p in plan['pages'] if p['sourceIndex'] == index), None)
        if specification:
            from pdf_text_edit import cleaned_page
            cleaned = cleaned_page(path, index, specification.get('elements', []), geometry, rotation)
    with pdfium.PdfDocument(cleaned if cleaned else str(path)) as document:
        page = document[0 if cleaned else index]
        scale = min(6 if max_size>1800 else 2, max(160,min(3600,max_size))/max(page.get_size()))
        bitmap = page.render(scale=scale, rotation=rotation)
        if cleaned:
            from pdf_text_edit import groups
            layouts=[{k:v for k,v in g.items() if not k.startswith('_')} for g in groups(page,reader.pages[index],geometry)]
            target.with_suffix('.carets.json').write_text(json.dumps(layouts,ensure_ascii=False),encoding='utf-8')
        try: bitmap.to_pil().save(target, format='PNG')
        finally: bitmap.close(); page.close()


def scalar(value):
    number = float(value)
    if not math.isfinite(number): raise ValueError('잘못된 편집 좌표입니다.')
    return number


def draw_element(canvas, element, width, height, font):
    from reportlab.lib import colors
    from reportlab.lib.styles import ParagraphStyle
    from reportlab.platypus import Paragraph
    from reportlab.lib.utils import ImageReader
    from PIL import Image
    x, y, w, h = [scalar(element[key]) for key in ('x', 'y', 'width', 'height')]
    angle = int(element.get('rotation', 0))
    if angle not in (0, 90, 180, 270) or min(w, h) <= 0: raise ValueError('잘못된 편집 항목 크기입니다.')
    cw, ch = (h, w) if angle in (90, 270) else (w, h)
    cx, cy = x+w/2, y+h/2
    if cx-cw/2 < -.5 or cy-ch/2 < -.5 or cx+cw/2 > width+.5 or cy+ch/2 > height+.5: raise ValueError('편집 항목을 페이지 안으로 옮겨 주세요.')
    kind = element['kind']
    if kind=='replace' and element.get('preserveSourceStyle'):return
    canvas.saveState()
    try:
        canvas.translate(cx, height-cy); canvas.rotate(-angle); canvas.translate(-w/2, -h/2)
        if kind in ('line', 'arrow', 'rectangle', 'ellipse', 'highlight', 'underline', 'strikeout', 'freehand'):
            color = element.get('color', '#111111')
            fill = element.get('fill', 'none')
            if not re.fullmatch(r'#[0-9a-fA-F]{6}', color) or (fill != 'none' and not re.fullmatch(r'#[0-9a-fA-F]{6}', fill)): raise ValueError('잘못된 도형 색상')
            stroke = scalar(element.get('strokeWidth', 2))
            if not .1 <= stroke <= 20: raise ValueError('잘못된 선 두께')
            canvas.setStrokeColor(colors.HexColor(color)); canvas.setLineWidth(stroke)
            canvas.setFillColor(colors.HexColor(fill if fill != 'none' else color))
            line_style = element.get('lineStyle') or ('dashed' if element.get('dashed') else 'solid')
            patterns = {'solid': [], 'dotted': [1,2], 'dashed': [4,3], 'dashdot': [8,3,1,3], 'dashdotdot': [8,3,1,3,1,3]}
            if not isinstance(line_style, str) or line_style not in patterns: raise ValueError('잘못된 선종류')
            canvas.setDash([stroke*v for v in patterns[line_style]])
            inset = min(stroke/2, w/2, h/2)
            if kind == 'highlight':
                canvas.setFillColor(colors.HexColor(color)); canvas.setFillAlpha(.3); canvas.rect(inset, inset, max(.1,w-2*inset), max(.1,h-2*inset), fill=1, stroke=0)
            elif kind in ('rectangle','ellipse'):
                if kind == 'rectangle': canvas.rect(inset,inset,max(.1,w-2*inset),max(.1,h-2*inset),fill=int(fill!='none'),stroke=1)
                else: canvas.ellipse(inset,inset,w-inset,h-inset,fill=int(fill!='none'),stroke=1)
            else:
                points = element.get('points', [[0,0],[1,1]])
                if not 1 <= len(points) <= 10000 or any(len(p)!=2 or any(not 0<=scalar(v)<=1 for v in p) for p in points): raise ValueError('잘못된 곡선 좌표')
                points = [(inset+p[0]*(w-2*inset), h-inset-p[1]*(h-2*inset)) for p in points]
                path = canvas.beginPath();path.moveTo(*points[0])
                for point in points[1:]: path.lineTo(*point)
                canvas.drawPath(path)
                if kind == 'arrow' and len(points)>1:
                    a,b=points[-2:]; angle=math.atan2(b[1]-a[1],b[0]-a[0]); length=min(min(w,h)/2,max(6,stroke*4))
                    canvas.setDash()
                    for offset in (-.5,.5): canvas.line(*b,b[0]-length*math.cos(angle+offset),b[1]-length*math.sin(angle+offset))
        elif kind == 'stamp':
            path = Path(element['imagePath'])
            if path.suffix.lower() not in ('.png', '.jpg', '.jpeg') or path.stat().st_size > 20_000_000: raise ValueError('도장은 20MB 이하 PNG/JPG를 사용하세요.')
            with Image.open(path) as image:
                if image.width*image.height > 40_000_000: raise ValueError('도장 이미지 크기 한도 초과')
                rgba = image.convert('RGBA')
                canvas.drawImage(ImageReader(rgba), 0, 0, width=w, height=h, mask='auto')
        elif kind in ('text', 'note', 'replace'):
            text = element.get('text', '')
            if kind == 'replace' and not text.strip(): return
            if not text.strip() or len(text) > 5000: raise ValueError('내용은 1~5000자로 입력하세요.')
            size = scalar(element.get('fontSize', 14))
            if not 6 <= size <= 96: raise ValueError('글자 크기는 6~96으로 지정하세요.')
            color = element.get('color', '#111111')
            if not re.fullmatch(r'#[0-9a-fA-F]{6}', color): raise ValueError('잘못된 글자 색상입니다.')
            pad = 7 if kind == 'note' else 1
            if kind == 'note':
                canvas.setFillColor(colors.HexColor('#FFF3AF')); canvas.setStrokeColor(colors.HexColor('#D6BB55'))
                canvas.roundRect(0, 0, w, h, 4, fill=1, stroke=1)
            selected_font = {'Malgun Gothic': font, 'Gulim': 'TOPDF-Gulim', 'Batang': 'TOPDF-Batang'}.get(element.get('fontFamily', 'Malgun Gothic'), font)
            style = ParagraphStyle('TOPDF', alignment={'left':0,'center':1,'right':2}.get(element.get('alignment','left'),0), fontName=selected_font, fontSize=size, leading=size*1.35, textColor=colors.HexColor(color), wordWrap='CJK')
            paragraph = Paragraph(escape(text).replace('\n', '<br/>'), style)
            _, needed = paragraph.wrap(w-pad*2, max(1, h-pad*2))
            if needed > h-pad*2+.5: raise ValueError('글자가 영역을 벗어납니다. 항목을 키우거나 글자 크기·내용을 줄여 주세요.')
            paragraph.drawOn(canvas, pad, h-pad-needed)
        else: raise ValueError('지원하지 않는 편집 항목입니다.')
    finally: canvas.restoreState()


def export(path, plan_path, target):
    from reportlab.pdfgen.canvas import Canvas
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.ttfonts import TTFont
    import os
    if target.exists() or target.resolve() == path.resolve(): raise ValueError('기존 파일을 덮어쓸 수 없습니다.')
    if plan_path.stat().st_size > 10_000_000: raise ValueError('편집 계획 크기 한도 초과')
    plan = json.loads(plan_path.read_text(encoding='utf-8-sig'))
    reader = open_source(path)
    if plan.get('sourceSha256') != source_hash(path): raise ValueError('편집용 PDF가 변경됐습니다. 다시 열어 주세요.')
    pages = plan.get('pages', [])
    indices = [int(p['sourceIndex']) for p in pages]
    if not indices or len(indices) != len(set(indices)) or any(i < 0 or i >= len(reader.pages) for i in indices): raise ValueError('잘못된 페이지 순서입니다.')
    if sum(len(p.get('elements', [])) for p in pages) > 5000: raise ValueError('편집 항목은 5000개까지 지원합니다.')
    has_text = any(e['kind'] in ('text', 'note', 'replace') for p in pages for e in p.get('elements', []))
    font = 'Helvetica'
    if has_text:
        font_path = Path(os.environ.get('WINDIR', 'C:/Windows'))/'Fonts'/'malgun.ttf'
        if not font_path.is_file(): raise ValueError('한글 글자 저장에 Windows 맑은 고딕 글꼴이 필요합니다.')
        pdfmetrics.registerFont(TTFont('TOPDF-Malgun', str(font_path))); font = 'TOPDF-Malgun'
        for name, filename in [('Gulim','gulim.ttc'),('Batang','batang.ttc')]:
            if any(e.get('fontFamily')==name for p in pages for e in p.get('elements', [])):
                pdfmetrics.registerFont(TTFont('TOPDF-'+name, str(font_path.parent/filename) if (font_path.parent/filename).is_file() else str(font_path), subfontIndex=0))
    writer = PdfWriter(); writer.append(reader, pages=indices, import_outline=True)
    overlays = []  # Keep reader identities/streams alive until pypdf finishes cloning resources.
    for position, specification in enumerate(pages):
        rotation = int(specification.get('rotation', 0))
        if rotation not in (0, 90, 180, 270): raise ValueError('잘못된 페이지 회전입니다.')
        page = writer.pages[position]
        from pdf_text_edit import cleaned_page
        cleaned = cleaned_page(path, specification['sourceIndex'], specification.get('elements', []), geometry, rotation)
        if cleaned:
            replacement_reader = PdfReader(cleaned); overlays.append(replacement_reader)
            replacement_page = replacement_reader.pages[0]
            for key in ('/Contents', '/Resources'):
                if key in replacement_page:
                    cloned=replacement_page[key].clone(writer)
                    if key=='/Contents' and hasattr(cloned,'get_data'):cloned=writer._add_object(cloned)
                    page[NameObject(key)] = cloned
            # PDFium may put page transparency settings in the cleaned page.
            if '/Group' in replacement_page: page[NameObject('/Group')] = replacement_page['/Group'].clone(writer)
        width, height, matrix = geometry(page, rotation)
        if rotation: page.rotate(rotation)
        elements = [e for e in specification.get('elements', []) if not(e['kind']=='replace' and e.get('preserveSourceStyle'))]
        if elements:
            # Real PDFs can share content/resources between distinct pages. Isolate
            # the container before merging so overlays cannot leak into other pages.
            content = page.get_contents()
            if content is not None:
                isolated = DecodedStreamObject(); isolated.set_data(content.get_data())
                page[NameObject('/Contents')] = writer._add_object(isolated.flate_encode())
            resources = DictionaryObject()
            original_resources = page.get('/Resources', {})
            if hasattr(original_resources, 'get_object'): original_resources = original_resources.get_object()
            for key, value in original_resources.items():
                resolved = value.get_object() if hasattr(value, 'get_object') else value
                if isinstance(resolved, DictionaryObject): resources[key] = DictionaryObject(dict(resolved))
                elif isinstance(resolved, ArrayObject): resources[key] = ArrayObject(resolved)
                else: resources[key] = value
            page[NameObject('/Resources')] = resources
            stream = io.BytesIO(); canvas = Canvas(stream, pagesize=(width, height), pageCompression=1)
            for element in elements: draw_element(canvas, element, width, height, font)
            canvas.save(); stream.seek(0)
            overlays.append(PdfReader(stream))
            overlay = overlays[-1].pages[0]
            page.merge_transformed_page(overlay, Transformation(matrix), expand=False)
    if reader.metadata: writer.add_metadata({str(k): str(v) for k, v in reader.metadata.items() if v is not None})
    writer.add_metadata({'/Producer': 'TOPDF PDF Editor'})
    with target.open('xb') as output: writer.write(output)
    check = PdfReader(target)
    if len(check.pages) != len(pages): raise ValueError('저장된 PDF 페이지 수가 다릅니다.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=['inspect', 'render', 'text', 'export'])
    parser.add_argument('source', type=Path); parser.add_argument('output', type=Path)
    parser.add_argument('--page', type=int, default=0); parser.add_argument('--rotation', type=int, default=0)
    parser.add_argument('--plan', type=Path)
    parser.add_argument('--max-size',type=int,default=1800)
    args = parser.parse_args()
    if args.operation == 'inspect': args.output.write_text(json.dumps(inspect(args.source), ensure_ascii=False), encoding='utf-8')
    elif args.operation == 'text':
        open_source(args.source)
        from pdf_text_edit import text_layer
        args.output.write_text(json.dumps(text_layer(args.source, args.page, geometry), ensure_ascii=False), encoding='utf-8')
    elif args.operation == 'render': render(args.source, args.page, args.rotation, args.output, args.plan, args.max_size)
    else: export(args.source, args.plan, args.output)


if __name__ == '__main__':
    try: main()
    except Exception as error: print(str(error), file=sys.stderr); sys.exit(1)
