# 플러그인(Reus2KR.dll)에 내장된 번역 목록

원본: `src/Plugin.cs`(`RuntimeUiFallback.RuntimeOverrides` 등), `src/KoreanFontPicker.cs`. ko.csv에 키가 없거나 게임이 직접 영어를 표시하는 문구를 DLL이 보충한다.
이 파일은 **참조용 사본**이다. 수정은 소스에서 하고 빌드한다(`plugin/src/build.ps1`). 마지막 검토: 2026-11-17.

## 1. 메인 메뉴·상점 홍보문 (`RuntimeOverrides`)

| # | 영문 | 한국어 |
|---|---|---|
| 1 | Diversity | 다양성 |
| 2 | Reus 2 on Console | Reus 2 콘솔 출시 |
| 3 | Reus 2 is out now on Switch and Xbox, along with the Ice Age DLC. | Reus 2가 빙하기 DLC와 함께 Nintendo Switch와 Xbox로 출시되었습니다. |
| 4 | Learn More | 자세히 보기 |
| 5 | Go to Store | 상점으로 *(ko.csv id 2880과 통일)* |
| 6 | Available only when UI scale is set to 130% | 인터페이스 크기가 130%일 때만 사용할 수 있습니다. |
| 7 | Jurassic DLC release! | 쥐라기 DLC 출시! |
| 8 | Jump back in time to find dinosaurs, new leaders and a planet-wide threat! | 과거로 돌아가 공룡과 새로운 지도자, 행성 전체를 위협하는 위험을 만나 보세요! |
| 9 | The new Mesozoic Wildlands biome shifts between three distinct modes … avert Armageddon. | 새로운 생물군계인 중생대 황야는 배치한 바이오티카에 따라 서로 다른 세 가지 상태로 바뀝니다. 상태마다 고유한 공생 효과가 해금되고 생물군계의 작동 방식도 달라집니다! 고생물학자를 비롯한 새로운 지도자 네 명이 ‘위대한 조각가’가 되고 ‘세계의 종말’을 막도록 도와주세요. |
| 10 | Grasslands DLC released! | 대초원 DLC 출시! |
| 11 | A full new biome where you start fires to strengthen it, … | 불을 일으켜 땅을 강화하는 새로운 생물군계와 새로운 영혼, 전환점을 만나 보세요! |
| 12 | A dry and brittle land, the Grasslands sits in a balance of life and destruction. … Manchuria support update. | 메마르고 부서지기 쉬운 초원은 생명과 파괴 사이에서 균형을 이룹니다. 바이오티카를 배치할 때마다 땅의 압력이 높아집니다. 피할 수 없는 불이 일어나 땅 전체로 번지면, 연쇄 효과를 활용해 불길이 닿은 슬롯을 강화할 수 있습니다. 강화하고 재배화한 바이오티카로 땅에 다시 생명을 불어넣으세요! 이 DLC는 만주 지원 업데이트와 함께 출시되었습니다. |
| 13 | Visit the Everglades! | 대습지로 떠나세요! |
| 14 | Explore the amphibian world in the Wetlands. More leaders, more eras. | 습지에서 물과 땅을 넘나드는 생명의 세계를 탐험하세요. 더 많은 지도자와 시대가 기다립니다. |
| 15 | The Everglades DLC introduces a whole new biome, the Wetlands, … | 대습지 DLC에는 새로운 생물군계인 습지와 새로운 지도자, 시대 선택지가 추가됩니다. 일부 토지는 물속에, 나머지는 물 밖에 있는 습지에서는 소형 창조물이 큰 역할을 합니다! 수륙 양용 바이오티카와 파충류, 새, 물고기를 활용하고, 최대한 많은 소형 창조물로 강화하세요. |
| 16 | The Ice Age DLC is out! | 빙하기 DLC 출시! |
| 17 | New Prehistoric Ice Age biome with unique mechanics & biotica. … | 독특한 작동 방식과 바이오티카를 갖춘 새로운 선사시대 빙하기 생물군계, 새로운 지도자와 시대 선택지가 추가됩니다. |
| 18 | The Ice Age DLC introduces a whole new biome, the Prehistoric Ice Age, … | 빙하기 DLC에는 새로운 생물군계인 선사시대 빙하기와 독특한 바이오티카, 서리 거인의 새로운 모습이 추가됩니다. 진화를 바탕으로 자원을 전략적으로 관리하는 새로운 도전과 함께, 새로운 지도자와 시대 선택지도 만나 볼 수 있습니다. 생명은 언제나 적응할 길을 찾아내기에 자연은 변화도 추위도 두려워하지 않습니다. |
| 19 | Join the Community | 커뮤니티에 참여하세요 |
| 20 | Discuss strategy, meet the devs and get the latest news. | 전략을 나누고 개발자들을 만나 최신 소식을 받아 보세요. |

## 2. 날짜·남은 시간 형식 (동적 변환)

| 영문 | 한국어 |
|---|---|
| `Month D, YYYY` | `YYYY년 M월 D일` |
| `N days, N hours, N minutes left` | `N일 N시간 N분 남음` |

## 3. 행성 이름의 그리스 문자 (`Greek letter table`)

알파, 베타, 감마, 델타, 엡실론, 제타, 에타, 세타, **이오타**(요타에서 수정), 카파, 람다, 뮤, 뉴, 크시, 오미크론, 파이(Pi), 로, 시그마, 타우, 입실론, 피(Phi), 카이, 프사이, 오메가

## 4. 언어·글꼴 선택 UI (`KoreanFontPicker.cs`)

| 용도 | 한국어 |
|---|---|
| 언어 이름 | 한국어 (MOD) |
| 글꼴 크기 슬라이더 | 글꼴 크기 (%) |
| 글꼴 크기 설명 | 기본값은 100%입니다. 게임 안의 글자를 80~120% 범위에서 1%씩 조절합니다. 설정 화면은 100%를 유지하며, 고정 박스에서는 공간에 맞춰 크기를 제한합니다. |
| 글꼴 선택 | 한국어 글꼴 |
| 글꼴 선택 설명 | Reus2KR 폴더의 TTF 글꼴을 선택합니다. 기본 글꼴을 선택하면 원래 글꼴로 돌아갑니다. |
| 기본 항목 | 기본 글꼴 |

## 5. 번역이 아닌 내장 데이터 (검토 제외)
- 조사 처리 규칙(`은(는)`·`을(를)`·`(으)로` 등), 받침 판정, 숫자 한글 읽기(`영일이삼사오육칠팔구`).
- 아이콘 마지막 음절 표(`SpriteReading`, 예: food=량, valuables=금, wealth=부): 조사 결정용 읽기이며 화면에 표시되지 않는다.

## 6. 2026-11-17 검토 결과
| 항목 | 변경 | 사유 |
|---|---|---|
| Go to Store | `상점으로 이동` → `상점으로` | ko.csv id 2880과 통일 |
| Mesozoic 홍보문 | `세 가지 모드` → `서로 다른 세 가지 상태`, 문장 구조 정리 | ko.csv 3241·5576(`상태`)과 통일. 지도자·시대 설명의 주어를 `‘위대한 조각가’`·`‘세계의 종말’` 전환점 이름으로 명확히 함 |
| Grasslands 홍보문 | `연약한 초원` → `부서지기 쉬운 초원` | brittle의 자연스러운 표현 |
| Greek `Iota` | `요타` → `이오타` | 표준 그리스 문자 표기 |
유지: `재배화한`(Domesticated, ko.csv id 6365 `재배화`와 일치), `압력`(Stress 계열 표기).
