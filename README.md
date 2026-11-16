# Reus2KR — Reus 2 한국어화

Reus 2(GOG v1.12.2 기준)의 한국어 번역과 BepInEx 플러그인, 그리고 다른 Unity 게임에도 쓸 수 있는 범용 설치기 도구입니다.
**설치 파일은 [Releases](../../releases)에서 받으세요.** (`Reus2KR_Setup.exe` 한 개, 추가 프로그램 설치 불필요)

## 구성

| 폴더 | 내용 |
|---|---|
| `plugin/src` | 플러그인 소스. 글꼴 선택, 한국어 조사 처리, 미번역 보충, 원본 버그 보정, ESC 로 번역 즉시 반영 |
| `plugin/translation` | 번역 본문 `ko_current.csv`(`KEY` + U+001D + 번역값, 여러 줄 레코드)와 플러그인 내장 번역 목록 |
| `plugin/tools` | 번역 작업 도구(`ko-tool.py`: JSONL 변환·가져오기·검증 등) |
| `plugin/tests` | 번역 파일 로더·조사 처리 회귀 테스트 |
| `installer` | 범용 설치기(단일 exe) 소스·빌더·테스트와 Reus 2 프로젝트 정의. 사용법은 `installer/README.md` |

## 빌드

```powershell
# 플러그인 DLL
pwsh -File .\plugin\src\build.ps1

# 설치기(exe) — 게임 폴더의 BepInEx\plugins\Reus2KR 내용이 포함됩니다
python .\installer\tools\build.py .\installer\projects\reus2-ko\project.json --zip
python .\installer\tests\test_installer.py
```

빌드에는 게임이 설치된 폴더(`C:\My Games\Reus 2`)와 .NET SDK 가 필요합니다. 경로는 `project.json` 의 `sourceGameDir` 와 각 스크립트에서 바꿉니다.
일부 도구(`plugin/tools`)는 작업용 폴더 구조(`.Works`)를 가정하므로 그대로는 동작하지 않을 수 있습니다.

## 포함하지 않는 것
게임 원본 데이터(언어 파일 원문, 추출 자료, 이미지), 게임 실행 파일. 오픈소스·글꼴 고지는 `installer/projects/reus2-ko/NOTICE.txt` 를 참고하세요.
