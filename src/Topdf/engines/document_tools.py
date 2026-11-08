"""Offline PDF inspection and OCR. Inputs are never modified; no network access."""
import argparse
import ctypes as c
import io
import json
import os
import re
import sys
import subprocess
import zipfile
from collections import Counter
from pathlib import Path
from xml.etree import ElementTree as ET

from pypdf import PdfReader, PdfWriter
import pypdfium2 as pdfium


def numeric_tokens(text):
    return {s.replace(',', '').lstrip('0') or '0' for s in re.findall(r'(?<![\d.])\d{1,3}(?:,\d{3})+(?!\d)|(?<![\d.])\d{4,}(?![\d.])', text)}


def source_text(source):
    if source.suffix.lower() == '.hwp':
        engine = Path(__file__).parent/'vendor'/'rhwp'/'rhwp.exe'
        result = subprocess.run([str(engine), 'export-text', str(source.resolve()), '--json', '--max-chars', '2000000'], capture_output=True, timeout=45, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        if result.returncode != 0: raise ValueError('HWP 원본 텍스트 추출 실패')
        data = json.loads(result.stdout)
        if data.get('truncated'): raise ValueError('HWP 원본 문자 대조 한도 초과')
        return ' '.join(p['text'] for p in data['pages']), set(), data.get('pageCount')
    if source.suffix.lower() in ('.docx', '.hwpx', '.xlsx'):
        chunks = []; fonts = set(); size = 0
        with zipfile.ZipFile(source) as archive:
            for entry in archive.infolist():
                if not entry.filename.endswith('.xml'): continue
                if source.suffix.lower() == '.docx' and not entry.filename.startswith('word/'): continue
                if entry.file_size > 8_000_000: raise ValueError('Source XML inspection limit exceeded')
                size += entry.file_size
                if size > 64_000_000: raise ValueError('Source XML inspection limit exceeded')
                root = ET.fromstring(archive.read(entry))
                for node in root.iter():
                    tag = node.tag.split('}')[-1]
                    if tag in ('t', 'v') and node.text: chunks.append(node.text)
                    if tag in ('rFonts', 'fontface', 'font'):
                        for key, value in node.attrib.items():
                            if key.split('}')[-1] in ('ascii', 'eastAsia', 'hAnsi', 'face', 'name'): fonts.add(value)
        return ' '.join(chunks), fonts, None
    if source.suffix.lower() == '.xls':
        import xlrd
        book = xlrd.open_workbook(source, on_demand=True)
        try:
            return ' '.join(str(int(cell.value)) if cell.ctype == 2 and cell.value == int(cell.value) else str(cell.value)
                            for sheet in book.sheets() for row in sheet for cell in row if cell.ctype in (1, 2)), set(), None
        finally: book.release_resources()
    return '', set(), None


def installed_fonts():
    import winreg
    names = {'맑은 고딕', 'Malgun Gothic', '굴림', 'Gulim', '돋움', 'Dotum', '바탕', 'Batang'}
    for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        try:
            with winreg.OpenKey(hive, r'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts') as key:
                i = 0
                while True:
                    try: name, _, _ = winreg.EnumValue(key, i)
                    except OSError: break
                    i += 1
                    names.update(re.sub(r'\s*\([^)]*\)', '', part).strip() for part in name.split('&'))
        except OSError: pass
    return {n.casefold() for n in names}


def analyze(source, pdf):
    if pdf.stat().st_size > 200_000_000: raise ValueError('Inspection limit: PDF must be under 200 MB')
    reader = PdfReader(pdf)
    if reader.is_encrypted: raise ValueError('암호 PDF는 품질 점검할 수 없습니다.')
    if len(reader.pages) > 500: raise ValueError('품질 점검은 500쪽까지 지원합니다.')
    pages = []; issues = []; total = 0
    with pdfium.PdfDocument(str(pdf)) as rendered:
        for index, page in enumerate(reader.pages):
            text = page.extract_text() or ''
            truncated = len(text) > 30000 or total + len(text) > 2_000_000
            text = text[:max(0, min(30000, 2_000_000-total))]; total += len(text)
            raster = rendered[index]
            bitmap = raster.render(scale=min(0.35, 280/max(raster.get_size())))
            image = bitmap.to_pil().convert('L')
            ink = sum(count for value, count in enumerate(image.histogram()) if value < 242)
            blank = ink < max(6, image.width*image.height*.0002) and not text.strip()
            bitmap.close(); raster.close()
            needs_ocr = not blank and len(re.sub(r'\s', '', text)) < 8
            pages.append(dict(number=index+1, text=text, blank=blank, needsOcr=needs_ocr))
            if blank: issues.append(dict(code='blank-page', page=index+1, message=f'{index+1}쪽: 빈 페이지로 추정됩니다.'))
            if needs_ocr: issues.append(dict(code='ocr-needed', page=index+1, message=f'{index+1}쪽: 검색 가능한 글자가 적습니다. OCR을 사용할 수 있습니다.'))
            if '\ufffd' in text: issues.append(dict(code='broken-text', page=index+1, message=f'{index+1}쪽: 깨진 문자 또는 텍스트 매핑 문제를 확인해 주세요.'))
            if truncated: issues.append(dict(code='text-limit', page=index+1, message=f'{index+1}쪽: 검색용 텍스트 일부가 길이 제한으로 제외됐습니다.'))
    try:
        original, fonts, source_pages = source_text(source)
        if source_pages is not None and source_pages != len(pages): issues.append(dict(code='page-count', page=0, message=f'원본 {source_pages}쪽과 PDF {len(pages)}쪽이 다릅니다.'))
        missing = sorted(numeric_tokens(original)-numeric_tokens(' '.join(p['text'] for p in pages)))
        if missing: issues.append(dict(code='amount-suspect', page=0, message='원본 숫자가 PDF에서 확인되지 않습니다(누락 의심): '+', '.join(missing[:20])))
        if fonts and os.name == 'nt':
            absent = sorted(f for f in fonts if f.casefold() not in installed_fonts())
            if absent: issues.append(dict(code='font-substitution', page=0, message='설치 글꼴 목록에서 찾지 못했습니다(대체 가능): '+', '.join(absent[:12])))
    except Exception as ex: issues.append(dict(code='source-check-unavailable', page=0, message='원본 대조를 완료하지 못했습니다: '+str(ex)))
    if source.suffix.lower() in ('.dwg', '.dxf'):
        dxf = source if source.suffix.lower() == '.dxf' else next(iter((pdf.parent/'cad-dxf').glob('*.dxf')), None)
        if dxf:
            try:
                import ezdxf
                drawing = ezdxf.readfile(dxf)
                for block in drawing.blocks:
                    ref = block.block.dxf.get('xref_path', '')
                    if ref:
                        # Do not probe remote paths embedded in untrusted CAD files.
                        path = Path(ref)
                        if path.is_absolute() or ref.startswith(('\\\\', '//')) or not (source.parent/path).is_file():
                            issues.append(dict(code='cad-xref', page=0, message='외부 참조 도면을 확인해 주세요: '+ref))
            except Exception as ex: issues.append(dict(code='cad-check-unavailable', page=0, message='CAD 참조 점검 실패: '+str(ex)))
    return dict(pages=pages, issues=issues, notice='자동 점검은 누락 가능성을 찾는 보조 기능이며 원본과의 완전 일치를 보증하지 않습니다.')


def ocr(pdf, target, engine):
    pdf = pdf.resolve(); target = target.resolve()
    if pdf.resolve() == target.resolve() or target.exists(): raise ValueError('OCR output must be a new file')
    reader = PdfReader(pdf)
    if reader.is_encrypted: raise ValueError('암호 PDF는 OCR할 수 없습니다.')
    if len(reader.pages) > 100: raise ValueError('OCR은 한 번에 100쪽까지 지원합니다.')
    if pdf.stat().st_size > 200_000_000: raise ValueError('OCR input limit: 200 MB')
    engine = engine.resolve(); data = engine/'tessdata'
    dll_dir = os.add_dll_directory(str(engine))
    lept = c.CDLL(str(engine/'leptonica-1.82.0.dll'))
    tess = c.CDLL(str(engine/'tesseract50.dll'))
    def api(lib, name, result, *args):
        fn = getattr(lib, name); fn.restype = result; fn.argtypes = list(args); return fn
    create = api(tess, 'TessBaseAPICreate', c.c_void_p)
    init = api(tess, 'TessBaseAPIInit3', c.c_int, c.c_void_p, c.c_char_p, c.c_char_p)
    destroy = api(tess, 'TessBaseAPIDelete', None, c.c_void_p)
    read = api(lept, 'pixReadMem', c.c_void_p, c.POINTER(c.c_ubyte), c.c_size_t)
    resolution = api(lept, 'pixSetResolution', c.c_int, c.c_void_p, c.c_int, c.c_int)
    pix_destroy = api(lept, 'pixDestroy', None, c.POINTER(c.c_void_p))
    renderer_create = api(tess, 'TessPDFRendererCreate', c.c_void_p, c.c_char_p, c.c_char_p, c.c_int)
    begin = api(tess, 'TessResultRendererBeginDocument', c.c_int, c.c_void_p, c.c_char_p)
    end = api(tess, 'TessResultRendererEndDocument', c.c_int, c.c_void_p)
    renderer_delete = api(tess, 'TessDeleteResultRenderer', None, c.c_void_p)
    process = api(tess, 'TessBaseAPIProcessPage', c.c_int, c.c_void_p, c.c_void_p, c.c_int, c.c_char_p, c.c_char_p, c.c_int, c.c_void_p)
    handle = create(); writer = PdfWriter(); changed = 0
    previous_directory = Path.cwd()
    try:
        # Native CRT file APIs do not reliably accept Unicode Windows paths.
        # Python sets the Unicode working directory; native calls use ASCII relative paths.
        os.chdir(engine)
        if init(handle, b'tessdata', b'kor+eng') != 0: raise RuntimeError('한국어·영어 OCR 언어 모델 초기화 실패')
        os.chdir(target.parent)
        with pdfium.PdfDocument(str(pdf)) as rendered:
            for index, original in enumerate(reader.pages):
                if len(re.sub(r'\s', '', original.extract_text() or '')) >= 8:
                    writer.add_page(original); continue
                page = rendered[index]
                width, height = page.get_size()
                if max(width, height)*300/72 > 12000 or width*height*(300/72)**2 > 40_000_000: page.close(); raise ValueError('OCR 페이지 이미지 크기 한도 초과')
                bitmap = page.render(scale=300/72)
                encoded = io.BytesIO()
                bitmap.to_pil().save(encoded, format='PNG', dpi=(300, 300)); bitmap.close(); page.close()
                image_bytes = encoded.getvalue()
                buffer = (c.c_ubyte * len(image_bytes)).from_buffer_copy(image_bytes)
                pix = c.c_void_p(read(buffer, len(image_bytes)))
                if not pix: raise RuntimeError('OCR 이미지 열기 실패')
                base = target.parent/f'ocr-layer-{index}'
                renderer = renderer_create(base.name.encode('ascii'), b'.', 1)
                try:
                    resolution(pix, 300, 300)
                    if not renderer or not begin(renderer, b'TOPDF OCR'): raise RuntimeError('OCR PDF 초기화 실패')
                    if not process(handle, pix, index, b'page.png', None, 120000, renderer): raise RuntimeError('OCR 처리 실패')
                    if not end(renderer): raise RuntimeError('OCR PDF 저장 실패')
                finally:
                    if renderer: renderer_delete(renderer)
                    pix_destroy(c.byref(pix))
                overlay = PdfReader(base.with_suffix('.pdf')).pages[0]
                overlay.scale_to(float(original.mediabox.width), float(original.mediabox.height))
                original.merge_page(overlay)
                writer.add_page(original); changed += 1
        with target.open('xb') as output: writer.write(output)
    finally:
        destroy(handle); dll_dir.close(); os.chdir(previous_directory)
    return dict(pages=len(reader.pages), processedPages=changed)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=['analyze', 'ocr'])
    parser.add_argument('source', type=Path)
    parser.add_argument('pdf', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--engine', type=Path)
    args = parser.parse_args()
    if args.operation == 'analyze':
        result = analyze(args.source, args.pdf)
        args.output.write_text(json.dumps(result, ensure_ascii=False), encoding='utf-8')
    else:
        result = ocr(args.pdf, args.output, args.engine)
        print(json.dumps(result))


if __name__ == '__main__':
    try: main()
    except Exception as error:
        print(str(error), file=sys.stderr); sys.exit(1)
