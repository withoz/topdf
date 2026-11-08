# 설치 파일 제작과 검증

0.3.1은 Windows 10/11 x64용 **로컬 시험 설치판**입니다. 공개 GitHub에는 설치 스크립트와 앱 소스를 올리며 바이너리 공개 릴리스는 아직 게시하지 않습니다.

## 포함 내용

- .NET 10 런타임을 포함한 TOPDF, 현재 5가지 문서용 엔진
- 독립 CAD 변환용 Python 3.14.3 및 고정 버전 패키지
- 엔진 라이선스/고지, LibreDWG 소스 아카이브, TOPDF 소스 ZIP, 파일별 SHA-256 목록
- 한국어 설치 화면, 시작 메뉴 바로가기, 선택 가능한 바탕 화면 바로가기와 연결 프로그램 등록
- 사용자별 설치/제거. 기존 기본 연결 앱은 변경하지 않습니다.

WebView2가 없는 PC에서는 Microsoft의 서명된 Evergreen 설치 도우미를 실행하므로 **최초 설치에 인터넷이 필요합니다**. 설치 후에도 없는 경우 완료 화면에서 알립니다. [Microsoft 배포 지침](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution)을 따릅니다.

설치는 관리자 권한을 요구하지 않는 사용자별 방식입니다. [Inno Setup 권한 설명](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)을 참고하세요.

## 재현 빌드

Windows x64의 .NET 10 SDK, Git, Python 3.14 x64/pip, Node.js와 공식 Inno Setup 7 컴파일러가 필요합니다.

```powershell
./scripts/Setup-Engines.ps1
./scripts/Setup-SmartEngines.ps1
./scripts/Prepare-PythonRuntime.ps1
./scripts/Build-Installer.ps1 -Compiler 'C:\path\to\ISCC.exe' -Bootstrapper 'C:\path\to\MicrosoftEdgeWebview2Setup.exe'
```

공식 [WebView2 배포 페이지](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution)에서 bootstrapper를 받아 Authenticode 서명을 확인하세요. Python 출력 폴더가 이미 있으면 재사용하거나 다른 `-Output`을 지정하세요. 빌드의 `-PythonRuntime`은 준비된 내장 Python 루트를 받습니다. 생성 위치는 `artifacts/installer/TOPDF-Setup-0.3.1-win-x64-preview.exe`입니다. 고유한 staging 폴더를 사용하고 고객 문서·캐시·변환 결과를 제외합니다.

## 데이터와 제거

변환 캐시·로그와 미리보기 데이터는 `%LOCALAPPDATA%\TOPDF`에 저장합니다. `TOPDF_DATA_DIR`로 시험용 위치를 지정할 수 있습니다. 제거 시 원본 및 사용자 데이터를 삭제하지 않습니다. 자동 만료·앱 내 캐시 삭제는 후속 보강 대상입니다.

## 공개 배포 전 남은 확인

- 앱과 설치 EXE 코드 서명, 깨끗한 Windows VM 및 WebView2 없는 PC의 설치·업데이트·제거 시험
- LibreDWG에 딸린 DLL의 대응 소스/빌드 절차를 포함한 전체 배포물 라이선스 검토와 SBOM 확정
- 글꼴이 다른 PC, 암호·대용량 문서 및 복잡한 CAD 객체 검증

로컬 시험 성공이 신규 PC 전체 호환성이나 배포 적합성의 확정을 뜻하지 않습니다.
