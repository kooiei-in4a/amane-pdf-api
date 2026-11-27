import argparse
from pathlib import Path
import sys

import requests


def main():
    parser = argparse.ArgumentParser(description="PDF を最適化して保存します。")
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    try:
        with args.input.open("rb") as pdf:
            response = requests.post(
                "http://127.0.0.1:8080/api/pdf/optimize",
                files={"file": (args.input.name, pdf, "application/pdf")},
                timeout=(5, 60),
                allow_redirects=False,
            )

        media_type = response.headers.get("Content-Type", "").split(";", 1)[0].strip().lower()
        if response.status_code == 200 and media_type == "application/pdf":
            args.output.write_bytes(response.content)
            return 0

        print(f"HTTP {response.status_code}", file=sys.stderr)
        if media_type == "application/problem+json":
            try:
                problem = response.json()
            except ValueError:
                problem = None
            if isinstance(problem, dict):
                for key in ("title", "reason"):
                    if isinstance(problem.get(key), str):
                        print(f"{key}: {problem[key]}", file=sys.stderr)
    except requests.RequestException:
        print("API との通信に失敗しました。", file=sys.stderr)
    except OSError:
        print("ファイルの読み書きに失敗しました。", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
