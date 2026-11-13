# 노출 응답과 FTPES 설정 오류 검증 (2026-11-13)

대상: 설치된 VisionMaster 4.4.50. 실제 카메라에 접속하거나 설정/파일을 변경하지 않고 SDK와 로컬 테스트로 확인했다.

## 노출 / 게인

`GlobalSmartCameraModuleCs.GlobalSmartCameraParamItem.GetGlobalSmartCameraDoubleValue`는 `$`로 분리한 응답의 길이가 **2보다 크면** 앞 세 필드를 사용한다. 첫 필드는 값, 두 번째는 최소값, 세 번째는 최대값이다. 기존 뷰어는 정확히 3개만 허용하여 뒤에 추가 필드가 있는 응답을 거부했다.

- DLL: `Development/V4.x/ComControls/Assembly/GlobalSmartCameraModuleCs.dll`
- SHA256: `a82ec1a3b5a8d5f92418b3535f65272fc605e8afa28432f743a63cf8590ca920`
- IL `0018–0021`: 배열 길이 `> 2` 검사.
- IL `0041`, `004F`, `005C`: 각각 필드 1, 2, 0을 숫자로 읽음.
- `ExposureTime`과 `Gain` getter 모두 이 함수를 사용한다.

뷰어도 추가 필드를 허용하도록 수정했다. 앞 세 필드의 숫자, 유한값, 범위 검증은 유지한다. 실패 안내에는 항목명과 응답 필드 수를 포함한다. 원래 현장 응답 원문은 확보하지 못했으므로 다른 형태의 응답까지 현장에서 해결됐다고 단정하지 않는다.

`tests/RunExposureTests.ps1`: 확장 응답과 잘못된 범위, 적용/복원 등 53개 검사 및 설치된 SDK의 IL/XML 계약 12개 검사를 통과했다. 자동 모드 OFF=0도 XML과 일치한다. IL 검사는 해당 SDK 버전에 의존하므로 SDK 변경 시 재검토가 필요하다.

## FTPES 설정

내장 FileTranferTool은 `FTP_SetProtocol(FTPES)` 반환값을 검사하지 않고 로그인을 진행한다. 관리 코드에 반환형이 `int`로 선언되어 있지만 실제 네이티브 구현은 상태 코드를 설정하지 않고 내부 객체 포인터를 반환 레지스터에 남긴다. 뷰어가 이 값을 오류 코드로 검사해 로그인 전에 중단한 것이 문제다. 화면의 `0xF34902D0`은 이 단계에서 표시된 값이며 비밀번호 오류라는 근거가 아니다.

네이티브 DLL을 pefile/capstone으로 읽어 확인한 RVA:

| DLL | 경로 | 호출 경로와 마지막 동작 |
| --- | --- | --- |
| x64 | `Applications/PublicFile/x64/FTPClientSDK.dll` | `FTP_SetProtocol` 0x27360 → 0x2EB40 → 0x2E5A0. RAX에 객체 포인터를 놓고 `[RAX+0x90]`에 프로토콜을 저장한 뒤 반환. |
| x86 | `Applications/Tools/FileTranferTool/FTPClientSDK.dll` | `_FTP_SetProtocol@4` 0x1F890 → 0x25990 → 0x25420. EAX에 객체 포인터를 놓고 `[EAX+0x70]`에 프로토콜을 저장한 뒤 반환. |

SHA256:

- x64: `b88469d9b5baba8f6bf749e4422fe947fedef44b1db46e1477257c7174c4939a`
- x86: `44f4d67d6fd4ab299d5b7f41ab14f06e6725becf80aef9ea871bddc31ca79a3d`

수정은 프로토콜 설정 반환값에 대한 잘못된 상태 검사만 제거한다. 호출 예외와 초기화, 활성화, 로그인, 조회, 삭제, 이름 변경의 실제 오류 검사는 유지한다. FTPES=2를 계속 사용하며 평문 FTP로 재시도하지 않는다.

`tests/RunRecipeFileTests.ps1`:

- 파일 보호/화면 59개, FTP 세션 모의 검사 42개, 설치 SDK 계약 35개 통과.
- 실제 x64/x86 `FTP_SetProtocol(2)`를 각각 별도 프로세스에서 호출하고 그 반환값을 사용하는 뷰어 세션이 모의 로그인/조회까지 진행하는 것을 확인했다.
- 네이티브 초기화/활성화/로그인/파일 작업은 호출하지 않았다. 실제 FTP 계정 권한과 현장 파일 변경의 성공 여부는 이 테스트 범위 밖이다.

## 삭제 접근 거부 — 2026-11-13 추가 확인

후속 화면에는 프로토콜 설정 오류가 아닌 `파일 삭제 실패: 0xE0001309`가 표시됐다. 사용자는 **VisionMaster 내장 FileTranferTool에서도 같은 레시피 삭제가 실패한다**고 확인했다.

- 설치된 `Development/V4.x/Includes/ErrorCodeDefine.h` 381행은 `0xE0001309`를 `IMVS_EC_FTP_ACCESS_DENIED`로 정의한다. 로그인 거부는 별도 `0xE0001367` (`IMVS_EC_FTP_LOGIN_DENIED`)이다.
- 내장 `DirectoryBrowser.AppViewModel.DeleteRemoteOperate`는 `FtpRemoveFile(CurrentRemoteItem.GetFtpPath())`를 호출한다.
- `DirectoryBrowser.RemoteDirectoryItem.GetFtpPath()`는 `/root/vmtempfiles/ftp/`와 상대 파일 경로를 결합한다. 뷰어도 동일한 전체 장비 경로를 UTF-8로 `FTP_RemoveFile`에 전달한다. 이번 확인으로 임의의 다른 경로로 삭제를 재시도할 근거는 없다.
- 확인한 DirectoryBrowser.exe SHA256: `dabbac4e016fd72479002b9a834904003712a5cb61f062d964aa97e41019341f`. REA 정적 메타데이터 확인 및 로컬 ILSpy로 해당 메서드를 읽었다. 삭제 동작을 실행하지 않았다.

뷰어에서는 접근 거부와 로그인 실패 안내를 분리하고, 삭제/이름 변경 오류에 대상 경로를 포함하도록 수정했다. 비밀번호를 바꾸라고 오해시키던 일괄 안내를 제거했다. 현재 파일 보호와 오류 후 자동 재시도 금지는 유지한다. 접근 거부 원인이 구체적으로 권한인지 파일 상태인지 이 코드만으로 확정할 수 없다. 카메라의 권한/상태를 확인할 수 없으므로 **실제 삭제 문제는 해결됐다고 판단하지 않는다.**
