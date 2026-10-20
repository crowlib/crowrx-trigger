# 검증과 측정

기존 리뷰·제안·적용 결과 문서의 검증 근거와 성능 결정을 통합했다. API 사용법은 [README](../Readme.md), 세부 계약은 [수명과 설계](../docs/Design.md)에 있다. 벤치마크 수치는 해당 단계의 측정값이며 이번 주석·문서 정리에서 새로 측정한 결과는 아니다.

## 실행

다음 명령은 Git 저장소 루트 기준이다. `global.json`에 맞는 .NET SDK와 .NET 8 런타임이 필요하다. 라이브러리는 netstandard2.1, 검증 프로그램은 net8.0이다.

```powershell
# 기본 동작·생성·용량 계약 검증
./src/CrowRx.Trigger/verification/run.ps1

# 로그 검증도 실행하고 기본 빌드로 복원
./src/CrowRx.Trigger/verification/run.ps1 -Mode Tests -Logging

# 성능 측정 / 순회 통합 후보 비교 / 모든 검증과 측정
./src/CrowRx.Trigger/verification/run.ps1 -Mode Benchmarks
./src/CrowRx.Trigger/verification/run.ps1 -Mode LoopComparison -Logging
./src/CrowRx.Trigger/verification/run.ps1 -Mode All -Logging
```

프로젝트 폴더에서는 `./verification/run.ps1`로 호출한다. 경로는 스크립트 위치를 기준으로 해석한다. 캐시에 필요한 패키지가 있고 NuGet에 접속할 수 없다면 `-Offline`을 추가한다. 해당 호출에만 `--ignore-failed-sources`와 `NuGetAudit=false`를 적용하며 최초 패키지 복원을 대신하지 않는다.

스크립트를 실행할 수 없다면 개별 프로젝트를 빌드한 뒤 콘솔 DLL을 실행한다. 테스트 프레임워크를 사용하지 않으므로 `dotnet test`는 이 검증을 실행하지 않는다.

```powershell
dotnet build src/CrowRx.Trigger/verification/Verification.slnx -c Release -m:1 -p:GeneratePackageOnBuild=false -p:UseSharedCompilation=false
dotnet src/CrowRx.Trigger/verification/Lifetime/bin/Release/net8.0/Lifetime.dll
dotnet src/CrowRx.Trigger/verification/Creation/bin/Release/net8.0/Creation.dll
dotnet src/CrowRx.Trigger/verification/Creation/bin/Release/net8.0/Creation.dll --capacity-tests
```

로그 모드는 라이브러리와 검증 프로그램을 `CROWRX_LOG_EXCEPTION`으로 다시 빌드하고 DLL에 `--logging`을 전달한다. 스크립트는 성공·실패 모두 기본 심볼 빌드를 복원한다. 같은 출력 폴더를 사용하는 빌드와 검증은 동시에 실행하지 않는다.

## 검증 구성

| 프로젝트·모드 | 역할과 출력 |
|---|---|
| [Lifetime](Lifetime/Lifetime.csproj), 기본 Tests | 미완료 검사 유지, 늦은 완료, 실패·취소, 중첩 정리, 중지·재시작, 재진입, Target 복구, 풀 참조 해제를 검증한다. |
| [Creation](Creation/Creation.csproj), 기본 Tests | 입력 복사·순서·참조, null·공변 배열, 한 번 열거·Dispose·실패, 초기화 지연, 훅·부분 성공, 그래프를 검증한다. |
| Creation `--capacity-tests`, 기본 Tests | 길이 0·1·8·32·128·1,024와 null 비율 0·50·90·100%에서 네 용량 경로의 계약을 확인한다. |
| [BoolPools](BoolPools/BoolPools.csproj) | bool 버퍼 대여·초기화·접근 비교다. `results/bool-pools.json`을 출력한다. |
| [Performance](Performance/Performance.csproj) | 현재 그룹 검사·리스너 재시작·Target 변경 측정이다. `results/performance.json`을 출력한다. |
| [BufferComparison](BufferComparison/Compare.csproj) | Array/List 완료 표시를 동일 프로세스에서 교대로 비교한다. `results/buffers-paired.json`을 출력한다. |
| [LoopComparison](LoopComparison/Compare.csproj), `-Mode LoopComparison` | 운영·기준·후보 검증과 순회 비교다. `results/loops-paired.json`을 출력한다. |
| [Construction](Construction/Construction.csproj), `-Mode Construction` | 기존 AddConditions 새 구성·재사용·한 번 검증 참고 경로다. `results/construction.json`을 출력한다. |
| Creation, `-Mode Creation` | 생성 검증 뒤 기존 추가와 factory를 교대 비교한다. `results/creation.json`을 출력한다. |
| Creation, `-Mode Enumeration` | 생성 검증 뒤 배열·비배열 래퍼를 비교한다. `results/enumeration.json`을 출력한다. |
| Creation, `-Mode Capacity` | 생성·용량 계약 검증 뒤 세 용량 정책과 실제 API를 비교한다. `results/capacity.json`을 출력한다. |

Benchmarks는 위 성능 측정을 포함하며 순회 비교는 별도 모드다. All은 둘 다 포함한다. Construction만 실행하면 동작 검증은 실행하지 않는다. [Verification.slnx](Verification.slnx)로 검증 프로젝트를 함께 열 수 있다.

현재 라이브러리는 ProjectReference로, Core·Pool은 NuGet으로 참조한다. 라이브러리의 컴파일·리소스·NuGet 입력에서 verification을 제외하며 검증 프로젝트는 IsPackable=false다. 결과 JSON, bin/obj, 패키지는 Git에 넣지 않는다. 개인 PC의 DLL·프로젝트 절대 경로는 필요하지 않다.

BufferComparison과 LoopComparison의 ConditionGroup.cs는 2026-10-14 비교 스냅샷이다. 나머지 공통 소스는 현재 라이브러리를 상대 경로로 참조한다. 스냅샷을 운영 소스로 자동 복제하지 않는다. 계약이 바뀌면 양쪽 스냅샷과 검증을 함께 검토해야 한다. 과거 전체 라이브러리와 CPU 시간을 고정해서 재현하는 자료는 아니다.

## 확인 결과

2026-10-15 사용자 코드 정리 후 기본·로그 검증을 다시 실행했다. Offline 모드여서 이 복원 호출의 NuGet 취약성 감사는 생략했다.

| 확인 | 결과 |
|---|---|
| 수명·리스너·Target·풀 계약 | 기본 69/69, 로그 활성화 72/72 |
| 생성·열거 계약 | 기본·로그 각각 24/24 |
| 용량 계약 | 네 경로 각각 32개 시나리오 |
| XML 문서 | 77개 항목, 공개·보호 메서드 34개의 인자·반환 주석 검사 통과. 단순 재구현은 문서 상속을 사용한다. |
| Debug 라이브러리·Release 검증 솔루션 | 경고를 오류로 처리한 빌드 통과, 경고·오류 0개 |
| NuGet 패키지 | DLL·XML·README·LICENSE 포함, 검증 소스 제외 확인 |
| 라이브러리 컴파일 입력 | 운영 C# 파일 9개, verification 파일 0개 |

아래 성능 결과는 2026-10-14 데스크톱 .NET 기록이다. 별도 표기가 없으면 Windows x64, .NET 8.0.32, Workstation GC, Release, `DOTNET_TieredCompilation=0`이다. 종료 작업 지연 생성의 최초 비교는 .NET 8.0.31이었다. Unity Editor·Mono·IL2CPP·Player와 실제 소비 프로젝트 실행은 검증하지 않았다.

### 2026-10-20 Execution·diagnostics 기능 검증

`Lifetime/ExecutionTests.cs`에 Execution 경계, 여러 awaiter와 완료 후 await, 중복 시작 거부, 자동 반복·예약 재시작, callback·Cleanup 오류와 원래 Result 보존, 원래 예외·phase·경로, 상세 요청 수명, 단락 평가·사용자 조건·중첩 pending, getter 호출 횟수 불변을 추가했다.

`run.ps1 -Mode Tests -Logging -Offline` 실행 결과: Lifetime 기본 92/92, 로그 활성화 95/95, Creation 기본·로그 각각 24/24, 네 용량 경로 각각 32개 시나리오가 통과했다. 기본 심볼 빌드로 복원했다. 이 호출에서는 캐시 패키지를 사용하고 NuGet 취약성 감사는 생략했다.

Unity 연결부의 검증 범위와 기록은 Unity 개발 프로젝트의 `Documentation/Development.md`에 둔다. QV의 과거 통과 결과를 이번 변경의 검증으로 사용하지 않았다. Player·IL2CPP와 CPU·GC는 이번에 측정하지 않았으므로 아래 과거 벤치마크는 새 Execution·diagnostics 구현의 수치가 아니다.

## 적용한 할당 개선

단위는 B/회이며 워밍업 후 값이다. 서로 다른 단계나 측정 구간을 한 합계로 더하지 않는다.

| 변경·측정 구간 | 이전 → 적용 후 |
|---|---|
| 종료 대기 Task 지연 생성, 평면 자동 재시작 8개 | 1,184 → 1,088 |
| 같은 변경, 중첩 자동 재시작 말단 32개 | 5,472 → 5,376 |
| 세션 스냅샷·완료 표시 풀링, 평면 자동 재시작 8개 | 1,088 → 968 |
| 같은 풀링, 중첩 자동 재시작 말단 32개 | 5,376 → 4,552 |
| 배열 직접 순회, 준비된 배열 AddConditions 1·8·32·128개 | 32 → 0 |
| 배열 직접 순회, Create | 당시 각 배열 입력에서 32 B 감소 |
| Create 용량 확보, 준비된 자식 1,024개 전달 | 16,648 → 8,296 |
| 같은 용량 확보, 입력 배열·자식 생성 포함 평면 1,024개 | 49,440 → 41,088 |
| 같은 용량 확보, 직접 자식 그룹 512개 전체 구성 | 160,008 → 155,776 |

매 실행마다 `StopListenAsync`로 기다리면 종료 작업을 생성해야 하므로 지연 생성의 절감은 없다. 풀링 후 세션 내 즉시 완료 재검사와 Target 변경은 측정 모형에서 0 B/회였다. 자동 재시작 전체의 Task·CTS·세션 할당까지 없앤 것은 아니다.

완료 표시 ArrayPool/ListPool은 통합 모형에서 할당이 같았다. 단독 버퍼의 큰 초기화·읽기·쓰기와 그룹 시작·검사·종료 N=8·32·128에서는 Array가 유리했다. 작은 그룹 자동 재구독은 두 실행 모두 List가 빨라 현재 ListPool을 선택했다. 평면 자동 재구독의 배치 범위는 겹쳤으므로 작은 CPU 차이를 보편적인 개선으로 해석하지 않는다.

## 구성과 배열 용량

기존 추가는 원소마다 그래프를 검증하므로 평면 구성에서 O(N²) 순회 비용이 든다. 콜백이 작업을 하지 않는 최초 생성 비교에서 1,024개는 기존 약 23–30 ms, Create 약 48–59 µs였다. 할당은 당시 양쪽 49,472 B/회로 같았다. 배열 순회·용량 개선 전 기록이다.

Create는 완성된 그래프를 한 번 검증한다. 초기화된 부모에 추가하는 경로와 Create 후 초기화하는 경로는 콜백이 보는 목록과 호출 순서가 다르다. 중첩 모형은 직접 자식 그룹마다 말단 4개를 기존 경로로 구성하고 루트 생성만 비교했다.

아래는 용량 확보 단계에서 입력 배열·자식을 미리 준비한 실제 Create의 결과다. 용량은 최종 목록 슬롯 수이며 생존 메모리 바이트가 아니다.

| 배열 길이 | 요청 null 비율 | 실제 자식 수 | 이전 → 적용 후 B/회 | 이전 → 적용 후 Capacity |
|---|---|---|---|---|
| 1 | 0% | 1 | 136 → 112 | 4 → 1 |
| 8 | 0% | 8 | 224 → 168 | 8 → 8 |
| 32 | 0% | 32 | 656 → 360 | 32 → 32 |
| 128 | 0% | 128 | 2,240 → 1,128 | 128 → 128 |
| 1,024 | 0% | 1,024 | 16,648 → 8,296 | 1,024 → 1,024 |
| 1,024 | 50% | 512 | 8,432 → 4,200 | 512 → 512 |
| 1,024 | 90% | 103 | 2,240 → 928 | 128 → 103 |

null 수는 floor(길이 × 비율 / 100)으로 만들었다. 15개 입력 조합에서 실제 factory의 할당·Capacity가 null 제외 개수 모형과 같았다. `growth_model`은 적용 전 목록 성장 정책, `production`은 실제 API다.

GC 감소를 우선한 선택이다. 배열을 한 번 더 읽으므로 긴 입력에 실제 자식이 적으면 CPU 비용이 늘 수 있다. 길이 1,024의 0·50·90% null 모형에서 이전 정책 중앙값은 40.19·20.79·5.01 µs, 실제 적용 API는 42.69·20.56·5.77 µs였다. 정책 모형은 helper·파생 그룹을 사용하므로 정확한 변경 전 factory의 CPU 대조는 아니다. CPU 개선율을 확정하지 않는다.

현재 배열·비배열 래퍼 Create의 할당 차이에는 열거자와 목록 성장 정책이 모두 포함된다. 래퍼의 전달 호출 비용도 있으므로 CPU 차이를 변경 전후 개선율로 해석하지 않는다. 일반 enumerable은 현재도 한 번만 열거한다.

## 적용하지 않은 순회 통합

시작 루프에서 완료 여부를 누적하면 뒤의 별도 순회를 줄일 수 있다. 하지만 뒤쪽 자식의 동기 코드가 앞쪽 Task를 완료시키면 누적 값이 오래된 상태로 남아 WhenAny를 불필요하게 만든다. 운영 구현은 모든 자식 시작 뒤 다시 확인해 이 할당을 피한다.

| 자식 수 | 별도 순회 B/회 | 통합 후보 B/회 | 증가 |
|---|---|---|---|
| 2 | 536 | 608 | 72 |
| 8 | 1,112 | 1,512 | 400 |
| 32 | 3,416 | 4,776 | 1,360 |
| 128 | 12,632 | 17,832 | 5,200 |

매 회 시작·검사·종료하며 자식별 TCS를 생성하는 재현 모형의 전체 할당이다. 일부 혼합 모형은 CPU가 줄었지만 추가 할당 때문에 통합하지 않았다. 기준과 후보 동작 검증은 각각 기본 69/69·로그 72/72였다.

## 측정 방법과 한계

할당은 `GC.GetAllocatedBytesForCurrentThread`로 확인한다. 무작업 0 B/회와 byte[64] 생성 88 B/회 양성 대조를 사용하며 통합 모형은 외부 스레드 큐 등록이 없음을 확인한다. 대조군이나 외부 스레드 조건이 실패하면 스크립트도 실패한다. 계측 컨텍스트 밖에서 일하는 임의의 사용자 작업 비용까지 측정하지는 않는다.

| 측정 | 워밍업·배치 |
|---|---|
| BoolPools | 2,000회 예열, 교대 7배치다. N≤512는 배치당 100,000회, N=4,096은 10,000회다. |
| Performance | 2,000회 예열, 8,000회 × 5배치다. |
| BufferComparison·LoopComparison | 2,000회 예열, 교대 4,000회 × 7배치다. |
| Enumeration | 128회 예열, 교대 4,000회 × 7배치다. |
| Capacity | 128회 예열, 순서 회전 8배치다. N=1·8·32는 16,384회, 128은 4,096회, 1,024는 512회다. |
| Construction·Creation | 규모별 횟수를 사용한다. 큰 구성과 기존·factory 반복 수가 다를 수 있으므로 JSON의 반복 수와 배치 범위를 확인한다. |

JSON은 런타임·OS·MVID와 배치별 값·중앙값·범위를 보관하며 같은 이름으로 실행하면 덮어쓴다. 과거 결과는 필요하면 별도 이름으로 로컬 보관한다. 강제 GC는 측정 밖에서 수행한다. 서로 다른 반복 수의 Gen0 횟수를 회당 할당처럼 직접 비교하지 않는다.

워밍업 후 0 B는 최초 대여, 용량 증가, 풀의 메모리 보유 비용을 포함하지 않는다. 풀링은 초기화와 복사 작업을 없애지 않는다. CPU는 환경에 따라 달라지며 .NET 결과로 Unity 프레임 시간이나 Player GC를 주장하지 않는다. 메모리 부족·풀 내부 실패의 강제 주입 검증도 수행하지 않았다.

남은 후보는 실제 소비 프로젝트의 Unity 측정, 목록 변경 통제·구조 버전 설계, Task·세션 할당의 기여 분석이다. 현재 비교·검증을 마친 미적용 성능 후보는 없다.
