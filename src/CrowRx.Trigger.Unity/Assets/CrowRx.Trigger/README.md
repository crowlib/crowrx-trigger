# CrowRx.Trigger.Unity

시간 경과, 오브젝트의 활성 상태, 거리 같은 조건이 만족됐을 때 원하는 동작을 실행하는 Unity 패키지다. 인스펙터에서 조건을 추가하고 AND·OR로 조합한 뒤 UnityEvent를 연결해 사용할 수 있다.

## 설치 준비

Unity 6000.0 이상에서 사용한다. 패키지를 사용하기 전에 NuGet으로 다음 패키지를 설치해 둬야 한다. NuGetForUnity를 이용해 설치할 수 있다.

- **CrowRx.Trigger 1.0.1**과 해당 의존성
- **R3 1.3.1**

Unity Package Manager용 **R3.Unity 1.3.1**과 **UniTask 2.5.11**도 필요하다. 두 패키지는 이 패키지의 UPM 의존성으로 선언되어 있다. NuGet 패키지는 별도로 설치해야 하며 DLL은 함께 제공하지 않는다.

## 처음 사용하기

예를 들어, 지정한 오브젝트가 기준점에서 5m 안으로 들어오면 한 번만 동작하도록 설정해 보자.

1. GameObject에 **Add Component > CrowRx > Trigger > Trigger Listener**를 추가한다.
2. **+ 조건 추가**에서 **거리**를 선택한다.
3. 거리 조건의 **기준점**에 Transform을, **타겟**에 검사할 GameObject를 지정하고 최대 거리를 5로 설정한다.
4. **성공 시 실행**에 호출할 메소드를 연결한다.
5. **실행 설정 > 성공 후 종료 · 한 번만 실행**을 켠다.
6. Play Mode를 시작한다. 기본 시작 시점은 **OnEnable**이므로 컴포넌트가 활성화되면 검사를 시작한다.

한 번만 실행 옵션을 끄면 성공 후 다시 검사한다. 조건이 계속 만족되는 동안 성공 이벤트도 반복되므로, 문 열기처럼 한 번만 처리할 동작에는 이 옵션을 켜 두면 된다.

## 조건 추가와 조합

**+ 조건 추가**에서 조건이나 그룹을 선택하면 해당 리스너 안에 설정이 저장된다. 별도 GameObject나 컴포넌트를 만들 필요는 없다.

조건이 하나면 그대로 사용하고, 둘 이상이면 **결합 방식**을 선택한다.

| 설정 | 성공하는 경우 |
|---|---|
| AND | 모든 조건이 만족할 때 |
| OR | 하나 이상의 조건이 만족할 때 |
| NOT | 해당 조건이나 그룹의 결과가 반대일 때 |

**조건 그룹** 안에도 조건과 그룹을 추가할 수 있다. 예를 들어 바깥 그룹을 AND로 설정하고, 안쪽에 OR 그룹을 넣으면 `거리 조건 AND (활성 상태 조건 OR 시간 경과 조건)`처럼 구성할 수 있다.

같은 목록 안에서 드래그해 순서를 바꿀 수 있다. 다만 목록 순서는 조건을 차례로 완료시키는 순서가 아니다. 비동기 검사는 함께 진행하므로, 첫 번째 조건이 끝난 뒤 두 번째 조건을 시작하는 순차 실행은 제공하지 않는다.

## 기본 조건

| 조건 | 사용 방법 |
|---|---|
| 시간 경과 | 검사를 시작할 때부터 지정한 시간이 지나면 만족한다. 기본값은 1초다. 게임 시간 또는 `Time.timeScale`의 영향을 받지 않는 실제 시간을 선택할 수 있다. 성공 후 다시 검사하거나 중지 후 재시작하면 타이머도 다시 시작한다. |
| 활성 상태 | 타겟 GameObject의 `activeInHierarchy`가 true이면 만족한다. NOT을 켜면 비활성 상태를 검사한다. |
| 거리 | 기준점 Transform과 타겟 GameObject 사이의 월드 거리가 최대 거리 이하이면 만족한다. 기본 최대 거리는 5m다. |

거리의 기준점은 직접 지정해야 한다. 비워 두어도 리스너 위치를 자동으로 사용하지 않는다. 시간과 거리는 0 이상의 유한한 값으로 설정한다.

타겟이나 기준점이 없거나 파괴되면 유효하지 않은 조건으로 처리한다. NOT을 켜도 누락된 오브젝트를 만족한 조건으로 취급하지 않는다. 기본 조건은 Update 주기에 맞춰 검사하지만, 성공 이벤트가 정확히 한 프레임 뒤에 호출되는 것은 보장하지 않는다.

## 조건 컴포넌트 재사용

여러 리스너가 같은 설정을 쓰게 하려면 **CrowRx > Trigger** 아래의 조건 컴포넌트를 추가하고, 리스너의 조건 목록으로 드래그하면 된다. **컴포넌트 참조** 항목에서 직접 선택할 수도 있다.

GameObject를 드래그할 때는 조건 컴포넌트가 하나만 붙어 있어야 한다. 여러 개라면 원하는 컴포넌트를 직접 드래그하거나 선택 필드에서 지정한다. 목록에서 참조를 제거해도 원본 컴포넌트는 삭제되지 않는다.

설정을 공유해도 각 리스너의 타이머와 실행 상태는 독립적이다. 조건 컴포넌트는 설정을 제공하는 용도이므로, 검사 시작 후 원본 컴포넌트를 비활성화하거나 삭제해도 이미 시작한 검사는 중지되지 않는다. 검사를 중지하려면 **리스너**를 비활성화하거나 `StopListen()`을 호출한다.

## 여러 조건에 같은 타겟 지정하기

**타겟 공유 그룹**에 GameObject를 지정하면 그룹 안의 활성 상태·거리 조건에 같은 타겟을 적용할 수 있다. 거리 조건의 기준점은 각 조건에서 따로 지정한다.

- 타겟 공유 그룹 안에 타겟 공유 그룹을 넣으면 **부모의 타겟**을 사용한다. 적용되는 타겟은 인스펙터에서 읽기 전용으로 표시된다.
- 중간에 **일반 조건 그룹**이 있으면 그 안까지 타겟을 전달하지 않는다.
- 서로 다른 타겟을 검사하려면 일반 AND·OR 그룹 아래에 타겟 공유 그룹들을 나란히 배치한다.

컴포넌트 참조에 타겟을 적용해도 원본 컴포넌트의 설정은 바뀌지 않는다. 타겟 공유는 GameObject를 대상으로 한다.

## 시작·중지와 이벤트

| 시작 시점 | 동작 |
|---|---|
| OnEnable | 리스너가 활성화될 때마다 시작한다. 기본값이다. |
| Start | 최초 `Start`에서 한 번 시작한다. 비활성화 후 다시 켜도 자동으로 재시작하지 않는다. |
| Manual | 코드나 UnityEvent에서 `StartListen()`을 호출해 시작한다. |

리스너가 비활성화되면 중지를 요청하고, 파괴되면 이벤트 연결과 남은 검사를 정리한다. 빠르게 껐다 켜도 이전 검사의 정리가 끝난 뒤 재시작한다. 정리 완료를 기다려야 하는 코드에서는 `await StopListenAsync()`를 사용하면 된다. 수동 시작은 Play Mode에서 리스너가 활성화된 상태일 때 사용할 수 있다.

**성공 시 실행**은 조건 만족 시 호출하는 UnityEvent다. 코드에서는 `OnSuccess` 또는 `Success`로 구독할 수 있다. **오류 발생 시 실행**은 오류 메시지를 받는 UnityEvent이며, 코드에서는 `OnError` 또는 원래 예외를 받는 `Exception` 이벤트를 사용한다. 오류는 Unity Console에도 기록된다.

고급 설정의 **유효하지 않은 조건의 오류 알림 생략**을 켜면 검사 대상이 유효하지 않을 때 오류 알림 없이 구독을 끝낸다. 비어 있는 조건 그룹, 누락된 컴포넌트 참조, 순환 참조 같은 구성 오류까지 무시하는 옵션은 아니다.

설정은 최초 초기화할 때 확정된다. 보통 첫 검사 시작 시 자동으로 초기화되며, 이후 설정이나 원본 컴포넌트를 수정해도 반영되지 않는다. Play Mode에서는 인스펙터가 읽기 전용이므로, 설정을 변경하려면 Play Mode를 종료하고 수정한다.

## Execution 결과와 Runtime 확인

특정 실행의 결과를 기다리려면 Play Mode의 활성 리스너에서 `StartExecution()`을 호출한다. 설정 생성 실패는 `PreparationError`에 기록하고 호출자에게 예외를 전달한다. 실행이나 Cleanup 중 중복 호출도 예외로 알린다. 기존 UnityEvent 연결에는 `StartListen()`을 그대로 사용하면 된다.

```csharp
var execution = listener.StartExecution();
var result = await execution.Completion;
Debug.Log($"Execution #{result.ExecutionId}: {result.Kind}");
if (result.HasErrors)
    foreach (var error in result.Errors) Debug.Log($"{error.Phase}: {error.Exception.Message}");
```

Execution은 조건 감시 시작부터 Cleanup 완료까지다. 결과는 `Satisfied`, `Canceled`, `Unavailable`, `Faulted` 중 하나이며 실행 오류와 취소는 Task 예외 대신 Result로 받는다. 성공 알림을 시작했다면 callback이나 Cleanup이 실패해도 `Satisfied`를 유지하고 오류를 별도로 남긴다. 오류가 있는 실행은 자동 반복을 중단한다.

한 번만 실행 옵션을 끄면 다음 Execution이 시작된다. 이전 핸들의 `Completion`은 다음 실행이나 UnityEvent가 시작한 별도 작업을 기다리지 않는다. `StopListenAsync()`도 호출 당시 실행의 Cleanup만 기다린다. `CurrentExecution`과 `LastResult`는 서로 다른 실행일 수 있으므로 ID로 구분한다.

Inspector의 **Runtime**을 펼치면 현재 ID·State, 재시작 예약, 마지막 Result·오류, Condition별 최근 관찰 상태를 볼 수 있다. `Satisfied · Errors present`는 성공 알림을 시작한 뒤 오류도 관찰한 결과다. Execution 생성 전 오류는 **Before Execution · Preparation error**로 별도 표시한다.

Runtime 패널은 100ms마다 저장된 정보만 읽는다. 실행 전에는 `Idle / Not initialized`를 표시하며 실행 트리를 만들지 않는다. 상세 진단은 펼쳐진 패널이 있는 동안만 기록하고, 접거나 Inspector를 닫으면 요청을 해제한다. `Unknown`은 관찰하지 않은 값이며, `Pending`은 검사 완료 대기다. 미충족을 오류로 취급하지 않는다. 경로는 해당 Execution의 실제 Condition 순서이며 에셋 ID가 아니다. 사용자 조건 내부나 기록 요청 이전의 상태는 표시되지 않을 수 있다.

코드에서도 `AcquireDiagnostics()`와 `CopyConditionDiagnostics(List<ConditionDiagnostic>)`로 같은 정보를 읽을 수 있다. 복사는 목록을 비우고 저장된 현재 실행 또는 Idle일 때 마지막 실행의 정보만 채운다. 여러 요청은 각각 Dispose해야 하며, 요청만으로 초기화하거나 조건을 다시 검사하지 않는다.

`CheckConditionsImmediately()`는 Execution 없는 동기 확인이다. 이 호출이 성공 이벤트를 내고 현재 감시를 중지하더라도, 감시 Execution의 결과는 일반 중지 규칙을 따른다.

## 코드에서 설정하기

코드로 조건을 추가할 때는 리스너가 자동으로 시작하기 전에 설정을 끝내야 한다. 다음 예제는 비활성 GameObject에서 준비한 뒤 수동으로 시작한다.

```csharp
using CrowRx.Trigger.Unity;
using UnityEngine;

public static class TriggerExample
{
    public static TriggerListenerBehaviour Create(Transform origin, GameObject target)
    {
        var owner = new GameObject("Distance Trigger");
        owner.SetActive(false);

        var listener = owner.AddComponent<TriggerListenerBehaviour>();
        listener.StartMode = ListenerStartMode.Manual;
        listener.IsOneShot = true;
        listener.Conditions.Add(new DistanceConditionDefinition
        {
            Origin = origin,
            Target = target,
            MaxDistance = 5f
        });
        listener.OnSuccess.AddListener(() => Debug.Log("조건 만족"));

        owner.SetActive(true);
        listener.StartListen();
        return listener;
    }
}
```

반환된 리스너를 `StopListen()`으로 중지하고 `StartListen()`으로 다시 시작할 수 있다. 재시작해도 처음 확정한 설정을 사용한다.

## 사용자 조건 추가하기

`[Serializable]` 클래스를 `ConditionDefinition`에서 파생하고 `CreateRuntimeCondition()`에서 새 실행 조건을 반환하면 된다. 실행 조건은 `CrowRx.Trigger.Condition`이나 Update 대기를 제공하는 `UpdateCondition`을 상속해 구현할 수 있다.

설정 클래스에 `[ConditionMenu("표시 이름", "분류")]`를 지정하고 매개변수 없는 생성자를 제공하면 **+ 조건 추가** 팝업에 나타난다. 실행 조건에는 필요한 설정을 복사해서 전달하고, 실행 중 원본 설정을 다시 읽지 않도록 한다. 별도 조건 컴포넌트가 필요하면 `ConditionBehaviour`를 상속하고 `Definition`에서 설정을 제공한다.
