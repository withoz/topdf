"""Generate editable text documents, workbooks and page-image presentations."""
from contextlib import closing
from io import BytesIO
import re
from xml.etree import ElementTree as ET
from zipfile import ZIP_DEFLATED, ZIP_STORED, ZipFile

REL = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
PKG = 'http://schemas.openxmlformats.org/package/2006/relationships'
TYPES = 'http://schemas.openxmlformats.org/package/2006/content-types'
SHEET = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
PRESENT = 'http://schemas.openxmlformats.org/presentationml/2006/main'
DRAW = 'http://schemas.openxmlformats.org/drawingml/2006/main'
XDR = 'http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing'
HP = 'http://www.hancom.co.kr/hwpml/2011/paragraph'
HS = 'http://www.hancom.co.kr/hwpml/2011/section'
HH = 'http://www.hancom.co.kr/hwpml/2011/head'
OPF = 'http://www.idpf.org/2007/opf/'
XML_SPACE = '{http://www.w3.org/XML/1998/namespace}space'
INVALID_XML = re.compile(r'[\x00-\x08\x0b\x0c\x0e-\x1f\ufffe\uffff]')

for prefix, uri in [('r', REL), ('p', PRESENT), ('a', DRAW), ('xdr', XDR), ('hp', HP), ('hs', HS), ('hh', HH), ('opf', OPF)]:
    ET.register_namespace(prefix, uri)


def child(parent, ns, tag, **attrs):
    return ET.SubElement(parent, f'{{{ns}}}{tag}', attrs)


def write_xml(archive, name, root):
    archive.writestr(name, ET.tostring(root, encoding='utf-8', xml_declaration=True))


def relationships(archive, name, entries):
    root = ET.Element('Relationships', xmlns=PKG)
    for identifier, kind, target in entries:
        ET.SubElement(root, 'Relationship', Id=identifier, Type=f'{REL}/{kind}', Target=target)
    write_xml(archive, name, root)


def content_types(archive, parts, images=False):
    root = ET.Element('Types', xmlns=TYPES)
    ET.SubElement(root, 'Default', Extension='rels', ContentType='application/vnd.openxmlformats-package.relationships+xml')
    if images:
        ET.SubElement(root, 'Default', Extension='png', ContentType='image/png')
    for name, kind in parts:
        ET.SubElement(root, 'Override', PartName='/' + name, ContentType='application/vnd.openxmlformats-officedocument.' + kind + '+xml')
    write_xml(archive, '[Content_Types].xml', root)


def page_text(page):
    with closing(page.get_textpage()) as text:
        return INVALID_XML.sub('', text.get_text_bounded())


def hwpx(pdf, pages, target):
    head = ET.Element(f'{{{HH}}}head', version='1.5', secCnt='1')
    child(head, HH, 'beginNum', page='1', footnote='1', endnote='1', pic='1', tbl='1', equation='1')
    refs = child(head, HH, 'refList')
    faces = child(refs, HH, 'fontfaces', itemCnt='7')
    languages = ['HANGUL', 'LATIN', 'HANJA', 'JAPANESE', 'OTHER', 'SYMBOL', 'USER']
    for language in languages:
        face = child(faces, HH, 'fontface', lang=language, fontCnt='1')
        child(face, HH, 'font', id='0', face='맑은 고딕', type='TTF', isEmbedded='0')
    chars = child(refs, HH, 'charProperties', itemCnt='1')
    char = child(chars, HH, 'charPr', id='0', height='1000', textColor='#000000', shadeColor='none')
    lang_attrs = dict.fromkeys(['hangul', 'latin', 'hanja', 'japanese', 'other', 'symbol', 'user'])
    for name, value in [('fontRef','0'), ('ratio','100'), ('spacing','0'), ('relSz','100'), ('offset','0')]:
        child(char, HH, name, **dict.fromkeys(lang_attrs, value))
    tabs = child(refs, HH, 'tabProperties', itemCnt='1')
    child(tabs, HH, 'tabPr', id='0', autoTabLeft='0', autoTabRight='0')
    paras = child(refs, HH, 'paraProperties', itemCnt='1')
    para = child(paras, HH, 'paraPr', id='0', tabPrIDRef='0')
    child(para, HH, 'align', horizontal='LEFT', vertical='BASELINE')
    child(para, HH, 'lineSpacing', type='PERCENT', value='160', unit='HWPUNIT')
    styles = child(refs, HH, 'styles', itemCnt='1')
    child(styles, HH, 'style', id='0', type='PARA', name='바탕글', engName='Normal', paraPrIDRef='0',
          charPrIDRef='0', nextStyleIDRef='0', langID='1042', lockForm='0')

    section = ET.Element(f'{{{HS}}}sec')
    identifier = 0
    for index, number in enumerate(pages):
        with closing(pdf[number - 1]) as page:
            value = page_text(page)
        if not value.strip():
            raise ValueError(f'{number}쪽에서 글자를 추출할 수 없습니다. 스캔본에 OCR을 적용한 후 다시 시도해 주세요.')
        for line_index, line in enumerate(value.splitlines()):
            paragraph = child(section, HP, 'p', id=str(identifier), paraPrIDRef='0', styleIDRef='0',
                              pageBreak='1' if index and line_index == 0 else '0', columnBreak='0', merged='0')
            run = child(paragraph, HP, 'run', charPrIDRef='0')
            if identifier == 0:
                props = child(run, HP, 'secPr', textDirection='HORIZONTAL', spaceColumns='0', tabStop='8000')
                paper = child(props, HP, 'pagePr', landscape='WIDELY', width='59528', height='84188', gutterType='LEFT_ONLY')
                child(paper, HP, 'margin', header='4252', footer='4252', gutter='0', left='8504', right='8504', top='5669', bottom='4252')
                control = child(run, HP, 'ctrl')
                child(control, HP, 'colPr', type='NEWSPAPER', layout='LEFT', colCount='1', sameSz='1', sameGap='0')
                run = child(paragraph, HP, 'run', charPrIDRef='0')
            child(run, HP, 't').text = line
            identifier += 1

    package = ET.Element(f'{{{OPF}}}package', version='', id='', **{'unique-identifier': ''})
    meta = child(package, OPF, 'metadata')
    child(meta, OPF, 'title').text = target.stem
    child(meta, OPF, 'language').text = 'ko'
    manifest = child(package, OPF, 'manifest')
    spine = child(package, OPF, 'spine')
    with ZipFile(target, 'w', ZIP_DEFLATED) as archive:
        archive.writestr('mimetype', 'application/hwp+zip', compress_type=ZIP_STORED)
        for identifier, root in [('header', head), ('section0', section)]:
            path = f'Contents/{identifier}.xml'
            child(manifest, OPF, 'item', id=identifier, href=path, **{'media-type': 'application/xml'})
            child(spine, OPF, 'itemref', idref=identifier, linear='yes')
            write_xml(archive, path, root)
        write_xml(archive, 'Contents/content.hpf', package)
        archive.writestr('META-INF/container.xml', '<ocf:container xmlns:ocf="urn:oasis:names:tc:opendocument:xmlns:container"><ocf:rootfiles><ocf:rootfile full-path="Contents/content.hpf" media-type="application/hwpml-package+xml"/></ocf:rootfiles></ocf:container>')
        archive.writestr('version.xml', '<hv:HCFVersion xmlns:hv="http://www.hancom.co.kr/hwpml/2011/version" tagetApplication="WORDPROCESSOR" major="5" minor="1" micro="0" buildNumber="1" os="1" xmlVersion="1.5" application="TOPDF" appVersion="1"/>')


def positioned_rows(page):
    import pypdfium2.raw as raw

    glyphs = []
    with closing(page.get_textpage()) as text:
        for index in range(text.count_chars()):
            code = raw.FPDFText_GetUnicode(text, index)
            if not 32 <= code <= 0x10ffff or 0xd800 <= code <= 0xdfff:
                continue
            char = chr(code)
            if char.isspace() or INVALID_XML.match(char):
                continue
            left, bottom, right, top = text.get_charbox(index, loose=True)
            if top > bottom:
                glyphs.append(((top+bottom)/2, left, right, top-bottom, char))
    rows = []
    for glyph in sorted(glyphs, key=lambda entry: (-entry[0], entry[1])):
        if not rows or abs(rows[-1][0][0]-glyph[0]) > max(2, min(rows[-1][0][3], glyph[3])*.48):
            rows.append([])
        rows[-1].append(glyph)

    result = []
    for row in rows:
        ordered = sorted(row, key=lambda entry: entry[1])
        first = ordered[0]
        previous = first
        cells = []
        value = first[-1]
        start = first[1]
        for glyph in ordered[1:]:
            gap = glyph[1]-previous[2]
            height = min(glyph[3], previous[3])
            if gap > max(8, height*1.15):
                cells.append((start, value))
                start, value = glyph[1], ''
            elif gap > max(1.5, height*.15):
                value += ' '
            value += glyph[-1]
            previous = glyph
        cells.append((start, value))
        result.append(cells)
    return result


def column_name(index):
    result = ''
    while index:
        index, digit = divmod(index-1, 26)
        result = chr(65+digit) + result
    return result


def xlsx(pdf, pages, target):
    from xlsx_grid import Styles, rebuild

    styles = Styles()
    workbook = ET.Element('workbook', xmlns=SHEET)
    ET.SubElement(ET.SubElement(workbook, 'bookViews'), 'workbookView')
    sheets = ET.SubElement(workbook, 'sheets')
    parts = [('xl/workbook.xml','spreadsheetml.sheet.main'), ('xl/styles.xml','spreadsheetml.styles')]
    entries = [('rIdStyles','styles','styles.xml')]
    with ZipFile(target, 'w', ZIP_DEFLATED) as archive:
        for index, number in enumerate(pages, 1):
            worksheet = ET.Element('worksheet', xmlns=SHEET)
            with closing(pdf[number-1]) as page:
                if not rebuild(page, worksheet, styles, column_name):
                    rows = positioned_rows(page)
                    if not rows:
                        raise ValueError(f'{number}쪽에서 글자를 추출할 수 없습니다. 스캔본에 OCR을 적용한 후 다시 시도해 주세요.')
                    append_extracted_cells(worksheet, rows, number)
            name = f'worksheets/sheet{index}.xml'
            identifier = f'rId{index}'
            ET.SubElement(sheets, 'sheet', {'name':f'Page {number}', 'sheetId':str(index), f'{{{REL}}}id':identifier})
            write_xml(archive, f'xl/{name}', worksheet)
            entries.append((identifier, 'worksheet', name))
            parts.append((f'xl/{name}', 'spreadsheetml.worksheet'))
        write_xml(archive, 'xl/styles.xml', styles.xml(SHEET))
        write_xml(archive, 'xl/workbook.xml', workbook)
        relationships(archive, 'xl/_rels/workbook.xml.rels', entries)
        relationships(archive, '_rels/.rels', [('rId1','officeDocument','xl/workbook.xml')])
        content_types(archive, parts)


def append_extracted_cells(worksheet, rows, number):
    data = ET.SubElement(worksheet, 'sheetData')
    # Use the starts of multi-cell rows to keep gaps for missing cells.
    starts = []
    for x in sorted(x for row in rows if len(row)>1 for x, _ in row):
        if not starts or x-starts[-1]>8:
            starts.append(x)
    for row_index, cells in enumerate(rows, 1):
        row = ET.SubElement(data, 'row', r=str(row_index))
        column = 0
        for x, value in cells:
            matched = 1
            if starts:
                nearest = min(range(len(starts)), key=lambda i: abs(starts[i]-x))
                if len(cells)>1 or abs(starts[nearest]-x)<=8:
                    matched = nearest+1
            column = max(column+1, matched)
            if column > 16384:
                raise ValueError(f'{number}쪽에서 엑셀 열 한도를 초과했습니다.')
            address = column_name(column)+str(row_index)
            if re.fullmatch(r'-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?', value) and sum(c.isdigit() for c in value)<=15:
                ET.SubElement(ET.SubElement(row, 'c', r=address), 'v').text = value
            else:
                cell = ET.SubElement(row, 'c', r=address, t='inlineStr')
                ET.SubElement(ET.SubElement(cell, 'is'), 't', {XML_SPACE:'preserve'}).text = value[:32767]


def slide_tree(root):
    tree = child(child(root, PRESENT, 'cSld'), PRESENT, 'spTree')
    props = child(tree, PRESENT, 'nvGrpSpPr')
    child(props, PRESENT, 'cNvPr', id='1', name='')
    child(props, PRESENT, 'cNvGrpSpPr')
    child(props, PRESENT, 'nvPr')
    child(tree, PRESENT, 'grpSpPr')
    return tree


def xlsx_visual(pdf, pages, target, dpi, render):
    workbook = ET.Element('workbook', xmlns=SHEET)
    sheets = ET.SubElement(workbook, 'sheets')
    parts = [('xl/workbook.xml', 'spreadsheetml.sheet.main')]
    entries = []
    with ZipFile(target, 'w', ZIP_DEFLATED) as archive:
        for index, number in enumerate(pages, 1):
            identifier = f'rId{index}'
            sheet_name = f'Page {number}'
            sheet_path = f'worksheets/sheet{index}.xml'
            drawing_path = f'drawings/drawing{index}.xml'
            image_path = f'media/page-{index}.png'
            with closing(pdf[number - 1]) as page, BytesIO() as data:
                width, height = page.get_size()
                render(page, dpi, data)
                archive.writestr(f'xl/{image_path}', data.getvalue(), compress_type=ZIP_STORED)

            ET.SubElement(sheets, 'sheet', {'name': sheet_name, 'sheetId': str(index), f'{{{REL}}}id': identifier})
            worksheet = ET.Element('worksheet', xmlns=SHEET)
            ET.SubElement(ET.SubElement(worksheet, 'sheetPr'), 'pageSetUpPr', fitToPage='1')
            ET.SubElement(ET.SubElement(worksheet, 'sheetViews'), 'sheetView',
                          workbookViewId='0', showGridLines='0')
            ET.SubElement(worksheet, 'sheetData')
            ET.SubElement(worksheet, 'pageMargins', left='0', right='0', top='0', bottom='0', header='0', footer='0')
            ET.SubElement(worksheet, 'pageSetup', orientation='landscape' if width > height else 'portrait',
                          fitToWidth='1', fitToHeight='1')
            ET.SubElement(worksheet, 'drawing', {f'{{{REL}}}id': 'rId1'})
            write_xml(archive, f'xl/{sheet_path}', worksheet)
            relationships(archive, f'xl/worksheets/_rels/sheet{index}.xml.rels',
                          [('rId1', 'drawing', f'../{drawing_path}')])

            extent = dict(cx=str(round(width * 12700)), cy=str(round(height * 12700)))
            drawing = ET.Element(f'{{{XDR}}}wsDr')
            anchor = child(drawing, XDR, 'oneCellAnchor')
            origin = child(anchor, XDR, 'from')
            for coordinate in ('col', 'colOff', 'row', 'rowOff'):
                child(origin, XDR, coordinate).text = '0'
            child(anchor, XDR, 'ext', **extent)
            picture = child(anchor, XDR, 'pic')
            props = child(picture, XDR, 'nvPicPr')
            child(props, XDR, 'cNvPr', id='1', name=sheet_name)
            child(props, XDR, 'cNvPicPr')
            fill = child(picture, XDR, 'blipFill')
            child(fill, DRAW, 'blip', **{f'{{{REL}}}embed': 'rId1'})
            child(child(fill, DRAW, 'stretch'), DRAW, 'fillRect')
            shape = child(picture, XDR, 'spPr')
            transform = child(shape, DRAW, 'xfrm')
            child(transform, DRAW, 'off', x='0', y='0')
            child(transform, DRAW, 'ext', **extent)
            child(child(shape, DRAW, 'prstGeom', prst='rect'), DRAW, 'avLst')
            child(anchor, XDR, 'clientData')
            write_xml(archive, f'xl/{drawing_path}', drawing)
            relationships(archive, f'xl/drawings/_rels/drawing{index}.xml.rels',
                          [('rId1', 'image', f'../{image_path}')])
            entries.append((identifier, 'worksheet', sheet_path))
            parts.extend([(f'xl/{sheet_path}', 'spreadsheetml.worksheet'),
                          (f'xl/{drawing_path}', 'drawing')])
        write_xml(archive, 'xl/workbook.xml', workbook)
        relationships(archive, 'xl/_rels/workbook.xml.rels', entries)
        relationships(archive, '_rels/.rels', [('rId1', 'officeDocument', 'xl/workbook.xml')])
        content_types(archive, parts, images=True)


def pptx(pdf, pages, target, dpi, render):
    with closing(pdf[pages[0]-1]) as page:
        width, height = page.get_size()
    slide_scale = min(12700, 51206400/max(width,height))
    sw, sh = max(914400, round(width*slide_scale)), max(914400, round(height*slide_scale))
    root = ET.Element(f'{{{PRESENT}}}presentation')
    master_list = child(root, PRESENT, 'sldMasterIdLst')
    child(master_list, PRESENT, 'sldMasterId', id='2147483648', **{f'{{{REL}}}id':'rIdMaster'})
    slides = child(root, PRESENT, 'sldIdLst')
    child(root, PRESENT, 'sldSz', cx=str(sw), cy=str(sh))
    child(root, PRESENT, 'notesSz', cx='6858000', cy='9144000')
    entries = [('rIdMaster','slideMaster','slideMasters/slideMaster1.xml')]
    parts = [('ppt/presentation.xml','presentationml.presentation.main'),
             ('ppt/slideMasters/slideMaster1.xml','presentationml.slideMaster'),
             ('ppt/slideLayouts/slideLayout1.xml','presentationml.slideLayout')]
    master = ET.Element(f'{{{PRESENT}}}sldMaster')
    slide_tree(master)
    child(master, PRESENT, 'clrMap', bg1='lt1', tx1='dk1', bg2='lt2', tx2='dk2', accent1='accent1',
          accent2='accent2', accent3='accent3', accent4='accent4', accent5='accent5', accent6='accent6',
          hlink='hlink', folHlink='folHlink')
    child(child(master, PRESENT, 'sldLayoutIdLst'), PRESENT, 'sldLayoutId', id='2147483649', **{f'{{{REL}}}id':'rId1'})
    layout = ET.Element(f'{{{PRESENT}}}sldLayout', type='blank', preserve='1')
    slide_tree(layout)
    child(child(layout, PRESENT, 'clrMapOvr'), DRAW, 'masterClrMapping')
    with ZipFile(target, 'w', ZIP_DEFLATED) as archive:
        write_xml(archive, 'ppt/slideMasters/slideMaster1.xml', master)
        write_xml(archive, 'ppt/slideLayouts/slideLayout1.xml', layout)
        relationships(archive, 'ppt/slideMasters/_rels/slideMaster1.xml.rels', [('rId1','slideLayout','../slideLayouts/slideLayout1.xml')])
        relationships(archive, 'ppt/slideLayouts/_rels/slideLayout1.xml.rels', [('rId1','slideMaster','../slideMasters/slideMaster1.xml')])
        for index, number in enumerate(pages, 1):
            slide = ET.Element(f'{{{PRESENT}}}sld')
            tree = slide_tree(slide)
            with closing(pdf[number-1]) as page, BytesIO() as data:
                width, height = page.get_size()
                fit = min(sw/width, sh/height)
                cx, cy = round(width*fit), round(height*fit)
                render(page, dpi, data)
                archive.writestr(f'ppt/media/page-{index}.png', data.getvalue(), compress_type=ZIP_STORED)
            picture = child(tree, PRESENT, 'pic')
            props = child(picture, PRESENT, 'nvPicPr')
            child(props, PRESENT, 'cNvPr', id='2', name=f'PDF page {number}')
            child(props, PRESENT, 'cNvPicPr')
            child(props, PRESENT, 'nvPr')
            fill = child(picture, PRESENT, 'blipFill')
            child(fill, DRAW, 'blip', **{f'{{{REL}}}embed':'rIdImage'})
            child(child(fill, DRAW, 'stretch'), DRAW, 'fillRect')
            shape = child(picture, PRESENT, 'spPr')
            transform = child(shape, DRAW, 'xfrm')
            child(transform, DRAW, 'off', x=str((sw-cx)//2), y=str((sh-cy)//2))
            child(transform, DRAW, 'ext', cx=str(cx), cy=str(cy))
            child(child(shape, DRAW, 'prstGeom', prst='rect'), DRAW, 'avLst')
            child(child(slide, PRESENT, 'clrMapOvr'), DRAW, 'masterClrMapping')
            name = f'slides/slide{index}.xml'
            write_xml(archive, f'ppt/{name}', slide)
            relationships(archive, f'ppt/slides/_rels/slide{index}.xml.rels',
                          [('rIdLayout','slideLayout','../slideLayouts/slideLayout1.xml'),
                           ('rIdImage','image',f'../media/page-{index}.png')])
            identifier = f'rId{index}'
            child(slides, PRESENT, 'sldId', id=str(255+index), **{f'{{{REL}}}id':identifier})
            entries.append((identifier,'slide',name))
            parts.append((f'ppt/{name}','presentationml.slide'))
        write_xml(archive, 'ppt/presentation.xml', root)
        relationships(archive, 'ppt/_rels/presentation.xml.rels', entries)
        relationships(archive, '_rels/.rels', [('rId1','officeDocument','ppt/presentation.xml')])
        content_types(archive, parts, images=True)
