# CrowRx.Trigger

비동기 조건을 AND·OR로 묶고, 조건을 만족하면 이벤트를 실행하는 라이브러리다. 조건 초기화와 활성화, 검사 취소, 중첩 그룹 정리, 재구독을 함께 관리한다.

## 환경과 설치

- 대상은 .NET Standard 2.1, C# 9이며 Unity 6 이상에서 사용하는 것을 전제로 한다.
- NuGet으로 설치한다. Unity에서는 NuGetForUnity를 통해 설치할 수 있다.
- 의존 패키지는 CrowRx.Core 1.0.12와 CrowRx.Pool 1.0.6이다.
- 공개 API의 XML 문서는 빌드 시 DLL과 함께 생성하며 NuGet 패키지에도 포함한다.

Core의 수명 검증은 데스크톱 .NET 호스트에서, Unity 연결부는 별도 개발 프로젝트의 Editor에서 수행한다. Player·IL2CPP 동작과 성능은 별도로 확인해야 한다. 라이브러리 자체에 Editor 전용 API 의존성은 없다.

## 조건 만들기

`Condition`을 상속해 유효성, 만족 여부, 비동기 검사를 구현한다. 검사 작업의 완료는 **이번 확인이 끝났다는 뜻**이다. 실제 만족 여부는 `IsSatisfied`로 전달한다.

```csharp
using System.Threading;
using System.Threading.Tasks;
using CrowRx.Trigger;

public sealed class FlagCondition : Condition
{
    public bool Value { get; set; }
    public override bool IsValid => true;
    public override bool IsSatisfied => IsNot ? !Value : Value;

    public override Task CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
```

위 예시는 외부에서 바뀌는 값을 확인하는 단순 조건이다. 실제 비동기 작업은 전달받은 토큰으로 취소하고 자원을 정리한 뒤 종료해야 한다. `IsNot`의 반전도 파생 조건에서 구현한다. `OnInitialize`, `OnActivate`, `OnDeactivate`에서 필요한 자원을 준비하거나 정리할 수 있다.

## 조건 묶기와 구독

```csharp
var first = new FlagCondition();
var second = new FlagCondition();

ConditionGroup group = ConditionGroup.Create(
    new ICondition[] { first, second },
    ConditionCombineType.And);

var listener = new Listener(group) { IsOneShot = true };
listener.Success += () => System.Console.WriteLine("조건을 만족했다.");
listener.Exception += error => System.Console.WriteLine(error.Message);
listener.StartListen();

// 같은 실행 컨텍스트에서 상태를 변경한다.
first.Value = true;
second.Value = true;
```

사용을 마칠 때는 현재 검사를 취소하고 정리를 기다린다.

```csharp
await listener.StopListenAsync();
```

`Create`는 최상위 null 원소를 생략하고 자식 참조와 순서를 새 목록에 복사한다. 전체 그래프를 검증한 미초기화·비활성 기본 그룹을 반환하며, 자식 초기화와 추가 훅은 호출하지 않는다. 리스너가 시작할 때 그룹과 자식을 초기화한다. 빈 그룹은 유효하지 않다. OR 그룹도 모든 자식이 유효해야 검사할 수 있다.

기존 그룹이나 `ConditionGroupShareTarget<TTarget>` 같은 파생 그룹에는 `AddCondition`·`AddConditions`를 사용한다. 부모가 이미 초기화되어 있으면 새 자식만 초기화한다. 여러 조건을 추가하다 실패하면 앞서 추가된 조건은 유지된다.

| 리스너 기능 | 동작 |
|---|---|
| `StartListen()` | 초기화·활성화 후 검사를 시작한다. 이미 구독 중이면 유지하며 종료 중이면 정리 후 시작을 예약한다. |
| `IsOneShot` | true면 성공 후 자동으로 재구독하지 않는다. 기본값 false에서는 성공한 경우에만 정리 후 다시 구독한다. |
| `IsIgnoreNotAvailableCondition` | true면 조건이 없거나 유효하지 않을 때 오류 알림 없이 종료한다. 검사 예외까지 무시하는 설정은 아니다. |
| `StopListen()` | 중지를 요청한다. 정리 완료까지 기다리지는 않는다. |
| `StopListenAsync()` | 현재 구독의 검사 정리·비활성화·상태 해제를 기다린다. 정리 오류는 `Exception` 이벤트로 알린다. |
| `CheckConditionsImmediately()` | 비동기 검사 없이 현재 상태를 확인한다. 만족하면 성공 이벤트를 동기 호출하고 중지를 요청한다. |

시작·중지·상태 변경은 같은 실행 컨텍스트에서 수행한다. Unity 메인 스레드에서 사용할 때도 이 규칙을 지킨다. `Task.Yield()`는 실행을 양보하는 용도이며 정확히 다음 프레임에 실행됨을 보장하지 않는다. 성공 이벤트 핸들러에서 중지를 요청하면 자동으로 재구독하지 않는다.

## Execution 결과 기다리기

`StartExecution()`은 조건 감시 시작부터 Cleanup 완료까지 한 번의 실행을 가리키는 `ListenerExecution`을 반환한다. 이미 실행 또는 Cleanup 중이면 `InvalidOperationException`을 던진다. 중복 요청을 무시하거나 Cleanup 뒤 시작을 예약하려면 기존 `StartListen()`을 사용한다.

```csharp
ListenerExecution execution = listener.StartExecution();
ListenerResult result = await execution.Completion;
System.Console.WriteLine($"#{result.ExecutionId}: {result.Kind}");
foreach (ListenerError error in result.Errors)
    System.Console.WriteLine($"{error.Phase} {error.ConditionPath}: {error.Exception.Message}");
```

| Result.Kind | 의미 |
|---|---|
| `Satisfied` | 성공 알림을 시작했다. callback이나 Cleanup 오류가 있으면 `HasErrors`와 `Errors`도 확인한다. |
| `Canceled` | 성공 알림 전에 중지됐다. 이후 Cleanup 오류가 생겨도 이 결과를 유지한다. |
| `Unavailable` | Condition이 없거나 `IsValid=false`였다. 미충족 상태와는 다르다. |
| `Faulted` | 초기화·활성화·검사·상태 조회 오류로 종료했다. |

`Completion`은 실행 중 오류나 취소로 faulted/canceled Task가 되지 않는다. 결과와 원래 예외를 Result로 받으며, 여러 호출자가 기다리거나 완료 후 다시 기다려도 같은 Execution의 결과를 받는다. 기존 `Success`·`Exception` 이벤트도 유지한다.

`CurrentExecution`은 검사 또는 Cleanup 중인 실행이고 Idle이면 null이다. `LastResult`는 마지막으로 Cleanup을 마친 실행의 결과이며 새 실행을 시작해도 유지한다. 핸들의 `Id`, `State`, `RestartRequested`, `ConditionSatisfied`, `LastError`로 진행을 확인할 수 있다. `ConditionSatisfied=true`여도 성공 알림 전에 중지됐다면 Result는 `Canceled`일 수 있다.

`IsOneShot=false`이면 성공 후 다음 Execution이 자동으로 시작된다. 이전 `Completion`을 기다리는 것으로 자동 반복이 중지되지는 않는다. 오류가 있으면 자동 반복은 중단하지만, 기존 API로 명시한 재시작 예약은 유지한다. `StopListenAsync()`도 호출 시점 Execution의 Cleanup만 기다리며 다음 예약 실행과 이벤트에서 시작한 별도 비동기 작업은 기다리지 않는다.

`CheckConditionsImmediately()`는 별도 동기 확인이며 Execution을 생성하지 않는다. 이 호출로 진행 중인 Execution을 중지하면 일반 취소 규칙을 적용한다. 동기 확인에서 발생한 Success를 해당 Execution의 자연 종료 결과로 취급하지 않는다.

## 저장된 Condition 진단 보기

기본 Execution 상태와 Result는 항상 제공한다. Condition별 상세 기록은 `AcquireDiagnostics()` 요청이 하나 이상 남아 있는 동안만 수집한다. 시작·중지·진단 API는 같은 실행 컨텍스트에서 사용한다.

```csharp
using (System.IDisposable request = listener.AcquireDiagnostics())
using (var records = CrowRx.Pool.Collections.ListPool<ConditionDiagnostic>.Get())
{
    listener.CopyConditionDiagnostics(records); // records를 비우고 저장된 정보만 복사한다.
    foreach (ConditionDiagnostic record in records)
        System.Console.WriteLine($"#{record.ExecutionId} {record.Path}: {record.CheckState}");
}
```

요청이나 복사는 초기화·활성화·검사를 실행하지 않으며 상태 getter도 추가로 읽지 않는다. 값은 정상 실행에서 최근 관찰한 정보다. `IsValid`·`IsSatisfied`의 null은 `Unknown`이며 false와 구분한다. `Pending`은 검사 Task를 기다리는 상태로, 최근 만족 판정이 false였다는 뜻과도 다르다.

`Root/0/1` 경로는 해당 Execution의 실제 자식 순서다. 영구 에셋 ID로 사용하지 않는다. 단락 평가로 읽지 않은 값과 사용자 조건의 비공개 내부 상태는 추측하지 않는다. 현재 실행이 있으면 그 기록만, Idle이면 마지막 실행의 기록만 복사한다. 전체 history는 보관하지 않으며 요청 전에 끝난 검사의 상세 기록은 없을 수 있다. 복사한 값과 공개 Result는 이후 실행이나 내부 풀 반환으로 바뀌지 않는다.

## 대상 공유와 직접 검사

`IConditionTargetSharable<TTarget>`을 구현한 조건을 공유 그룹에 추가하면 초기화 전에 그룹의 `Target`을 전달한다. 중첩 공유 그룹도 자신의 자식에게 전달한다. 대상은 그룹의 활성화와 검사가 끝난 뒤 변경한다. 전달 실패 시 이전 값을 복구하며 복구에도 실패하면 두 오류를 함께 알린다.

그룹을 리스너 없이 직접 검사할 때는 남아 있는 자식 검사를 명시적으로 정리한다.

```csharp
group.Initialize();
group.Activate();
try
{
    await group.CheckAsync(cancellationToken);
    bool satisfied = group.IsValid && group.IsSatisfied;
}
finally
{
    try { await group.StopChecksAsync(); }
    finally { group.Deactivate(); }
}
```

그룹의 `CheckAsync`는 하나의 자식 검사 완료를 관찰하면 반환할 수 있다. 나머지 검사는 다음 호출까지 유지하며, 모든 자식의 완료를 기다리는 API가 아니다. 동일 그룹의 동시 검사는 허용하지 않는다. 조건 목록은 활성화 또는 검사 중에 바꾸지 않으며, 같은 트리에서 조건 인스턴스를 중복 사용하지 않는다.

## 개선과 검증

미완료 자식 검사 유지, 늦게 완료된 결과의 전달, 중첩 정리 완료 후 재구독, 추가·대상 변경의 재진입 방어를 적용했다. 임시 컬렉션과 검사 세션 버퍼는 CrowRx.Pool을 사용하고, 종료 대기 작업은 필요할 때 생성한다. 대량 구성에는 한 번의 전체 검증을 수행하는 `Create`를 제공하며 배열 입력은 직접 순회와 최종 목록 용량 확보를 사용한다.
