# 정식 서명 및 별도 PC 검증

2026-11-02 현재 **정식 서명 대기, 별도 PC 검증 대기**입니다. 로컬 시험 성공을 새 PC 검증 완료로 표시하지 않습니다.

## 개인 명의 인증서

한국 거주 개인은 [Microsoft Artifact Signing Public Trust의 개인 발급 대상](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)에 포함되지 않습니다. 해당 문서의 개인 대상은 미국·캐나다이며 한국은 조직 대상에 포함됩니다.

개인 발급을 지원하는 인증기관에서 인증서를 준비해야 합니다. [Certum](https://www.certum.eu/en/code-signing-certificates/)은 개인/회사 Standard 상품과 개인용 Open Source 상품을 안내합니다. 무료 오픈소스 배포를 유지한다면 Open Source가 후보지만, [공식 필요 서류 및 조건](https://support.certum.eu/en/code-signing-required-documents/)에는 상업 배포 제한이 있습니다. 유료 배포 계획이 있다면 Standard 개인용 조건을 확인하세요. 한국 거주자 신원 확인 지원과 최종 금액은 구매 전에 발급기관에 확인해야 합니다.

가입·결제·신분 확인은 신청자 본인이 발급기관에서 수행합니다. 신분증·인증서 암호·개인 키를 GitHub나 채팅으로 보내지 않습니다. 발급 후 USB 토큰 또는 클라우드 제공자의 Windows 연동 프로그램을 통해 CurrentUser/My 인증서 저장소에서 사용할 수 있도록 준비합니다. 클라우드 제품은 해당 제공자의 연동 방식을 별도로 확인해야 합니다.

무료 대안으로 [SignPath Foundation](https://signpath.org/terms)이 있지만 프로젝트 심사와 빌드/승인 절차가 필요하며, 서명자는 개인 명의가 아닌 Foundation입니다. 현재 승인됐거나 사용할 수 있는 것으로 가정하지 않습니다.

## 준비된 서명 절차

- `Sign-Artifact.ps1`: Code Signing EKU·개인 키 접근·기간·공용 신뢰 체인을 검증한 뒤 SHA-256/RFC3161 타임스탬프로 서명합니다. 자체 서명 인증서는 거부하며 신뢰 저장소를 변경하지 않습니다.
- `Build-Installer.ps1`: TOPDF.exe와 TOPDF.dll을 먼저 서명하고 해시 목록을 만듭니다. Inno Setup이 설치·제거 프로그램을 서명하며, 최종 설치 프로그램도 다시 검증합니다. 외부 엔진과 Microsoft/Python 바이너리를 TOPDF 명의로 재서명하지 않습니다.
- 인증서를 지정하지 않으면 `-preview.exe`로만 만듭니다. 인증서를 지정했는데 검증·타임스탬프가 실패하면 빌드를 중단합니다.

```powershell
# 발급된 공용 신뢰 인증서가 Windows 인증서 저장소에 준비된 후
./scripts/Build-Installer.ps1 -CertificateThumbprint '40자리_인증서_지문'
```

서명 동작과 검증 옵션은 [Microsoft SignTool 문서](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)를 기준으로 합니다. 인증서가 없어서 실제 정식 서명 성공 경로는 아직 실행하지 못했습니다. 자체 서명 거부 경로는 확인했습니다. 서명이 있어도 SmartScreen의 모든 경고가 즉시 사라진다고 보장하지 않습니다.

## 별도 Windows PC 검증

`Test-NewPC.ps1`은 기존 TOPDF 설치가 없는 시험 PC/계정에서 실행합니다. 관리자 권한을 요구하지 않고 기본 연결 앱을 변경하지 않습니다.

1. 설치 프로그램 서명과 타임스탬프, 설치 성공과 설치된 파일 해시를 검사합니다.
2. 개발 PC의 PATH와 TOPDF_PYTHON에 의존하지 않도록 시험 프로세스 환경을 제한합니다.
3. **설치된 TOPDF.exe**의 `--verify-install` 진단을 실행합니다. 실제 자동 변환·파일별 WebView2 탐색 성공·원본 해시 보존을 기록합니다.
4. 옵션으로 제거와 제거 등록 정보 정리를 확인합니다.
5. `new-pc-report.json`, `app-diagnostics.json`, 설치/제거 로그를 남깁니다. 외부로 전송하지 않습니다.

```powershell
./scripts/Test-NewPC.ps1 -Installer '정식서명설치파일.exe' -Fixtures '시험문서폴더' -CleanEnvironment -UninstallAfter
```

인증서 발급 전 시험은 `-AllowUnsignedPreview`를 명시해야 합니다. 이 경우 검사가 성공해도 정식 배포 적격으로 표시하지 않습니다. `-CleanEnvironment`는 실행자가 실제 별도 시험 환경임을 확인한 경우에만 사용합니다. HWP/DOCX/XLS/XLSX/DWG/DXF/PDF가 모두 포함되고 설치·변환·미리보기·제거 및 서명이 성공해야 이 도구의 `releaseQualified`가 true입니다. 이는 전체 배포 승인이나 라이선스 검토를 대체하지 않습니다.

업무 문서를 넣은 개인 검증 키트와 결과는 공개 저장소에 업로드하지 않습니다. 보고서에는 문서 이름이 포함됩니다. 보안 제품·SmartScreen·스크립트 정책에 의해 차단되면 우회하거나 해제하지 말고 오류를 기록합니다.

## 남은 공개 배포 조건

실제 인증서 발급 및 서명, 서명 후 최종 파일로 별도 PC 재검증, Windows 버전별 시험, 외부 엔진/DLL의 대응 소스와 고지·SBOM 검토가 필요합니다. 인증서 발급 전의 unsigned 키트를 통과한 보고서를 signed 설치판 검증 결과로 재사용하지 않습니다.
