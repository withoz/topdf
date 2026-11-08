from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
from pypdf import PdfWriter
out=Path('artifacts/smart-test');out.mkdir(parents=True,exist_ok=True)
image=Image.new('RGB',(2480,3508),'white');draw=ImageDraw.Draw(image)
font=ImageFont.truetype('C:/Windows/Fonts/malgun.ttf',58)
draw.multiline_text((180,230),'TOPDF OCR TEST\nInvoice total: 123,456,789 won\nDeadline: 2026-12-31\n한국어 금액 확인',font=font,fill='black',spacing=45)
image.save(out/'scan.pdf',resolution=300)
writer=PdfWriter();writer.add_blank_page(width=595,height=842)
with (out/'blank.pdf').open('wb') as stream:writer.write(stream)
