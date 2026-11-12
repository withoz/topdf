"""Image import and PDF export for the TOPDF editor."""
import argparse
from contextlib import closing
import io
import math
import re
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile
from xml.etree import ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parent))

MAX_PIXELS = 40_000_000
W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
WP = 'http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing'
A = 'http://schemas.openxmlformats.org/drawingml/2006/main'
PIC = 'http://schemas.openxmlformats.org/drawingml/2006/picture'
for prefix, uri in [('w', W), ('r', R), ('wp', WP), ('a', A), ('pic', PIC)]:
    ET.register_namespace(prefix, uri)


def node(parent, ns, tag, **attributes):
    return ET.SubElement(parent, f'{{{ns}}}{tag}', attributes)


def image_to_pdf(source, target):
    from PIL import Image, ImageOps
    from reportlab.lib.utils import ImageReader
    from reportlab.pdfgen import canvas

    if Path(source).stat().st_size > 80 * 1024**2:
        raise ValueError('이미지 파일은 80 MB 이하여야 합니다.')
    with Image.open(source, formats=['PNG', 'JPEG']) as original:
        if original.width * original.height > MAX_PIXELS:
            raise ValueError('이미지는 4,000만 픽셀 이하여야 합니다.')
        xdpi, ydpi = original.info.get('dpi', (96, 96))
        if original.getexif().get(274) in (5, 6, 7, 8):
            xdpi, ydpi = ydpi, xdpi
        with ImageOps.exif_transpose(original) as oriented:
            width = oriented.width * 72 / min(1200, max(24, xdpi))
            height = oriented.height * 72 / min(1200, max(24, ydpi))
            scale = min(1, 14400 / max(width, height))
            width, height = width * scale, height * scale
            pdf = canvas.Canvas(target, pagesize=(width, height))
            mode = 'RGBA' if 'A' in oriented.getbands() or 'transparency' in oriented.info else 'RGB'
            with oriented.convert(mode) as image:
                pdf.drawImage(ImageReader(image), 0, 0, width, height, mask='auto')
                pdf.save()


def render(page, dpi, output, jpeg=False):
    width, height = page.get_size()
    scale = dpi / 72
    if math.ceil(width * scale) * math.ceil(height * scale) > MAX_PIXELS:
        raise ValueError('내보낼 이미지가 4,000만 픽셀을 넘습니다. 해상도를 낮춰 주세요.')
    bitmap = page.render(scale=scale, draw_annots=True)
    try:
        image = bitmap.to_pil()
        if jpeg:
            with image.convert('RGB') as rgb:
                rgb.save(output, format='JPEG', quality=95, subsampling=0, dpi=(dpi, dpi))
        else:
            image.save(output, format='PNG', dpi=(dpi, dpi))
    finally:
        bitmap.close()


def append_text(body, value):
    for line in value.splitlines() or ['']:
        paragraph = node(body, W, 'p')
        run = node(paragraph, W, 'r')
        element = node(run, W, 't', **{'{http://www.w3.org/XML/1998/namespace}space': 'preserve'})
        element.text = re.sub(r'[\x00-\x08\x0b\x0c\x0e-\x1f\ufffe\uffff]', '', line)


def word_section(parent, size):
    section = node(parent, W, 'sectPr')
    if size:
        width, height = size
        node(section, W, 'type', **{f'{{{W}}}val': 'nextPage'})
        node(section, W, 'pgSz', **{f'{{{W}}}w': str(width), f'{{{W}}}h': str(height),
                                   f'{{{W}}}orient': 'landscape' if width > height else 'portrait'})
    else:
        node(section, W, 'pgSz', **{f'{{{W}}}w': '11906', f'{{{W}}}h': '16838'})
    node(section, W, 'pgMar', **{f'{{{W}}}{edge}': '0' if size else '720'
                                for edge in ('top', 'bottom', 'left', 'right', 'header', 'footer', 'gutter')})


def append_image(body, page, index, size=None, section_break=False):
    width, height = page.get_size()
    fit = (min(size[0] / 20 / width, (size[1] / 20 - 2) / height)
           if size else min(500 / width, 690 / height))
    extent = dict(cx=str(round(width * fit * 12700)), cy=str(round(height * fit * 12700)))
    paragraph = node(body, W, 'p')
    props = node(paragraph, W, 'pPr')
    if size:
        node(props, W, 'spacing', **{f'{{{W}}}before': '0', f'{{{W}}}after': '0',
                                     f'{{{W}}}line': str(round(height * fit * 20)),
                                     f'{{{W}}}lineRule': 'exact'})
    node(props, W, 'jc', **{f'{{{W}}}val': 'center'})
    if section_break:
        word_section(props, size)
    run = node(paragraph, W, 'r')
    inline = node(node(run, W, 'drawing'), WP, 'inline')
    node(inline, WP, 'extent', **extent)
    name = f'Page {index}'
    node(inline, WP, 'docPr', id=str(index), name=name)
    graphic = node(node(inline, A, 'graphic'), A, 'graphicData', uri=PIC)
    picture = node(graphic, PIC, 'pic')
    props = node(picture, PIC, 'nvPicPr')
    node(props, PIC, 'cNvPr', id=str(index), name=name)
    node(props, PIC, 'cNvPicPr')
    fill = node(picture, PIC, 'blipFill')
    node(fill, A, 'blip', **{f'{{{R}}}embed': f'rId{index}'})
    node(node(fill, A, 'stretch'), A, 'fillRect')
    shape = node(picture, PIC, 'spPr')
    transform = node(shape, A, 'xfrm')
    node(transform, A, 'off', x='0', y='0')
    node(transform, A, 'ext', **extent)
    node(node(shape, A, 'prstGeom', prst='rect'), A, 'avLst')


def docx(pdf, pages, target, visual):
    document = ET.Element(f'{{{W}}}document')
    body = node(document, W, 'body')
    relations = ET.Element('Relationships', xmlns='http://schemas.openxmlformats.org/package/2006/relationships')
    size = None
    with ZipFile(target, 'w', ZIP_DEFLATED) as archive:
        for index, number in enumerate(pages, 1):
            if index > 1 and not visual:
                node(node(node(body, W, 'p'), W, 'r'), W, 'br', **{f'{{{W}}}type': 'page'})
            with closing(pdf[number - 1]) as page:
                value = ''
                if visual:
                    width, height = page.get_size()
                    scale = min(20, 31680 / max(width, height))
                    size = (max(144, round(width * scale)), max(144, round(height * scale)))
                else:
                    with closing(page.get_textpage()) as text_page:
                        value = text_page.get_text_bounded()
                if value.strip():
                    append_text(body, value)
                else:
                    append_image(body, page, index, size, visual and index < len(pages))
                    media = f'media/page-{index}.png'
                    ET.SubElement(relations, 'Relationship', Id=f'rId{index}', Type=f'{R}/image', Target=media)
                    with io.BytesIO() as data:
                        render(page, 150, data)
                        archive.writestr(f'word/{media}', data.getvalue(), compress_type=0)
        word_section(body, size)
        archive.writestr('[Content_Types].xml', '''<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="png" ContentType="image/png"/>
<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>''')
        archive.writestr('_rels/.rels', f'''<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="{R}/officeDocument" Target="word/document.xml"/>
</Relationships>''')
        archive.writestr('word/document.xml', ET.tostring(document, encoding='utf-8'))
        if len(relations):
            archive.writestr('word/_rels/document.xml.rels', ET.tostring(relations, encoding='utf-8'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('format', choices=['image-to-pdf', 'docx', 'png', 'jpg', 'hwpx', 'xlsx', 'pptx', 'dxf'])
    parser.add_argument('source')
    parser.add_argument('target', type=Path)
    parser.add_argument('--pages', default='')
    parser.add_argument('--dpi', type=int, choices=[96, 150, 300], default=150)
    parser.add_argument('--visual', action='store_true')
    parser.add_argument('--cad-scale', type=float, default=1)
    args = parser.parse_args()
    if args.format == 'dxf' and not 1 <= args.cad_scale <= 1000000:
        parser.error('DXF 도면 축척은 1:1부터 1:1000000까지 입력해 주세요.')
    if args.format == 'image-to-pdf':
        image_to_pdf(args.source, str(args.target))
        return
    import pypdfium2 as pdfium

    with pdfium.PdfDocument(args.source) as pdf:
        pages = range(1, len(pdf) + 1)
        if args.pages:
            selected = {}
            for token in args.pages.split(','):
                first, _, last = token.partition('-')
                start, end = int(first), int(last or first)
                if not 1 <= start <= end <= len(pdf):
                    raise ValueError('내보낼 페이지 번호가 문서 범위를 벗어났습니다.')
                selected.update(dict.fromkeys(range(start, end + 1)))
            pages = list(selected)
        if not pages or any(number < 1 or number > len(pdf) for number in pages):
            raise ValueError('내보낼 페이지 번호가 문서 범위를 벗어났습니다.')
        if args.format == 'docx':
            docx(pdf, pages, args.target, args.visual)
            return
        if args.format in ('hwpx', 'xlsx', 'pptx'):
            import office_export
            if args.format == 'pptx':
                office_export.pptx(pdf, pages, args.target, args.dpi, render)
            elif args.format == 'xlsx' and args.visual:
                office_export.xlsx_visual(pdf, pages, args.target, args.dpi, render)
            else:
                exporter = office_export.hwpx if args.format == 'hwpx' else office_export.xlsx
                exporter(pdf, pages, args.target)
            return
        for number in pages:
            output = args.target if len(pages) == 1 else args.target.with_stem(f'{args.target.stem}-{number:04}')
            with closing(pdf[number - 1]) as page:
                if args.format == 'dxf':
                    from cad_export import dxf
                    dxf(page, number, output, args.cad_scale)
                else:
                    render(page, args.dpi, output, jpeg=args.format == 'jpg')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError) as error:
        if len(sys.argv) > 3 and sys.argv[1] == "image-to-pdf":
            Path(sys.argv[3] + ".error.txt").write_text(str(error), encoding="utf-8")
        sys.exit(str(error))
