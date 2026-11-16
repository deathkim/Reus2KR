import UnityPy
import csv
import re
from pathlib import Path

# 게임 설치 경로에 맞게 수정
GAME_DIR = Path(
    r"C:\My Games\Reus 2"
)

ASSETS_FILE = GAME_DIR / "Reus 2_Data" / "sharedassets0.assets"
OUTPUT_DIR = GAME_DIR / '.Works' / 'extracted'

OUTPUT_DIR.mkdir(parents=True, exist_ok=True)


def safe_name(name):
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name)
    return name.strip(" .")[:100] or "unnamed"


def main():
    if not ASSETS_FILE.is_file():
        print(f"파일이 없습니다: {ASSETS_FILE}")
        return

    print("=" * 60)
    print("Reus 2 TextAsset 추출")
    print("=" * 60)

    print("Unity 파일 불러오는 중...")
    env = UnityPy.load(str(ASSETS_FILE))

    records = []
    errors = []
    count = 0

    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue

        try:
            data = obj.read()
            name = data.m_Name or "unnamed"
            raw = data.m_Script

            if isinstance(raw, str):
                raw = raw.encode("utf-8")
            else:
                raw = bytes(raw)

            count += 1
            stem = f"{count:04d}_{safe_name(name)}"

            # 원본 데이터 보존
            raw_path = OUTPUT_DIR / f"{stem}.bytes"
            raw_path.write_bytes(raw)

            # UTF-8로 해석할 수 있는 데이터만 텍스트 저장
            text_saved = False

            try:
                content = raw.decode("utf-8-sig")
                text_path = OUTPUT_DIR / f"{stem}.txt"
                text_path.write_text(
                    content, encoding="utf-8"
                )
                text_saved = True
            except UnicodeDecodeError:
                pass

            records.append({
                "name": name,
                "path_id": obj.path_id,
                "size": len(raw),
                "raw_file": raw_path.name,
                "text_saved": text_saved
            })

            print(
                f"[{count:04d}] {name} "
                f"({len(raw):,} bytes)"
            )

        except Exception as e:
            errors.append(
                f"PathID {obj.path_id}: {e}"
            )

    # 추출 목록 저장
    csv_path = OUTPUT_DIR / "manifest.csv"

    with csv_path.open(
        "w", encoding="utf-8-sig", newline=""
    ) as f:
        writer = csv.DictWriter(
            f,
            fieldnames=[
                "name", "path_id", "size",
                "raw_file", "text_saved"
            ]
        )
        writer.writeheader()
        writer.writerows(records)

    if errors:
        (OUTPUT_DIR / "errors.txt").write_text(
            "\n".join(errors), encoding="utf-8"
        )

    print("\n" + "=" * 60)
    print(f"추출된 TextAsset: {count}개")
    print(f"오류: {len(errors)}개")
    print(f"저장 위치: {OUTPUT_DIR}")
    print("=" * 60)


if __name__ == "__main__":
    main()
