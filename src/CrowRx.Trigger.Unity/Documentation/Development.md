# CrowRx.Trigger.Unity 개발 가이드

이 문서는 패키지 구현과 검증·배포를 관리하는 개발자를 위한 문서다. 사용자 안내는 패키지의 [README](../Assets/CrowRx.Trigger/README.md)에 둔다.

## 개발 위치와 배포 경계

배포 루트는 Unity 프로젝트의 `Assets/CrowRx.Trigger`다. 개발 프로젝트에서는 Assets 아래의 소스를 직접 컴파일하며, 같은 폴더를 UPM으로 중복 등록하지 않는다. Git UPM 배포 시에도 이 폴더를 패키지 경로로 지정한다.

이 문서는 프로젝트 루트의 `Documentation`에 보관한다. 패키지 루트 밖에 있으므로 `Assets/CrowRx.Trigger`만 배포하면 함께 포함되지 않는다. 프로젝트 전체를 배포 대상으로 묶지 않는다.

| 위치 | 역할 |
|---|---|
| `Assets/CrowRx.Trigger/package.json` | 패키지 ID·버전·최소 Unity 버전·UPM 의존성 |
| `Assets/CrowRx.Trigger/Runtime` | 리스너 컴포넌트, 직렬화 설정, Unity 실행 조건 |
| `Assets/CrowRx.Trigger/Editor` | UI Toolkit 인스펙터와 설정 검증 |
| `Assets/CrowRx.Trigger/Tests/Editor` | 설정·직렬화·편집기 테스트 |
| `Assets/CrowRx.Trigger/Tests/Runtime` | 실행 수명과 비동기 정리 테스트 |
| `Documentation` | 배포에 포함하지 않는 개발 기록 |

패키지 ID는 `com.crowlib.crowrx-trigger`, 표시 이름과 기본 네임스페이스는 `CrowRx.Trigger.Unity`다. 초기 버전은 `0.1.0`, 최소 Unity 버전은 `6000.0`, 라이선스는 MIT다.

NuGet의 CrowRx.Trigger 1.0.0과 해당 의존성, R3 1.3.1은 선행 설치를 전제로 한다. DLL 복사나 자동 설치는 구현하지 않는다. UPM 의존성은 R3.Unity(`com.cysharp.r3`) 1.3.1과 UniTask(`com.cysharp.unitask`) 2.5.11이다.

Runtime 어셈블리는 `R3.Unity`와 `UniTask`를 참조하고 NuGet DLL은 기본 사전 컴파일 어셈블리 참조를 사용한다. Editor 어셈블리는 Runtime을 참조하며 Editor 플랫폼으로 제한한다. Unity가 생성한 `.meta`를 소스와 함께 관리한다.

## 설정과 실행 객체 분리

기존 NuGet의 `Listener`, `ConditionGroup`, `ConditionGroupShareTarget<GameObject>`를 사용하고 Unity 연결부를 추가하는 구조다. 코어의 Task 실행부를 재구현하지 않는다.

- `ConditionDefinition`은 직렬화할 설정이다. `CreateRuntimeCondition()`에서 새 `ICondition`을 만든다.
- `ConditionBehaviour`는 설정 공급 컴포넌트다. 컴포넌트 자체가 실행 조건은 아니다.
- `ComponentConditionReference`는 컴포넌트의 설정으로부터 별도의 실행 조건을 만든다. 같은 컴포넌트를 여러 번 참조해도 실행 상태를 공유하지 않는다.
- `TriggerListenerBehaviour`는 설정을 실행 트리로 구성하고 기존 Listener에 수명과 이벤트를 연결한다.

루트 목록이 하나면 그 조건을 직접 사용하고, 여러 개면 AND·OR 그룹을 만든다. 조건 목록의 순서와 호출 순서는 유지하되 비동기 검사는 함께 진행한다. 순차 완료나 순서대로 만족하는 기능은 제공하지 않는다.

트리는 최초 `Initialize()`에서 생성한다. `StartListen()`과 `CheckConditionsImmediately()`는 필요하면 초기화를 호출한다. 중지와 재시작에는 같은 트리를 재사용하며, 최초 구성 이후 원본 설정을 다시 읽지 않는다. 설정 목록의 변경을 런타임 트리에 반영하는 기능은 제공하지 않는다.

설정 공급 컴포넌트를 비활성화하거나 파괴해도 만들어 둔 실행 조건을 중지하지 않는다. 실제 Target과 Origin의 유효성은 실행 조건에서 계속 확인한다. 초기화·활성화·구독 정리는 실행 조건이 담당하며 공급 컴포넌트의 OnEnable·OnDisable에 직접 연결하지 않는다.

## 실행 수명과 오류 전달

기본 자동 시작은 OnEnable이다. Start 모드는 최초 MonoBehaviour.Start에서 시작하고, Manual은 명시적 호출을 기다린다. 수동 시작은 Play Mode에서 활성화된 리스너에만 적용한다. 기본은 성공 후 반복이며 false→true 변화 감지가 아니다.

OnDisable은 중지를 요청한다. 빠르게 재활성화하면 기존 검사의 정리와 비활성화 이후 다시 시작하도록 코어 Listener에 맡긴다. OnDestroy는 사용자 이벤트 연결을 해제하고 비동기 정리를 관찰한다. 정리 완료가 필요한 호출자는 `StopListenAsync()`를 기다린다.

`OnSuccess`와 `OnError`는 UnityEvent, `Success`와 `Exception`은 C# 이벤트다. 오류는 Unity Console에도 기록한다. 파괴 이후 사용자 콜백은 전달하지 않으며 남은 정리 중 오류는 로그로 관찰한다. 오류 이벤트 처리기의 예외도 로그로 남긴다.

유효하지 않은 조건의 오류 알림 생략은 코어 Listener의 `IsValid` 실패 처리에 적용한다. 설정 누락·빈 그룹·순환 참조 등 실행 트리 생성 오류까지 생략하지 않는다.

`UpdateCondition`은 `UniTask.NextFrame`의 Update 시점 대기를 Task로 연결한다. 다음 프레임 대기와 코어 Listener의 비동기 처리 때문에 이벤트의 정확한 한 프레임 지연은 보장하지 않는다.

## 조건과 타겟 공유 계약

시간 경과는 활성화할 때마다 타이머를 시작한다. 기본은 1초이며 scaled·unscaled 시간을 선택한다. 활성 상태는 `activeInHierarchy`를 확인한다. 거리는 명시적으로 지정한 Origin Transform과 Target GameObject 사이의 월드 거리가 최대 거리 이하인지 확인하며 기본은 5m다. Origin을 리스너 위치로 대체하지 않는다.

시간·거리 설정은 0 이상의 유한한 값이어야 한다. 실제 검사 대상이 없거나 파괴되면 NOT과 관계없이 `IsValid`가 false다.

첫 버전의 타겟 공유는 `GameObject` 타입만 지원한다. 기존 코어 규칙을 유지한다.

- 같은 타입의 중첩 공유 그룹은 부모 타겟을 사용한다.
- 일반 그룹은 타겟 전파 경계다. 일반 그룹 내부에는 자동으로 전달하지 않는다.
- 타겟을 분리하려면 일반 AND·OR 그룹 아래에 공유 그룹을 나란히 배치한다.
- 거리의 Origin은 타겟 공유로 변경하지 않는다.
- 실행 객체에만 적용하며 원본 컴포넌트 설정은 변경하지 않는다.

사용자 조건도 `IConditionTargetSharable<GameObject>`를 구현하면 기존 공유 규칙에 따라 타겟을 받을 수 있다. 추가적인 유효성은 해당 실행 조건에서 검사한다.

## 인스펙터와 확장 규칙

UI Toolkit의 공통 조건 편집기를 리스너·조건 컴포넌트 인스펙터에서 사용한다. `[SerializeReference]` 목록에 내부 조건, 컴포넌트 참조, 중첩 그룹을 순서대로 보관한다.

추가 팝업은 `[ConditionMenu]`가 지정된 구체적인 `ConditionDefinition` 파생 클래스를 찾는다. 설정 클래스는 공개된 `[Serializable]` 클래스와 매개변수 없는 생성자를 제공한다. 실행 객체는 `Condition` 또는 `UpdateCondition`을 상속할 수 있다. 설정의 값과 Unity 객체 참조를 실행 객체에 복사하며 원본 설정을 실행 중 읽지 않는다.

별도 설정 컴포넌트는 `ConditionBehaviour`를 상속하고 `Definition`을 제공한다. 컴포넌트 참조를 제거해도 원본 컴포넌트는 삭제하지 않는다. GameObject 드래그는 조건 컴포넌트가 정확히 하나일 때 받아들이고, 여러 개면 컴포넌트를 직접 선택하게 한다.

목록 재정렬은 같은 목록 안에서 지원한다. 서로 다른 그룹 사이 이동은 제공하지 않는다. 공유되는 부모 타겟은 읽기 전용으로 표시하고 Play Mode에서는 설정 편집을 막는다. 편집은 Undo·Redo와 프리팹 override가 유지되도록 처리한다.

## 검증 방법과 현재 기록

Unity Test Runner에서 다음 어셈블리를 실행한다.

| 모드 | 어셈블리 | 확인 범위 |
|---|---|---|
| EditMode | `CrowRx.Trigger.Unity.Editor.Tests` | 설정 독립성, 중첩·타겟 공유, 잘못된 설정, 직렬화, Undo·Redo, 인스펙터 |
| PlayMode | `CrowRx.Trigger.Unity.Tests` | 반복·단발 실행, 이벤트, 중지·재시작·파괴, 비동기 정리, 시간 조건 |

2026-10-15에 Unity **6000.6.5f1 Editor**에서 확인한 기능 구현 검증 기록이다.

- EditMode **14/14 통과**: 씬 저장·재로드, 프리팹 적용·되돌리기, Undo·목록 재정렬·삭제, 중첩 필드, 개별 조건 편집기, 추가 팝업 검색·선택과 레이아웃 등을 포함한다.
- PlayMode **10/10 통과**: 단발·반복·이벤트와 실행 트리 재사용, 중지 후 콜백 차단, 빠른 비활성화·재활성화, 남은 자식 검사 정리, 공급 컴포넌트와 실제 타겟의 파괴, 리스너 파괴, unscaled 시간과 타이머 재시작, 시간 조건 NOT 등을 포함한다.
- 최종 컴파일과 Console 확인에서 오류·경고가 없었다.

테스트는 Preview Scene과 임시 에셋을 사용하고 정리하며 사용자 씬을 저장하지 않는다. 씬 직렬화 테스트는 Test Runner의 미저장 씬 제약을 피하기 위해 이름이 있는 빈 씬 fixture를 복사해 사용한다.

위 결과는 당시 Unity Editor에서의 기능 검증이다. Mono·IL2CPP Player 동작과 성능·GC 측정은 별도로 확인해야 한다. 문서만 변경한 경우 이 기록을 새 테스트 실행 결과처럼 갱신하지 않는다.
