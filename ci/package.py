"""Assemble redistributable Windows package from same-pipeline build artifacts."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import urllib.request
import zipfile

BASE_URL = ('https://gitlab.com/api/v4/projects/rygt-team%2FRyGTS/packages/generic/'
            'gt-sport-prerelease/gt-sport-2026-10-25/'
            'GTSport-Ryty-shadGT-2026-10-25-prerelease%2Ezip')
BASE_SHA = '563349eb82d091d16a7d6864dca437d17a6dd3aee6f994d256399bf29aae7702'
BASE_FOLDER = 'GTSport-Ryty-shadGT-2026-10-25-prerelease'
FORBIDDEN = {'.self', '.elf', '.sprx', '.pkg', '.vol', '.adc', '.log', '.pdb'}


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write_packaged_defaults(stage):
    config_path = stage / 'user/custom_configs/CUSA03220.json'
    config = json.loads(config_path.read_text(encoding='utf-8-sig'))
    config.setdefault('GPU', {})['precompile_shaders'] = True
    config.setdefault('Vulkan', {})['pipeline_cache_enabled'] = True
    config_path.write_text(json.dumps(config, indent=2) + '\n', encoding='utf-8')


def verify_artifact(folder, filename, commit, kind="pe"):
    actual_commit = (folder / 'RYGT-COMMIT.txt').read_text(encoding='utf-8-sig').strip()
    if actual_commit != commit:
        raise RuntimeError(f'Artifact revision mismatch: {folder}')
    entries = {}
    for line in (folder / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
        digest, name = line.split('  ', 1)
        if (Path(name).name != name or any(c in name for c in ('/', '\\', ':')) or name in entries
                or not re.fullmatch('[0-9a-fA-F]{64}', digest)):
            raise RuntimeError('Invalid artifact manifest entry')
        if sha(folder / name) != digest.lower():
            raise RuntimeError(f'Artifact checksum mismatch: {name}')
        entries[name] = digest
    if filename not in entries:
        raise RuntimeError(f'Missing artifact checksum: {filename}')
    if kind == 'pe' and (folder / filename).read_bytes()[:2] != b'MZ':
        raise RuntimeError(f'Expected Windows PE artifact: {filename}')
    if kind == 'pdb' and not (folder / filename).read_bytes().startswith(b'Microsoft C/C++ MSF 7.00'):
        raise RuntimeError('Expected a verified Windows PDB')
    return folder / filename


def verify_bundle(folder, filename, commit, flavor):
    binary = verify_artifact(folder, filename, commit)
    metadata = json.loads(verify_artifact(folder, 'BUILD-PROFILE.json', commit, 'data').read_text(encoding='utf-8-sig'))
    if (metadata.get('schema') != 1 or metadata.get('flavor') != flavor or
            metadata.get('commit') != commit or metadata.get('optimized') is not True):
        raise RuntimeError('Build profile mismatch')
    if flavor == 'Diagnostics':
        pdb = verify_artifact(folder, Path(filename).stem + '.pdb', commit, 'pdb')
        symbols = json.loads(verify_artifact(folder, 'SYMBOLS.json', commit, 'data').read_text(encoding='utf-8-sig'))
        spec = importlib.util.spec_from_file_location('symbol_check', Path(__file__).parent / 'diagnostics/verify-symbols.py')
        checker = importlib.util.module_from_spec(spec); spec.loader.exec_module(checker)
        guid, age = checker.codeview(binary.read_bytes())
        if (metadata.get('full_symbols') is not True or symbols.get('binary') != filename or
                symbols.get('pdb') != pdb.name or symbols.get('binary_sha256') != sha(binary) or
                symbols.get('pdb_sha256') != sha(pdb) or symbols.get('guid') != guid or symbols.get('age') != age):
            raise RuntimeError('Symbols do not match the diagnostics binary')
    return binary


VARIANTS = (('Release', 'ci', ''), ('Diagnostics', 'debug-ci', '-diagnostics'))


def extract_runtime(base, stage):
    copied = set()
    with zipfile.ZipFile(base) as archive:
        for entry in archive.infolist():
            path = PurePosixPath(entry.filename)
            if (path.is_absolute() or '..' in path.parts or '\\' in entry.orig_filename
                    or any(':' in part for part in path.parts)
                    or not path.parts or path.parts[0] != BASE_FOLDER):
                raise RuntimeError('Unsafe runtime archive path')
            if entry.is_dir():
                continue
            relative = PurePosixPath(*path.parts[1:])
            lowered = relative.as_posix().lower()
            if (relative.suffix.lower() in FORBIDDEN or relative.name.lower() == 'gtsport.exe'
                    or (relative.suffix.lower() == '.dll' and relative.name.lower().startswith(('nvngx_', 'sl.')))
                    or any(part.lower() in {'app0', 'sys', 'shaders', 'logs', 'saves', '_sd', 'convert'} for part in relative.parts)
                    or (relative.parts[0].lower() == 'user' and lowered != 'user/custom_configs/cusa03220.json')
                    or (entry.external_attr >> 16) & 0o170000 == 0o120000):
                raise RuntimeError(f'Forbidden runtime package content: {relative}')
            if lowered in copied:
                raise RuntimeError('Duplicate package path')
            copied.add(lowered)
            if lowered in {'build_info.txt', 'sha256sums.txt', 'readme.txt', 'rygt.exe', 'rygts.exe', 'libs/libscevideoout.prx'}:
                continue
            target = stage / relative.as_posix()
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(entry))


def assemble(base, commit, flavor, channel, suffix):
    launcher_folder = Path('artifacts/launcher' + suffix)
    renderer_folder = Path('artifacts/renderer' + suffix)
    launcher = verify_bundle(launcher_folder, 'RyGTS.exe', commit, flavor)
    renderer = verify_bundle(renderer_folder, 'libSceVideoOut.prx', commit, flavor)
    root = Path(__file__).resolve().parent.parent
    pin = json.loads((root / 'renderer/source.json').read_text())
    source_file = verify_artifact(renderer_folder, 'CI-SOURCE.txt', commit, 'data')
    source = source_file.read_text(encoding='utf-8-sig')
    lines = source.splitlines()
    expected = os.environ.get('RENDERER_SHA')
    if len(lines) != 2 or lines[0] != pin['repository'] or not re.fullmatch('[0-9a-f]{40}', lines[1]) or (expected and lines[1] != expected) or (pin['commit'] != 'HEAD' and pin['commit'] != lines[1]):
        raise RuntimeError('Renderer source revision mismatch')
    expected = lines[1]
    if b'1440140585595047977' not in renderer.read_bytes() or b'DISCORD-RPC: compiled in;' not in renderer.read_bytes():
        raise RuntimeError('Discord RPC missing or application ID incorrect')
    name = f'RyGTS-{channel}-{commit[:8]}'
    destination = Path('dist'); stage = destination / name
    stage.mkdir(parents=True, exist_ok=False)
    extract_runtime(base, stage)
    shutil.copy2(root / 'LICENSE', stage / 'LICENSE')
    shutil.copy2(launcher, stage / 'RyGTS.exe')
    shutil.copy2(renderer, stage / 'libs/libSceVideoOut.prx')
    # aminenrr/RyGTS-SPS fork: adcpatch.exe rebuilt from this revision (manifestprep mode).
    fork_adcpatch = Path('artifacts/adcpatch/adcpatch.exe')
    if fork_adcpatch.is_file():
        if fork_adcpatch.read_bytes()[:2] != b'MZ':
            raise RuntimeError('Expected a Windows PE adcpatch.exe')
        (stage / 'tools/adcpatch').mkdir(parents=True, exist_ok=True)
        shutil.copy2(fork_adcpatch, stage / 'tools/adcpatch/adcpatch.exe')
    (stage / 'tools/amd').mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / 'tools/amd/Add-To-AMD-Software.ps1', stage / 'tools/amd/Add-To-AMD-Software.ps1')
    for name_required in ('converter/ryty.exe', 'tools/adcpatch/adcpatch.exe', 'libs/libSceBluetoothHid.prx',
                          'amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-LICENSE.txt', 'launcher/background.png',
                          'user/custom_configs/CUSA03220.json'):
        if not (stage / name_required).is_file():
            raise RuntimeError(f'Incomplete runtime package: {name_required}')
    write_packaged_defaults(stage)
    provenance = dict(commit=commit, flavor=flavor, channel=channel,
        configuration='RelWithDebInfo' if suffix else 'Release', optimized=True,
        pipeline=os.environ.get('CI_PIPELINE_URL'), runtime_base_url=BASE_URL, runtime_base_sha256=BASE_SHA,
        renderer_source=source, launcher_sha256=sha(launcher), renderer_sha256=sha(renderer),
        rebuilt=['RyGTS.exe', 'libs/libSceVideoOut.prx'] + (['tools/adcpatch/adcpatch.exe'] if fork_adcpatch.is_file() else []),
        fork_patches=os.environ.get('RYGT_FORK_PATCHES', ''),
        retained='Other runtime libraries and Windows conversion helpers from pinned October 25 archive')
    if suffix:
        # PDBs are forbidden in the base. Only these explicitly verified same-build
        # symbols are admitted to debug-ci, adjacent to their matching modules.
        for folder, pdb_name, target in ((launcher_folder, 'RyGTS.pdb', 'RyGTS.pdb'),
                                        (renderer_folder, 'libSceVideoOut.pdb', 'libs/libSceVideoOut.pdb')):
            shutil.copy2(verify_artifact(folder, pdb_name, commit, 'pdb'), stage / target)
        tools = stage / 'tools/diagnostics'; tools.mkdir(parents=True)
        for file in ('analyze-trace.py', 'frame-capture-hotkey.py', 'watch-capture.py', 'Start-Capture.cmd', 'README.txt'):
            shutil.copy2(root / 'ci/diagnostics' / file, tools / file)
        (stage / 'investigation-build').mkdir()
        layout = verify_artifact(renderer_folder, 'frame-capture-layouts.json', commit, 'data')
        if sha(renderer).upper() not in json.loads(layout.read_text()):
            raise RuntimeError('Capture layout does not match the diagnostics binary')
        shutil.copy2(layout, stage / 'investigation-build/frame-capture-layouts.json')
        provenance['symbols'] = [json.loads((folder / 'SYMBOLS.json').read_text(encoding='utf-8-sig')) for folder in (launcher_folder, renderer_folder)]
        shutil.copy2(root / 'ci/diagnostics/DEBUG-README.txt', stage / 'README.txt')
    else:
        (stage / 'README.txt').write_text(
            'RyGTS Release CI package (ci)\n\nRun RyGTS.exe to set up your own decrypted GT Sport CUSA03220 v1.69 dump.\n'
            'Debug/Diagnostics is distributed separately as debug-ci.\n'
            'Game executable/data, firmware, saves, logs, caches and NVIDIA SDK DLLs are excluded.\n'
            'Supply required firmware modules and optional NVIDIA upscaler files yourself.\n'
            'Launcher and renderer are built from this pipeline revision; other runtime files\n'
            'and Windows conversion helpers come from the checksummed October 25 archive.\n'
            'This is a CI artifact, not a tagged release. See BUILD_INFO.txt and SHA256SUMS.txt.\n', encoding='utf-8')
    (stage / 'BUILD_INFO.txt').write_text(json.dumps(provenance, indent=2) + '\n', encoding='utf-8')
    allowed_pdb = {'RyGTS.pdb', 'libs/libSceVideoOut.pdb'} if suffix else set()
    for file in stage.rglob('*'):
        if file.is_file() and file.suffix.lower() == '.pdb' and file.relative_to(stage).as_posix() not in allowed_pdb:
            raise RuntimeError('Unexpected packaged symbols')
    manifest = {file.relative_to(stage).as_posix(): sha(file) for file in sorted(stage.rglob('*')) if file.is_file()}
    (stage / 'SHA256SUMS.txt').write_text(''.join(f'{digest}  {name}\n' for name, digest in manifest.items()), encoding='utf-8')
    result = destination / (name + '.zip')
    with zipfile.ZipFile(result, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(stage.rglob('*')):
            if file.is_file(): archive.write(file, file.relative_to(destination).as_posix())
    with zipfile.ZipFile(result) as archive:
        if archive.testzip() is not None: raise RuntimeError('ZIP integrity failure')
        for file, digest in manifest.items():
            if hashlib.sha256(archive.read(name + '/' + file)).hexdigest() != digest: raise RuntimeError('Packaged checksum mismatch')
    digest = sha(result)
    (destination / (result.name + '.sha256')).write_text(digest + '  ' + result.name + '\n')
    return dict(channel=channel, flavor=flavor, archive=result.name, sha256=digest, files=len(manifest)+1, renderer_commit=expected)


def package(channels=None):
    commit = os.environ['CI_COMMIT_SHA']
    if not re.fullmatch('[0-9a-f]{40}', commit): raise RuntimeError('Invalid source revision')
    if channels is None:
        selected = os.environ.get('CI_CHANNEL')
        channels = (selected,) if selected else tuple(channel for _, channel, _ in VARIANTS)
    else:
        channels = (channels,) if isinstance(channels, str) else tuple(channels)
    valid_channels = tuple(channel for _, channel, _ in VARIANTS)
    if not channels or len(set(channels)) != len(channels) or any(channel not in valid_channels for channel in channels):
        raise ValueError('Invalid CI package channel selection')
    requested = set(channels)
    channels = tuple(channel for channel in valid_channels if channel in requested)
    base = Path(os.environ.get('RYGT_PACKAGE_BASE', '.ci-runtime-base.zip'))
    if not base.exists():
        headers = {'JOB-TOKEN': os.environ['CI_JOB_TOKEN']} if os.environ.get('CI_JOB_TOKEN') else {}
        with urllib.request.urlopen(urllib.request.Request(BASE_URL, headers=headers)) as response, base.open('wb') as output:
            shutil.copyfileobj(response, output)
    if sha(base) != BASE_SHA: raise RuntimeError('Pinned runtime archive checksum mismatch')
    variants = [assemble(base, commit, *variant) for variant in VARIANTS if variant[1] in channels]
    if len({variant['renderer_commit'] for variant in variants}) != 1:
        raise RuntimeError('Renderer revisions differ between package variants')
    (Path('dist') / 'CI-PACKAGES.json').write_text(json.dumps(dict(schema=2, commit=commit, renderer_commit=variants[0]['renderer_commit'], variants=variants), indent=2) + '\n')
    print(json.dumps(variants, indent=2))


if __name__ == '__main__':
    package()
