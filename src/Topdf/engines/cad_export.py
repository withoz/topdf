"""Export editable PDF vector paths as millimetre DXF entities."""
from ctypes import c_float, c_int, c_uint, c_ushort
from functools import lru_cache
from math import atan2, degrees, dist, isfinite, pi

import ezdxf
from ezdxf import units
from ezdxf.addons import text2path
from ezdxf.fonts.fonts import FontFace
import pypdfium2.raw as raw


def page_point(left, bottom, right, top, x, y, rotation):
    x, y = {
        0: (x-left, y-bottom),
        90: (y-bottom, right-x),
        180: (right-x, top-y),
        270: (top-y, x-left),
    }[rotation]
    return x * 25.4 / 72, y * 25.4 / 72


def page_matrix(obj):
    matrix = obj.get_matrix()
    container = obj.container
    while container is not None:
        matrix = matrix.multiply(container.get_matrix())
        container = container.container
    return matrix


def object_rgb(obj, getter):
    channels = [c_uint() for _ in range(4)]
    if getter(obj, *channels) and channels[-1].value:
        return tuple(channel.value for channel in channels[:3])
    return None


def attributes(drawing, rgb, kind):
    layer = kind + '_' + ''.join(f'{value:02X}' for value in rgb)
    if layer not in drawing.layers:
        drawing.layers.new(layer, dxfattribs={'color': 7})
    result = {'layer': layer}
    if rgb not in ((0, 0, 0), (255, 255, 255)):
        result['true_color'] = ezdxf.colors.rgb2int(rgb)
    return result


def path_style(obj):
    fill, stroke = c_int(), c_int()
    if not raw.FPDFPath_GetDrawMode(obj, fill, stroke):
        return None, None
    return (object_rgb(obj, raw.FPDFPageObj_GetFillColor) if fill.value else None,
            object_rgb(obj, raw.FPDFPageObj_GetStrokeColor) if stroke.value else None)


def text_value(obj, text_page):
    count = raw.FPDFTextObj_GetText(obj, text_page, None, 0)
    if count < 2:
        return ''
    buffer = (c_ushort * ((count+1)//2))()
    raw.FPDFTextObj_GetText(obj, text_page, buffer, count)
    value = bytes(buffer).decode('utf-16-le', errors='replace').rstrip('\0')
    return ''.join(char for char in value if char.isprintable())


def outline_match(objects, styles, index, value, claimed):
    # CAD PDF drivers may emit one filled path per glyph immediately before
    # the invisible text run. Check its geometry before replacing the paths.
    count = sum(not char.isspace() for char in value)
    if count < 2 or count > index or '\ufffd' in value:
        return None
    indices = range(index-count, index)
    color = styles.get(index-1, (None, None))[0]
    if color is None or any(i in claimed or styles.get(i) != (color, None) for i in indices):
        return None
    text = objects[index]
    if text.level or any(objects[i].level for i in indices):
        return None
    left, bottom, right, top = text.get_bounds()
    width, height = right-left, top-bottom
    if width <= 0 or height <= 0:
        return None
    boxes = [objects[i].get_bounds() for i in indices]
    margin = max(0.75, min(width, height)*0.12)
    if any(x1 <= x0 or y1 <= y0 or x0 < left-margin or x1 > right+margin
           or y0 < bottom-margin or y1 > top+margin for x0,y0,x1,y1 in boxes):
        return None
    x0, y0 = min(b[0] for b in boxes), min(b[1] for b in boxes)
    x1, y1 = max(b[2] for b in boxes), max(b[3] for b in boxes)
    if not (0.66 <= (x1-x0)/width <= 1.24 and 0.66 <= (y1-y0)/height <= 1.24):
        return None
    return color, indices


@lru_cache(maxsize=512)
def font_bounds(value):
    path = text2path.make_path_from_str(value, FontFace(filename='malgun.ttf'))
    return path.bbox()


def fit_outline_text(value, points, origin, direction):
    length = dist(origin, direction)
    if not points or length <= 0:
        return None
    dx, dy = (direction[0]-origin[0])/length, (direction[1]-origin[1])/length
    xs = [x*dx+y*dy for x,y in points]
    ys = [-x*dy+y*dx for x,y in points]
    bounds = font_bounds(value)
    if not bounds.has_data or bounds.size.x <= 0 or bounds.size.y <= 0:
        return None
    height = (max(ys)-min(ys))/bounds.size.y
    width = (max(xs)-min(xs))/(height*bounds.size.x) if height > 0 else 0
    if not (isfinite(height) and height > 0 and 0.1 <= width <= 10):
        return None
    x = min(xs)-bounds.extmin.x*height*width
    y = min(ys)-bounds.extmin.y*height
    return (x*dx-y*dy, x*dy+y*dx), height, width


def add_text(drawing, obj, value, rgb, project, outline_points=None):
    size = c_float()
    if not raw.FPDFTextObj_GetFontSize(obj, size):
        return False
    matrix = page_matrix(obj)
    origin = project(*matrix.on_point(0, 0))
    direction = project(*matrix.on_point(1, 0))
    height = dist(origin, project(*matrix.on_point(0, size.value)))
    if not isfinite(height) or height <= 0:
        return False
    angle = degrees(atan2(direction[1]-origin[1], direction[0]-origin[0]))
    width = 1.0
    if outline_points is not None:
        fit = fit_outline_text(value, outline_points, origin, direction)
        if fit is None:
            return False
        origin, height, width = fit
    drawing.modelspace().add_text(value, dxfattribs={
        **attributes(drawing, rgb, 'TEXT'), 'insert': origin, 'height': height,
        'width': width, 'rotation': angle, 'style': 'TOPDF_TEXT'})
    return True


def export_text(drawing, page, objects, styles, project, number):
    drawing.styles.new('TOPDF_TEXT', dxfattribs={'font': 'malgun.ttf'})
    replaced = set()
    with_text = page.get_textpage()
    try:
        for index, obj in enumerate(objects):
            if obj.type != raw.FPDF_PAGEOBJ_TEXT:
                continue
            mode = raw.FPDFTextObj_GetTextRenderMode(obj)
            if mode == 7:
                continue
            value = text_value(obj, with_text)
            if not value.strip():
                continue
            if mode == 3:
                match = outline_match(objects, styles, index, value, replaced)
                if match:
                    rgb, indices = match
                    points = [point for i in indices for segments, _ in contours(objects[i], project, number)
                              for segment in segments for point in sampled_points(segment)]
                    if add_text(drawing, obj, value, rgb, project, points):
                        replaced.update(indices)
            else:
                getter = raw.FPDFPageObj_GetStrokeColor if mode in (1, 5) else raw.FPDFPageObj_GetFillColor
                rgb = object_rgb(obj, getter)
                if rgb is not None:
                    add_text(drawing, obj, value, rgb, project)
    finally:
        with_text.close()
    return replaced


def contours(obj, project, number):
    matrix = page_matrix(obj)
    start = previous = None
    segments, controls = [], []
    for index in range(raw.FPDFPath_CountSegments(obj)):
        segment = raw.FPDFPath_GetPathSegment(obj, index)
        x, y = c_float(), c_float()
        if not raw.FPDFPathSegment_GetPoint(segment, x, y):
            raise ValueError(f'{number}쪽의 벡터 좌표를 읽을 수 없습니다.')
        point = project(*matrix.on_point(x.value, y.value))
        kind = raw.FPDFPathSegment_GetType(segment)
        if kind == raw.FPDF_SEGMENT_MOVETO:
            if segments:
                yield segments, previous == start
            start = previous = point
            segments, controls = [], []
        elif kind == raw.FPDF_SEGMENT_LINETO and previous is not None:
            if previous != point:
                segments.append((previous, point))
            previous = point
        elif kind == raw.FPDF_SEGMENT_BEZIERTO and previous is not None:
            controls.append(point)
            if len(controls) == 3:
                if any(control != previous for control in controls):
                    segments.append((previous, *controls))
                previous = point
                controls.clear()
        if raw.FPDFPathSegment_GetClose(segment) and previous is not None and start is not None:
            if previous != start:
                segments.append((previous, start))
            previous = start
    if segments:
        yield segments, previous == start


def sampled_points(segment):
    yield segment[0]
    if len(segment) == 4:
        for t in (0.25, 0.5, 0.75):
            u = 1-t
            weights = (u**3, 3*u*u*t, 3*u*t*t, t**3)
            yield tuple(sum(w*p[axis] for w,p in zip(weights,segment)) for axis in (0,1))
    yield segment[-1]


def circle_geometry(segments, closed):
    if not closed or len(segments) < 4:
        return None
    points = [point for segment in segments for point in list(sampled_points(segment))[:-1]]
    if len(points) < 12:
        return None
    xs, ys = zip(*points)
    left, right, bottom, top = min(xs), max(xs), min(ys), max(ys)
    radius = (right-left+top-bottom)/4
    if radius <= 0 or abs((right-left)-(top-bottom)) > radius*.03:
        return None
    center = ((left+right)/2, (bottom+top)/2)
    if any(abs(dist(center, point)-radius) > radius*.014 for point in points):
        return None
    angles = [atan2(y-center[1], x-center[0]) for x,y in points]
    steps = [(b-a+pi)%(2*pi)-pi for a,b in zip(angles, angles[1:]+angles[:1])]
    if max(map(abs,steps)) > pi/5 or abs(abs(sum(steps))-2*pi) > 1e-6:
        return None
    if any(step*sum(steps) < -1e-6 for step in steps):
        return None
    return center, radius


def export_contour(model, segments, closed, outline):
    circle = circle_geometry(segments, closed)
    if circle:
        model.add_circle(*circle, dxfattribs=outline)
        return
    vertices = []

    def flush(close=False):
        if len(vertices) == 2:
            model.add_line(*vertices, dxfattribs=outline)
        elif len(vertices) > 2:
            model.add_lwpolyline(vertices[:-1] if close else vertices, close=close, dxfattribs=outline)
        vertices.clear()

    straight = True
    for segment in segments:
        if len(segment) == 4:
            flush()
            model.add_open_spline(segment, degree=3, dxfattribs=outline)
            straight = False
        else:
            if not vertices:
                vertices.append(segment[0])
            vertices.append(segment[-1])
    flush(closed and straight)


def dxf(page, number, target, scale=1):
    drawing = ezdxf.new('R2013', units=units.MM)
    model = drawing.modelspace()
    left, bottom, right, top = page.get_bbox()
    rotation = page.get_rotation()

    def project(x, y):
        px, py = page_point(left, bottom, right, top, x, y, rotation)
        return px*scale, py*scale

    objects = list(page.get_objects())
    styles = {index: path_style(obj) for index,obj in enumerate(objects) if obj.type == raw.FPDF_PAGEOBJ_PATH}
    replaced = export_text(drawing, page, objects, styles, project, number)
    text_entities = list(model)
    for index, (fill_rgb, stroke_rgb) in styles.items():
        if index in replaced or (fill_rgb is None and stroke_rgb is None):
            continue
        fill = attributes(drawing, fill_rgb, 'FILL') if fill_rgb is not None else None
        stroke = attributes(drawing, stroke_rgb, 'LINE') if stroke_rgb is not None else None
        boundaries = []
        has_curve = False
        for segments, closed in contours(objects[index], project, number):
            export_contour(model, segments, closed, stroke or fill)
            if fill:
                if all(len(segment) == 2 for segment in segments):
                    vertices = [segment[0] for segment in segments]
                    if not closed:
                        vertices.append(segments[-1][-1])
                    if len(set(vertices)) >= 3:
                        boundaries.append(vertices)
                else:
                    has_curve = True
        if boundaries and not has_curve:
            hatch = model.add_hatch(dxfattribs=fill)
            for vertices in boundaries:
                hatch.paths.add_polyline_path(vertices, is_closed=True, flags=0)
    model.set_redraw_order((entity.dxf.handle, f'{0xFFFFFF00 + index:X}')
                           for index, entity in enumerate(text_entities))
    if not len(model):
        raise ValueError(f'{number}쪽에 DXF로 내보낼 벡터 선, 곡선 또는 문자가 없습니다.')
    drawing.saveas(target)
