# WILLB SC6000 Monitor

VisionMaster 4.4.x SDK와 .NET Framework 4.6.1 개발 도구가 설치된 Visual Studio에서 `SC6000_Dual_OperationInterface.sln`을 열어 빌드합니다.

- 저장소는 어느 폴더에 복제해도 됩니다. 최초 빌드 시 `SC6000DualMonitor/config.ini`가 없으면 `config.example.ini`에서 자동 생성합니다. 기존 설정은 덮어쓰지 않습니다.
- 생성된 `config.ini`의 카메라 IP와 비밀번호 등을 실제 장비에 맞게 설정하고 다시 빌드하세요. 예제 IP는 실제 장비 주소가 아닙니다.
- 프로그램은 작업 디렉터리와 관계없이 실행 파일 옆의 `config.ini`를 읽습니다. 배포할 때는 `bin/Release`의 전체 내용을 함께 복사하고, 대상 PC에도 VisionMaster 런타임을 설치하세요.
- 배포 폴더의 `config.ini`를 직접 수정한 경우 해당 파일을 별도로 보관하세요. 소스 설정을 수정하고 다시 빌드하면 출력 폴더의 설정이 갱신될 수 있습니다.
- 대경테크노 로고는 `SC6000DualMonitor/Assets/daekyung-logo.jpg`에서 실행 파일에 내장되므로 별도 로고 파일이나 원본 이미지 경로가 필요하지 않습니다.
- 실제 장비 설정, 공급업체 바이너리, 로그 및 빌드 결과는 Git에서 제외됩니다.

## 시작 Solution 자동 로딩

각 `[CAMERA1]` ~ `[CAMERA4]` 구역에 아래 설정을 추가하세요. 기존 `config.ini`는 보존되므로 새 항목은 직접 추가해야 합니다.

```ini
[CAMERA1]
IP=192.0.2.10
PASSWORD=
TITLE=Camera 1
SOLUTION_PATH=/root/vmtempfiles/ftp/solution/검사.solx
SOLUTION_PASSWORD=
SOLUTION_DIRECTORY=
```

`SOLUTION_PATH`는 실제 카메라 내부 파일의 전체 경로로 바꾸세요. PC의 파일 경로가 아닙니다. 카메라 접속 직후 해당 Solution을 로딩하고 Operation Interface를 표시합니다. `SOLUTION_PASSWORD`는 Solution 파일 비밀번호이며 접속 비밀번호 `PASSWORD`와 구분됩니다. 경로가 비어 있거나 항목이 없으면 기존처럼 카메라의 현재 Solution을 표시합니다. 로딩 실패 시 카메라와 실패 단계, 지정 경로를 표시하고 화면 로딩을 중단합니다.

소스 폴더의 `config.ini`를 수정했다면 다시 빌드하세요. 실행 파일 옆의 설정을 수정했다면 프로그램을 다시 실행하세요.

## Visual Studio 시작 항목

폴더 열기 대신 `파일 → 열기 → 프로젝트/솔루션`에서 `SC6000_Dual_OperationInterface.sln`을 여세요. 이 솔루션에는 실행 프로젝트 `SC6000DualMonitor` 하나만 있습니다. 폴더 모드에서 `.sln`과 `.csproj`가 시작 후보로 함께 표시되어도 실행 프로그램이 두 개 생긴 것은 아닙니다. 필요하면 솔루션 탐색기에서 `SC6000DualMonitor`를 우클릭해 시작 프로젝트로 설정하세요.

카메라 IP가 누락되거나 비어 있어도 메인 화면은 실행됩니다. 해당 카메라 영역에 설정 안내를 표시하고 연결을 건너뜁니다. 다른 카메라는 정상적으로 연결하며, 연결 실패도 해당 영역에 표시합니다.


## 고객 검사 화면: PART NO / Count / Set Up

각 카메라 영역에 다음 기능을 제공합니다.

- **PART NO**: 입력 후 `모델명 저장`을 누릅니다. 카메라 IP와 실제 Solution 경로별로 `%LOCALAPPDATA%\WILLB\SC6000Monitor\Models`에 저장하여 재실행/레시피 전환 때 복원합니다. 표시 이름을 바꾸는 기능이며 Solution 선택은 기존 목록을 이용합니다. 장비의 현재 경로를 검증할 수 없으면 저장을 비활성화합니다.
- **Count**: 검사 흐름 완료 콜백마다 `OK / NG / Errors / Total`을 집계합니다. 화면 갱신 타이머는 집계하지 않습니다. 실행 오류, 판정 읽기 실패, OK/NG 이외의 결과는 Errors로 집계합니다. Total = OK + NG + Errors입니다. 숫자 판정값의 의미는 SDK 문서에 정의되지 않아 추측하지 않고 `StrResult`의 OK/NG를 사용합니다.
- **Reset**: 해당 카메라의 카운터만 초기화합니다. 카운터는 프로그램 실행 세션 동안 유지하고 Solution 전환 시 초기화합니다. 두 카메라의 수량은 각각 집계하며 제품 기준으로 합산하지 않습니다.
- **Set Up**: 카메라 내부 Condition Detection의 현재 최소/최대값을 읽고 수정합니다. `적용 및 카메라 저장`은 입력 검증 → 해당 흐름 일시 정지 → 범위 변경 → 카메라 재조회 확인 → `VmSolution.Save()` → 기존 실행 상태 복원을 수행합니다. 부분 실패 시 기존 범위를 복원하고, 복원 실패 시 해당 흐름을 정지 상태로 유지합니다. 생산 트리거를 멈춘 상태에서 설정하세요. 설정 중 검사 결과는 집계에서 제외합니다.

기존 `config.ini`는 자동으로 덮어쓰지 않습니다. 각 카메라 구역에 아래 값을 추가하세요. 생략 시에도 아래 기본값을 사용합니다. 아래 흐름/모듈 이름은 예제이며 실제 카메라의 SDK 이름과 일치해야 합니다.

```ini
[CAMERA1]
; 기존 IP, PASSWORD, SOLUTION_PATH 등은 유지
PROCEDURE=Proc1
CONDITION_MODULE=Condition Detection1
CONDITION_ITEMS=float0;float1;float2;int0;float3
PART_NO=
```

CAMERA2도 해당 레시피에 맞게 같은 항목을 지정합니다. `CONDITION_ITEMS`는 `조건이름:화면표시명`을 세미콜론으로 구분합니다. 정수 조건은 정수만 허용합니다. float1/float2 표시명은 필요에 따라 수정하세요. 치수 단위는 Variable Calculation의 출력 단위 그대로이며 프로그램에서 mm 변환을 추가하지 않습니다. 모듈/조건 이름이 맞지 않으면 해당 카메라의 검사 UI에 오류를 표시하고 기존 영상 화면은 계속 표시합니다.

빌드 시 VisionMaster 설치 SDK의 **IfModuleCs.dll** 참조가 추가로 필요합니다. 자동으로 찾아지지 않으면 Visual Studio의 참조에서 설치된 SDK DLL을 지정하세요. SDK 공급업체 DLL은 저장소에 포함하지 않습니다.

### 현장 확인

1. 두 카메라 연결 후 PART NO를 각각 저장하고 재실행하여 복원 확인.
2. 정상 3회 / 불량 2회 트리거 후 해당 카메라 OK=3, NG=2, Total=5 확인. 대기 중 카운터가 증가하지 않는지 확인.
3. Reset 및 레시피 전환 후 중복 집계가 없는지 확인.
4. 생산 트리거 정지 → Set Up에서 상단/원 치수 변경 → 저장 후 VisionMaster에서 실제 범위 및 IO 판정 확인.
5. 카메라 전원 재투입 후 변경한 범위가 유지되는지 확인. 기존 비활성 흐름이 저장 과정에서 활성화되지 않았는지도 확인.

SDK API는 제공된 .NET V4.4.30 문서와 C# 예제를 기준으로 구현했습니다. 실제 V4.4.50 공급업체 DLL 빌드와 카메라 Remote 동작은 Windows 현장 검증이 필요합니다.

## Set Up 재조회 확인

Set Up은 VisionMaster의 메뉴 창을 여는 대신, 지정한 흐름/Condition Detection의 SDK 파라미터를 읽는 별도 편집창입니다. 매번 `DynamicParams`에서 실제 숫자 조건 목록을 조회하고 각 조건의 `_Min`, `_Max`, `_ReMark`를 조회합니다. 검사 항목은 카메라 Remark를 우선 사용하며, Remark가 비어 있을 때만 `CONDITION_ITEMS` 표시명을 사용합니다. 새로 추가한 `int1` 등의 항목도 자동 표시됩니다.

창 위쪽의 카메라 IP, PROCEDURE, CONDITION_MODULE, 모듈 ID를 VisionMaster에서 연 대상과 비교하세요. `14 Condition Detection` 화면을 확인할 때는 같은 카메라와 Solution/흐름의 실제 모듈 이름을 `CONDITION_MODULE`에 지정해야 합니다. 번호만 보고 다른 모듈을 선택하지 마세요. VisionMaster에서 값 편집 후 Confirm으로 적용해야 장비에 반영된 값을 조회할 수 있습니다. 로컬 PC에서 연 Solution과 카메라의 Solution이 다르면 값도 다를 수 있습니다.

재조회 시 조회 시각과 항목 수를 표시하며, 실패하면 이전 표를 지우고 적용을 비활성화합니다. 기존 SDK의 GetIfItemParam도 장비 조회를 호출하므로 이전 증상을 캐시 문제로 단정할 수 없습니다. 실제 카메라 없이 연결 대상과 장비 변경값 반영까지 검증할 수는 없습니다.

## 재리뷰 수정 사항

- 화면 제목만 보고 `Condition Detection` 또는 `Condition Detection1` 중 하나라고 단정하지 않습니다. SDK에서 해당 흐름의 실제 Condition Detection 모듈을 열거하고 정확한 `Name`으로 선택합니다. 이름이 틀리면 확인된 이름과 ID를 오류 메시지에 표시합니다. 연결 성공 후 Set Up에도 실제 SDK 이름을 표시합니다.
- 기존 GetIfItemParam과 변경한 GetParamValue 모두 IMVS_GetParamValue를 호출합니다. 재읽기의 원래 값 불일치 원인은 아직 실장비에서 확정하지 못했습니다. 다른 카메라/흐름/Solution이나 VisionMaster의 미적용 편집 여부를 확인해야 합니다.
- 표 정렬을 막고 행에 조건 이름을 연결하여 다른 조건의 값이 저장되지 않게 했습니다. 숫자를 다시 저장해도 정밀도가 손실되지 않도록 round-trip 형식으로 표시합니다.
- 편집을 시작한 뒤 장비 값이 변경됐다면 적용을 중단하고 재읽기를 요구합니다. 조건 비교는 행 순서가 아닌 조건 이름을 사용합니다.
- 저장 후 화면 재조회가 실패하면 그 오류를 유지하며 성공 문구로 덮지 않습니다.
- 빌드와 모의 세션 회귀 테스트를 수행했습니다. 실제 카메라 연결, 원격 값 변경 반영, 저장 후 재부팅 지속성은 현장 검증이 필요합니다.

## Set Up 메뉴와 BLOB 조건 설정

각 카메라의 `Set Up`을 누르면 다음 두 메뉴가 나옵니다.

- `OK/NG 설정 조건`: 기존 Condition Detection 조건의 최소/최대값을 편집합니다.
- `BLOB 조건 설정`: 같은 검사 흐름의 Blob Analysis 모듈을 조회하여 Low/High Threshold, 면적 필터 사용 여부, 최소/최대 면적을 편집합니다.

BLOB 목록은 실제 카메라에서 가져옵니다. 모듈이 하나면 자동 선택하고, 여러 개면 이름과 ID를 보고 선택하세요. 특정 모듈을 기본 선택하려면 각 `[CAMERA1]`, `[CAMERA2]` 구역에 `BLOB_MODULE=실제 모듈 이름`을 추가합니다. 생략하거나 비워도 됩니다. 기존 config.ini를 덮어쓸 필요는 없습니다. 카메라마다 별도 세션이므로 설정도 각각 적용됩니다.

Low/High는 0~255, 면적은 1~999999999이며 최소값은 최대값 이하여야 합니다. Low/High 편집은 Double Threshold 모드에서 활성화합니다. 다른 모드는 그대로 유지하고 면적 설정만 편집할 수 있습니다. Threshold Mode 자체를 변경하는 기능은 이번 범위에 포함하지 않았습니다.

`카메라 값 다시 읽기`는 선택한 모듈의 현재 값을 조회합니다. 적용 시 값 검증 → 편집 시작 이후 변경 여부 확인 → 검사 흐름 일시 정지 → 변경 → 재조회 확인 → Solution 저장 → 기존 실행 상태 복원 순서로 처리합니다. 부분 실패는 기존 값 복원을 시도하고, 복원도 실패하면 정지 상태를 유지하며 오류를 표시합니다. 생산 트리거를 멈춘 상태에서 적용하세요.

추가 SDK 참조는 설치된 VisionMaster의 `IMVSBlobFindModuCs.dll`입니다. 실제 공급업체 DLL로 빌드했으며, 모의 세션에서 재읽기·저장·롤백·모듈 선택·카메라별 분리를 검사했습니다. 실제 원격 장비의 적용과 전원 재투입 후 저장 유지 여부는 현장 확인이 필요합니다.

대경테크노 프로그램 아이콘은 `SC6000DualMonitor/Assets/daekyung.ico`이며 16~256픽셀 크기를 포함합니다. 헤더 로고와 아이콘 모두 실행 파일에 내장됩니다.

### VisionMaster 접속 후 영상이 비어 있을 때

각 카메라 하단의 `화면 다시 읽기`는 SDK의 `LoadFrontendSource()`로 현재 Operation Interface를 다시 불러옵니다. 시작 Solution을 로드하거나 검사를 재시작하지 않으며 카운터를 초기화하지 않습니다. VisionMaster의 카메라 연결을 종료한 다음 해당 카메라에서 눌러 보고, 다음 트리거의 영상과 검사 결과를 확인하세요. 호출 성공은 영상 수신이나 검사 정상 완료를 보장하지 않습니다.

`Total`에는 실행 오류도 포함됩니다. `Errors`가 증가하면 `오류 상세`에서 최근 오류의 시각, 모듈 이름/ID, 16진수 오류 코드를 확인하세요. 이후 OK가 들어와도 최근 오류는 유지되며 Solution을 다시 바인딩하면 지워집니다. SDK에서 상세 정보를 제공하지 않는 경우도 표시합니다. 이 기능은 현장 원인을 확인하기 위한 진단 및 화면 재로딩 기능이며, 실제 장비에서 VisionMaster 접속 전후의 영상 복구는 별도 확인이 필요합니다.

### 자동 검사 NG 항목 표시

각 카메라 영상 바로 위 왼쪽에 큰 OK / NG / ERROR 표시를 추가했습니다. NG 아래에는 Condition Detection의 개별 판정이 실패한 모든 항목을 표시합니다. 예: `Middle_Size 에러`, `홀 유무검사 에러`, `Blob 에러`. 항목명은 Set Up과 동일한 카메라 Remark를 우선 사용합니다. float 치수와 int 홀/Blob 조건을 모두 지원하며 config.ini를 추가로 수정할 필요는 없습니다. 다음 OK 및 Reset/솔루션 전환 시 이전 사유를 지웁니다. 목록이 길면 스크롤할 수 있습니다.

카메라의 판단을 그대로 표시하며 모니터링 PC에서 치수를 재판정하지 않습니다. 카메라 콜백의 `%float1Out%`, `%int0Out%` 등 이름별 결과(0=조건 불충족, 1=충족)를 복사하고 검사 회차로 연결합니다. 조건 배열의 순서나 최신값 재조회에 의존하지 않습니다. 개별 결과가 누락되면 `판정 확인 불가`로 표시하며, 해당 회차의 Condition Detection 콜백 자체가 없으면 Errors로 구분합니다. 장비 실행 오류도 제품 NG와 별도로 ERROR로 표시합니다. 수신 데이터로 측정값까지 확인하지 않았으므로 측정값/범위 표시는 추가하지 않았습니다.

검증: 설치된 VisionMaster 4.4.50 SDK 빌드, 실제 SDK 구조체를 사용한 모의 콜백(항목 순서 변경, float/int 동시 실패, 회차 캐시 제한, 단독 모듈 실행 제외), 카운터/카메라 분리, NG→OK 화면 전환을 테스트했습니다. 카메라 실물에서의 고속 연속 트리거 및 영상과 표시의 시간 일치는 현장 검증이 필요합니다. SDK가 그리는 영상 내부 OK/NG 글자는 그대로이며, 추가 판정 영역은 모니터 프로그램이 표시합니다.
