"""Generate synthetic fixtures, never copy private documents into the repository."""
import argparse
from pathlib import Path
from docx import Document
from docx.shared import Pt
from openpyxl import Workbook
from openpyxl.styles import Font, Alignment
import ezdxf
import xlwt
import zipfile
import shutil

out = Path(argparse.ArgumentParser().parse_known_args()[1][0])
out.mkdir(parents=True, exist_ok=True)
doc = Document()
doc.styles['Normal'].font.name = 'Malgun Gothic'
doc.styles['Normal'].font.size = Pt(11)
doc.add_heading('표와 긴 문장 회귀 시험', 0)
doc.add_paragraph('자동 변환과 문서 전환을 확인하는 비식별 시험 문서입니다. ' * 12)
table = doc.add_table(rows=1, cols=3)
table.style = 'Table Grid'
for cell, value in zip(table.rows[0].cells, ['항목', '금액', '비고']): cell.text=value
for title, amount in [('설계비', '123,456,789'), ('공사비', '987,654,321')]:
    for cell, value in zip(table.add_row().cells, [title, amount, '병합 및 줄바꿈 확인']): cell.text=value
doc.add_page_break()
doc.add_paragraph('SECOND_PAGE_END 2026')
doc.save(out/'table-and-wrap.docx')
wb=Workbook(); ws=wb.active; ws.title='금액'
ws.merge_cells('A1:D1'); ws['A1']='금액 표시 및 병합 셀 시험'
for row, val in enumerate([123456789, -9876543, 0, 0.125], 3):
    ws.merge_cells(start_row=row,start_column=2,end_row=row,end_column=4)
    ws.cell(row,1,'금액' if row<6 else '비율'); c=ws.cell(row,2,val)
    c.number_format='#,##0;[Red](#,##0)' if row<6 else '0.0%'
    c.font=Font(name='Malgun Gothic',size=11)
    c.alignment=Alignment(horizontal='right')
for col in 'ABCD': ws.column_dimensions[col].width=18
ws['A9']='FORMULA'; ws['B9']='=1000+2000'
ws2=wb.create_sheet('두 번째'); ws2['A1']='SECOND_SHEET_END'; ws2['B2']=2468000; ws2['B2'].number_format='#,##0'
wb.save(out/'amounts-and-sheets.xlsx')
shutil.copy2(out/'amounts-and-sheets.xlsx', out/'invalid-missing-formula-cache.xlsx')
# Supply the expected cached result as an authoring program would do after calculation.
with zipfile.ZipFile(out/'amounts-and-sheets.xlsx') as z:
    parts={n:z.read(n) for n in z.namelist()}
parts['xl/worksheets/sheet1.xml']=parts['xl/worksheets/sheet1.xml'].replace(b'<f>1000+2000</f><v></v>',b'<f>1000+2000</f><v>3000</v>')
with zipfile.ZipFile(out/'amounts-and-sheets.xlsx','w',zipfile.ZIP_DEFLATED) as z:
    for n,data in parts.items(): z.writestr(n,data)
(out/'invalid-corrupt.docx').write_bytes(b'This is deliberately not a ZIP document.')
book=xlwt.Workbook(); sheet=book.add_sheet('금액')
style=xlwt.easyxf('font: name Malgun Gothic, height 220; align: horiz right, shrink_to_fit on; borders: left thin, right thin, top thin, bottom thin;',num_format_str='#,##0;[Red](#,##0)')
for col in range(5): sheet.col(col).width=2200
sheet.write_merge(0,0,0,4,'금액 병합 셀 회귀 시험')
for row,value in enumerate([123456789,987654321,-12345678,0],2):
    sheet.write(row,0,'금액'); sheet.write_merge(row,row,1,4,value,style)
book.save(str(out/'merged-amounts.xls'))
drawing=ezdxf.new('R2018'); model=drawing.modelspace()
model.add_lwpolyline([(0,0),(200,0),(200,100),(0,100)],close=True)
model.add_circle((100,50),25)
model.add_text('CAD_TEST_2026',dxfattribs={'height':8,'insert':(10,110)})
drawing.saveas(out/'drawing.dxf')
print(out)
