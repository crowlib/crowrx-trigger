# Changelog

## [1.0.1] - 2026-10-20

### Added

- 특정 Execution의 결과와 Cleanup 완료를 기다리는 StartExecution API를 추가했다.
- 현재 Execution, 마지막 Result와 실행 전 설정 준비 오류를 조회할 수 있게 했다.
- 요청한 동안만 Condition 상세 관찰을 기록하는 diagnostics API를 연결했다.
- 실행 상태·오류·Condition 경로를 보여 주는 읽기 전용 Runtime Inspector를 추가했다.
- Execution 수명·진단 요청·Runtime Inspector의 EditMode·PlayMode 회귀 테스트를 추가했다.

### Changed

- CrowRx.Trigger NuGet 1.0.1을 사용하도록 요구 버전과 사용자 문서를 갱신했다.
- Play Mode에서 설정 편집을 막으면서 Runtime 패널은 펼쳐 볼 수 있게 했다.

## [0.1.0] - 2026-10-15

### Added

- UPM 패키지 메타데이터와 Runtime·Editor 어셈블리 구성을 추가했다.
- 기존 Listener를 감싸는 Unity 리스너 컴포넌트와 조건 설정·실행 객체 분리를 추가했다.
- 시간 경과·활성 상태·거리 조건, AND·OR 중첩 그룹과 GameObject 타겟 공유를 추가했다.
- UI Toolkit 인스펙터, 검색 가능한 조건 추가 팝업, 컴포넌트 참조와 순서 편집을 추가했다.
- 씬·프리팹·Undo를 확인하는 EditMode 테스트와 실행 수명·정리를 확인하는 PlayMode 테스트를 추가했다.
