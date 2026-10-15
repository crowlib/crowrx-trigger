# QueuePool 사용 계약

`PooledQueue<T>`는 `Queue<T>`를 상속한 `IDisposable` 클래스다.
다른 컬렉션 풀처럼 큐 객체와 내부 배열을 재사용하며 `netstandard2.1`과 엔진 독립성을 유지한다.
`Enqueue`, `Dequeue`, `Count`, 조회·복사·순회는 기본 Queue 구현을 직접 사용한다.
원소마다 세대 검사, 핸들 검증, 최대 개수 추적을 수행하지 않는다.

```csharp
using CrowRx.Pool.Collections;

QueuePool<int>.Warmup(inactiveCount: 4, capacity: 256);
using var queue = QueuePool<int>.Get(256);
queue.Enqueue(1);
while (queue.TryDequeue(out int item))
{
    // 일반 Queue와 같은 원소 처리 경로다.
}
```

## 반환과 사용 범위

- Dispose는 원소를 정리하고 큐를 풀에 반환한다. 배열은 유지하며 자동 Trim은 하지 않는다.
- 재대여 전의 중복 Dispose는 무시한다.
- 반환한 참조는 사용하지 않아야 한다. 반환 후 접근은 ObjectDisposedException으로 검출하지 않는다.
- 재대여 후 오래된 참조로 Dispose를 호출하면 현재 사용 중인 큐도 정리하고 반환한다.
  반환 후 Enqueue도 다음 사용자의 큐를 오염시킬 수 있다.
- using 범위 밖으로 큐나 큐의 열거자를 보관하지 말고 반환 책임자를 하나로 정한다.
- default는 null이다. null에 대한 접근과 직접 Dispose는 일반 참조 타입의 동작을 따른다.
- 빈 큐의 Peek·Dequeue, 복사 인수 오류, 순회 중 변경은 기본 Queue의 계약을 따른다.
  열거자에는 풀의 별도 수명 검사가 없다.
- Queue가 제공하는 IReadOnlyCollection, IEnumerable, 비제네릭 ICollection을 그대로 사용한다.
  Queue는 제네릭 ICollection을 구현하지 않는다.

## 용량과 할당

`Get()`은 대기 큐를 LIFO로 재사용하고 없을 때만 빈 큐를 만든다.
객체 네 개 선생성, 최근 반환 객체 전용 캐시, 용량별 검색은 사용하지 않는다.

`Get(capacity)`는 기록한 용량 하한이 충분한 맨 위 큐를 재사용한다.
부족하면 Queue 생성자로 지정 용량의 새 PooledQueue를 만들어 교체한다.
netstandard2.1에는 Queue.EnsureCapacity와 배열 용량 조회 API가 없으므로 내부 배열 reflection은 사용하지 않는다.

용량 하한은 생성 용량과 Dispose 시 남아 있는 Count로만 기록한다.
따라서 크게 자동 증설한 뒤 완전히 비운 큐의 여유 용량은 확인하지 못하고,
더 큰 명시적 용량 요청에서 충분한 기존 큐를 교체할 수도 있다.
반복 작업은 예상 최대 원소 수로 Warmup하고 같은 용량 요청을 사용하면 이 교체를 피할 수 있다.

PooledQueue.TrimExcess는 기본 Queue 정책으로 축소한 뒤 기록한 용량 하한을 현재 Count로 낮춘다.
Queue로 변환하여 기본 TrimExcess를 직접 호출하면 이 기록 갱신을 우회하므로,
이후 `Get(capacity)`의 증설 없는 사용 보장을 신뢰할 수 없다.
일반 Queue 연산에 검사 비용을 추가하지 않는 대신 호출자가 이 사용 규칙을 지켜야 한다.

`Warmup(inactiveCount, capacity)`는 기존 대기 큐의 용량을 준비하고 지정 수까지 채운다.
활성 대여는 변경하지 않으며 기존 용량 하한이 부족한 큐만 교체한다.
inactiveCount가 현재 대기 수보다 작거나 0이어도 기존 대기 큐는 용량 준비 대상이다.
기본 capacity는 0이다. 생성·예열·증설에는 할당이 있고 충분히 준비한 반복 사용은 객체와 배열을 재사용한다.

`Get(source)`는 입력 개수를 알 수 있으면 먼저 용량을 준비하고 FIFO 순서로 복사한다.
새 큐가 필요한 배열, List 등 ICollection<T> 입력은 기본 Queue의 복사 생성자를 사용한다.
충분한 용량의 큐를 재사용할 때나 그 밖의 입력은 기존 복사 경로를 사용한다.
배열, List, Queue를 순회할 때는 실제 구체 타입으로 순회하여 입력 열거자 박싱을 피한다.
다른 IEnumerable 입력은 입력 구현에 따라 열거자 할당이 발생할 수 있다.
Count 조회는 대여 전에 수행하며, 대여한 큐의 입력 처리에 실패하면 큐를 정리하고 반환한 뒤 예외를 다시 던진다.
용량이 부족한 대기 큐를 교체하는 중 복사 생성자가 실패하면 기존 대기 큐를 풀에 복원한다.
null 입력은 빈 대여다.

직접 using과 구체 타입 foreach는 반환 핸들이나 열거자를 박싱하지 않는다.
클래스인 PooledQueue를 읽기 인터페이스나 IDisposable로 변환해도 큐 자체는 박싱하지 않는다.
인터페이스 순회는 Queue의 구조체 열거자를 박싱할 수 있고 비제네릭 순회는 값형 원소도 박싱할 수 있다.
비어 있지 않은 ToArray 결과는 새 배열을 할당한다.

## 보관과 스레드

기존 MaxInactive, CountInactive, Clear API는 유지한다.
MaxInactive는 T별 대기 큐 개수 상한이며 기본값은 32다. 배열 크기 또는 총 메모리 상한은 아니다.
상한 축소는 초과 대기 큐를 놓아주고 0이면 이후 반환 객체를 보관하지 않는다.
Warmup 개수는 상한을 넘을 수 없다.
Clear는 대기 큐만 풀에서 놓아주고 활성 대여는 건드리지 않는다.
Clear 이후 활성 큐가 반환되면 현재 상한을 적용해 다시 보관한다.

큐와 풀은 스레드 안전하지 않다. 같은 T의 풀 작업과 큐 접근을 외부에서 직렬화해야 한다.
ICollection.SyncRoot도 풀 전체의 스레드 안전성을 제공하지 않는다.

## 이전 세대 검사 핸들에서의 변경

2026-10-07부터 성능을 우선하여 readonly struct 세대 검사 핸들을 Queue 상속 클래스로 변경했다.
기존 using var와 명시적인 PooledQueue 선언은 유지되며 Queue 매개변수로 직접 전달할 수 있다.
이전 struct 바이너리와 호환되지 않으므로 소비자와 의존 라이브러리를 새 DLL에 대해 다시 빌드해야 한다.
기본 Get/Warmup의 생성 용량은 4에서 0으로 변경했다.

default의 무해한 Dispose, 반환 후 ObjectDisposedException, 오래된 핸들의 무해한 반환,
열거자의 세대 검사는 더 이상 현재 계약이 아니다.
기존 tests/CrowRx.Pool.QueueTests의 세대 검사 테스트와 과거 성능·검증 기록은 이전 구현을 대상으로 한다.
세대 상태 reflection을 사용하는 QueueComparisonBenchmarks도 이전 구현 전용이다.
이 문서의 현재 계약 검증과 과거 세대 검사 결과를 구분해야 한다.

이 변경은 CrowRx 소스에 적용한다. 소비자 설치 DLL, Unity 패키지 복사본과 배포 버전은 갱신하지 않는다.
