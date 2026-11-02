# 개발 안내

현재는 Windows x64 WPF 앱입니다. .NET 10 SDK와 WebView2 Runtime이 필요합니다.

## 엔진 준비

```powershell
pwsh -File scripts/Setup-Engines.ps1
```

프로젝트 내 `.local/engine-downloads`에 다운로드하고 `src/Topdf/engines/vendor`에 준비합니다. 두 경로 모두 Git에서 제외됩니다. 버전과 다운로드 해시는 `scripts/engines.lock.json`에 고정합니다.

XLS만 다시 빌드하려면 다음 명령을 사용합니다.

```powershell
cd tests/OpenSource/ream-build
npm ci --ignore-scripts
node build.mjs
```

이 과정은 Ream의 병합 영역 너비 계산을 수정한 브라우저 번들을 만듭니다. 원본 소스의 해시와 수정 내용을 `vendor/ream/source/change.json`에 기록합니다. HTML 진입점과 고지는 `src/Topdf/engines/ream`에서 복사합니다.

## 테스트

- `ExportSmoke`: 파일 선택 목록 저장, 이름 충돌, 원본 보존, 실패 후 계속 처리. 별도 문서 엔진 없이 실행 가능합니다.
- `PreviewSmoke --export-ui`: Windows 데스크톱 세션에서 체크박스 바인딩·개수·전체 선택 검사.
- `PreviewSmoke --shell-open <파일>`: 경로 인수 → 변환 → 미리보기 검사. 연결 프로그램 레지스트리는 쓰지 않습니다.
- `PreviewSmoke --folder <폴더> <결과폴더>`: 사용자가 준비한 실제 샘플 검사. 문서·결과·캐시는 커밋하지 마세요.
- `Smoke`: 설치형 호환 경로를 위한 RTF 검사로 LibreOffice가 필요합니다.

GitHub Actions는 Windows에서 XLS 번들 빌드, .NET 빌드 및 `ExportSmoke`를 실행합니다. 실제 고객 문서 재현성과 Windows 탐색기 기본 앱 선택까지 자동 검증하는 것은 아닙니다.

## 개인정보

버그 재현에는 실제 고객 문서를 사용하지 마세요. 출력 경로·본문·파일명·금액이 로그에 남을 수 있습니다. 개인 회귀 시험 스크립트와 보고서는 공개 저장소에서 제외합니다.
