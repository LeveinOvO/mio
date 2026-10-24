#!/usr/bin/env python3
"""Read the release version and generate the native resource definitions."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent


def version():
    value = (ROOT / 'VERSION').read_text().strip()
    if not re.fullmatch(r'\d+\.\d+\.\d+', value):
        raise ValueError('VERSION must contain major.minor.patch')
    return value


if __name__ == '__main__':
    value = version()
    output = ROOT / 'NativeHost' / 'build'
    output.mkdir(parents=True, exist_ok=True)
    (output / 'version.h').write_text(
        f'#define MIO_VERSION "{value}"\n#define MIO_FILE_VERSION "{value}.0"\n'
        f'#define MIO_VERSION_NUM {value.replace(".", ",")},0\n', encoding='ascii')
    print(value)
