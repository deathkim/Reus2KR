#!/usr/bin/env python3
"""범용 한글화 설치기 빌더.

project.json(프로젝트 정의)을 읽어
  1) payload.zip (package.json 매니페스트 + 배포 파일 + 델타 패치)
  2) 단일 설치 exe (installer/src/Installer.cs 를 Windows 기본 .NET Framework csc 로 컴파일, payload 를 리소스로 포함)
를 만든다. 다른 Unity 프로젝트는 project.json 만 새로 쓰면 된다.

사용: python build.py <project.json> [--out <폴더>] [--payload-only] [--zip] [--keep-work]

project.json 경로 규칙: 상대 경로는 project.json 이 있는 폴더 기준. {sourceGameDir}, {project} 치환 지원.
파일 항목:  {"from": "...", "to": "게임 기준 상대경로"}
            {"fromDir": "...", "toDir": "...", "include": ["*.dll"]}
패치 항목:  {"target": "게임 기준 상대경로", "original": "원본 파일", "modified": "수정본 파일"}
"distFiles": 설치 exe 옆에 함께 배포할 문서 목록(--zip 이면 릴리스 zip 에 포함).
verify 항목의 "sha256": "auto" 는 sourceGameDir 의 해당 파일 해시로 채운다.
"""
import fnmatch
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from make_delta import make_delta, apply_delta  # noqa: E402

INSTALLER_SRC = os.path.join(os.path.dirname(HERE), 'src', 'Installer.cs')


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


class Ctx:
    def __init__(self, project_path, project):
        self.base = os.path.dirname(os.path.abspath(project_path))
        self.project = project
        self.subs = {
            '{project}': self.base,
            '{sourceGameDir}': project.get('sourceGameDir', ''),
        }

    def path(self, p):
        for k, v in self.subs.items():
            p = p.replace(k, v)
        return p if os.path.isabs(p) else os.path.normpath(os.path.join(self.base, p))


def expand(ctx, items):
    """파일 항목 → [(원본 절대경로, 설치 상대경로)]"""
    result = []
    for it in items or []:
        if 'fromDir' in it:
            root = ctx.path(it['fromDir'])
            pats = it.get('include', ['*'])
            for dirpath, _dirs, files in os.walk(root):
                for fn in sorted(files):
                    if any(fnmatch.fnmatch(fn, p) for p in pats):
                        full = os.path.join(dirpath, fn)
                        rel = os.path.relpath(full, root).replace('\\', '/')
                        result.append((full, (it['toDir'].rstrip('/') + '/' + rel).lstrip('/')))
        else:
            result.append((ctx.path(it['from']), it['to'].replace('\\', '/')))
    return result


def build_payload(project_path, out_dir):
    with open(project_path, encoding='utf-8') as f:
        project = json.load(f)
    ctx = Ctx(project_path, project)
    manifest = {k: v for k, v in project.items()
                if k not in ('sourceGameDir', 'output', 'bepinex', 'modes', 'verify')}
    zipname = os.path.join(out_dir, 'payload.zip')
    os.makedirs(out_dir, exist_ok=True)
    stored = {}

    with zipfile.ZipFile(zipname, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:

        def add_files(group, items):
            out = []
            for src, dst in expand(ctx, items):
                if not os.path.isfile(src):
                    raise FileNotFoundError(src)
                arc = 'files/%s/%s' % (group, dst)
                if arc not in stored:
                    z.write(src, arc)
                    stored[arc] = True
                out.append({'src': arc, 'dst': dst, 'sha256': sha256_file(src)})
            return out

        # BepInEx
        bep = project.get('bepinex')
        if bep:
            manifest['bepinex'] = {
                'detect': bep.get('detect', 'BepInEx/core/BepInEx.dll'),
                'files': add_files('bepinex', bep.get('files', [])),
            }

        # verify
        verify = []
        for v in project.get('verify', []):
            v = dict(v)
            if v.get('sha256') == 'auto':
                v['sha256'] = sha256_file(os.path.join(ctx.subs['{sourceGameDir}'], v['file']))
            verify.append(v)
        if verify:
            manifest['verify'] = verify

        # modes
        modes = {}
        for mid, md in project.get('modes', {}).items():
            m = {k: v for k, v in md.items() if k not in ('files', 'patches')}
            m['files'] = add_files(mid, md.get('files', []))
            patches = []
            for n, p in enumerate(md.get('patches', [])):
                orig = ctx.path(p['original'])
                mod = ctx.path(p['modified'])
                a = open(orig, 'rb').read()
                b = open(mod, 'rb').read()
                delta = make_delta(a, b)
                assert apply_delta(a, delta) == b, 'delta self-check failed'
                arc = 'patch/%s_%d.rdp' % (mid, n)
                z.writestr(arc, delta)
                patches.append({
                    'target': p['target'].replace('\\', '/'),
                    'delta': arc,
                    'sourceSha256': hashlib.sha256(a).hexdigest(),
                    'resultSha256': hashlib.sha256(b).hexdigest(),
                })
                print('  patch %s: delta %d bytes (orig %d -> %d)' % (p['target'], len(delta), len(a), len(b)))
            if patches:
                m['patches'] = patches
            modes[mid] = m
        manifest['modes'] = modes

        z.writestr('package.json', json.dumps(manifest, ensure_ascii=False, indent=2))
    return project, zipname


def find_csc():
    win = os.environ.get('WINDIR', r'C:\Windows')
    for sub in ('Framework64', 'Framework'):
        p = os.path.join(win, 'Microsoft.NET', sub, 'v4.0.30319', 'csc.exe')
        if os.path.isfile(p):
            return p
    raise FileNotFoundError('csc.exe (.NET Framework 4) 를 찾을 수 없습니다.')


def compile_installer(payload_zip, exe_path):
    csc = find_csc()
    os.makedirs(os.path.dirname(exe_path), exist_ok=True)
    refs = ['System.Windows.Forms.dll', 'System.Drawing.dll', 'System.IO.Compression.dll',
            'System.IO.Compression.FileSystem.dll', 'System.Web.Extensions.dll']
    cmd = [csc, '/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
           '/out:' + exe_path, '/resource:%s,payload.zip' % payload_zip]
    cmd += ['/r:' + r for r in refs]
    cmd.append(INSTALLER_SRC)
    res = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if res.returncode != 0:
        sys.stderr.write(res.stdout + res.stderr)
        raise SystemExit('컴파일 실패')
    return exe_path


def main():
    args = sys.argv[1:]
    if not args or args[0].startswith('-'):
        print(__doc__)
        return 2
    project_path = args[0]
    out = os.path.join(os.path.dirname(HERE), 'dist')
    if '--out' in args:
        out = args[args.index('--out') + 1]
    project, zipname = build_payload(project_path, os.path.join(out, '_work'))
    print('payload: %s (%.1f MB)' % (zipname, os.path.getsize(zipname) / 1048576))
    if '--payload-only' in args:
        return 0
    exe = os.path.join(out, project.get('output', project['id'] + '_Setup.exe'))
    compile_installer(zipname, exe)
    print('installer: %s (%.1f MB)' % (exe, os.path.getsize(exe) / 1048576))
    ctx = Ctx(project_path, project)
    extra = [(ctx.path(f), os.path.basename(ctx.path(f))) for f in project.get('distFiles', [])]
    for src, name in extra:
        shutil.copyfile(src, os.path.join(out, name))
    work = os.path.join(out, '_work')
    if '--keep-work' not in args and os.path.isdir(work):
        shutil.rmtree(work, ignore_errors=True)
    if '--zip' in args:
        zpath = os.path.join(out, '%s_v%s.zip' % (project['id'], project.get('version', '0')))
        with zipfile.ZipFile(zpath, 'w', zipfile.ZIP_DEFLATED) as z:
            z.write(exe, os.path.basename(exe))
            for src, name in extra:
                z.write(src, name)
        print('release zip: %s (%.1f MB)' % (zpath, os.path.getsize(zpath) / 1048576))
    return 0


if __name__ == '__main__':
    sys.exit(main())
