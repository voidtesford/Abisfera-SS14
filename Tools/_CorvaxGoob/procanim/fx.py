# v12 hit effects / face layers: a 1:1 mirror of ProcAnimFx (Content.Shared/_CorvaxGoob/ProcAnim/ProcAnimFx.cs).
# Colours are (r, g, b, a) floats; dots are (x, y, w, h, colour) in forward-frame px.
import math
import solve as SV

FACES = ('neutral', 'pain', 'dazed', 'angry', 'dead')
EYES = ('open', 'closed', 'narrowed')
ANCHORED = ('Spatter', 'Pool', 'Dust')
DRAWN = ('Spatter', 'Pool', 'Dust', 'Bruise', 'Drips')

def hexf(h):
    h = h.lstrip('#'); a = int(h[6:8], 16) / 255 if len(h) >= 8 else 1.0
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)) + (a,)

def _g(fx, k, d):
    v = fx.get(k); return d if v is None else v

def count_of(fx):
    c = int(_g(fx, 'count', -1))
    return c if c >= 0 else {'Spatter': 14, 'Bruise': 4, 'Drips': 6, 'Dust': 10}.get(fx['type'], 0)

def duration_of(fx):
    d = float(_g(fx, 'duration', -1))
    return d if d >= 0 else {'Spatter': 8, 'Recoil': 0.35, 'Bruise': 0.6, 'Drips': 3, 'Pool': 4, 'Dust': 0.8,
                             'Shake': 0.3, 'Stagger': 2.5}.get(fx['type'], 0.0)

def life(fx): return duration_of(fx) + (60 if fx['type'] == 'Pool' else 0.05)

def color_of(fx, blood):
    if fx.get('color'): return hexf(fx['color'])
    return {'Bruise': hexf('#5a1f2b'), 'Dust': hexf('#8e877b')}.get(fx['type'], blood)

def shade(c, k, a): return (min(1, c[0] * k), min(1, c[1] * k), min(1, c[2] * k), c[3] * max(0.0, min(1.0, a)))
def smooth(u): u = max(0.0, min(1.0, u)); return u * u * (3 - 2 * u)
R = SV.rnd

def origin(fx, pose):
    p = SV.point(pose, fx.get('part', 'Head'), SV.vec(fx.get('at'), (0, 9)))
    return (p[0], float(_g(fx, 'floor', -14))) if fx['type'] in ('Pool', 'Dust') else p

def particles(fx, seed, o, age, blood):
    res = []
    if age < 0: return res
    col = color_of(fx, blood); n = count_of(fx); dur = duration_of(fx)
    d = math.radians(float(_g(fx, 'dir', 0))); spread = float(_g(fx, 'spread', 50)); st = float(_g(fx, 'strength', 1))
    size = float(_g(fx, 'size', 1)); floor0 = float(_g(fx, 'floor', -14)); ty = fx['type']
    if ty == 'Spatter':
        if age > dur: return res
        fade = 1.0 if age < dur - 1 else max(0.0, dur - age)
        if age < 0.12:
            u = age / 0.12
            for i in range(4):
                a = d + (R(seed, i, 20) - 0.5) * math.radians(spread * 0.6)
                r = (2 + 6 * u) * (0.6 + 0.6 * R(seed, i, 21)) * st
                res.append((o[0] + math.cos(a) * r, o[1] + math.sin(a) * r, 1, 1, shade(col, 1.25, 0.55 * (1 - u))))
        g = 380.0
        for i in range(n):
            a = d + (R(seed, i, 0) - 0.5) * math.radians(spread)
            s = (50 + 110 * R(seed, i, 1)) * st
            big = R(seed, i, 2) < 0.3
            fl = floor0 + (R(seed, i, 4) - 0.5) * 4
            tt = age - R(seed, i, 5) * 0.03
            if tt < 0: continue
            vx, vy = s * math.cos(a), s * math.sin(a)
            disc = vy * vy + 2 * g * (o[1] - fl)
            land = (vy + math.sqrt(disc)) / g if disc >= 0 else 1e30
            if tt >= land:
                ln = max(1.0, min(3.0, 1 + abs(vx) / 60)) + (1 if big else 0)
                dry = 0.85 - 0.25 * smooth((tt - land) / 3)
                sg = (vx > 0) - (vx < 0)
                res.append((o[0] + vx * land + sg * (ln - 1) * 0.5, fl, ln, 1, shade(col, dry * (0.8 + 0.3 * R(seed, i, 3)), 0.9 * fade)))
            else:
                sz = 2 if big else 1
                res.append((o[0] + vx * tt, o[1] + vy * tt - 0.5 * g * tt * tt, sz, sz, shade(col, 0.85 + 0.3 * R(seed, i, 3), fade)))
    elif ty == 'Bruise':
        a = 0.75 * smooth(age / dur if dur > 0 else 1)
        for i in range(n):
            ox = round((R(seed, i, 0) - 0.5) * 3 * size); oy = round((R(seed, i, 1) - 0.5) * 2 * size)
            res.append((o[0] + ox, o[1] + oy, 1, 1, shade(col, 0.75 + 0.45 * R(seed, i, 2), a * (1 if i == 0 else 0.8))))
    elif ty == 'Drips':
        g = 300.0
        for i in range(n):
            rel = 0.1 + dur * (i + 0.6 * R(seed, i, 0)) / max(1, n)
            x = o[0] + round((R(seed, i, 1) - 0.5) * 2)
            tt = age - rel
            if tt < -0.35: continue
            if tt < 0:
                gr = 1 + tt / 0.35
                res.append((x, o[1] - 0.5 - gr, 1, 1, shade(col, 0.9, 0.6 + 0.4 * gr))); continue
            fall = o[1] - floor0
            land = math.sqrt(2 * fall / g) if fall > 0 else 0
            if tt < land:
                v = g * tt
                res.append((x, o[1] - 0.5 * g * tt * tt, 1, 2 if v > 70 else 1, shade(col, 0.95, 1)))
            elif tt < land + 1.5:
                res.append((x, floor0, 2, 1, shade(col, 0.7, 0.85 * (1 - (tt - land) / 1.5))))
    elif ty == 'Pool':
        u = min(1.0, age / dur) if dur > 0 else 1.0
        r = size * 7 * math.sqrt(u) * (1 - 0.15 * (1 - u))
        if r < 0.75: return res
        ry = max(1.0, r * 0.4); rows = int(math.ceil(ry))
        for y in range(-rows, rows + 1):
            q = 1 - (y / (ry + 0.5)) ** 2
            if q <= 0: continue
            hw = r * math.sqrt(q) * (0.85 + 0.3 * R(seed, y + 50, 0))
            if hw < 0.5: continue
            cx = o[0] + (R(seed, y + 50, 1) - 0.5) * 1.2
            edge = abs(y) == rows
            res.append((cx, floor0 + y, 2 * hw, 1, shade(col, 0.62 if edge else 0.5, 0.7 if edge else 0.88)))
    elif ty == 'Dust':
        if age > dur: return res
        sp = math.radians(spread) / 2
        for i in range(n):
            tt = age - R(seed, i, 3) * 0.06
            if tt < 0: continue
            u = tt / dur
            side = -1 if R(seed, i, 0) < 0.5 else 1
            v = (12 + 40 * R(seed, i, 1)) * st
            x = o[0] + side * v * dur / 3 * (1 - math.exp(-3 * u))
            y = floor0 + (2 + 5 * R(seed, i, 2)) * math.sin(sp) * 1.4 * (1 - math.exp(-2.5 * u))
            sz = 1 + 2 * u
            res.append((x, y, sz, sz, shade(col, 1, 0.55 * (1 - u) ** 1.5)))
        g = 400.0
        for i in range(n // 3):
            side = -1 if R(seed, i, 10) < 0.5 else 1
            vx = side * (30 + 50 * R(seed, i, 11)) * st; vy = (40 + 50 * R(seed, i, 12)) * st
            land = 2 * vy / g; tt = min(age, land)
            res.append((o[0] + vx * tt, floor0 + vy * tt - 0.5 * g * tt * tt, 1, 1, shade(col, 0.45, 1 - smooth((age - dur * 0.6) / (dur * 0.4)))))
    return res

def recoil_env(age, dur):
    if age < 0 or dur <= 0 or age >= dur: return 0.0
    tau = dur * 0.18; u = age / tau
    main = u * math.exp(1 - u)
    over = -0.18 * math.sin(math.pi * age / dur) * smooth(age / tau - 1)
    return (main + over) * (1 - smooth((age - dur * 0.8) / (dur * 0.2)))

def apply_pose(proto, actor, t, pose, f):
    """ProcAnimFx.ApplyPose: Recoil + Stagger layers (pose: {part: [x, y, rot]})."""
    for ev in sorted(proto['events'], key=lambda e: e['t']):
        if ev['t'] > t: break
        if ev['_target'] != actor or not ev.get('effects'): continue
        age = t - ev['t']
        for fx in ev['effects']:
            dur = duration_of(fx)
            if age >= dur: continue
            st = float(_g(fx, 'strength', 1))
            if fx['type'] == 'Recoil':
                e = recoil_env(age, dur) * st
                part = fx.get('part', 'Head'); p = pose[part]
                d = float(_g(fx, 'dir', 0)) - (0 if part == 'Body' else pose['Body'][2])
                a = math.radians(d); c, s = math.cos(a), math.sin(a)
                p[0] += 2 * c * e; p[1] += 2 * s * e; p[2] += -10 * c * e
                if part == 'Body': pose['Body'][1] -= 0.8 * abs(e)
                if part == 'Head': pose['Body'][2] += -2.5 * c * e
            elif fx['type'] == 'Stagger':
                env = smooth(age / 0.15) * (1 - age / dur) ** 2 * st
                ph = 2 * math.pi * age / 1.1
                pose['Body'][2] += 4 * env * math.sin(ph)
                pose['Body'][0] += 1.5 * env * math.sin(ph + 0.6)
                pose['Head'][2] += 8 * env * math.sin(ph - 1)
                b = env * max(0.0, math.sin(2 * ph))
                pose['LegNear'][2] -= f * 6 * b; pose['LegNearLow'][2] += f * 9 * b
                pose['LegFar'][2] -= f * 3 * b; pose['LegFarLow'][2] += f * 5 * b
                pose['ArmNear'][2] += 6 * env * math.sin(ph - 1.4)
                pose['ArmFar'][2] += 5 * env * math.sin(ph - 1.8)

def shake(proto, actor, real):
    sx = sy = 0.0
    for ev in proto['events']:
        if ev['_target'] != actor or not ev.get('effects'): continue
        age = real - SV.real_time(proto, ev['t'])
        if age < 0: continue
        for fx in ev['effects']:
            if fx['type'] != 'Shake': continue
            dur = duration_of(fx)
            if age >= dur: continue
            k = 1.5 * float(_g(fx, 'strength', 1)) * (1 - age / dur) ** 2
            sx += k * math.sin(age * 90); sy += 0.45 * k * math.sin(age * 67 + 1)
    return sx, sy

FACE_DEFAULT = {'eye': (2.5, 9.5), 'mouth': (3.5, 7.5), 'frontEyes': [(-1.5, 9.5), (0.5, 9.5)], 'frontMouth': (-0.5, 7.5),
                'lid': '#6b3f33', 'brow': '#2b1c16', 'mouthColor': '#3a0d0d', 'deadColor': '#1c1c1c', 'glow': False,
                'glowColor': '#ff3020', 'disabled': False}

def face_cfg(tr):
    c = dict(FACE_DEFAULT); c.update(tr.get('face') or {}); return c

def face_dots(cfg, expr, pose, fs, front):
    res = []
    if cfg['disabled']: return res
    face = expr[0] or 'neutral'; eyes = expr[1] or 'open'
    glow = bool(cfg['glow'])
    L, B, MC, D, G = (hexf(cfg[k]) for k in ('lid', 'brow', 'mouthColor', 'deadColor', 'glowColor'))
    def px(at, c):
        p = SV.point(pose, 'Head', at); res.append((p[0], p[1], 1, 1, c))
    ev = SV.vec
    eye_list = [ev(e) for e in cfg['frontEyes']] if front else [(fs * ev(cfg['eye'])[0], ev(cfg['eye'])[1])]
    m = ev(cfg['frontMouth']) if front else (fs * ev(cfg['mouth'])[0], ev(cfg['mouth'])[1])
    u = 1.0 if front else fs
    add = lambda e, dx, dy: (e[0] + dx, e[1] + dy)
    for e in eye_list:
        if face == 'dead':
            if glow:
                px(e, D); px(add(e, -u, 0), D); continue
            for dx, dy in ((-u, 1), (u, 1), (0, 0), (-u, -1), (u, -1)): px(add(e, dx, dy), D)
            continue
        if face == 'pain':
            if not glow:
                px(add(e, -u, 0), L); px(e, L); px(add(e, 0, 1), B); px(add(e, -u, 1), B)
            continue
        if face == 'dazed':
            if not glow: px(add(e, -u, 0), L); px(e, L)
            continue
        if face == 'angry':
            if glow:
                px(e, G); px(add(e, -u, 0), G); px(add(e, -u, 1), shade(G, 0.6, 0.8))
            else:
                px(add(e, -u, 2), B); px(add(e, 0, 1), B)
        if glow: continue
        if eyes == 'closed': px(add(e, -u, 0), L); px(e, L)
        elif eyes == 'narrowed': px(add(e, 0, 1), L)
    if not glow:
        if face == 'pain': px(m, MC); px(add(m, -u, 0), MC)
        elif face in ('dazed', 'dead'): px(m, MC)
    return res

def _view(tr, t):
    for v in tr.get('views') or []:
        if float(v['start']) <= t < float(v['end']): return (bool(v.get('turned')), v.get('facing', 'North'))
    return None

def view_flip_scale(tr, t, width=0.07):
    vs = tr.get('views') or []
    if not vs: return 1.0
    best = 1e30
    for v in vs:
        for edge in (float(v['start']), float(v['end'])):
            a, b = _view(tr, edge - 1e-3), _view(tr, edge + 1e-3)
            if a == b or (a and b and a[0] and b[0]): continue
            best = min(best, abs(t - edge))
    if best >= width: return 1.0
    return 0.3 + 0.7 * smooth(best / width)
