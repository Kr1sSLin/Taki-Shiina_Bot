"""Import a copied legacy memory JSON into an isolated reliable SQLite store.

Run from the Taki_Shiina_Bot directory. DATA_ENC_KEY is read from the existing
environment and never printed; the source file is opened read-only.
"""
import argparse
import json

from secure_storage import load_encryption_config
from services.legacy_migration import migrate_file


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--destination", required=True)
    args = parser.parse_args()
    report = migrate_file(args.source, args.destination, load_encryption_config().key)
    print(json.dumps(report, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
