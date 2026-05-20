"""
PCT oemer sidecar
=================

Long-running Python process that imports oemer once and converts images on demand.
The C# server keeps a single instance alive, sending image paths via stdin and
reading status lines from stdout.

Wire protocol (UTF-8, line-delimited; stdin/stdout):

    Server -> sidecar : "<image_path>\\t<output_path>\\n"
    Sidecar -> server : "READY\\n"  (one line on startup, after oemer is loaded)
                       "OK\\t<musicxml_path>\\n"  (on successful conversion)
                       "ERR\\t<single-line message>\\n"  (on failure)

All oemer/TensorFlow stdout chatter is redirected to stderr so it never
contaminates the protocol stream. Stderr is forwarded to the C# console for
debugging.

The sidecar exits when stdin is closed.
"""

from __future__ import annotations

import os
import sys
import argparse
import contextlib
import traceback


def _emit(line: str) -> None:
    """Write one protocol line to stdout (and flush so the server sees it)."""
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def _log(msg: str) -> None:
    """Diagnostic logging — goes to stderr, never to stdout."""
    sys.stderr.write(f"[oemer-sidecar] {msg}\n")
    sys.stderr.flush()


def _load_oemer():
    """Import oemer once. This is the slow part (~15-30s on cold start)."""
    _log("loading oemer + tensorflow ...")
    # Force CPU; matches the existing Program.cs behavior.
    os.environ.setdefault("CUDA_VISIBLE_DEVICES", "-1")
    # Quiet TensorFlow's INFO/WARN spam; keep ERROR.
    os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

    # Redirect any *Python-level* prints during import to stderr so they don't
    # break the protocol (TF's C-level logs already go to stderr).
    with contextlib.redirect_stdout(sys.stderr):
        from oemer.ete import extract, clear_data  # noqa: F401  (used in convert)
    _log("oemer ready")
    return extract, clear_data


def _build_args(image_path: str, output_path: str) -> argparse.Namespace:
    """Build the Namespace that oemer.ete.extract() expects.

    Field reference (from oemer/ete.py:get_parser()):
      img_path, output_path, use_tf, save_cache, without_deskew
    """
    return argparse.Namespace(
        img_path=image_path,
        output_path=output_path,
        use_tf=False,
        save_cache=False,
        without_deskew=False,
    )


def _convert(extract, clear_data, image_path: str, output_path: str) -> str:
    """Run one conversion. Returns the resulting musicxml path."""
    if not os.path.isfile(image_path):
        raise FileNotFoundError(f"image not found: {image_path}")

    out_dir = os.path.dirname(output_path) or "."
    os.makedirs(out_dir, exist_ok=True)

    args = _build_args(image_path, output_path)

    # Clear any cached layer state from a previous run so results don't bleed.
    clear_data()

    # All oemer prints (tqdm bars, info messages) -> stderr, away from protocol.
    with contextlib.redirect_stdout(sys.stderr):
        result_path = extract(args)

    # extract() returns the path it wrote to. Trust it but verify.
    if not result_path or not os.path.isfile(result_path):
        # Fall back to derived path if extract() returned something odd.
        if os.path.isfile(output_path):
            return output_path
        raise FileNotFoundError(
            f"extract() reported '{result_path}' but the file is not on disk"
        )
    return result_path


def main() -> int:
    try:
        extract, clear_data = _load_oemer()
    except Exception as exc:  # pragma: no cover - import failure path
        _log(f"failed to import oemer: {exc}")
        traceback.print_exc(file=sys.stderr)
        _emit(f"ERR\toemer import failed: {exc}")
        return 2

    _emit("READY")

    for raw_line in sys.stdin:
        line = raw_line.rstrip("\r\n")
        if not line:
            continue

        parts = line.split("\t", 1)
        if len(parts) != 2:
            _emit("ERR\tprotocol error: expected '<image_path>\\t<output_path>'")
            continue

        image_path, output_path = parts[0], parts[1]
        try:
            result = _convert(extract, clear_data, image_path, output_path)
            _emit(f"OK\t{result}")
        except Exception as exc:
            # One-line error so the protocol stays line-delimited.
            msg = str(exc).replace("\t", " ").replace("\n", " ").replace("\r", " ").strip()
            if not msg:
                msg = exc.__class__.__name__
            traceback.print_exc(file=sys.stderr)
            _emit(f"ERR\t{msg}")

    return 0


if __name__ == "__main__":
    # Ensure UTF-8 stdio regardless of console code page.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    sys.exit(main())
