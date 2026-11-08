# 외부 구성 요소

TOPDF 자체 소스의 MIT 라이선스는 아래 외부 구성 요소의 라이선스를 대체하지 않습니다.

| 구성 요소 | 버전 | upstream 표시 라이선스 | 연결 |
|---|---|---|---|
| rhwp | 0.8.7 | MIT | https://github.com/edwardkim/rhwp |
| office2pdf | 0.10.0 | Apache-2.0 | https://github.com/developer0hye/office2pdf |
| MiniPdf | 0.43.2 | Apache-2.0 | https://github.com/mini-software/MiniPdf |
| Ream | 1.33.1 | MIT | https://github.com/alex-krassavin/reamkit |
| LibreDWG | 0.14 | GPL-3.0-or-later | https://github.com/LibreDWG/libredwg |
| ezdxf | requirements.txt 참조 | MIT | https://github.com/mozman/ezdxf |
| matplotlib | requirements.txt 참조 | upstream license | https://matplotlib.org/stable/project/license.html |
| WebView2 | csproj 참조 | Microsoft package terms | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| Tesseract / 언어 모델 | smart-engines.lock.json | Apache-2.0 | https://github.com/tesseract-ocr/tesseract / https://github.com/tesseract-ocr/tessdata_fast |
| Leptonica | OCR DLL 배포본 참조 | upstream BSD-style | https://github.com/DanBloomberg/leptonica |
| pypdfium2 / PDFium | Python lock 참조 | upstream 고지와 포함 종속성 라이선스 | https://github.com/pypdfium2-team/pypdfium2 |
| xlrd | 2.0.2 | BSD | https://github.com/python-excel/xlrd |
| llama.cpp | b12199 | MIT / 포함 LLVM OpenMP 고지 | https://github.com/ggml-org/llama.cpp |
| Qwen2.5-0.5B-Instruct-GGUF | Q4_K_M, SHA-256 고정 | Apache-2.0 | https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF |
| Microsoft app-local CRT | Visual Studio Redist 14.44 | Microsoft software license terms | https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files |

다운로드 도구는 고정 버전의 공식 파일을 검증합니다. 이 저장소에는 외부 엔진 바이너리·글꼴을 커밋하지 않습니다. 다운로드 후 실행 파일과 각 라이선스는 로컬 `engines/vendor`에 준비합니다.

Ream은 병합 셀의 shrink-to-fit 계산을 보정합니다. 변경 내역·원본 및 수정 소스·의존성 고지는 XLS 빌드 과정에서 생성합니다. 소스 HTML과 변경 설명은 `src/Topdf/engines/ream`에 있습니다.

LibreDWG는 별도 프로세스로 실행하지만 이것만으로 배포 의무가 사라지는 것으로 판단하지 않습니다. 공개 바이너리 배포 전 엔진 DLL·하위 의존성·대응 소스 및 글꼴 고지를 포함해 전체 패키지를 검토해야 합니다.
