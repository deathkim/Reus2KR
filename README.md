# Reus2KR — Reus 2 한국어화

Reus 2(GOG v1.12.2 기준) 한국어 번역과 BepInEx 플러그인(`Plugin.cs`)입니다.
**설치 파일은 [Releases](../../releases)에서 받으세요.** (`Reus2KR_Setup.exe` 한 개, 추가 프로그램 설치 불필요)

## 구성

| 경로 | 내용 |
|---|---|
| `plugin/src/Plugin.cs` | 플러그인 소스. 한국어 번역 로딩, 조사 처리, 미번역 보충, 원본 버그 보정, ESC 로 번역 즉시 반영 |
| `plugin/src/Reus2KRBuild.csproj` | 플러그인 프로젝트 파일 |
| `plugin/translation/ko_current.csv` | 번역 본문(`KEY` + U+001D + 번역값, 여러 줄 레코드). 게임의 `BepInEx/plugins/Reus2KR/ko.csv` 와 동일 |
| `plugin/translation/plugin-embedded.md` | 플러그인에 내장된 번역 목록 |
| `plugin/tools/ko-tool.py`, `plugin/ko-tool.bat` | 번역을 JSONL 로 내보내고 가져오는 작업 도구와 실행 배치 |

게임 데이터와 실행 파일은 포함하지 않습니다.
