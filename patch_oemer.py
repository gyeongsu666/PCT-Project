"""
oemer 패치 스크립트
1. [build_system.py] slot_duras numpy array 비교 버그 (ValueError)
2. [build_system.py] last_pos - cur_pos 정수 오버플로 (RuntimeWarning)
3. [sklearn] SVC pickle 버전 불일치 경고 억제 (InconsistentVersionWarning)
4. [build_system.py] invalid 음표 드랍 방지 → 그대로 포함
5. [build_system.py] 옥타브 범위 초과 음표 드랍 방지 → 가장 가까운 유효 음으로 클램핑
"""
import importlib.util
import pathlib
import sys

# ---------------------------------------------------------------------------
# 헬퍼
# ---------------------------------------------------------------------------

def find_module(name: str) -> pathlib.Path:
    spec = importlib.util.find_spec(name)
    if spec is None:
        sys.exit(f"❌ 모듈을 찾을 수 없습니다: {name}")
    return pathlib.Path(spec.origin)

def apply_patch(path: pathlib.Path, old: str, new: str, label: str) -> bool:
    text = path.read_text(encoding="utf-8")
    if new in text:
        print(f"  ℹ️  이미 패치됨: {label}")
        return False
    if old not in text:
        print(f"  ⚠️  대상 라인 없음 (oemer 버전 불일치?): {label}")
        return False
    path.write_text(text.replace(old, new, 1), encoding="utf-8")
    print(f"  ✅ 패치 완료: {label}")
    return True

# ---------------------------------------------------------------------------
# 패치 정의
# ---------------------------------------------------------------------------

PATCHES = [
    # 1. slot_duras numpy array 비교 (ValueError)
    {
        "module": "oemer.build_system",
        "label": "slot_duras numpy array guard",
        "old_patterns": [
            "if not self.slot_duras:  # [PCT-patch] slot_duras NoneType guard",
            "if not self.slot_duras:",
        ],
        "new": "if self.slot_duras is None or not len(self.slot_duras):  # [PCT-patch] numpy array safe guard",
    },
    # 2. last_pos - cur_pos 정수 오버플로 (RuntimeWarning → 잘못된 결과)
    {
        "module": "oemer.build_system",
        "label": "overflow-safe pos diff",
        "old_patterns": [
            "diff = last_pos - cur_pos",
        ],
        "new": "diff = int(last_pos) - int(cur_pos)  # [PCT-patch] cast to Python int to avoid numpy overflow",
    },
    # 4. invalid 음표 드랍 방지: decode_note에서 note.invalid 시 None 반환하던 것 제거
    {
        "module": "oemer.build_system",
        "label": "invalid note drop prevention",
        "old_patterns": [
            "    if note.invalid:\n        return None  # type: ignore\n\n    # Element order matters!!",
        ],
        "new": (
            "    # [PCT-patch] invalid 음표도 드랍하지 않고 staff_line_pos 기반으로 그대로 포함\n"
            "    # (note.invalid=True 여도 위치 정보는 있으므로 최선의 음으로 출력)\n\n"
            "    # Element order matters!!"
        ),
    },
    # 5. 옥타브 범위 초과 음표 클램핑: None 반환 대신 가장 가까운 유효 음으로 조정
    {
        "module": "oemer.build_system",
        "label": "out-of-range octave clamping",
        "old_patterns": [
            "    # Check the pitch is within A0~C8\n"
            "    if (int(octave.text) < 0 or int(octave.text) > 8) \\\n"
            "            or (int(octave.text) == 0 and step.text != \"A\") \\\n"
            "            or (int(octave.text) == 8 and step.text != \"C\"):\n"
            "        return None  # type: ignore",
        ],
        "new": (
            "    # [PCT-patch] 범위 초과 시 드랍 대신 가장 가까운 유효 음으로 클램핑\n"
            "    _oct = int(octave.text)\n"
            "    if _oct < 0:\n"
            "        octave.text, step.text, alter.text = '0', 'A', '0'\n"
            "    elif _oct > 8:\n"
            "        octave.text, step.text, alter.text = '8', 'C', '0'\n"
            "    elif _oct == 0 and step.text != 'A':\n"
            "        octave.text = '1'\n"
            "    elif _oct == 8 and step.text != 'C':\n"
            "        octave.text = '7'"
        ),
    },
    # 6. 음표 없는 마디 스킵: 오탐 바라인으로 생기는 빈/쪼개진 마디를 다음 마디에 합침
    {
        "module": "oemer.build_system",
        "label": "empty measure merge (too many measures fix)",
        "old_patterns": [
            "                if isinstance(inst, Barline):\n"
            "                    if len(buffer) == 0:\n"
            "                        # Double barline\n"
            "                        double_barline = True\n"
            "                    else:\n"
            "                        mm = gen_measure(buffer, grp, num, at_beginning, double_barline)\n"
            "                        self.measures[grp].append(mm)\n"
            "\n"
            "                        num += 1\n"
            "                        buffer = []\n"
            "                        at_beginning = False\n"
            "                        double_barline = False\n"
            "                    continue",
        ],
        "new": (
            "                if isinstance(inst, Barline):\n"
            "                    if len(buffer) == 0:\n"
            "                        # Double barline\n"
            "                        double_barline = True\n"
            "                    elif not any(isinstance(s, Voice) for s in buffer):\n"
            "                        # [PCT-patch] 음표(Voice) 없는 마디는 확정하지 않고 다음 마디에 합침\n"
            "                        # (오탐 바라인으로 생긴 빈 마디 제거)\n"
            "                        pass\n"
            "                    else:\n"
            "                        mm = gen_measure(buffer, grp, num, at_beginning, double_barline)\n"
            "                        self.measures[grp].append(mm)\n"
            "\n"
            "                        num += 1\n"
            "                        buffer = []\n"
            "                        at_beginning = False\n"
            "                        double_barline = False\n"
            "                    continue"
        ),
    },
]

# ---------------------------------------------------------------------------
# sklearn InconsistentVersionWarning 억제 패치 (oemer/__init__.py 또는 ete.py)
# ---------------------------------------------------------------------------

SKLEARN_SUPPRESS = '''\
import warnings as _warnings
from sklearn.exceptions import InconsistentVersionWarning as _IVW
_warnings.filterwarnings("ignore", category=_IVW)
# [PCT-patch] suppress SVC pickle version mismatch warning
'''

def patch_sklearn_warning():
    # oemer/ete.py 상단에 경고 억제 코드 삽입
    try:
        ete_path = find_module("oemer.ete")
    except SystemExit:
        print("  ⚠️  oemer.ete 모듈을 찾을 수 없어 sklearn 경고 억제 패치 생략")
        return

    text = ete_path.read_text(encoding="utf-8")
    marker = "# [PCT-patch] suppress SVC pickle version mismatch warning"
    if marker in text:
        print("  ℹ️  이미 패치됨: sklearn InconsistentVersionWarning 억제")
        return

    # 첫 번째 import 줄 앞에 삽입
    lines = text.splitlines(keepends=True)
    insert_at = 0
    for i, line in enumerate(lines):
        if line.startswith("import ") or line.startswith("from "):
            insert_at = i
            break
    lines.insert(insert_at, SKLEARN_SUPPRESS)
    ete_path.write_text("".join(lines), encoding="utf-8")
    print("  ✅ 패치 완료: sklearn InconsistentVersionWarning 억제")

# ---------------------------------------------------------------------------
# 실행
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    print("=== oemer 패치 시작 ===\n")

    patched_files: dict[str, pathlib.Path] = {}

    for p in PATCHES:
        mod_path = find_module(p["module"])
        patched_files[p["module"]] = mod_path
        print(f"[{p['label']}]  {mod_path}")
        for old in p["old_patterns"]:
            if apply_patch(mod_path, old, p["new"], p["label"]):
                break

    print(f"\n[sklearn 경고 억제]")
    patch_sklearn_warning()

    print("\n=== 완료 ===")

