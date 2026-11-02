"""Local conversion worker. Never modifies the input file."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import traceback
import re
import zipfile


def hwp_to_pdf(source, target):
    import pythoncom
    import win32com.client
    pythoncom.CoInitialize()
    hwp = None
    try:
        # DispatchEx creates an independent automation object; never attach to a user's document.
        hwp = win32com.client.DispatchEx('HWPFrame.HwpObject')
        hwp.XHwpWindows.Item(0).Visible = True
        # File access prompts are left intact. No registry/security policy modifications.
        if not hwp.Open(str(source), '', 'versionwarning:false;'):
            raise RuntimeError('한글 문서 열기에 실패했습니다. 접근 허용·암호·파일 상태를 확인하세요.')
        source_pages = hwp.PageCount
        intermediate = target.parent / 'document.hwpx'
        if not hwp.SaveAs(str(intermediate), 'HWPX', ''):
            raise RuntimeError('한글 인쇄 설정을 확인하기 위한 임시 HWPX 저장 실패.')
        normalized = target.parent / 'single-page.hwpx'
        changed = False
        with zipfile.ZipFile(intermediate) as archive, zipfile.ZipFile(normalized, 'w') as result:
            for item in archive.infolist():
                data = archive.read(item.filename)
                if item.filename == 'settings.xml':
                    pattern = rb'(<config:config-item\s+name="PrintMethod"[^>]*>)([^<]+)(</config:config-item>)'
                    updated = re.sub(pattern, lambda m: m[1] + b'0' + m[3], data)
                    changed = updated != data
                    data = updated
                result.writestr(item, data)
        if changed:
            hwp.Clear(1)
            if not hwp.Open(str(normalized), 'HWPX', ''):
                raise RuntimeError('모아찍기를 해제한 임시 문서 열기에 실패했습니다.')
            if hwp.PageCount != source_pages:
                raise RuntimeError('임시 HWPX 변환 후 원본 쪽수가 달라졌습니다. 수동 검수가 필요합니다.')
        if not hwp.SaveAs(str(target), 'PDF', ''):
            raise RuntimeError('한글 PDF 저장에 실패했습니다.')
        from pypdf import PdfReader
        pdf_pages = len(PdfReader(target).pages)
        if pdf_pages != source_pages:
            raise RuntimeError(f'한글 원본 {source_pages}쪽과 PDF {pdf_pages}쪽이 일치하지 않습니다.')
        target.with_suffix('.conversion.json').write_text(json.dumps({
            'engine': 'Hancom HWP Automation', 'sourcePages': source_pages,
            'pdfPages': pdf_pages, 'nUpPrintingNormalized': changed,
        }, ensure_ascii=False, indent=2), encoding='utf-8')
    finally:
        if hwp is not None:
            try:
                hwp.Clear(1)  # discard changes in this worker's document
                hwp.Quit()
            except Exception:
                pass
        pythoncom.CoUninitialize()


def cad_to_pdf(source, target, oda='', libredwg=''):
    import ezdxf
    from ezdxf.addons.drawing import Frontend, RenderContext
    from ezdxf.addons.drawing.config import Configuration, ColorPolicy, BackgroundPolicy
    from ezdxf.addons.drawing.matplotlib import MatplotlibBackend
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.backends.backend_pdf import PdfPages

    dxf = source
    if source.suffix.lower() == '.dwg':
        staging = target.parent / 'cad-input'
        converted = target.parent / 'cad-dxf'
        staging.mkdir(); converted.mkdir()
        shutil.copy2(source, staging / source.name)
        dxf = converted / (source.stem + '.dxf')
        if libredwg and Path(libredwg).is_file():
            command = [libredwg, '-y', '--as', 'r2018', '-o', str(dxf), str(staging / source.name)]
        elif oda and Path(oda).is_file():
            command = [oda, str(staging), str(converted), 'ACAD2018', 'DXF', '0', '0', '*.dwg']
        else:
            raise RuntimeError('DWG 변환 엔진 파일을 찾을 수 없습니다.')
        process = subprocess.run(command, capture_output=True, timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
        (target.parent/'dwg-converter.log').write_bytes(process.stdout+b'\n'+process.stderr)
        if process.returncode != 0 or not dxf.is_file():
            raise RuntimeError('DWG→DXF 변환에 실패했습니다.')
    doc = ezdxf.readfile(dxf)
    # Some Korean exports advertise a modern DXF version but contain legacy CP949 bytes.
    def text_errors(document):
        return sum(str(e.dxf.get('text', '')).count('\ufffd') for e in document.entitydb.values()
                   if e.dxftype() in ('TEXT', 'MTEXT'))
    if text_errors(doc) and doc.encoding.lower() in ('cp949', 'euc_kr'):
        alternative = ezdxf.readfile(dxf, encoding='cp949')
        if text_errors(alternative) < text_errors(doc):
            doc = alternative
    context = RenderContext(doc)
    from ezdxf.fonts import fonts
    substitutions = []
    fallback = fonts.resolve_font_face('malgun.ttf')
    for style in doc.styles:
        name = style.dxf.name.lower()
        requested = style.dxf.get('font', '')
        resolved = context.fonts.get(name)
        # Missing CAD TTF/SHX often resolves to Arial, which lacks Korean glyphs.
        # Use the installed Korean font and record the substitution explicitly.
        if resolved is None or not requested or resolved.filename.lower() == 'arial.ttf' and requested.lower() != 'arial.ttf':
            context.fonts[name] = fallback
            substitutions.append({'style': style.dxf.name, 'requested': requested, 'used': fallback.filename})
    # Model plus non-empty paper layouts; no silently omitted non-empty layouts.
    layouts = [doc.modelspace()] + [l for l in doc.layouts if l.name != 'Model' and len(l) > 0]
    config = Configuration(color_policy=ColorPolicy.BLACK, background_policy=BackgroundPolicy.WHITE)
    with PdfPages(target) as output:
        for drawing in layouts:
            if len(drawing) == 0:
                continue
            fig = plt.figure(figsize=(16.54, 11.69))  # A3 landscape, fit drawing; not a scale promise.
            ax = fig.add_axes([0.025, 0.025, 0.95, 0.95])
            ax.set_axis_off()
            try:
                Frontend(context, MatplotlibBackend(ax), config=config).draw_layout(drawing, finalize=True)
                output.savefig(fig)
            finally:
                plt.close(fig)
    if not target.is_file():
        raise RuntimeError('출력할 도면 객체가 없습니다.')
    target.with_suffix('.conversion.json').write_text(json.dumps({
        'engine': ('LibreDWG' if libredwg else 'ODA') + ' + ezdxf/matplotlib' if source.suffix.lower() == '.dwg' else 'ezdxf/matplotlib',
        'mode': '2D A3 landscape fit-to-page, black on white',
        'layouts': [l.name for l in layouts if len(l) > 0],
        'limitations': 'Not a scale-certified CAD plot. SHX/CTB/STB, proxy objects, XREF and 3D require review.',
        'replacementCharactersInSourceText': text_errors(doc),
        'fontSubstitutions': substitutions,
    }, ensure_ascii=False, indent=2), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('target', type=Path)
    parser.add_argument('--oda', default='')
    parser.add_argument('--libredwg', default='')
    args = parser.parse_args()
    source, target = args.source.resolve(), args.target.resolve()
    if source == target:
        raise ValueError('원본에 덮어쓸 수 없습니다.')
    target.parent.mkdir(parents=True, exist_ok=True)
    if source.suffix.lower() in ('.hwp', '.hwpx'):
        hwp_to_pdf(source, target)
    elif source.suffix.lower() in ('.dwg', '.dxf'):
        cad_to_pdf(source, target, args.oda, args.libredwg)
    else:
        raise ValueError('지원하지 않는 worker 입력 형식')
    if not target.is_file() or not target.read_bytes().startswith(b'%PDF-'):
        raise RuntimeError('유효한 PDF가 생성되지 않았습니다.')


if __name__ == '__main__':
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
