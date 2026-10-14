# 검증 및 성능 측정

[Create 배열 입력의 용량 확보](Creation/CAPACITY_CHANGE.md)를 적용했습니다. 실제 공개 API는 준비된 자식 1,024개에서 16,648 → 8,296 B/회, 전체 평면 구성은 49,440 → 41,088 B/회였습니다. 운영 기본 69/69·로그 72/72, 생성·열거 기본·로그 각각 24/24와 용량 32개 시나리오가 통과했습니다. 이하 이전 단계 수치는 당시 기록입니다.

[Create 목록 용량 비교](Creation/CAPACITY_COMPARISON.md)를 완료했습니다. 현재·배열 길이 확보·null 제외 개수 확보의 세 모형과 실제 factory를 비교했습니다. GC 감소를 우선할 때 null 제외 개수 확보가 권장 후보이며, 운영 정책은 이번 비교에서 변경하지 않았습니다.

[배열 입력 열거 개선](Creation/ENUMERATION_CHANGE.md)을 적용했습니다. 워밍업된 AddConditions의 배열 입력은 32 → 0 B/회, Create는 호출당 32 B 감소를 확인했습니다. 현재 운영 검증은 기본 69/69·로그 72/72, 생성·열거 검증은 기본·로그 각각 24/24입니다. 아래 기존 측정 결과는 당시 기록입니다.

[ConditionGroup.Create 적용 결과](Creation/README.md): 기존 검증은 기본 69/69·로그 72/72, 생성 API는 기본·로그 각각 18/18을 통과했습니다. 기본 Tests 모드는 두 프로젝트를 실행합니다. 새 평면 그룹 1,024개 구성은 이번 .NET 교대 모형에서 기존 약 23–30 ms, Create 약 48–59 µs였으며 할당량은 같았습니다.

[AddConditions 구성 측정](Construction/README.md)도 완료했습니다. 새 평면 그룹의 조건 1,024개 구성은 약 23 ms, 초기화된 부모에 추가하면 약 34 ms였습니다. 두 프로세스의 63개 항목에서 할당 값이 일치했습니다. 이 결과는 .NET 호스트 모형이며 운영 API는 변경하지 않았습니다.

최신 검증은 운영 코드·순회 비교 기준·통합 후보 각각 기본 69/69, 로그 72/72입니다. [CheckAsync 순회 통합 비교](LoopComparison/README.md)에서는 일부 완료 시점의 할당 증가가 확인되어 운영 구현을 유지했습니다. 아래 보존 직후 68/68·71/71 결과는 당시 기록입니다.

임시 폴더에서 사용하던 동작 검증과 벤치마크를 보존했습니다. 현재 라이브러리는 ProjectReference로, CrowRx.Core·CrowRx.Pool은 NuGet PackageReference로 참조합니다. 개인 PC의 프로젝트·DLL·NuGet 캐시 절대 경로는 필요하지 않습니다.

검증 소스는 라이브러리의 컴파일·리소스·패키지 입력에서 제외됩니다. 이 폴더의 프로젝트도 IsPackable=false입니다. 결과 JSON과 bin/obj 생성물은 Git에 포함하지 않습니다.

## 실행

저장소 루트에서 실행하는 PowerShell 명령입니다. global.json에 맞는 .NET SDK와 .NET 8 런타임이 필요합니다. 라이브러리의 대상 프레임워크는 netstandard2.1로 유지됩니다.

```powershell
# 기본 동작 검증
./src/CrowRx.Trigger/verification/run.ps1

# 기본 검증 및 로그 활성화 검증, 종료 시 기본 빌드 복원
./src/CrowRx.Trigger/verification/run.ps1 -Logging

# Release 성능 측정만 실행
./src/CrowRx.Trigger/verification/run.ps1 -Mode Benchmarks

# 운영·기준·순회 통합 후보 검증과 동일 프로세스 교대 비교
./src/CrowRx.Trigger/verification/run.ps1 -Mode LoopComparison -Logging

# AddConditions의 조건 수별 구성·검증 비용만 측정
./src/CrowRx.Trigger/verification/run.ps1 -Mode Construction

# ConditionGroup.Create 검증과 기존 생성 경로의 교대 비교
./src/CrowRx.Trigger/verification/run.ps1 -Mode Creation -Logging

# 배열 직접 순회와 기존 IEnumerable 경로의 할당 비교
./src/CrowRx.Trigger/verification/run.ps1 -Mode Enumeration -Logging

# Create의 목록 용량 정책 비교
./src/CrowRx.Trigger/verification/run.ps1 -Mode Capacity

# 모든 검증과 측정
./src/CrowRx.Trigger/verification/run.ps1 -Mode All -Logging
```

스크립트 경로가 기준이므로 다른 작업 디렉터리에서도 절대 경로로 호출할 수 있습니다. 빌드·검증·대조군 실패는 오류로 반환합니다. 실행 정책상 스크립트가 허용되지 않는 환경에서는 아래 개별 명령을 사용합니다. 스크립트 실행을 위해 영구 실행 정책을 바꿀 필요는 없습니다.

```powershell
dotnet build src/CrowRx.Trigger/verification/Verification.slnx -c Release -m:1 -p:GeneratePackageOnBuild=false -p:UseSharedCompilation=false
dotnet src/CrowRx.Trigger/verification/Lifetime/bin/Release/net8.0/Lifetime.dll
```

로그 검증은 라이브러리와 검증 프로젝트를 CROWRX_LOG_EXCEPTION 심볼로 다시 빌드한 후 Lifetime.dll에 --logging을 전달합니다. 스크립트는 로그 검증의 성공·실패 모두 기본 심볼로 재빌드합니다. 같은 출력 폴더를 사용하는 빌드·검증을 동시에 실행하지 마세요.

NuGet 접속이 불가능하지만 필요한 패키지가 캐시에 있다면 -Offline을 추가할 수 있습니다. 이 옵션은 해당 실행에만 --ignore-failed-sources와 NuGetAudit=false를 전달합니다. 이 실행은 패키지 취약성 검사를 수행하지 않습니다. 기본 모드는 NuGet 감사 설정을 변경하지 않습니다. 캐시에 패키지가 없는 새 환경의 최초 복원에는 NuGet 접속이 필요합니다.

## 구성

| 프로젝트 | 역할 |
|---|---|
| [Lifetime](Lifetime/Lifetime.csproj) | Task 검사 수명, 중지·재시작, 실패·재진입, Target 롤백, 풀 반환·참조 해제 검증 |
| [BoolPools](BoolPools/BoolPools.csproj) | bool ArrayPool/ListPool 초기화 및 인덱스 접근 비교 |
| [Performance](Performance/Performance.csproj) | 현재 라이브러리의 그룹 검사·리스너 재시작·Target 변경 CPU·할당 측정 |
| [BufferComparison](BufferComparison/Compare.csproj) | bool 방식만 다른 두 구현을 같은 프로세스에서 교대 비교 |
| [LoopComparison](LoopComparison/README.md) | 완료 확인 순회 통합 전후의 동작·CPU·할당 비교, 운영 적용하지 않은 후보 |
| [Construction](Construction/README.md) | 초기화 전후·평면·중첩 그룹의 추가·새 구성 비용과 한 번 검증 참고 경로 |
| [Creation](Creation/README.md) | 새 기본 그룹 factory의 계약 검증과 기존 생성 경로 CPU·할당 교대 비교 |
| [Verification.slnx](Verification.slnx) | 검증·측정 프로젝트를 함께 열거나 빌드하는 별도 솔루션 |

BufferComparison의 두 ConditionGroup.cs는 2026-10-14 비교 당시의 스냅샷입니다. 그 외 라이브러리 소스는 상대 경로로 공유합니다. 현재 운영 ConditionGroup.cs를 자동 복제하지 않습니다. 향후 라이브러리 계약이 바뀌면 두 비교 스냅샷을 함께 검토해야 합니다. 과거 기준 구현의 전체 소스나 당시 CPU 시간을 고정해서 재현하는 자료는 아닙니다.

LoopComparison 역시 기준·후보 ConditionGroup.cs 스냅샷과 공통 라이브러리 소스를 사용합니다. 두 구현의 콘솔 검증은 Lifetime/Program.cs를 공유합니다. -Mode LoopComparison은 운영 코드 검증도 포함하며, -Mode All은 기존 측정과 순회 비교를 모두 실행합니다.

Construction은 현재 라이브러리를 ProjectReference로 참조하며 -Mode Construction으로 별도 실행할 수 있습니다. -Mode Benchmarks와 -Mode All에도 포함됩니다. -Mode Construction은 동작 검증을 함께 실행하는 모드가 아닙니다.

Creation은 현재 라이브러리를 참조하는 별도 검증 프로젝트입니다. 과거 순회·버퍼 비교 스냅샷의 검증에는 새 API 호출을 추가하지 않았습니다. -Mode Creation은 생성 API 검증과 비교를 실행하며 기본 Tests 모드는 기존 Lifetime과 Creation 검증 및 용량 계약 검증을 함께 실행합니다.

-Mode Enumeration은 Creation의 생성·열거 검증과 배열 입력 경로의 집중 비교를 실행합니다. AddConditions 및 Create에 같은 배열을 직접 전달하거나 비배열 래퍼로 전달하여 기존 IEnumerable 경로와 비교합니다. Benchmarks와 All에도 포함됩니다.

-Mode Capacity는 현재 생성 검증 뒤 배열 입력의 목록 용량 계약 검증과 세 정책·실제 factory 비교를 실행합니다. 현재 운영 factory는 null 제외 개수 확보 정책이며 growth_model은 적용 전 성장 정책을 보존합니다. Benchmarks와 All에도 포함됩니다.

## 측정 조건과 결과

벤치마크는 Release, DOTNET_TieredCompilation=0으로 실행합니다. 스크립트가 호출한 측정 프로세스에만 적용하고 기존 환경 변수를 복원합니다. GC.GetAllocatedBytesForCurrentThread로 할당을 측정하며 무작업 0 B 및 byte[64] 할당 양성 대조를 확인합니다. 단일 스레드 큐 밖의 실행이 감지되면 통합 측정은 실패합니다.

- BoolPools: 워밍업 2,000회, 교대 7배치. N≤512는 배치당 100,000회, N=4,096은 10,000회.
- Performance: 워밍업 2,000회, 배치당 8,000회, 5배치.
- BufferComparison: 워밍업 2,000회, 배치당 4,000회, 교대 7배치.
- Construction: 7배치, 규모별 워밍업·반복 횟수. 큰 구성은 워밍업 2회·배치당 3회이며 정확한 횟수와 한계는 해당 결과 문서와 JSON에 기록합니다.

출력은 이 폴더의 results/bool-pools.json, results/performance.json, results/buffers-paired.json입니다. 반복 실행하면 같은 파일을 덮어씁니다. 비교할 결과는 별도 이름으로 보관하세요. 런타임·OS·어셈블리 MVID와 배치 중앙값·범위·할당이 JSON에 기록됩니다.

순회 비교는 results/loops-paired.json을 출력하며 워밍업 2,000회, 배치당 4,000회, 교대 7배치입니다. 이 비교에서는 구현별 할당 차이 자체가 판단 근거이므로 두 구현의 할당이 같다고 가정하지 않습니다.

구성 측정은 results/construction.json에 저장합니다. 이 결과는 fresh 전체 구성, 기존 객체 reuse, validate 한 번 검증을 구분해서 읽어야 합니다. 생성물은 Git에서 제외합니다.

factory 비교는 results/creation.json에 저장합니다. 기존·Create의 순서를 교대하되 속도 차이가 커 반복 횟수를 다르게 사용하므로 횟수와 Gen0 수집 해석은 해당 결과 문서를 참고하세요. -Mode Benchmarks와 -Mode All에도 포함됩니다.

배열 입력 집중 비교는 results/enumeration.json에 저장하며 워밍업 128회, 배치당 4,000회, 교대 7배치입니다. 래퍼의 전달 호출 비용이 포함되므로 CPU를 변경 전후 개선율로 해석하지 않습니다. [배열 입력 적용 결과](Creation/ENUMERATION_CHANGE.md)는 열거 변경 당시 기록이며, 현재 Create 배열·래퍼의 할당 차이에는 [용량 확보](Creation/CAPACITY_CHANGE.md)의 목록 성장 차이도 포함됩니다.

목록 용량 비교는 results/capacity.json에 저장하며 워밍업 128회, 순서 회전 8배치입니다. 입력 길이와 null 비율별 할당·CPU·최종 Capacity를 기록하며 반복 횟수와 모형의 한계는 [용량 비교 결과](Creation/CAPACITY_COMPARISON.md)를 참고하세요.

워밍업 후 0 B는 최초 대여·용량 증가·풀의 메모리 보유 비용을 포함하지 않습니다. CPU 시간은 PC와 실행 조건에 따라 달라집니다. .NET 호스트 검증이며 Unity Editor·Mono·IL2CPP·Player 증거로 해석하지 않습니다. 이 프로젝트는 별도 테스트 프레임워크를 사용하지 않는 콘솔 검증이므로 dotnet test 대신 위 명령으로 실행합니다.

## 보존 후 확인 결과 (2026-10-14)

Windows x64, .NET 8.0.32에서 run.ps1 -Mode All -Logging -Offline을 실행했습니다. NuGet 감사 서버 접속이 실패하여 이번 확인은 캐시된 패키지를 사용하고 감사를 생략한 실행입니다. 온라인 최초 복원이나 Unity 실행을 검증한 결과는 아닙니다.

| 확인 | 결과 |
|---|---|
| 기본 동작 검증 | 68/68 통과 |
| 로그 활성화 검증 | 71/71 통과, 종료 후 기본 빌드 복원 |
| BoolPools | 26개 행의 할당 값이 보존 전과 동일 |
| Performance | 27개 행의 할당 값이 보존 전과 동일 |
| BufferComparison | 24개 행의 할당 값이 보존 전과 동일 |
| 대조군 | 무작업 0 B/회, byte[64] 88 B/회 |
| 통합 측정의 외부 스레드 등록 | 0회 |
| 오프라인 옵션으로 실행한 프로젝트 빌드 | 경고 0개·오류 0개 |
| 라이브러리 컴파일 입력 | 기존 C# 파일 9개, verification 파일 0개 |
| 생성한 NuGet 패키지 | 라이브러리 DLL·README·LICENSE 포함, 검증 파일 제외 |

평면 자동 재시작 968 B/회, 중첩 자동 재시작 4,552 B/회가 유지됐습니다. 모든 측정 행의 배치별 할당 값이 안정적이었습니다. CPU 시간의 일치나 추가 성능 향상을 주장하는 결과는 아닙니다. 보존한 C# 소스 6개는 줄 끝 공백과 개행을 제외하면 기존 임시 소스와 동일합니다.
