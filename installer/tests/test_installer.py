#!/usr/bin/env python3
"""설치기 통합 테스트: 가짜 Unity 게임 폴더로 플러그인/직접 패치/제거를 실제 exe 로 검증한다."""
import hashlib
import json
import os
import random
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import build  # noqa: E402


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


def run(exe, *args):
    r = subprocess.run([exe] + list(args), capture_output=True, text=True, encoding='utf-8', errors='replace')
    return r.returncode, r.stdout


def main():
    tmp = tempfile.mkdtemp(prefix='locinst_')
    ok = True

    def check(cond, msg):
        nonlocal ok
        print(('PASS ' if cond else 'FAIL ') + msg)
        ok = ok and bool(cond)

    try:
        rnd = random.Random(7)
        src = os.path.join(tmp, 'src')
        game = os.path.join(tmp, 'Game')
        os.makedirs(os.path.join(game, 'Game_Data', 'Managed'))
        os.makedirs(src)
        open(os.path.join(game, 'Game.exe'), 'wb').write(b'MZ' + bytes(rnd.getrandbits(8) for _ in range(2000)))
        dll = os.path.join(game, 'Game_Data', 'Managed', 'Assembly-CSharp.dll')
        open(dll, 'wb').write(bytes(rnd.getrandbits(8) for _ in range(5000)))
        orig = bytes(rnd.getrandbits(8) for _ in range(3000000))
        mod = bytearray(orig)
        mod[1000:1010] = b'0123456789'              # 제자리 수정
        mod[500000:500000] = b'INSERTED-' * 5000    # 중간 삽입(이후 전체가 밀림)
        del mod[2000000:2000100]                    # 삭제
        mod += b'TAIL' * 100
        mod = bytes(mod)
        asset = os.path.join(game, 'Game_Data', 'level0.assets')
        open(asset, 'wb').write(orig)
        open(os.path.join(src, 'modified.assets'), 'wb').write(mod)
        files = {'bep/winhttp.dll': b'doorstop', 'bep/core/BepInEx.dll': b'bepinex',
                 'plug/Plugin.dll': b'plugin', 'plug/ko.csv': u'한글'.encode('utf-8')}
        for rel, data in files.items():
            p = os.path.join(src, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            open(p, 'wb').write(data)
        project = {
            'id': 'testgame-ko', 'name': u'테스트 게임 한글화', 'version': '0.1', 'description': 'test',
            'output': 'Test_Setup.exe', 'sourceGameDir': game,
            'game': {'exe': 'Game.exe', 'detectFiles': ['Game.exe', 'Game_Data/Managed/Assembly-CSharp.dll']},
            'verify': [{'file': 'Game_Data/Managed/Assembly-CSharp.dll', 'sha256': 'auto', 'onMismatch': 'block'}],
            'bepinex': {'detect': 'BepInEx/core/BepInEx.dll', 'files': [
                {'from': '{project}/bep/winhttp.dll', 'to': 'winhttp.dll'},
                {'fromDir': '{project}/bep/core', 'toDir': 'BepInEx/core', 'include': ['*.dll']}]},
            'modes': {
                'plugin': {'label': u'플러그인', 'requiresBepInEx': True,
                           'files': [{'from': '{project}/plug/Plugin.dll', 'to': 'BepInEx/plugins/T/Plugin.dll'},
                                     {'from': '{project}/plug/ko.csv', 'to': 'BepInEx/plugins/T/ko.csv'}]},
                'patch': {'label': u'직접 패치',
                          'patches': [{'target': 'Game_Data/level0.assets', 'original': asset,
                                       'modified': '{project}/modified.assets'}],
                          'files': []},
            }}
        pj = os.path.join(src, 'project.json')
        json.dump(project, open(pj, 'w', encoding='utf-8'), ensure_ascii=False)
        out = os.path.join(tmp, 'dist')
        proj, zipname = build.build_payload(pj, os.path.join(out, '_work'))
        exe = build.compile_installer(zipname, os.path.join(out, 'Test_Setup.exe'))
        print('built %s %.2f MB' % (exe, os.path.getsize(exe) / 1048576))

        mod_sha = hashlib.sha256(mod).hexdigest()
        orig_sha = sha(asset)

        def snapshot():
            s = {}
            for d, _, fs in os.walk(game):
                for f in fs:
                    p = os.path.join(d, f)
                    s[os.path.relpath(p, game)] = sha(p)
            return s

        before = snapshot()

        code, o = run(exe, '--info')
        check(code == 0 and 'plugin' in o and 'patch' in o, '--info 모드 목록')

        code, o = run(exe, '--silent', '--game', game, '--mode', 'patch')
        check(code == 0, '직접 패치 설치 종료코드 0')
        check(sha(asset) == mod_sha, '직접 패치 결과 해시 일치')
        check(os.path.exists(os.path.join(game, '.locpatch', 'testgame-ko', 'backup', 'Game_Data', 'level0.assets')), '백업 생성')
        code, o = run(exe, '--status', '--game', game)
        check(u'직접 패치' in o, '상태 표시')
        code, o = run(exe, '--silent', '--game', game, '--mode', 'patch')
        check(code == 0 and sha(asset) == mod_sha, '재설치(기존 제거 후 재적용)')
        code, o = run(exe, '--silent', '--uninstall', '--game', game)
        check(code == 0, '제거 종료코드 0')
        check(snapshot() == before, '직접 패치 제거 후 게임 폴더 원상 복구')

        code, o = run(exe, '--silent', '--game', game, '--mode', 'plugin')
        check(code == 0, '플러그인 설치 종료코드 0')
        check(os.path.exists(os.path.join(game, 'BepInEx', 'core', 'BepInEx.dll')), 'BepInEx 설치')
        check(open(os.path.join(game, 'BepInEx', 'plugins', 'T', 'ko.csv'), encoding='utf-8').read() == u'한글', '플러그인 파일(한글) 설치')
        check(sha(asset) == orig_sha, '플러그인 방식은 원본 에셋을 건드리지 않음')
        code, o = run(exe, '--silent', '--uninstall', '--game', game)
        check(code == 0 and snapshot() == before, '플러그인 제거 후 원상 복구(BepInEx 포함)')

        os.makedirs(os.path.join(game, 'BepInEx', 'plugins', 'Other'))
        open(os.path.join(game, 'BepInEx', 'plugins', 'Other', 'o.dll'), 'wb').write(b'x')
        run(exe, '--silent', '--game', game, '--mode', 'plugin')
        run(exe, '--silent', '--uninstall', '--game', game)
        check(os.path.exists(os.path.join(game, 'BepInEx', 'core', 'BepInEx.dll')), '다른 플러그인이 있으면 BepInEx 유지')
        check(not os.path.exists(os.path.join(game, 'BepInEx', 'plugins', 'T')), '우리 플러그인 폴더는 제거')

        data = open(dll, 'rb').read()
        open(dll, 'wb').write(data + b'!')
        code, o = run(exe, '--silent', '--game', game, '--mode', 'plugin')
        check(code == 1 and u'지원하지 않는' in o, '게임 버전 불일치 시 설치 차단')
        open(dll, 'wb').write(data)

        open(asset, 'ab').write(b'x')
        code, o = run(exe, '--silent', '--game', game, '--mode', 'patch')
        check(code == 1 and u'예상과 다릅니다' in o, '원본 해시 불일치 시 패치 거부')
        check(not os.path.exists(os.path.join(game, '.locpatch', 'testgame-ko', 'install.json')), '실패 시 설치 기록 없음')

        print('ALL PASS' if ok else 'SOME FAILED')
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
