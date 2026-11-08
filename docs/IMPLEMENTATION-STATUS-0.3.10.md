# TOPDF 0.3.10 구현 현황과 복구 안내

기준일: 2026-11-08. 이 문서는 지금까지 구현한 기능과 연결 지점을 보존하는 개발 기준표입니다. 배포 파일은 서명되지 않은 Windows x64 시험판입니다.

## 구현 보존 목록

| 영역 | 현재 동작 | 주요 코드 / 검사 |
|---|---|---|
| 변환 | 한글(HWP/HWPX), 워드, 엑셀(XLS/XLSX), DWG, DXF → PDF, 기존 PDF 바로 보기 | ConversionService, engines/convert.py, 고정 엔진 잠금 파일 |
| HWP 배치 | 머리글·꼬리글·배경 보정 엔진 포함 | engines/rhwp/layout.patch, docs/HWP-LAYOUT-FIX.md |
| XLS 금액 | 표시 형식·병합 영역 보정 엔진 포함 | engines/ream, 고정 Ream 번들 |
| CAD | 원본 DWG 버전 확인, LibreDWG → DXF → PDF | docs/DWG-VERSION-FIX.md |
| 화면 | 작은 한 줄 리본 → 문서 탭 → PDF, 메뉴 접기, 전체 화면 | MainWindow, PdfEditorView.xaml, UnifiedUiTest |
| 아이콘 | 사용자 제공 SVG 11종의 WPF 벡터 변환, 원본 SVG 보관 | Assets/RibbonIcons, PdfEditorView.xaml |
| 보기 | native PDF 보기, 2/3페이지 이미지 보기, 숨김 가능한 축소 이미지 목록 | PdfEditorView.Pages.cs, FontViewsUiTest |
| 도형 | 선·화살표·사각형·타원·자유곡선, 형광펜·밑줄·취소선 | PdfEditorView.Drawing.cs, test_drawing.py |
| 그리기 안전 | 클릭/손떨림은 생성하지 않음, 확대율 고려한 최소 드래그 거리 | HasDrawingGesture, DrawingUiTest |
| Esc | 그리기 중 미완성 항목 취소 후 선택, 완료 항목 유지; 선택에서 다시 Esc는 보기 | StopDrawingAndSelect, EditorShortcuts |
| 끝점 | 회전된 선·화살표도 두 끝점으로 방향·길이 변경, 페이지 경계 제한 | PdfEditorView.Endpoints.cs |
| 메모 | 문서 클릭 후 입력 포커스 유지, 생성 후 선택 도구, 클릭 재편집, Enter 줄바꿈 | BeginAddedText, DrawingUiTest |
| 선택 | 드래그 영역, Ctrl 토글, Shift 추가, 여러 항목 이동·삭제·스타일 | PdfEditorView.Selection.cs |
| 단축키 | Delete 삭제, Ctrl+Z/Y 취소/다시 실행, Ctrl+D 복제, 방향키 1pt/Shift 10pt | EditorShortcuts; native 보기에서는 편집 키를 가로채지 않음 |
| 색상 | 초기 주황색 #FF6500, 팔레트·두께·채움·점선 | InitializeRibbon, PenWidthChanged |
| 이력 | 최근 100개 작업, 주석 취소 시 배경 재렌더 최소화 | Capture/Commit/Restore |
| 본문 | 명시적 본문 도구, 원본 PDF 글자 좌표 커서, 즉시 입력 표시 후 정확한 PDF 갱신 | Text/Caret/LiveText/Typography, pdf_typography.py |
| 한글 글꼴 | Unicode/Johab 문자표 우선순위, 비ASCII PostScript 이름 정규화; 설치 글꼴 불변 | register_original_font, test_legacy_font.py |
| PDF 저장 | 원본 보존, 새 파일만 생성, 기존 파일 덮어쓰기 금지, 현재 수정 반영 | PdfEditService.SaveNewAsync, PdfExportService |
| 편집 저장 | 원본 사본·항목·도장 포함 .topdf, 버전/해시/경로/크기 검증, 다시 편집 | SaveProjectAsync/LoadProjectAsync |
| 스마트 | 품질 점검·OCR·검색·비교·로컬 AI, 폴더 자동 변환 | DocumentIntelligence, LocalDocumentAi, FolderAutomation |
| 설치 | 사용자별 설치·제거, WebView2 확인, 연결 프로그램 등록 | installer/Topdf.iss, Build-Installer.ps1 |

## 연결 점검 기준

- XAML 이벤트 핸들러와 새 partial 코드 파일은 WPF Release 빌드로 확인합니다.
- 드래그·키보드 → 모델 → Undo/Redo → 화면 → .topdf → PDF 출력까지 한 경로로 검사합니다.
- 원본 PDF 해시, 새 파일 전용 저장, 이름 충돌 처리와 편집 파일 경로 탈출 차단을 검사합니다.
- 리본이 한 줄인지, 중복 글자 입력란이 숨겨졌는지, 도형의 삭제 버튼이 항상 존재하는지 유지합니다.
- 고객 문서·캐시·결과 PDF·설치 바이너리·모델·외부 엔진은 Git에 넣지 않습니다.

검증 결과는 [0.3.10 점검 기록](VALIDATION-0.3.10.md)에 보관합니다.

## 재검사

```powershell
# 엔진과 개발용 Python 패키지가 준비된 Windows 환경
pwsh -NoProfile -File scripts/Test-EditorAudit.ps1 -IncludeUi
```

로그와 단계별 종료 코드는 `artifacts/audit-0.3.10/`에 남습니다. 합성 문서만 사용합니다. Python 엔진의 선택/삭제/배경/기준선/회전·CropBox·UserUnit, 벡터 출력, 구형 글꼴, 저장 안전, Release 빌드, 화면의 드래그·메모·끝점·복사·삭제·Esc·다중선택·프로젝트/PDF 저장을 검사합니다. 스마트 기능은 이 명령에서 빌드만 검사하며 실제 OCR·AI나 새 PC를 자동 검증했다고 간주하지 않습니다.

## 보관과 복구

- `artifacts/safety/2026-11-08/before-audit-source.zip`: 점검 시작 전 작업 트리 사본.
- `artifacts/safety/2026-11-08/topdf-0.3.10-source.zip`: 최종 소스 사본.
- `artifacts/safety/2026-11-08/topdf-0.3.10.bundle`: Git 커밋과 이력. `git bundle verify`로 검증합니다.
- `artifacts/safety/2026-11-08/SHA256.json`: 백업·설치 파일 검증값과 기준 커밋.
- 최종 설치 파일에는 self-contained .NET, Python, 고정 변환 엔진, 로컬 모델, 소스 ZIP와 파일별 SHA-256 목록이 들어갑니다.

새 폴더로 Git bundle을 clone하면 소스와 이력을 복원할 수 있습니다. 외부 엔진/런타임은 설치 파일을 보관하거나 개발 안내와 잠금 파일에 따라 다시 준비합니다. 소스 ZIP만으로 변환 엔진이나 AI 모델까지 포함되었다고 간주하면 안 됩니다. 기존 작업 폴더 위에 덮어쓰지 말고 별도 폴더에 복원해 확인합니다.

## 유지할 제한

- 본문 편집은 줄 단위이며 Word식 문단 재배치가 아닙니다. PDF 구조·원본 글꼴·새 글자 지원에 따라 실패할 수 있습니다. 검토 작업은 선·도형·메모를 우선 사용합니다.
- 2/3페이지는 이미지 기반 읽기용입니다. PDF 텍스트 선택·검색·인쇄는 한 페이지 native 보기를 사용합니다.
- 영역 선택은 회전 반영 경계 상자와 겹치는 추가 항목을 대상으로 합니다. 원본 PDF의 일반 도형을 새 편집 항목으로 분해하지 않습니다.
- PDF로 저장하면 주석은 내용에 합쳐집니다. 계속 수정할 작업은 .topdf도 함께 저장합니다. 자동 충돌 복구/무조건 자동 저장은 구현했다고 안내하지 않습니다.
- 코드 서명 인증서와 별도 Windows PC의 설치 검증은 사용자가 추후 진행하기로 한 상태입니다. 이 PC의 검사와 구분합니다.
