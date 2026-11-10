"""Build checksum-bound per-file indexes from official release ZIPs.

The signed catalog binds each compressed index; indexes bind file bytes and ZIP
ranges. Archives are never modified. Sign the resulting catalog after this step.
"""
import argparse
import gzip
import hashlib
import json
import re
import stat
import struct
import zipfile
from pathlib import Path


def digest(stream):
    result = hashlib.sha256()
    while block := stream.read(1024 * 1024):
        result.update(block)
    return result.hexdigest()


def safe_path(name):
    return (bool(name) and '\\' not in name and not name.startswith('/')
            and all(part and part not in {'.', '..'} and not part.endswith(('.', ' '))
                    and not any(ord(c) < 32 or c in ':*?"<>|' for c in part)
                    for part in name.split('/')))


def build_index(component, archive):
    with archive.open('rb') as stream:
        if archive.stat().st_size != component['size'] or digest(stream) != component['sha256'].lower():
            raise ValueError('Official package checksum/size mismatch: ' + component['id'])
    source = component['source'].rstrip('/') + '/'
    files, seen, total = [], set(), 0
    with zipfile.ZipFile(archive) as zipped, archive.open('rb') as raw:
        if len(zipped.infolist()) > 50000:
            raise ValueError('Too many archive entries')
        for entry in zipped.infolist():
            # Reject malformed or linked entries anywhere, as the full extractor does.
            name = entry.filename.rstrip('/')
            if not safe_path(name) or name.casefold() in seen or stat.S_ISLNK(entry.external_attr >> 16):
                raise ValueError('Unsafe or duplicate ZIP entry: ' + entry.filename)
            seen.add(name.casefold())
            total += entry.file_size
            if total > 4 * 1024**3 or entry.file_size > 512 * 1024**2:
                raise ValueError('Archive exceeds extraction limits')
            if entry.is_dir() or not entry.filename.startswith(source):
                continue
            relative = entry.filename[len(source):]
            if not safe_path(relative) or entry.flag_bits & 1 or entry.compress_type not in (0, 8):
                raise ValueError('Unsupported indexed file: ' + relative)
            raw.seek(entry.header_offset)
            header = raw.read(30)
            if len(header) != 30 or header[:4] != b'PK\x03\x04':
                raise ValueError('Invalid ZIP local header')
            filename_length, extra_length = struct.unpack_from('<HH', header, 26)
            offset = entry.header_offset + 30 + filename_length + extra_length
            if offset < 0 or offset + entry.compress_size > component['size']:
                raise ValueError('Invalid ZIP data range')
            with zipped.open(entry) as payload:
                checksum = digest(payload)
            files.append(dict(path=relative, size=entry.file_size, sha256=checksum,
                              offset=offset, compressedSize=entry.compress_size, compression=entry.compress_type))
    if not files:
        raise ValueError('Package source has no files: ' + component['id'])
    return dict(schemaVersion=1, archiveSha256=component['sha256'].lower(),
                archiveSize=component['size'], source=component['source'],
                files=sorted(files, key=lambda file: file['path'].casefold()))


def build(manifest_path, package_directories, output, base_url):
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    output.mkdir(parents=True, exist_ok=True)
    if not base_url.startswith('https://') or '?' in base_url or '#' in base_url:
        raise ValueError('An immutable HTTPS release asset base URL is required')
    summaries = []
    for component in manifest['components']:
        if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,79}', component['id']):
            raise ValueError('Invalid component identity')
        asset = component['asset']
        if Path(asset).name != asset or '/' in asset or '\\' in asset:
            raise ValueError('Invalid package filename')
        matches = [directory / asset for directory in package_directories if (directory / asset).is_file()]
        if not matches:
            raise FileNotFoundError('Official release ZIP required: ' + asset)
        index = build_index(component, matches[0])
        content = gzip.compress(json.dumps(index, ensure_ascii=False, separators=(',', ':')).encode('utf-8'), mtime=0)
        if len(content) > 2 * 1024**2:
            raise ValueError('Compressed file index exceeds client metadata limit')
        name = component['id'] + '-' + component['sha256'][:16].lower() + '.files.json.gz'
        destination = output / name
        temporary = destination.with_suffix(destination.suffix + '.tmp')
        temporary.write_bytes(content)
        temporary.replace(destination)
        component['fileIndex'] = dict(asset=name, assetUrl=base_url.rstrip('/') + '/' + name,
                                      size=len(content), sha256=hashlib.sha256(content).hexdigest())
        summaries.append(dict(component=component['id'], files=len(index['files']), indexBytes=len(content)))
    manifest['minimumLauncherVersion'] = '0.7.35'
    destination = output / 'ksr-release.json'
    temporary = output / 'ksr-release.json.tmp'
    temporary.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    temporary.replace(destination)
    return summaries


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--packages', type=Path, action='append', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--base-url', required=True)
    args = parser.parse_args()
    print(json.dumps(build(args.manifest, args.packages, args.output, args.base_url), indent=2))
