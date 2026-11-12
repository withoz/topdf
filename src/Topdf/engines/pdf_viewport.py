"""Render a visible rectangle directly from a PDF page."""
import math
from contextlib import closing

import pypdfium2 as pdfium


def render_bitmap(page, rotation, region):
    pixels, x, y, w, h = region
    if not all(map(math.isfinite, region)) or min(x, y) < 0 or min(w, h, pixels) <= 0 or max(x+w, y+h) > 1.000001:
        raise ValueError('잘못된 화면 렌더링 영역입니다.')
    width, height = page.get_size()
    if rotation in (90, 270):
        width, height = height, width
    scale = min(pixels / max(width, height), math.sqrt(12_000_000 / (width * w * height * h)))
    crop = (x * width, max(0, 1-y-h) * height, max(0, 1-x-w) * width, y * height)
    return page.render(scale=scale, rotation=rotation, crop=crop, limit_image_cache=True)


def render_to_file(page, rotation, region, output):
    with closing(render_bitmap(page, int(rotation), tuple(map(float, region)))) as bitmap:
        bitmap.to_pil().save(output, format='BMP')


def serve():
    import json
    import os
    import sys

    document = None
    page = None
    cached_source = None
    cached_index = None
    try:
        for line in sys.stdin:
            try:
                source, output, index, rotation, *region = json.loads(line)
                index = int(index)
                stat = os.stat(source)
                identity = source, stat.st_size, stat.st_mtime_ns
                if cached_source != identity:
                    if page is not None:
                        page.close()
                        page = None
                    if document is not None:
                        document.close()
                        document = None
                    cached_source = None
                    document = pdfium.PdfDocument(source)
                    cached_source = identity
                if page is None or cached_index != index:
                    if page is not None:
                        page.close()
                        page = None
                    page = document[index]
                    cached_index = index
                render_to_file(page, rotation, region, output)
                print("ok", flush=True)
            except Exception as error:
                print(str(error).replace("\n", " ").replace("\r", " ") or "PDF 화면 렌더링 실패", flush=True)
    finally:
        if page is not None:
            page.close()
        if document is not None:
            document.close()


def main():
    import sys

    if sys.argv[1:] == ["--serve"]:
        serve()
        return
    source, output, index, rotation, *region = sys.argv[1:]
    with pdfium.PdfDocument(source) as document:
        with closing(document[int(index)]) as page:
            render_to_file(page, rotation, region, output)


if __name__ == '__main__':
    main()
