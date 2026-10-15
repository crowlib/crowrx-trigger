# CrowRx.Trigger

비동기 조건을 AND·OR로 묶고, 조건을 만족하면 이벤트를 실행하는 라이브러리다. 조건 초기화와 활성화, 검사 취소, 중첩 그룹 정리, 재구독을 함께 관리한다.

## 환경과 설치

- 대상은 .NET Standard 2.1, C# 9이며 Unity 6 이상에서 사용하는 것을 전제로 한다.
- NuGet으로 설치한다. Unity에서는 NuGetForUnity를 통해 설치할 수 있다.
- 의존 패키지는 CrowRx.Core 1.0.12와 CrowRx.Pool 1.0.6이다.
- 공개 API의 XML 문서는 빌드 시 DLL과 함께 생성하며 NuGet 패키지에도 포함한다.

현재 검증은 데스크톱 .NET 호스트에서 수행했다. Unity Editor·Mono·IL2CPP·Player 실행과 성능은 별도로 확인해야 한다. 라이브러리 자체에 Editor 전용 API 의존성은 없다.

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
