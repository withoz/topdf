<p align="center"><img src="assets/branding/topdf-logo.png" width="160" alt="PDF와 아래 화살표로 구성된 TOPDF 로고"></p>

# TOPDF

**문서와 CAD 도면을 PDF로 변환하고 편집하는 Windows 앱**

[![Windows build](https://github.com/withoz/topdf/actions/workflows/build.yml/badge.svg)](https://github.com/withoz/topdf/actions/workflows/build.yml)

문서를 끌어놓으면 PDF로 변환해 앱 안에서 열고 편집할 수 있습니다. 기존 PDF는 바로 엽니다.

## 설치

Windows 10/11 x64용 **TOPDF 0.4.6 스마트판**을 GitHub에서 받을 수 있습니다. OCR과 로컬 AI 모델을 포함해 설치 파일 크기는 약 700MB입니다. [GitHub 릴리스](https://github.com/withoz/topdf/releases/tag/v0.4.6) · [스마트판 설치 파일](https://github.com/withoz/topdf/releases/download/v0.4.6/TOPDF-Setup-0.4.6-Smart-win-x64.exe)

설치 파일에는 .NET과 Python 런타임이 포함됩니다. WebView2가 없는 PC에서는 최초 설치에 인터넷 연결이 필요합니다. [설치판 안내](docs/INSTALLER.md)

## 지원 형식

| 형식 | 확장자 | 처리 방식 |
|---|---|---|
| 한글 | HWP, HWPX | rhwp |
| 워드 | DOCX | office2pdf |
| 엑셀 | XLS, XLSX | 수정 Ream / MiniPdf |
| 파워포인트 | PPTX | 내장 office2pdf |
| 파워포인트 구형 형식 | PPT | 별도로 설치한 LibreOffice로 PDF 변환 |
| DWG 도면 | DWG | LibreDWG → ezdxf |
| DXF 도면 | DXF | ezdxf / matplotlib |
| PDF | PDF | 변환 없이 열기·편집 |

PPTX는 기본 모드에서 열 수 있습니다. 구형 PPT를 열면 설치된 LibreOffice를 자동으로 사용합니다. DOC, ODT, ODS, ODP, RTF 등은 설치형 호환 모드에서 처리합니다.

## 주요 기능

- [PDF 수정](docs/PDF-EDITING.md): 작은 아이콘 리본에서 도구 선택으로 바로 편집, 그리기 중 Esc로 선택 전환·다시 Esc로 보기 복귀(수정 상태 유지), 원문 글꼴·크기를 유지하는 본문 교체, 한·두·세 페이지 보기, 선택형 축소 이미지 패널, 글자·메모·이미지 도장·도형·자유곡선·검토 표시, 여러 항목 편집, 페이지 변경, PDF 및 `.topdf` 편집 파일 저장

- 스마트 패널: 변환 품질 점검, 한국어·영어 OCR, 열린 문서 검색, 문장 비교, 로컬 AI 근거 답변, 폴더 자동 변환
- 내장 경량 로컬 AI 모델 포함. 문서는 외부 AI 서비스에 전송하지 않습니다.
- 파일 추가·드래그앤드롭 즉시 자동 변환, 변환 중 추가 파일도 순차 처리, 취소·재시도
- 작은 문서 탭으로 전환·닫기, 같은 파일 중복 열기 방지
- PDF 미리보기, 확대·페이지 이동·검색·인쇄
- 창 전체 미리보기, 메뉴 버튼으로 파일 목록·기능 표시, F11 전체 화면
- 체크박스 선택 저장, 전체 선택·해제, 같은 이름 충돌 시 번호 추가
- 탐색기 연결 프로그램 등록과 파일 열기 시 자동 변환
- XLS 병합 셀 금액이 지나치게 작아지는 오류 보완

## 빌드 및 실행

Windows 10/11 x64, .NET 10 SDK, Node.js 20 이상과 Rust/MSVC 빌드 도구(엔진 빌드 때만), Python 3.11 이상(CAD), Microsoft Edge WebView2 Runtime이 필요합니다.

```powershell
git clone https://github.com/withoz/topdf.git
cd topdf
pwsh -File scripts/Setup-Engines.ps1
# OCR·로컬 AI 포함 빌드는 다음 단계도 실행 (모델 약 491MB)
pwsh -File scripts/Setup-SmartEngines.ps1
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r src/Topdf/engines/requirements.txt
$env:TOPDF_PYTHON = (Resolve-Path .venv/Scripts/python.exe).Path
dotnet build src/Topdf/Topdf.csproj -o artifacts/app
.\artifacts\app\Topdf.exe
```

`Setup-Engines.ps1`은 고정 버전 공식 엔진을 프로젝트 안으로 다운로드하고 SHA-256을 확인한 뒤 HWP 레이아웃 보정 패치와 XLS용 JavaScript 번들을 빌드합니다. HWP는 고정된 upstream 소스와 패치 해시로 재현하며, 설치 사용자에게 Rust/MSVC 설치는 필요하지 않습니다. [HWP 레이아웃 보정](docs/HWP-LAYOUT-FIX.md)을 참고하세요. 시스템 기본 앱이나 레지스트리는 변경하지 않습니다. Node는 XLS 런타임에 사용하지 않습니다.

빌드만 확인하려면 `-SkipNative` 옵션으로 네이티브 엔진 다운로드를 생략할 수 있습니다. 이 경우 해당 엔진의 실제 변환은 동작하지 않습니다. [개발 안내](docs/DEVELOPMENT.md)를 참고하세요.

## 사용법

스마트 기능은 상단 **스마트** 버튼에서 엽니다. [스마트 기능 사용법·검증·제한](docs/SMART-FEATURES.md)

1. **파일 → 열기**(`Ctrl+O`)로 파일을 선택하거나 창에 끌어놓습니다. 변환할 파일을 추가하면 열린 파일 목록이 자동으로 표시됩니다.
2. 상단 탭으로 문서를 전환합니다. **파일 → 열린 파일 목록**(`Ctrl+M`)에서 변환 결과를 확인하거나 실패·취소된 파일을 재시도할 수 있습니다.
3. **파일 → PDF 내보내기**(`Ctrl+Shift+S`)로 편집 결과를 저장합니다. 여러 파일은 열린 파일 목록에서 체크한 뒤 **선택 저장**을 누릅니다.
4. 탐색기에서 쓰려면 열린 파일 목록의 **더보기 → 연결 프로그램 등록**을 누른 뒤 파일을 우클릭해 **연결 프로그램 → TOPDF**를 선택합니다.

`Ctrl+M`으로 메뉴를 열고 닫으며 `Esc`로 메뉴를 닫을 수 있습니다. `F11`은 화면 전체 보기/창 모드 전환입니다.

기본 연결 앱은 자동으로 바꾸지 않습니다. 설치형 호환 모드는 사용자가 체크한 경우에만 기존 프로그램을 사용합니다.

## 정확도와 제한

- 실제 검증한 문서 묶음에서는 6개 파일(HWP/DOCX/XLS/XLSX/DWG/DXF)의 변환·내부 미리보기를 확인했습니다. 고객 문서와 결과는 저장소에 포함하지 않습니다.
- 글꼴·줄바꿈·행 높이·테두리 배치는 원본 프로그램 출력과 다를 수 있습니다.
- CAD는 A3 가로 전체 맞춤의 **2D 미리보기**입니다. 정밀 축척, 3D, 전문 객체, XREF, CTB/STB는 별도 검증 대상입니다.
- 일부 CAD 텍스트는 윤곽으로 출력되어 검색·복사가 불가능합니다.
- 암호·손상·대용량 문서와 모든 문서 객체를 보장하지 않습니다.
- PDF 편집은 200MB 이하, 500쪽 이하에서 지원합니다. 0.4.6 빌드에서 약 173MB PDF 두 개를 열어 글자를 추가하고 새 PDF로 저장하는 검증을 통과했습니다.
- 임시 입력 복사본·PDF·엔진 로그와 WebView2 데이터는 `%LOCALAPPDATA%\TOPDF`에 남습니다. 자동 만료·앱 내 삭제는 후속 과제입니다.
- XLSX 수식은 저장된 계산 결과를 표시하며, 계산 결과가 없으면 빈 금액 출력 대신 변환을 중단합니다. 자체 수식 재계산은 지원하지 않습니다.
- 한글 렌더링에는 Windows 글꼴을 사용합니다. 이 저장소는 독점 글꼴을 포함하지 않습니다.

## 검증 및 개선

```powershell
dotnet run --project tests/ExportSmoke/ExportSmoke.csproj
dotnet build tests/PreviewSmoke/PreviewSmoke.csproj
# Windows 데스크톱 세션의 UI 검사
dotnet run --project tests/PreviewSmoke/PreviewSmoke.csproj -- --export-ui
```

[0.2.0 실제·합성 회귀 검증](docs/VALIDATION-0.2.0.md)에 자동 변환·탭·금액·오류 입력 시험을 정리했습니다.

우선 보강 항목과 근거는 [개선 계획](docs/IMPROVEMENTS.md)에 정리했습니다. 이슈에는 실제 업무 문서를 첨부하지 말고 비식별 재현 파일과 환경·엔진 버전을 제공해 주세요.

## 라이선스

TOPDF 자체 코드와 로고는 [MIT](LICENSE)로 공개합니다. 외부 엔진과 의존성에는 각자의 라이선스가 적용됩니다. **전체 배포물을 단일 MIT 라이선스로 해석하면 안 됩니다.** LibreDWG는 GPL-3.0-or-later입니다.

이 저장소는 외부 엔진 바이너리·글꼴·개인 문서·생성 결과를 배포하지 않습니다. 자세한 내용은 [외부 구성 요소](docs/THIRD_PARTY.md)를 확인하세요.

- [0.3.10 구현 현황과 복구 안내](docs/IMPLEMENTATION-STATUS-0.3.10.md): 기능 연결, 검사 방법, 백업과 남은 제한
