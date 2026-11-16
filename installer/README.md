# 범용 한글화 설치기

Unity 게임 한글화를 **단일 exe 설치기**로 배포하는 도구 모음. Reus 2 전용이 아니며 `project.json` 한 개만 새로 쓰면 다른 프로젝트에도 쓴다.
사용자는 추가 프로그램을 설치하지 않는다(Windows 10/11 기본 .NET Framework 4.x 로 동작).

## 지원하는 두 가지 설치 방식

| 방식 | 하는 일 | 원본 파일 |
|---|---|---|
| `plugin` (BepInEx) | BepInEx(없을 때만)와 플러그인·번역·글꼴 파일을 복사 | 수정하지 않음 |
| `patch` (직접 패치) | 게임 원본 파일에 델타(RDP1)를 적용. 원본 SHA-256 검증 → 백업 → 적용 → 결과 해시 검증 | 수정함(백업 후 복원 가능) |

한 프로젝트에 두 방식을 모두 넣어도 되고 하나만 넣어도 된다. 설치기 화면은 `modes` 에 정의된 방식만 보여 준다.
설치된 방식은 게임 폴더의 `.locpatch/<id>/install.json` 에 기록되고, **제거**는 이 기록으로 백업을 복원하거나 설치한 파일을 지운다.

## 폴더

```
installer/
  src/Installer.cs            설치기 소스(WinForms, C# 5). 프로젝트 내용과 무관한 범용 코드
  tools/build.py              project.json → payload.zip → 설치 exe (+ 릴리스 zip)
  tools/make_delta.py         원본→수정본 델타 생성기(직접 패치용)
  tests/test_installer.py     가짜 게임으로 설치/제거/차단 동작을 검증하는 통합 테스트
  projects/reus2-ko/          Reus 2 프로젝트 정의(project.json, NOTICE.txt, 사용방법.txt)
  dist/                       빌드 결과(Reus2KR_Setup.exe, 릴리스 zip)
```

## 빌드

```powershell
python .\tools\build.py .\projects\reus2-ko\project.json --zip
python .\tests\test_installer.py     # 설치기 변경 후 반드시 실행
```

결과: `dist/Reus2KR_Setup.exe`(약 4 MB, 번역·플러그인 포함), `dist/reus2-ko_v1.0.0.zip`(exe + NOTICE + 사용방법).
번역(`ko.csv`)이나 `Reus2KR.dll` 을 바꾼 뒤에는 **게임 폴더의 `BepInEx/plugins/Reus2KR` 에 먼저 반영하고** 다시 빌드한다(빌드는 그 폴더를 읽는다).

## 새 프로젝트 만들기

1. `projects/<새이름>/project.json` 을 작성한다(아래 스키마).
2. `python tools/build.py projects/<새이름>/project.json --zip`
3. 직접 패치를 쓰려면 **수정된 게임 파일**(번역 텍스트·이미지를 넣어 UnityPy 등으로 다시 저장한 파일)을 준비하고 `patches` 에 `original`(원본)/`modified`(수정본)를 적는다. 빌드가 델타를 만들고 해시를 기록한다. 원본 데이터는 exe 에 들어가지 않고 차이만 들어간다.

### project.json 스키마(빌드 입력)

```jsonc
{
  "id": "mygame-ko",                 // 설치 기록 폴더 이름. 영문/숫자/-
  "name": "My Game 한국어화", "version": "1.0.0", "description": "창 상단 설명",
  "output": "MyGame_Setup.exe",
  "sourceGameDir": "C:/Games/MyGame",   // 빌드 때 파일을 가져올 위치({sourceGameDir} 치환)
  "notice": "안내 버튼에 보일 문구(\\n 은 줄바꿈)",
  "game": {
    "exe": "MyGame.exe",                  // 실행 중 검사에 사용
    "detectFiles": ["MyGame.exe", "MyGame_Data/Managed/Assembly-CSharp.dll"],
    "searchPaths": ["C:\\Games\\MyGame"],   // 자동 탐지 후보. exe 가 있는 폴더·상위 폴더도 자동 검사
    "steamFolder": "MyGame", "gogId": "123456"   // 선택: Steam/GOG 설치 경로 탐지
  },
  "verify": [{ "file": "MyGame_Data/Managed/Assembly-CSharp.dll", "sha256": "auto",
               "onMismatch": "warn",      // warn=경고 후 진행, block=설치 차단
               "message": "..." }],       // sha256List 로 여러 버전 허용 가능
  "bepinex": { "detect": "BepInEx/core/BepInEx.dll", "files": [ /* 파일 항목 */ ] },
  "modes": {
    "plugin": { "label": "플러그인 방식", "description": "...", "requiresBepInEx": true,
                "files": [ /* 파일 항목 */ ] },
    "patch":  { "label": "원본 파일 직접 패치", "description": "...",
                "patches": [ { "target": "MyGame_Data/resources.assets",
                               "original": "원본 파일 경로", "modified": "수정본 파일 경로" } ],
                "files": [ /* 추가로 복사할 파일 */ ] }
  },
  "distFiles": ["NOTICE.txt", "사용방법.txt"]   // exe 옆에 함께 배포(--zip 시 zip 에 포함)
}
```

파일 항목: `{"from": "경로", "to": "게임 기준 상대경로"}` 또는 `{"fromDir": "폴더", "toDir": "상대 폴더", "include": ["*.dll"]}`.
경로의 `{project}` 는 project.json 폴더, `{sourceGameDir}` 는 위 설정값으로 치환된다.

## 설치기 동작 규칙

- 게임이 실행 중이면 설치/제거를 거부한다.
- `verify` 로 게임 버전을 확인한다(`block` 이면 차단, `warn` 이면 경고).
- BepInEx 가 이미 있으면 덮어쓰지 않고 그대로 쓴다. 제거 시에는 설치기가 깐 BepInEx 만 지우며, 다른 플러그인이 있으면 남긴다.
- 이미 설치된 상태에서 다시 설치하면 기존 설치를 먼저 제거(원복)한 뒤 적용한다.
- 중간에 실패하면 그때까지의 변경을 자동으로 되돌린다.
- 설치 대상 경로는 게임 폴더 밖으로 나갈 수 없다.

### 명령줄(자동화·테스트)

```
Setup.exe --info                          프로젝트·방식 목록
Setup.exe --silent --game <폴더> --mode plugin|patch
Setup.exe --silent --uninstall --game <폴더>
Setup.exe --status --game <폴더>
Setup.exe --snapshot out.png              창 화면을 PNG 로 저장(개발용)
Setup.exe --payload <zip>                 외부 payload 로 실행(개발용)
```

## 한계와 주의

- Windows 전용. 서명되지 않은 exe 라 **SmartScreen/백신이 경고할 수 있다**(`winhttp.dll` 은 BepInEx 로더). 사용방법.txt 에 안내가 들어 있다.
- 직접 패치는 원본 해시가 정확히 맞아야 적용된다. **게임이 업데이트되면 패치를 다시 만들어야 한다**.
- `make_delta.py` 는 파일 전체를 메모리에 올린다. 수 GB 짜리 `.resS` 같은 파일은 다루지 못한다(Python 메모리 한계). 큰 파일은 델타 대신 수정이 일어난 작은 에셋 파일 단위로 나누어 패치한다.
- 직접 패치 경로는 **합성 데이터(삽입·삭제·수정이 섞인 3 MB 파일)로만 검증**했다. 실제 Unity 에셋(번역·이미지 교체)에 대한 검증은 해당 프로젝트에서 따로 해야 한다.
- 설치기 문구는 한국어 고정이다. 다른 언어가 필요하면 `Installer.cs` 의 문자열을 바꿔 빌드한다.
- Reus 2 배포본은 플러그인 방식만 포함한다(직접 패치는 이번 범위가 아님).
