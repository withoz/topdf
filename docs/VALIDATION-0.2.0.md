# 0.2.0 회귀 검증

검증일: 2026-11-02. 원본과 결과 PDF는 공개 저장소에 포함하지 않습니다.

## 자동 열기 및 문서 탭

- 파일 선택·드롭·탐색기 열기가 같은 자동 변환 경로를 사용합니다.
- 실제 업무용 HWP/DOCX/XLS/XLSX/DWG/DXF 6개에서 변환과 탭별 미리보기를 확인했습니다.
- 첫 파일 변환 중 추가한 두 번째 묶음도 같은 대기열에서 처리했습니다.
- 중복 파일은 기존 탭을 재사용하고 선택 탭 닫기 후 인접 문서를 표시했습니다.
- 모든 원본 SHA-256이 유지됐습니다.

## 결과 비교

기존 승인된 6개 결과와 17쪽 전체를 비교했습니다. 페이지 수·용지 크기·추출 가능한 금액 토큰이 같고, Poppler 900px 렌더링의 픽셀 평균 절대 차이는 모두 0이었습니다. 기존 결과와 동일하다는 뜻이며 원본 프로그램과 완전히 같다는 보장은 아닙니다.

## 합성 회귀 묶음

`tests/Regression/generate_fixtures.py`로 비식별 시험 파일을 만듭니다.

- DOCX: 한국어 긴 문장·표·금액·명시적 페이지 나눔, 2쪽과 마지막 페이지 표식 확인
- XLS: 병합 셀에 큰 금액·음수·0, 숫자 누락 없음과 11pt 표시 확인
- XLSX: 병합 셀·음수·백분율·다중 시트·저장된 수식 결과, 2쪽과 숫자 표식 확인
- DXF: 사각형·원·텍스트의 비어 있지 않은 도면 출력
- 손상 DOCX와 계산 결과가 없는 XLSX는 실패로 안내하고 이후 정상 문서는 계속 처리

시험 중 저장된 계산 결과가 없는 XLSX 수식이 빈칸으로 출력되는 문제를 발견했습니다. 해당 셀을 안내하고 변환을 중단하도록 보완했습니다. **수식을 재계산하는 기능은 아닙니다.** 저장된 결과가 오래된 수식도 정확도를 보장하지 않습니다.

## 실행 방법

```powershell
# 제작: python-docx, openpyxl, ezdxf, xlwt 필요
python tests/Regression/generate_fixtures.py artifacts/regression/input
dotnet build tests/PreviewSmoke/PreviewSmoke.csproj
tests/PreviewSmoke/bin/Debug/net10.0-windows/PreviewSmoke.exe --auto-folder artifacts/regression/input artifacts/regression/output
# 검사: PyMuPDF, Pillow 및 PATH의 pdftoppm 필요
python tests/Regression/verify_synthetic.py artifacts/regression/output
python tests/Regression/compare_pdfs.py BASELINE_FOLDER CURRENT_FOLDER artifacts/regression/comparison
```

손상·암호·대용량·HWPX 복합 객체·CAD 전문 객체의 폭넓은 검증은 계속 필요합니다. 합성 정상 문서 4개 및 오류 입력 2개가 모든 문서의 정확도를 대표하지 않습니다.
