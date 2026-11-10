# 0.3.13 Windows 편집 명령과 Undo 안정화

2026-11-10. 기존 설치 ZIP 0.3.11과 0.3.12 소스 보관본은 유지합니다.

## 원인과 수정

그리기 시작 시 마우스 캡처만 하고 키보드 포커스를 이동하지 않아 페이지 입력란/배율 컨트롤이 Ctrl+Z를 받을 수 있었습니다. 단축키는 PreviewKeyDown의 개별 분기로, 리본/메뉴는 직접 메서드 호출로 나뉘어 있었습니다.

- 그리기·끝점/크기 조절 시작 시 캔버스에 키보드 포커스를 지정합니다.
- ApplicationCommands.Undo/Redo/Copy/Cut/Paste/Delete/SelectAll/Save/SaveAs를 CommandBindings로 연결합니다. Ctrl+Shift+Z, Ctrl+Shift+S도 명시적으로 연결합니다.
- 리본 Undo/Redo는 현재 키보드 포커스에 맞춰 입력창 또는 캔버스를 명확히 대상으로 삼습니다. 단순 Click 이벤트를 발생시키는 검사 대신 실제 Button.OnClick 명령 실행 경로도 검사합니다.
- 실행할 수 없는 명령은 비활성화합니다. Undo/Redo 아이콘은 비활성 상태에서 흐리게 표시합니다. 텍스트 이력이 없다고 객체 이력으로 넘어가지 않습니다.
- 미완성 도형·선택 영역은 Ctrl+Z로 해당 제스처만 취소합니다. 진행 중인 끝점·크기·이동도 취소하고 늦게 들어온 마우스 놓기가 재기록되지 않게 합니다.
- 완료된 작업은 기존 모델 스냅샷 이력으로 되돌리며 새 작업은 Redo 분기를 비웁니다.
- 우클릭 메뉴의 객체 명령은 같은 캔버스 명령으로 연결합니다. 텍스트 입력창의 기본 편집 명령은 우선합니다.
- native PDF 보기의 기본 복사·검색 등은 가로채지 않습니다. 도형 복사는 기존 내부 편집기 버퍼를 유지하며 다른 프로그램과의 도형 클립보드 연동을 새로 구현한 것은 아닙니다.

## 검증과 보관

CommandsUiTest는 페이지/배율 컨트롤 이후의 그리기 포커스, Undo/Redo 상태, 리본/컨텍스트 명령 대상, 미완성 도형과 끝점·크기·그룹 이동 취소, 늦은 완료 이벤트, Cut/Paste/Delete와 분기 이력, 메모의 네이티브 글자 Undo/Redo, 프로젝트 저장과 원본 보존을 검사합니다.

`pwsh -NoProfile -File scripts/Test-EditorAudit.ps1 -IncludeUi`에 명령 검사를 추가했습니다. 현재 17단계이며 결과는 artifacts/audit-0.3.13에 보관합니다. 실제 텍스트 커서 검사는 창 활성화와 입력 포커스가 준비된 뒤 확인합니다.

주요 코드: PdfEditorView.Commands.cs, xaml.cs, Drawing.cs, Endpoints.cs, XAML 리본/ContextMenu와 tests/PreviewSmoke/CommandsUiTest.cs.
