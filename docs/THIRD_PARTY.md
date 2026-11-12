# 외부 구성 요소

TOPDF 자체 소스의 MIT 라이선스는 아래 외부 구성 요소의 라이선스를 대체하지 않습니다.

| 구성 요소 | 버전 | upstream 표시 라이선스 | 연결 |
|---|---|---|---|
| rhwp | 0.8.7-topdf.1 | MIT | https://github.com/edwardkim/rhwp |
| office2pdf | 0.10.0 | Apache-2.0 | https://github.com/developer0hye/office2pdf |
| MiniPdf | 0.43.2 | Apache-2.0 | https://github.com/mini-software/MiniPdf |
| Ream | 1.33.1 | MIT | https://github.com/alex-krassavin/reamkit |
| LibreDWG | 0.14 | GPL-3.0-or-later | https://github.com/LibreDWG/libredwg |
| GNU libiconv | 1.18-1 (MSYS2) | LGPL-2.1-or-later | https://www.gnu.org/software/libiconv/ |
| PCRE2 | 10.47-1 (MSYS2) | BSD-3-Clause 및 원본 고지 | https://github.com/PCRE2Project/pcre2 |
| ezdxf | requirements.txt 참조 | MIT | https://github.com/mozman/ezdxf |
| matplotlib | requirements.txt 참조 | upstream license | https://matplotlib.org/stable/project/license.html |
| WebView2 | csproj 참조 | Microsoft package terms | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| Tesseract / 언어 모델 | smart-engines.lock.json | Apache-2.0 | https://github.com/tesseract-ocr/tesseract / https://github.com/tesseract-ocr/tessdata_fast |
| Leptonica | OCR DLL 배포본 참조 | upstream BSD-style | https://github.com/DanBloomberg/leptonica |
| pypdfium2 / PDFium | Python lock 참조 | upstream 고지와 포함 종속성 라이선스 | https://github.com/pypdfium2-team/pypdfium2 |
| ReportLab | 5.1.0 | BSD | https://www.reportlab.com/opensource/ |
| xlrd | 2.0.2 | BSD | https://github.com/python-excel/xlrd |
| llama.cpp | b12199 | MIT / 포함 LLVM OpenMP 고지 | https://github.com/ggml-org/llama.cpp |
| Qwen2.5-0.5B-Instruct-GGUF | Q4_K_M, SHA-256 고정 | Apache-2.0 | https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF |
| Microsoft app-local CRT | Visual Studio Redist 14.44 / 14.50 | Microsoft software license terms | https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files |

다운로드 도구는 고정 버전의 공식 파일을 검증합니다. 이 저장소에는 외부 엔진 바이너리·글꼴을 커밋하지 않습니다. 다운로드 후 실행 파일과 각 라이선스는 로컬 `engines/vendor`에 준비합니다.

Ream은 병합 셀의 shrink-to-fit 계산을 보정합니다. 변경 내역·원본 및 수정 소스·의존성 고지는 XLS 빌드 과정에서 생성합니다. 소스 HTML과 변경 설명은 `src/Topdf/engines/ream`에 있습니다.

LibreDWG와 함께 제공하는 DLL의 라이선스, 소스 및 Windows 빌드 레시피는 [DWG 재배포 안내](DWG-REDISTRIBUTION.md)에 정리했습니다. Microsoft 구성 요소의 조건은 [런타임 고지](MICROSOFT-RUNTIME-NOTICE.md)를 참고하세요.

HWP 엔진은 고정된 rhwp 0.8.7 소스에 `src/Topdf/engines/rhwp/layout.patch`를 적용합니다. 원본 MIT 고지와 패치, 소스·패치·바이너리 SHA-256 정보를 엔진 폴더에 포함합니다. 변경 범위와 검증 한계는 `docs/HWP-LAYOUT-FIX.md`를 참고하세요.

- fontTools 4.68.0 (MIT): 로컬 설치 글꼴 이름과 문자 지원 확인에 사용합니다. https://github.com/fonttools/fonttools
