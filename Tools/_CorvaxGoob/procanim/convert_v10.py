#!/usr/bin/env python3
"""One-off converter (v11): `gilbertFinisher` prototypes (attacker / victim fields) -> `procAnimScene` (actors list).
Text based, keeps every key / comment / formatting of the tracks. Usage: convert_v10.py <in.yml> [out.yml]"""
import re
import sys


def convert(text):
    out, lines, i = [], text.split('\n'), 0
    final, hold = [], False
    while i < len(lines):
        ln = lines[i]
        if ln.startswith('- type: gilbertFinisher'):
            out.append('- type: procAnimScene')
            final, hold = [], False
            i += 1
            continue
        if ln.startswith('  victimStart:'):
            i += 1
            continue
        if ln.startswith('  finalDamage:'):
            final = [ln]
            i += 1
            while i < len(lines) and lines[i].startswith('   '):
                final.append(lines[i])
                i += 1
            continue
        if ln.startswith('  holdAlive:'):
            hold = ln.split(':', 1)[1].strip() == 'true'
            i += 1
            continue
        if ln == '  attacker:':
            out += ['  actors:', '  - role: attacker', '    facing: 1']
            i += 1
            continue
        if ln == '  victim:':
            out += ['  - role: victim', '    facing: -1', '    standUp: true', '    holdCrit: true', '    knockDown: true']
            if hold:
                out.append('    holdAlive: true')
            out += ['  ' + f for f in final]
            i += 1
            continue
        ln = re.sub(r'target: (Victim|Attacker)\b', lambda m: 'target: ' + m.group(1).lower(), ln)
        out.append(ln)
        i += 1
    return '\n'.join(out)


if __name__ == '__main__':
    src = open(sys.argv[1]).read()
    open(sys.argv[2] if len(sys.argv) > 2 else sys.argv[1], 'w').write(convert(src))
