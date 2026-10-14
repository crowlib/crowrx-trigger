# 배열 입력 열거 할당 감소 (2026-10-14)

후속으로 [Create 배열의 목록 용량 확보](CAPACITY_CHANGE.md)를 적용했습니다. 아래 Create 할당 차이는 열거만 바꾼 당시 기록입니다. 현재 배열·비배열의 차이에는 목록 성장 정책 차이도 포함됩니다.

AddConditions와 ConditionGroup.Create에서 입력이 ICondition[]이면 배열을 직접 foreach하도록 변경했습니다. 공변 배열도 포함됩니다. 그 외 IEnumerable은 기존 foreach 경로를 유지합니다.

워밍업된 AddConditions의 배열 입력은 .NET 측정에서 32 B/회에서 0 B/회로 줄었습니다. Create는 호출당 32 B가 줄었으며 새 그룹과 최종 목록의 할당은 남아 있습니다. 추가 훅, 초기화, 실패 전 추가된 자식, 입력 순서와 그래프 검증 계약은 유지했습니다.

## 변경 범위와 동작 검증

- AddConditions는 배열의 원소마다 기존 AddCondition을 호출합니다. 각 원소의 훅·초기화·검증을 생략하지 않습니다.
- 배열은 복사하지 않습니다. 앞쪽 원소의 훅 또는 초기화가 뒤쪽 원소를 바꾸면 변경된 원소를 방문합니다. null 원소 처리도 유지합니다.
- Create는 null이 아닌 원소를 새 목록에 복사하고 완성된 그래프를 한 번 검증합니다.
- List 등 다른 컬렉션을 직접 순회하는 특수 경로는 추가하지 않았습니다. List 파생형이 재구현한 IEnumerable도 기존 방식으로 관찰됩니다.
- 입력 크기를 이용한 목록 용량 확보는 이번 변경에 포함하지 않았습니다.

| 확인 | 결과 |
|---|---|
| 기존 수명·재진입·취소·Target·풀 검증 | 기본 69/69, 로그 활성화 72/72 |
| 생성 및 입력 열거 검증 | 기본·로그 각각 24/24 |
| Release 검증 솔루션 및 Debug 라이브러리 빌드 | 경고 0개·오류 0개 |

추가한 여섯 검증은 훅·초기화에 의한 뒤쪽 배열 변경, 초기화 실패의 부분 성공 및 추가 가드 복구, 공변 배열·null, 비배열 열거 실패의 부분 성공과 Dispose, 추가 실패 시 Dispose, 사용자 정의 List 열거 유지입니다. 기존 생성 API의 참조 동일성, 순서, 초기화 지연, 그래프 오류, 실패 시 풀 참조 해제 검증도 함께 통과했습니다.

## 동일 프로세스의 입력 경로 비교

미리 만든 같은 배열을 직접 전달하거나, 배열 열거자를 반환하는 비배열 래퍼를 통해 전달했습니다. 래퍼 객체는 측정 밖에서 생성합니다. 비배열 래퍼는 현재 구현의 기존 IEnumerable 경로를 선택합니다. 래퍼의 전달 호출 비용이 있어 이 비교의 CPU 수치를 변경 전후의 개선율로 해석하지 않습니다.

| 자식 수 | 재사용 AddConditions: 배열 / IEnumerable | Create: 배열 / IEnumerable |
|---|---|---|
| 1 | 0 / 32 B | 136 / 168 B |
| 8 | 0 / 32 B | 224 / 256 B |
| 32 | 0 / 32 B | 656 / 688 B |
| 128 | 0 / 32 B | 2,240 / 2,272 B |

워밍업 128회, 배치당 4,000회, 순서 교대 7배치입니다. 20개 결과 행 모두 배치별 할당의 최솟값과 최댓값이 같았습니다. 무작업 대조군은 0 B/회, byte[64] 양성 대조군은 88 B/회였습니다.

재사용 AddConditions의 0 B는 부모·자식·목록·검증 풀을 준비한 뒤의 결과입니다. 최초 생성, 목록 성장, 풀의 메모리 보유를 포함한 모든 사용에서 0 B라는 뜻은 아닙니다.

## 이전 전체 구성 측정과의 비교

같은 Construction 및 Creation 측정 코드를 현재 라이브러리로 다시 실행했습니다. Construction 63개 행, Creation 48개 행 모두 예상한 할당 차이와 일치했고 이번 배치별 할당 값도 안정적이었습니다.

| 경로 | 이전 → 적용 후 |
|---|---|
| 재사용 AddConditions: 평면·중첩, 초기화 전후 | 32 → 0 B/회 |
| 새 평면 그룹: 기존 추가 및 Create | 32 B/회 감소 |
| 새 평면 그룹: 자식 1,024개 | 49,472 → 49,440 B/회 |
| 새 중첩 그룹: 직접 자식 그룹 N개, 각 그룹의 자식 배열 추가 포함 | 32 × (N + 1) B/회 감소 |
| 새 중첩 그룹: 직접 자식 그룹 512개 | 176,424 → 160,008 B/회 |
| 배열 직접 순회 AddCondition 및 완성 그래프 검증 참고 경로 | 기존 0 B/회 유지 |

중첩 모형의 감소에는 각 자식 그룹을 구성하는 AddConditions 호출도 포함됩니다. 최상위 호출 하나의 열거 할당이 더 크게 줄어든 것은 아닙니다.

이번 Creation 실행에서 미초기화 평면 1,024개는 기존 추가 약 22.7 ms, Create 약 43.4 µs였습니다. 입력 열거 최적화가 기존 추가의 O(N²) 반복 검증을 바꾼 것은 아닙니다. 호스트 실행별 CPU 변동이 있으므로 이번 작은 변경의 CPU 개선율을 주장하지 않습니다.

## 재실행과 원시 자료

저장소 루트에서 실행합니다.

```powershell
./src/CrowRx.Trigger/verification/run.ps1 -Mode Tests -Logging -Offline
./src/CrowRx.Trigger/verification/run.ps1 -Mode Enumeration -Offline
./src/CrowRx.Trigger/verification/run.ps1 -Mode Construction -Offline
./src/CrowRx.Trigger/verification/run.ps1 -Mode Creation -Offline
```

Enumeration 모드는 생성·열거 동작 검증 뒤 집중 비교를 실행합니다. Benchmarks와 All 모드에도 집중 비교를 포함했습니다.

- [집중 비교 당시 기록](../results/enumeration-before-capacity.json)
- [이전 전체 구성](../results/enumeration-before-construction.json), [적용 후 전체 구성](../results/construction.json)
- [이전 생성 API 비교](../results/enumeration-before-creation.json), [열거 변경 후 생성 API 비교](../results/capacity-before-creation.json)

JSON은 로컬 생성물로 Git에서 제외합니다. 반복 실행하면 현재 결과 파일을 덮어쓰며 이전 결과는 별도 이름으로 보관했습니다. 변경 전후 비교의 이전 JSON은 앞선 측정 기록이고, 동일 프로세스의 집중 비교는 현재 두 입력 경로를 비교합니다.

Windows x64, .NET 8.0.32, Release, DOTNET_TieredCompilation=0에서 GC.GetAllocatedBytesForCurrentThread로 측정했습니다. 세 실행의 라이브러리 MVID는 7384e5b1-e4a8-436b-a223-2f2c86ea9d3e입니다. 빌드에는 캐시된 패키지를 사용했고 Offline 옵션으로 이번 복원 호출의 NuGet 감사를 생략했습니다. Unity Editor·Mono·IL2CPP·Player 측정은 수행하지 않았습니다.
