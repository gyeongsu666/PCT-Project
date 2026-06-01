"""
oemer 패치 스크립트 (v4)
========================
두 가지 버그를 한 번에 패치합니다.

  1. build_system.py  — get_key()의 sfns_cands 빈 리스트 IndexError
  2. rhythm_extraction.py — scan_beam_flag()의 hit 변수 UnboundLocalError

기존 패치가 있으면 백업에서 복원 후 재패치합니다.
"""

import importlib.util
import pathlib
import shutil
import sys

PATCH_MARKER = "# [PCT-patch]"


# ─────────────────────────────────────────────────────────────────
# 공통 유틸
# ─────────────────────────────────────────────────────────────────

def find_oemer_file(relative: str) -> pathlib.Path:
    spec = importlib.util.find_spec("oemer")
    if spec is None:
        sys.exit("❌ oemer 패키지를 찾을 수 없습니다.")
    path = pathlib.Path(spec.origin).parent / relative
    if not path.exists():
        sys.exit(f"❌ 파일 없음: {path}")
    return path


def restore_if_needed(path: pathlib.Path) -> str:
    """기존 패치가 있으면 백업에서 원본을 복원한다."""
    backup = path.with_suffix(".py.bak")
    content = path.read_text(encoding="utf-8")
    if PATCH_MARKER in content:
        if not backup.exists():
            sys.exit(
                f"❌ 이전 패치가 감지됐지만 백업 파일이 없습니다: {backup}\n"
                "수동으로 복원하세요."
            )
        shutil.copy2(backup, path)
        print(f"♻️  이전 패치 제거 후 원본 복원: {path}")
        return path.read_text(encoding="utf-8")
    return content


def apply_patch(path: pathlib.Path, content: str,
                target_line: str, new_lines_fn) -> None:
    """
    content 에서 target_line 을 찾아 그 앞에 new_lines_fn(indent, eol) 의
    반환값(list[str])을 삽입한다.
    """
    lines = content.splitlines(keepends=True)

    target_idx = None
    for i, line in enumerate(lines):
        if target_line in line:
            target_idx = i
            break

    if target_idx is None:
        print(f"⚠️  대상 라인을 찾지 못했습니다 (이미 수정됐거나 버전이 다름): '{target_line}'")
        return

    original_line = lines[target_idx]

    # 들여쓰기·줄 끝 감지
    indent = ""
    for ch in original_line:
        if ch in (" ", "\t"):
            indent += ch
        else:
            break
    eol = "\r\n" if original_line.endswith("\r\n") else "\n"

    inserted = new_lines_fn(indent, eol)

    # 백업 (원본이 아직 없을 때만)
    backup = path.with_suffix(".py.bak")
    if not backup.exists():
        shutil.copy2(path, backup)
        print(f"📦 백업 저장: {backup}")

    lines[target_idx:target_idx] = inserted
    new_content = "".join(lines)

    # 문법 검사
    try:
        compile(new_content, str(path), "exec")
    except SyntaxError as e:
        sys.exit(f"❌ 패치 후 문법 오류: {e}\n수동 확인이 필요합니다.")

    path.write_text(new_content, encoding="utf-8")
    print(f"✅ 패치 완료 (line {target_idx + 1}): {path}")


# ─────────────────────────────────────────────────────────────────
# 패치 1: build_system.py — sfns_cands 빈 리스트 guard
# ─────────────────────────────────────────────────────────────────

def patch_build_system():
    path = find_oemer_file("build_system.py")
    print(f"\n🔍 [1/2] 대상: {path}")
    content = restore_if_needed(path)

    def make_lines(indent, eol):
        return [
            f"{indent}if not sfns_cands:  {PATCH_MARKER} empty sfns_cands guard{eol}",
            f"{indent}    import types{eol}",
            f"{indent}    return types.SimpleNamespace(value=0, label=0){eol}",
        ]

    apply_patch(path, content,
                target_line="sfn_label = sfns_cands[0].label",
                new_lines_fn=make_lines)


# ─────────────────────────────────────────────────────────────────
# 패치 2: rhythm_extraction.py — scan_beam_flag hit UnboundLocalError
#
# 원인: hit 변수가 루프 안의 조건 분기에서만 설정되는데,
#       루프가 한 번도 해당 분기를 통과하지 않으면 UnboundLocalError.
# 수정: scan_beam_flag 함수 내부에서 hit = False 를 삽입하는 줄
#       바로 앞에(= 사용되는 첫 줄 앞에) 초기화 구문을 추가한다.
# ─────────────────────────────────────────────────────────────────

def patch_rhythm_extraction():
    path = find_oemer_file("rhythm_extraction.py")
    print(f"\n🔍 [2/2] 대상: {path}")
    content = restore_if_needed(path)

    def make_lines(indent, eol):
        # Python은 함수 내 어딘가에 hit 할당이 있으면 컴파일 타임에
        # 지역변수로 분류 → 루프를 안 타면 UnboundLocalError.
        # try/except UnboundLocalError 로 안전하게 초기화한다.
        return [
            f"{indent}try: hit  {PATCH_MARKER} UnboundLocalError guard{eol}",
            f"{indent}except UnboundLocalError: hit = False{eol}",
        ]

    apply_patch(path, content,
                target_line="beam_count = math.ceil(width / max_width) if hit else 1",
                new_lines_fn=make_lines)


# ─────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    patch_build_system()
    patch_rhythm_extraction()
    print("\n✨ 모든 패치 완료. 서버를 재시작한 뒤 다시 변환해보세요.")
