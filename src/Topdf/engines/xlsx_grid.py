"""Rebuild editable worksheet cells from a PDF's axis-aligned table geometry."""
from bisect import bisect_right
from collections import defaultdict
from contextlib import closing
from ctypes import c_float, c_int, c_uint
from statistics import median
from xml.etree import ElementTree as ET

import pypdfium2.raw as raw

TOLERANCE = 0.8
SIDES = ('left', 'right', 'top', 'bottom')


def color(getter, *args):
    rgba = [c_uint() for _ in range(4)]
    if not getter(*args, *rgba) or not rgba[3].value:
        return None
    alpha = rgba[3].value / 255
    return 'FF' + ''.join(f'{round(channel.value * alpha + 255 * (1-alpha)):02X}' for channel in rgba[:3])


def subpaths(path, height):
    matrix = path.get_matrix()
    points = []
    for index in range(raw.FPDFPath_CountSegments(path)):
        segment = raw.FPDFPath_GetPathSegment(path, index)
        kind = raw.FPDFPathSegment_GetType(segment)
        if kind == raw.FPDF_SEGMENT_MOVETO:
            if points:
                yield points
            points = []
        x, y = c_float(), c_float()
        raw.FPDFPathSegment_GetPoint(segment, x, y)
        px, py = matrix.on_point(x.value, y.value)
        points.append((px, height-py, kind))
        if raw.FPDFPathSegment_GetClose(segment) and points:
            yield points
            points = []
    if points:
        yield points


def geometry(page, height):
    vertical, horizontal, fills = [], [], []
    for path in page.get_objects(filter=[raw.FPDF_PAGEOBJ_PATH]):
        fill, stroke = c_int(), c_int()
        if not raw.FPDFPath_GetDrawMode(path, fill, stroke):
            continue
        fill_color = color(raw.FPDFPageObj_GetFillColor, path) if fill.value else None
        stroke_color = color(raw.FPDFPageObj_GetStrokeColor, path) if stroke.value else None
        thickness = c_float()
        raw.FPDFPageObj_GetStrokeWidth(path, thickness)
        matrix = path.get_matrix()
        weight = thickness.value * max(abs(matrix.a), abs(matrix.b), abs(matrix.c), abs(matrix.d))
        for points in subpaths(path, height):
            if any(kind == raw.FPDF_SEGMENT_BEZIERTO for _, _, kind in points):
                continue
            xs, ys = sorted({round(x, 2) for x, _, _ in points}), sorted({round(y, 2) for _, y, _ in points})
            if fill_color and len(xs) == len(ys) == 2 and len({(round(x, 2), round(y, 2)) for x,y,_ in points}) == 4:
                x0, x1 = xs
                y0, y1 = ys
                if x1-x0 <= 1.5 and y1-y0 > 2:
                    vertical.append(((x0+x1)/2, y0, y1, x1-x0, fill_color))
                elif y1-y0 <= 1.5 and x1-x0 > 2:
                    horizontal.append(((y0+y1)/2, x0, x1, y1-y0, fill_color))
                else:
                    fills.append((x0,y0,x1,y1,fill_color))
            if stroke_color:
                for (x0,y0,_),(x1,y1,_) in zip(points, points[1:]):
                    if abs(x1-x0) < .1 and abs(y1-y0) > 2:
                        vertical.append((x0,min(y0,y1),max(y0,y1),weight,stroke_color))
                    elif abs(y1-y0) < .1 and abs(x1-x0) > 2:
                        horizontal.append((y0,min(x0,x1),max(x0,x1),weight,stroke_color))
    return vertical, horizontal, fills


def coordinates(values, limit):
    clusters = []
    for value in sorted(v for v in values if TOLERANCE < v < limit-TOLERANCE):
        if not clusters or value-clusters[-1][0] > TOLERANCE:
            clusters.append([])
        clusters[-1].append(value)
    return [0, *(median(group) for group in clusters), limit]


def edge_map(lines, axis, spans):
    result = {}
    for position, start, end, width, shade in lines:
        index = min(range(len(axis)), key=lambda i: abs(axis[i]-position))
        for i in range(max(0,bisect_right(spans,start+TOLERANCE)-1),len(spans)-1):
            middle = (spans[i]+spans[i+1])/2
            if middle > end:
                break
            if middle >= start and width > result.get((index,i), (0,None))[0]:
                result[index,i] = (width,shade)
    return result


def glyphs(page, height):
    with closing(page.get_textpage()) as text:
        for i in range(text.count_chars()):
            code = raw.FPDFText_GetUnicode(text,i)
            if not 32 <= code < 0x110000 or 0xd800 <= code <= 0xdfff or code in (0xfffe,0xffff):
                continue
            left,bottom,right,top = text.get_charbox(i, loose=True)
            if right > left and top > bottom:
                yield (left,right,height-(bottom+top)/2,chr(code),
                       raw.FPDFText_GetFontSize(text,i),color(raw.FPDFText_GetFillColor,text,i))


def text_lines(chars):
    lines = []
    for glyph in sorted(chars, key=lambda g:g[2]):
        if not lines or abs(lines[-1][0][2]-glyph[2]) > 2.2:
            lines.append([])
        lines[-1].append(glyph)
    values = []
    for line in lines:
        value, previous = '', None
        for glyph in sorted(line, key=lambda g:g[0]):
            if previous and glyph[0]-previous[1] > max(1.1,glyph[4]*.19) and not value.endswith(' ') and glyph[3] != ' ':
                value += ' '
            value += glyph[3]
            previous = glyph
        if value.strip():
            values.append(value.strip())
    return '\n'.join(values)


class Styles:
    def __init__(self):
        self.fonts = {(8,'FF000000'):0}
        self.fills = {None:0}
        self.borders = {(None,)*4:0}
        self.formats = {(0,0,0,'left',False):0}

    def add(self, size, ink, fill, edges, align, wrap):
        font = self.fonts.setdefault((size,ink),len(self.fonts))
        background = self.fills.setdefault(fill,len(self.fills)+1) if fill else 0
        border = self.borders.setdefault(edges,len(self.borders))
        return self.formats.setdefault((font,background,border,align,wrap),len(self.formats))

    def xml(self, namespace):
        root = ET.Element('styleSheet',xmlns=namespace)
        fonts = ET.SubElement(root,'fonts',count=str(len(self.fonts)))
        for size,ink in self.fonts:
            font = ET.SubElement(fonts,'font')
            ET.SubElement(font,'sz',val=str(size))
            ET.SubElement(font,'color',rgb=ink)
            ET.SubElement(font,'name',val='Malgun Gothic')
        fills = ET.SubElement(root,'fills',count=str(len(self.fills)+1))
        for pattern in ('none','gray125'):
            ET.SubElement(ET.SubElement(fills,'fill'),'patternFill',patternType=pattern)
        for shade in list(self.fills)[1:]:
            fill = ET.SubElement(ET.SubElement(fills,'fill'),'patternFill',patternType='solid')
            ET.SubElement(fill,'fgColor',rgb=shade)
            ET.SubElement(fill,'bgColor',indexed='64')
        borders = ET.SubElement(root,'borders',count=str(len(self.borders)))
        for edges in self.borders:
            border = ET.SubElement(borders,'border')
            for side,edge in zip(SIDES,edges):
                element = ET.SubElement(border,side)
                if edge:
                    width,shade = edge
                    element.set('style','hair' if width < .25 else 'thin' if width < .85 else 'medium')
                    ET.SubElement(element,'color',rgb=shade)
            ET.SubElement(border,'diagonal')
        ET.SubElement(ET.SubElement(root,'cellStyleXfs',count='1'),'xf',numFmtId='0',fontId='0',fillId='0',borderId='0')
        formats = ET.SubElement(root,'cellXfs',count=str(len(self.formats)))
        for font,fill,border,align,wrap in self.formats:
            xf = ET.SubElement(formats,'xf',numFmtId='0',fontId=str(font),fillId=str(fill),borderId=str(border),
                               xfId='0',applyFont='1',applyFill='1',applyBorder='1',applyAlignment='1')
            ET.SubElement(xf,'alignment',horizontal=align,vertical='center',wrapText=str(int(wrap)),
                          shrinkToFit=str(int(not wrap)))
        ET.SubElement(ET.SubElement(root,'cellStyles',count='1'),'cellStyle',name='Normal',xfId='0',builtinId='0')
        return root


def rebuild(page, sheet, styles, column_name):
    width,height = page.get_size()
    vertical,horizontal,fills = geometry(page,height)
    if len(vertical)<2 or len(horizontal)<2 or page.get_rotation():
        return False
    chars = list(glyphs(page,height))
    if not chars:
        return False
    # Outside ruled areas, give text its own rows so headings and notes keep their position.
    top,bottom = min(line[0] for line in horizontal),max(line[0] for line in horizontal)
    free_text = [g for g in chars if g[2] < top or g[2] > bottom or not any(
        start-TOLERANCE <= g[2] <= end+TOLERANCE for _,start,end,_,_ in vertical)]
    free_lines = []
    for g in sorted(free_text,key=lambda item:item[2]):
        if not free_lines or abs(free_lines[-1][0][2]-g[2])>2.2:
            free_lines.append([])
        free_lines[-1].append(g)
    starts = []
    for line in free_lines:
        previous = None
        for g in sorted((item for item in line if item[3].strip()),key=lambda item:item[0]):
            if previous is None or g[0]-previous[1]>max(22,g[4]*4):
                starts.append((g[0],g[2]))
            previous = g
    xs = coordinates([*(x for x,_ in starts),*(v[0] for v in vertical),*(n for a,_,b,_,_ in fills for n in (a,b)),
                      *(n for _,a,b,_,_ in horizontal for n in (a,b))],width)
    ys = coordinates([*(h[0] for h in horizontal),*(n for _,a,_,b,_ in fills for n in (a,b)),
                      *(n for _,a,b,_,_ in vertical for n in (a,b)),
                      *(n for g in free_text for n in (g[2]-max(2,g[4]/2),g[2]+max(2,g[4]/2)))],height)
    rows,cols = len(ys)-1,len(xs)-1
    if rows*cols>50000 or rows<3 or cols<3:
        return False
    v_edges,h_edges = edge_map(vertical,xs,ys),edge_map(horizontal,ys,xs)
    left_edge = min(line[1] for line in horizontal)
    left_column = min(range(len(xs)),key=lambda i:abs(xs[i]-left_edge))
    text_boundaries = {(min(range(len(xs)),key=lambda i:abs(xs[i]-x)),bisect_right(ys,y)-1) for x,y in starts}
    text_boundaries.update((left_column,r) for r in range(rows) if top <= (ys[r]+ys[r+1])/2 <= bottom)
    centers_x = [(a+b)/2 for a,b in zip(xs,xs[1:])]
    centers_y = [(a+b)/2 for a,b in zip(ys,ys[1:])]
    backgrounds = {}
    for x0,y0,x1,y1,shade in fills:
        for r,y in enumerate(centers_y):
            if y0 <= y < y1:
                for c,x in enumerate(centers_x):
                    if x0 <= x < x1:
                        backgrounds[r,c] = shade if shade != 'FFFFFFFF' else None
    by_cell = defaultdict(list)
    for g in chars:
        c = bisect_right(xs,g[0]+min(.5,(g[1]-g[0])/2))-1
        r = bisect_right(ys,g[2])-1
        if 0 <= c < cols and 0 <= r < rows:
            by_cell[r,c].append(g)
    ET.SubElement(ET.SubElement(sheet,'sheetPr'),'pageSetUpPr',fitToPage='1')
    ET.SubElement(ET.SubElement(sheet,'sheetViews'),'sheetView',workbookViewId='0',showGridLines='0')
    columns = ET.SubElement(sheet,'cols')
    for c,(left,right) in enumerate(zip(xs,xs[1:]),1):
        ET.SubElement(columns,'col',min=str(c),max=str(c),width=f'{min(255,(right-left)/4.5):.4f}',customWidth='1')
    data = ET.SubElement(sheet,'sheetData')
    xml_rows = [ET.SubElement(data,'row',r=str(r+1),ht=f'{min(409,ys[r+1]-ys[r]):.2f}',customHeight='1') for r in range(rows)]
    used = set()
    merges = []
    cells = defaultdict(list)
    for r in range(rows):
        for c in range(cols):
            if (r,c) in used:
                continue
            shade = backgrounds.get((r,c))
            end_c,end_r = c+1,r+1
            while end_c<cols and (r,end_c) not in used and (end_c,r) not in v_edges and (end_c,r) not in text_boundaries and backgrounds.get((r,end_c))==shade:
                end_c+=1
            # Keep outside text on its original line. Inside a table, an absent
            # horizontal divider and matching sides indicate a vertical merge.
            while end_r<rows and (c,r) in v_edges and (end_c,r) in v_edges and all(
                    (end_r,k) not in used and (end_r,k) not in h_edges and backgrounds.get((end_r,k))==shade
                    for k in range(c,end_c)) and (c,end_r) in v_edges and (end_c,end_r) in v_edges and not any(
                    (k,end_r) in v_edges for k in range(c+1,end_c)):
                end_r+=1
            region = [(i,j) for i in range(r,end_r) for j in range(c,end_c)]
            used.update(region)
            content = [g for key in region for g in by_cell.get(key,())]
            value = text_lines(content)
            if not (value or shade or any((j,i) in v_edges or (j+1,i) in v_edges or
                                         (i,j) in h_edges or (i+1,j) in h_edges for i,j in region)):
                continue
            lead = next((g for g in sorted(content,key=lambda g:(round(g[2]/2),g[0])) if g[3].strip()),None)
            size = max(4,min(72,round(lead[4]*2)/2)) if lead else 8
            if '\n' in value:
                size = max(4,min(size*.8,(ys[end_r]-ys[r])/(len(value.splitlines())*1.25)))
                size = round(size*2)/2
            ink = (lead[5] or 'FF000000') if lead else 'FF000000'
            align = 'left'
            if value:
                left,right = min(g[0] for g in content),max(g[1] for g in content)
                before,after = left-xs[c],xs[end_c]-right
                if after<6 and before>12:
                    align = 'right'
                elif min(before,after)>2 and abs(before-after)<max(5,(xs[end_c]-xs[c])*.12):
                    align = 'center'
            if len(region)>1:
                merges.append(f'{column_name(c+1)}{r+1}:{column_name(end_c)}{end_r}')
            for i,j in region:
                edges = (v_edges.get((j,i)) if j==c else None, v_edges.get((j+1,i)) if j==end_c-1 else None,
                         h_edges.get((i,j)) if i==r else None, h_edges.get((i+1,j)) if i==end_r-1 else None)
                style = styles.add(size,ink,shade,edges,align,'\n' in value)
                cells[i].append((j,style,value if (i,j)==(r,c) else ''))
    for i,items in cells.items():
        for j,style,value in sorted(items):
            cell = ET.SubElement(xml_rows[i],'c',r=f'{column_name(j+1)}{i+1}',s=str(style))
            if value:
                cell.set('t','inlineStr')
                ET.SubElement(ET.SubElement(cell,'is'),'t').text = value[:32767]
    if merges:
        group = ET.SubElement(sheet,'mergeCells',count=str(len(merges)))
        for address in merges:
            ET.SubElement(group,'mergeCell',ref=address)
    ET.SubElement(sheet,'pageMargins',left='0',right='0',top='0',bottom='0',header='0',footer='0')
    ET.SubElement(sheet,'pageSetup',paperSize='9',orientation='landscape' if width>height else 'portrait',
                  fitToWidth='1',fitToHeight='1')
    return True
