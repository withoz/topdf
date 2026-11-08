"""Local PDF layout comparison. Inputs/results may be private: never commit them."""
import argparse
import collections
import hashlib
import json
import re
import unicodedata
from pathlib import Path
import fitz
from PIL import Image, ImageDraw, ImageFont


def normalize(text):
    return re.sub(r'\s+', '', unicodedata.normalize('NFKC', text))


def lines(page):
    return [dict(text=''.join(s['text'] for s in line['spans']), bbox=list(line['bbox']))
            for block in page.get_text('dict')['blocks'] for line in block.get('lines', [])]


def compare(reference, candidate):
    frequencies=collections.Counter(normalize(l['text']) for p in reference for l in lines(p)
                                    if l['bbox'][1]<85 and len(normalize(l['text']))>3)
    records=[]
    for index, page in enumerate(reference):
        expected=lines(page); actual=lines(candidate[index])
        headers=[l for l in expected if l['bbox'][1]<85 and frequencies[normalize(l['text'])]>=2]
        footers=[l for l in expected if l['bbox'][1]>page.rect.height-65 and len(normalize(l['text']))>=3]
        numbers=[l for l in expected if l['bbox'][1]>page.rect.height-65 and normalize(l['text']).isdigit()]
        for kind, group in [('header',headers),('footer',footers),('pageNumber',numbers)]:
            for item in group:
                matches=[l for l in actual if normalize(l['text'])==normalize(item['text'])]
                record=dict(page=index+1,kind=kind,text=item['text'],referenceBox=item['bbox'],missing=not matches)
                if matches:
                    found=min(matches,key=lambda l:abs(l['bbox'][1]-item['bbox'][1]))
                    record.update(candidateBox=found['bbox'],dxMm=(found['bbox'][0]-item['bbox'][0])*25.4/72,
                                  dyMm=(found['bbox'][1]-item['bbox'][1])*25.4/72,
                                  clipped=found['bbox'][0]<0 or found['bbox'][1]<0 or found['bbox'][2]>candidate[index].rect.width or found['bbox'][3]>candidate[index].rect.height)
                records.append(record)
    return records


def summarize(records):
    result={}
    for kind in ('header','footer','pageNumber'):
        items=[r for r in records if r['kind']==kind];found=[r for r in items if not r['missing']]
        result[kind]=dict(count=len(items),missing=sum(r['missing'] for r in items),clipped=sum(r.get('clipped',False) for r in items),
                          maxAbsDxMm=max((abs(r['dxMm']) for r in found),default=None),maxAbsDyMm=max((abs(r['dyMm']) for r in found),default=None))
    return result


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--before',required=True,type=Path);parser.add_argument('--after',required=True,type=Path)
    parser.add_argument('--reference',required=True,type=Path);parser.add_argument('--output',required=True,type=Path);parser.add_argument('--pages',default='4,29,30')
    args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    with fitz.open(args.reference) as ref,fitz.open(args.before) as before,fitz.open(args.after) as after:
        if not len(ref)==len(before)==len(after):raise ValueError('Page counts differ; cannot compare page correspondence')
        old=compare(ref,before);new=compare(ref,after)
        report=dict(pageCount=len(ref),before=summarize(old),after=summarize(new),beforeRecords=old,afterRecords=new,
                    files={name:dict(path=str(path.resolve()),sha256=hashlib.sha256(path.read_bytes()).hexdigest()) for name,path in [('reference',args.reference),('before',args.before),('after',args.after)]},
                    imageCounts=[dict(page=i+1,reference=len(ref[i].get_images()),before=len(before[i].get_images()),after=len(after[i].get_images())) for i in range(len(ref))])
        (args.output/'comparison.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        font=ImageFont.truetype('C:/Windows/Fonts/malgun.ttf',20)
        for number in map(int,args.pages.split(',')):
            if not 1<=number<=len(ref):continue
            sheet=Image.new('RGB',(1440,750),'#eeeeee');draw=ImageDraw.Draw(sheet)
            for col,(label,doc) in enumerate([('한글 기준',ref),('수정 전',before),('수정 후',after)]):
                page=doc[number-1];pix=page.get_pixmap(matrix=fitz.Matrix(.8,.8),alpha=False)
                image=Image.frombytes('RGB',(pix.width,pix.height),pix.samples);image.thumbnail((465,700))
                sheet.paste(image,(col*480+(480-image.width)//2,40));draw.text((col*480+14,8),f'{label} · PDF {number}쪽',font=font,fill='#17324D')
            sheet.save(args.output/f'page-{number}-comparison.png')
        # All-page contact sheets preserve A3 aspect ratios inside fixed-size tiles.
        for name,doc in [('reference',ref),('after',after)]:
            for start in range(0,len(doc),20):
                count=min(20,len(doc)-start);sheet=Image.new('RGB',(1400,385*((count+4)//5)),'#dddddd');draw=ImageDraw.Draw(sheet)
                for j in range(count):
                    pix=doc[start+j].get_pixmap(matrix=fitz.Matrix(.5,.5),alpha=False);image=Image.frombytes('RGB',(pix.width,pix.height),pix.samples);image.thumbnail((270,360))
                    x=(j%5)*280;y=(j//5)*385;sheet.paste(image,(x+(280-image.width)//2,y+20));draw.text((x+8,y+3),str(start+j+1),fill='black')
                sheet.save(args.output/f'{name}-contact-{start//20+1}.png')
        print(json.dumps({k:report[k] for k in ('pageCount','before','after')},ensure_ascii=False))


if __name__=='__main__':main()
