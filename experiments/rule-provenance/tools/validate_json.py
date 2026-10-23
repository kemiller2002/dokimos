"""Validate JSON instances against a JSON Schema (draft 2020-12).

Usage: python3 -I validate_json.py SCHEMA INSTANCE [INSTANCE...]
Exit 0 when every instance is valid; 1 otherwise (errors on stderr).
"""
import json
import sys

import jsonschema


def main(argv):
    if len(argv) < 3:
        print(__doc__, file=sys.stderr)
        return 2
    with open(argv[1], encoding="utf-8") as f:
        schema = json.load(f)
    validator = jsonschema.Draft202012Validator(schema)
    failed = False
    for path in argv[2:]:
        with open(path, encoding="utf-8") as f:
            instance = json.load(f)
        errors = sorted(validator.iter_errors(instance), key=lambda e: list(e.path))
        for e in errors:
            failed = True
            print(f"{path}: {'/'.join(map(str, e.path))}: {e.message}", file=sys.stderr)
        if not errors:
            print(f"valid: {path}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
