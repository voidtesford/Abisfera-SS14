# ProcAnim pose solver, a 1:1 mirror of ProcAnimTimeline (Content.Shared/_CorvaxGoob/ProcAnim) used by preview.py:
# keyframes (all easings) + procedural walk cycle / drag / daze / idle, joint limits, two-segment limbs, 2-bone IK,
# head look-at, hit-stop time warp and the deterministic blood droplet simulation.
# A scene is the normalized dict from load_scene(): {'id', 'length', 'intro', 'actors': [actor dicts], 'events': [...]}
# with role references resolved to actor indices (`_look`, `_ik`, event `_target`), as ProcAnimScenePrototype.Resolve.
import math

PARTS = ['Body', 'Head', 'ArmNear', 'ArmFar', 'LegNear', 'LegFar', 'ArmNearLow', 'ArmFarLow', 'LegNearLow', 'LegFarLow',
         'ArmNearHand', 'ArmFarHand', 'LegNearFoot', 'LegFarFoot', 'HeldNear', 'HeldFar']
# child -> parent, parents first (chain order)
LOW_PARENT = {'ArmNearLow': 'ArmNear', 'ArmFarLow': 'ArmFar', 'LegNearLow': 'LegNear', 'LegFarLow': 'LegFar',
              'ArmNearHand': 'ArmNearLow', 'ArmFarHand': 'ArmFarLow', 'LegNearFoot': 'LegNearLow', 'LegFarFoot': 'LegFarLow',
              'HeldNear': 'ArmNearHand', 'HeldFar': 'ArmFarHand'}
SEG_PARENT = {k: v for k, v in LOW_PARENT.items() if not k.startswith('Held')}

BACK_C1 = 1.70158; BACK_C2 = BACK_C1 * 1.525; BACK_C3 = BACK_C1 + 1; ELASTIC_C4 = 2 * math.pi / 3
HIT_STOP_HOLD = 0.35
EASES = ('Linear', 'In', 'Out', 'InOut', 'Step', 'BackIn', 'BackOut', 'BackInOut', 'ElasticIn', 'ElasticOut', 'HitStop')
def ease(kind, u):
    u = min(max(u, 0.0), 1.0)
    if kind == 'Linear': return u
    if kind == 'In': return u ** 3
    if kind == 'Out': return 1 - (1 - u) ** 3
    if kind == 'Step': return 1.0 if u >= 1 else 0.0
    if kind == 'BackIn': return BACK_C3 * u ** 3 - BACK_C1 * u * u
    if kind == 'BackOut': return 1 + BACK_C3 * (u - 1) ** 3 + BACK_C1 * (u - 1) ** 2
    if kind == 'BackInOut':
        return ((2 * u) ** 2 * ((BACK_C2 + 1) * 2 * u - BACK_C2) / 2 if u < 0.5
                else ((2 * u - 2) ** 2 * ((BACK_C2 + 1) * (2 * u - 2) + BACK_C2) + 2) / 2)
    if kind == 'ElasticIn':
        return 0.0 if u <= 0 else 1.0 if u >= 1 else -2 ** (10 * u - 10) * math.sin((10 * u - 10.75) * ELASTIC_C4)
    if kind == 'ElasticOut':
        return 0.0 if u <= 0 else 1.0 if u >= 1 else 2 ** (-10 * u) * math.sin((10 * u - 0.75) * ELASTIC_C4) + 1
    if kind == 'HitStop': return 0.0 if u < HIT_STOP_HOLD else 1 - (1 - (u - HIT_STOP_HOLD) / (1 - HIT_STOP_HOLD)) ** 3
    return 4 * u ** 3 if u < 0.5 else 1 - (-2 * u + 2) ** 3 / 2   # InOut (default)

def _key(k): return (float(k.get('t', 0)), float(k.get('x', 0)), float(k.get('y', 0)), float(k.get('rot', 0)), k.get('ease', 'InOut'))
def _tangent(ks, j, c):
    """Monotone (harmonic-mean) slope at key j for the smooth spline; 0 at ends, extrema and next to eased segments."""
    if j <= 0 or j >= len(ks) - 1: return 0.0
    if ks[j][4] != 'InOut' or ks[j + 1][4] != 'InOut': return 0.0
    h0, h1 = ks[j][0] - ks[j - 1][0], ks[j + 1][0] - ks[j][0]
    if h0 <= 0 or h1 <= 0: return 0.0
    d0, d1 = (ks[j][c] - ks[j - 1][c]) / h0, (ks[j + 1][c] - ks[j][c]) / h1
    if d0 * d1 <= 0: return 0.0
    return 2 * d0 * d1 / (d0 + d1)

def evaluate(keys, t, smooth=False):
    """smooth: default (InOut) segments become a monotone cubic spline through the keys, so motion flows through
    them instead of stopping at every key."""
    ks = [_key(k) for k in (keys or [])]
    if not ks: return (0.0, 0.0, 0.0)
    if t <= ks[0][0]: return ks[0][1:4]
    for i in range(1, len(ks)):
        a, b = ks[i - 1], ks[i]
        if t > b[0]: continue
        span = b[0] - a[0]
        if span <= 0: return b[1:4]
        u = (t - a[0]) / span
        if smooth and b[4] == 'InOut':
            u2, u3 = u * u, u * u * u
            h00, h10, h01, h11 = 2 * u3 - 3 * u2 + 1, u3 - 2 * u2 + u, -2 * u3 + 3 * u2, u3 - u2
            return tuple(h00 * a[c] + h10 * span * _tangent(ks, i - 1, c) + h01 * b[c] + h11 * span * _tangent(ks, i, c)
                         for c in (1, 2, 3))
        u = ease(b[4], u)
        return tuple(a[c] + (b[c] - a[c]) * u for c in (1, 2, 3))
    return ks[-1][1:4]

def vec(p, d=(0.0, 0.0)):
    if p is None: return d
    if isinstance(p, (list, tuple)): return (float(p[0]), float(p[1]))
    a, b = str(p).split(','); return (float(a), float(b))

def rot(v, deg):
    r = math.radians(deg); c, s = math.cos(r), math.sin(r)
    return (v[0] * c - v[1] * s, v[0] * s + v[1] * c)

def pivot_offset(pv, pose):
    r = rot(pv, pose[2]); return (pose[0] + pv[0] - r[0], pose[1] + pv[1] - r[1])

# ---- hit-stop time warp ----
def stops(proto):
    return sorted((float(e['t']), float(e.get('hitStop', 0) or 0)) for e in proto['events'] if float(e.get('hitStop', 0) or 0) > 0)
def total_length(proto): return float(proto['length']) + sum(s for _, s in stops(proto))
def anim_time(proto, real):
    if real < 0: return real   # intro (holster beat): no hit-stops
    acc = 0.0
    for t0, s in stops(proto):
        if real < t0 + acc: break
        if real < t0 + acc + s: return t0
        acc += s
    return min(real - acc, float(proto['length']))
def real_time(proto, anim):
    """Real time at which anim time `anim` is first reached (events fire at the start of their stop)."""
    if anim < 0: return anim
    return anim + sum(s for t0, s in stops(proto) if t0 < anim)

# ---- scene loading / role resolution (ProcAnimScenePrototype.Resolve) ----
def actor_index(scene, role):
    if role is None: return -1
    for i, a in enumerate(scene['actors']):
        if str(a['role']).lower() == str(role).lower(): return i
    return -1
def default_target(scene, self=0):
    n = len(scene['actors']); return (0 if self == 1 else 1) if n > 1 else 0
IK_LIMBS = ('ArmNear', 'ArmFar', 'LegNear', 'LegFar')
def normalize(doc):
    """procAnimScene yml dict -> scene dict with resolved actor indices (does not modify `doc`)."""
    import copy
    sc = copy.deepcopy(doc)
    sc.setdefault('intro', 0); sc.setdefault('events', []); sc.setdefault('actors', [])
    for i, a in enumerate(sc['actors']):
        a['_index'] = i
        a['facing'] = float(a.get('facing', 1))
        a.setdefault('move', [])
        lk = a.get('look')
        if lk is not None:
            a['_look'] = actor_index(sc, lk['actor']) if lk.get('actor') is not None else default_target(sc, i)
        for ik in a.get('ik') or []:
            ik['_actor'], ik['_bone'] = -1, 'Body'
            ik['_valid'] = ik.get('limb', 'ArmNear') in IK_LIMBS
            rel = ik.get('relativeTo')
            if rel is None: continue
            role, _, bone = str(rel).partition('.')
            ik['_actor'] = actor_index(sc, role)
            if ik['_actor'] < 0: ik['_valid'] = False
            if bone:
                m = [p for p in PARTS if p.lower() == bone.lower()]
                if m: ik['_bone'] = m[0]
                else: ik['_valid'] = False
    for e in sc['events']:
        e['_target'] = actor_index(sc, e['target']) if e.get('target') is not None else default_target(sc)
        if not 0 <= e['_target'] < len(sc['actors']): continue
        tr = sc['actors'][e['_target']]
        if e.get('holster') and tr.get('holsterAt') is None:
            tr['holsterHeldItems'] = True; tr['holsterAt'] = float(e['t'])
        if e.get('unholster') and tr.get('unholsterAt') is None: tr['unholsterAt'] = float(e['t'])
    return sc

def load_scene(res, scene_id):
    """Finds the procAnimScene `scene_id` in <res>/Prototypes/**/*.yml."""
    import glob, yaml
    for f in sorted(glob.glob(res + '/Prototypes/**/*.yml', recursive=True)):
        txt = open(f, encoding='utf-8', errors='ignore').read()
        if 'procAnimScene' not in txt or scene_id not in txt: continue
        for d in yaml.safe_load(txt) or []:
            if isinstance(d, dict) and d.get('type') == 'procAnimScene' and d.get('id') == scene_id:
                return normalize(d), f
    raise SystemExit(f'procAnimScene {scene_id} not found under {res}/Prototypes')

def list_scenes(res):
    import glob, re
    out = []
    for f in sorted(glob.glob(res + '/Prototypes/**/*.yml', recursive=True)):
        txt = open(f, encoding='utf-8', errors='ignore').read()
        out += re.findall(r'- type: procAnimScene\n\s+id: (\S+)', txt)
    return out

def _get(d, k, default):
    v = d.get(k) if d else None
    return default if v is None else float(v)

def solve(proto, actor, t, piv=None, look=True, pivs_all=None, ik=True):
    """-> {part: (x, y, rot)} final layer offsets (px, forward frame, relative to the sprite origin) and rotations;
    'Body' is the sprite offset / rotation. piv: {part: pivot} overriding the yml pivots (mask-derived joints);
    pivs_all: {actor index: piv} of the other actors (their bones drive the look-at / IK targets); ik=False skips IK."""
    tr = proto['actors'][actor]; f = tr['facing']
    parts = tr.get('parts', {})
    sm = bool(tr.get('smooth'))
    pose = {p: list(evaluate(parts.get(p, {}).get('keys'), t, sm)) for p in PARTS}
    pivot = {p: (piv.get(p) if (piv and p in piv and p != 'Body') else vec(parts.get(p, {}).get('pivot'))) for p in PARTS}

    # walk cycle, driven by the distance travelled along Move
    walk = tr.get('walk'); w = 0.0
    if walk is not None and tr['move']:
        walk = walk or {}
        h = 0.03
        m0, m1 = evaluate(tr['move'], t - h)[0], evaluate(tr['move'], t + h)[0]
        v = (m1 - m0) / (2 * h)
        d = evaluate(tr['move'], t)[0]
        w = min(abs(v) / _get(walk, 'fullSpeed', 0.7), 1.0)
        if walk.get('weight'): w *= max(0.0, evaluate(walk['weight'], t)[0])
        ph = math.pi * d / _get(walk, 'stride', 0.45)
        sn, cs = math.sin(ph), math.cos(ph)
        dirv = 1.0 if v * f >= 0 else -1.0
        leg, knee = _get(walk, 'legSwing', 24), _get(walk, 'knee', 30)
        arm, elbow = _get(walk, 'armSwing', 18), _get(walk, 'elbow', 12)
        pose['LegNear'][2] += f * w * leg * sn
        pose['LegFar'][2] -= f * w * leg * sn
        pose['LegNearLow'][2] -= f * w * knee * max(0.0, cs * dirv)
        pose['LegFarLow'][2] -= f * w * knee * max(0.0, -cs * dirv)
        pose['ArmNear'][2] -= f * w * arm * sn
        pose['ArmFar'][2] += f * w * arm * sn
        pose['ArmNearLow'][2] += f * w * elbow * (1 + 0.5 * max(0.0, -sn))
        pose['ArmFarLow'][2] += f * w * elbow * (1 + 0.5 * max(0.0, sn))
        sh = _get(walk, 'shoulder', 0.8)
        pose['ArmNear'][0] -= f * w * sh * sn
        pose['ArmFar'][0] += f * w * sh * sn
        pose['Body'][1] += w * _get(walk, 'bob', 1.0) * (abs(cs) - 1)
        pose['Body'][2] += -f * w * _get(walk, 'lean', 4) + w * _get(walk, 'sway', 1.0) * math.sin(2 * ph)
        pose['Head'][2] += f * w * _get(walk, 'nod', 1.5) * math.cos(2 * ph)

    # secondary motion: head / arms lag behind the body, forearms behind the upper arms (keyed motion only)
    dr = tr.get('drag')
    if dr is not None:
        dr = dr or {}
        lag, mx = _get(dr, 'lag', 0.08), _get(dr, 'max', 25)
        t0 = t - lag
        def delta(p):
            k = parts.get(p, {}).get('keys')
            return evaluate(k, t0, sm)[2] - evaluate(k, t, sm)[2]
        cl = lambda v: max(-mx, min(mx, v))
        bd = delta('Body')
        pose['Head'][2] += cl(_get(dr, 'head', 0.5) * bd)
        for p in ('ArmNear', 'ArmFar'): pose[p][2] += cl(_get(dr, 'arms', 0.4) * bd)
        for p in ('LegNear', 'LegFar'): pose[p][2] += cl(_get(dr, 'legs', 0.0) * bd)
        for low, up in SEG_PARENT.items():
            if low.endswith('Low'): g = _get(dr, 'low', 0.35) if low.startswith('Arm') else _get(dr, 'legsLow', 0.0)
            else: g = _get(dr, 'hands', 0.3) if low.startswith('Arm') else _get(dr, 'feet', 0.0)
            pose[low][2] += cl(g * delta(up))

    # concussion: drunken sway, lolling head, dangling arms, buckling knees
    dz = tr.get('daze'); dw = daze_weight(tr, t)
    if dz is not None and dw > 0:
        dz = dz or {}
        per = _get(dz, 'period', 1.4)
        a = 2 * math.pi * t / per
        s1 = math.sin(a) + 0.35 * math.sin(2.3 * a + 1.0)
        pose['Body'][2] += dw * _get(dz, 'sway', 6) * s1
        pose['Body'][0] += dw * _get(dz, 'shift', 1) * math.sin(a + 0.5)
        dip = 0.5 + 0.5 * math.sin(2 * a + 0.7)
        pose['Body'][1] -= dw * _get(dz, 'dip', 1) * dip
        pose['Head'][2] += dw * _get(dz, 'loll', 12) * (math.sin(a - 1.1) + 0.3 * math.sin(3.1 * a))
        am = dw * _get(dz, 'arms', 10)
        pose['ArmNear'][2] += am * math.sin(a - 0.7); pose['ArmFar'][2] += am * 0.8 * math.sin(a - 1.2)
        pose['ArmNearLow'][2] += am * 0.6 * math.sin(a - 1.3); pose['ArmFarLow'][2] += am * 0.5 * math.sin(a - 1.8)
        pose['ArmNearHand'][2] += am * 0.5 * math.sin(a - 2.0); pose['ArmFarHand'][2] += am * 0.4 * math.sin(a - 2.4)
        kn = dw * _get(dz, 'knees', 6) * dip
        pose['LegNear'][2] -= f * kn; pose['LegNearLow'][2] += f * kn * 1.6
        pose['LegFar'][2] -= f * kn * 0.7; pose['LegFarLow'][2] += f * kn * 1.2

    idle = tr.get('idle')
    if idle is not None:
        idle = idle or {}
        k = 1 - w
        per = _get(idle, 'period', 2.6)
        b = math.sin(2 * math.pi * t / per)
        pose['Body'][2] += k * _get(idle, 'weightShift', 1.0) * math.sin(2 * math.pi * t / (per * 1.7) + 1.0)
        pose['Head'][2] += k * f * _get(idle, 'headNod', 1.5) * math.sin(2 * math.pi * t / per - 0.6)
        br = _get(idle, 'breath', 0.5) * k * b
        pose['Head'][1] += br; pose['ArmNear'][1] += br; pose['ArmFar'][1] += br
        sw = _get(idle, 'armSway', 2.0) * k * b
        pose['ArmNear'][2] += f * sw; pose['ArmFar'][2] -= f * sw * 0.7
        pose['ArmNearLow'][2] += f * sw * 0.8; pose['ArmFarLow'][2] += f * sw * 0.8
        pose['ArmNearHand'][2] += f * sw * 0.5; pose['ArmFarHand'][2] += f * sw * 0.5

    # v12 hit effects: impact recoil of the hit part and the concussion stagger
    import fx as FX
    FX.apply_pose(proto, actor, t, pose, f)

    side = view_at(tr, t) is None
    limits = tr.get('joints')
    apply_joint_limits(pose, f, side, limits)
    out = _chain(pose, pivot)

    if ik and tr.get('ik'):
        any_ik = False
        for track in tr['ik']:
            any_ik |= _solve_ik(proto, actor, tr, track, t, pose, pivot, out, pivs_all)
        if any_ik:
            apply_joint_limits(pose, f, side, limits)
            out = _chain(pose, pivot)

    lk = tr.get('look')
    if look and lk is not None:
        lk = lk or {}
        wt = evaluate(lk.get('keys'), t)[0] if lk.get('keys') else _get(lk, 'weight', 1.0)
        if wt > 0:
            own = point(out, 'Head', vec(lk.get('at'), (0, 9)))
            me = evaluate(tr['move'], t)
            other = tr['_look']
            if other < 0 or other >= len(proto['actors']) or other == actor: return out
            oout = solve(proto, other, t, (pivs_all or {}).get(other), look=False, ik=False)
            tp = point(oout, lk.get('target', 'Head'), vec(lk.get('targetAt'), (0, 9)))
            om = evaluate(proto['actors'][other]['move'], t)
            dx = (om[0] * 32 + tp[0]) - (me[0] * 32 + own[0])
            dy = (om[1] * 32 + tp[1]) - (me[1] * 32 + own[1])
            wt *= min(max(f * dx / 8.0, 0.0), 1.0)          # target behind: stop looking
            desired = math.degrees(math.atan2(dy, max(f * dx, 1e-3)))
            mx = _get(lk, 'max', 30)
            desired = max(-mx, min(mx, desired))
            cur = f * (out['Body'][2] + out['Head'][2])
            add = wt * (desired - cur)
            neck = _get(lk, 'neck', 40)
            rel = f * pose['Head'][2] + add
            rel = max(-neck, min(neck, rel))
            pose['Head'][2] = f * rel
            out = _chain(pose, pivot)
    return out

# joint limits (degrees, relative to the parent segment, in the actor's forward frame: + = swung towards its facing)
KNEE = (-150.0, 0.0)      # the shin only folds back behind the thigh
ELBOW = (-5.0, 150.0)     # the forearm only folds forward / up
HIP = (-60.0, 125.0)
SHOULDER = (-80.0, 190.0)  # v10: arm swung slightly behind the body .. overhead (forward)
BACK_VIEW_BEND = 100.0    # back-to-camera views: no facing in the screen plane, only a range
def _cl(v, lo, hi): return max(lo, min(hi, v))
SOFT_LIMIT = 4.0
def soft_clamp(x, lo, hi, k=SOFT_LIMIT):
    """ProcAnimTimeline.SoftClamp: quadratic ease into the limit over k degrees each side (C1, no popping)."""
    if lo > hi: lo, hi = hi, lo
    k = min(k, (hi - lo) * 0.5)
    if k <= 0: return _cl(x, lo, hi)
    if x > hi - k: return hi if x >= hi + k else hi - (hi + k - x) ** 2 / (4 * k)
    if x < lo + k: return lo if x <= lo - k else lo + (x - lo + k) ** 2 / (4 * k)
    return x
def apply_joint_limits(pose, f, side, limits=None):
    """limits: the actor's `joints` override (knee / elbow / hip / shoulder 'min, max', backViewBend)."""
    l = limits or {}
    knee, elbow = vec(l.get('knee'), KNEE), vec(l.get('elbow'), ELBOW)
    hip, shoulder = vec(l.get('hip'), HIP), vec(l.get('shoulder'), SHOULDER)
    bvb = _get(l, 'backViewBend', BACK_VIEW_BEND)
    for n in ('LegNearLow', 'LegFarLow', 'ArmNearLow', 'ArmFarLow'):
        lo, hi = knee if n.startswith('Leg') else elbow
        pose[n][2] = f * soft_clamp(f * pose[n][2], lo, hi) if side else soft_clamp(pose[n][2], -bvb, bvb)
    if side:
        for n in ('LegNear', 'LegFar'): pose[n][2] = f * soft_clamp(f * pose[n][2], *hip)
        for n in ('ArmNear', 'ArmFar'): pose[n][2] = f * soft_clamp(f * pose[n][2], *shoulder)
    for n in ('ArmNearHand', 'ArmFarHand'): pose[n][:] = [0.0, 0.0, 0.0]   # the hand is rigid with the forearm (no wrist)
    for n in ('LegNearFoot', 'LegFarFoot'): pose[n][2] = 0.0   # legs have no foot segment (shoe rides on the shin)

# ---- two-bone IK (ProcAnimTimeline.SolveIk) ----
IK_CHAIN = {'ArmNear': ('ArmNearLow', 'ArmNearHand'), 'ArmFar': ('ArmFarLow', 'ArmFarHand'),
            'LegNear': ('LegNearLow', 'LegNearFoot'), 'LegFar': ('LegFarLow', 'LegFarFoot')}
def ik_weight(track, t):
    w = evaluate(track['weights'], t)[0] if track.get('weights') else _get(track, 'weight', 1.0)
    w = min(max(w, 0.0), 1.0)
    return w * w * (3 - 2 * w)   # smoothstep (ProcAnimTimeline.IkWeight)
def ik_target(proto, actor, track, t, own, pivs_all=None):
    k = evaluate(track.get('keys'), t); off = (k[0], k[1])
    at = vec(track.get('at'))
    if track['_actor'] < 0: return off
    if track['_actor'] == actor:
        p = point(own, track['_bone'], at); return (p[0] + off[0], p[1] + off[1])
    other = track['_actor']
    op = solve(proto, other, t, (pivs_all or {}).get(other), look=False, ik=False)
    tp = point(op, track['_bone'], at)
    me = evaluate(proto['actors'][actor]['move'], t); om = evaluate(proto['actors'][other]['move'], t)
    return (om[0] * 32 + tp[0] - me[0] * 32 + off[0], om[1] * 32 + tp[1] - me[1] * 32 + off[1])
def _atan2d(v): return math.degrees(math.atan2(v[1], v[0]))
def _wrap(d):
    d = math.fmod(d, 360.0)
    if d > 180: d -= 360
    elif d <= -180: d += 360
    return d
def _solve_ik(proto, actor, tr, track, t, pose, pivot, out, pivs_all):
    if not track.get('_valid', True): return False
    w = ik_weight(track, t)
    if w <= 0: return False
    upper = track.get('limb', 'ArmNear'); low, tip = IK_CHAIN[upper]
    target = ik_target(proto, actor, track, t, out, pivs_all)
    b = out['Body']
    local = rot((target[0] - b[0], target[1] - b[1]), -b[2])
    u, l = pose[upper], pose[low]
    s = (pivot[upper][0] + u[0], pivot[upper][1] + u[1])
    v1 = (pivot[low][0] - pivot[upper][0] + l[0], pivot[low][1] - pivot[upper][1] + l[1])
    tp = vec(track['tip']) if track.get('tip') is not None else pivot[tip]
    v2 = (tp[0] - pivot[low][0], tp[1] - pivot[low][1])
    l1, l2 = math.hypot(*v1), math.hypot(*v2)
    if l1 < 1e-3 or l2 < 1e-3: return False
    d = (local[0] - s[0], local[1] - s[1])
    dist = min(max(math.hypot(*d), abs(l1 - l2) + 1e-3), l1 + l2 - 1e-3)
    cos_a = min(max((l1 * l1 + dist * dist - l2 * l2) / (2 * l1 * dist), -1.0), 1.0)
    alpha = math.degrees(math.acos(cos_a)); dir_d = _atan2d(d)
    def bend(sign):
        th_u = dir_d + sign * alpha - _atan2d(v1)
        e = rot(v1, th_u); elbow = (s[0] + e[0], s[1] + e[1])
        th_l = _atan2d((local[0] - elbow[0], local[1] - elbow[1])) - _atan2d(v2) - th_u
        return (_wrap(th_u), _wrap(th_l))
    plus, minus = bend(1.0), bend(-1.0)
    leg = upper.startswith('Leg'); f = tr['facing']; mode = track.get('bend', 'Auto')
    if mode == 'Positive': pick = plus if plus[1] >= minus[1] else minus
    elif mode == 'Negative': pick = plus if plus[1] <= minus[1] else minus
    else: pick = plus if (f * plus[1] <= f * minus[1] if leg else f * plus[1] >= f * minus[1]) else minus
    pose[upper][2] = u[2] + w * _wrap(pick[0] - u[2])
    pose[low][2] = l[2] + w * _wrap(pick[1] - l[2])
    return True

def expression_at(tr, t):
    """(face, eyes) of the stub expression layer at t (step keys)."""
    face = eyes = None
    for k in tr.get('expressions') or []:
        if float(k['t']) > t: break
        face = k.get('face', face) if k.get('face') is not None else face
        eyes = k.get('eyes', eyes) if k.get('eyes') is not None else eyes
    return face, eyes

def view_at(tr, t):
    """'Turned' for a turned view (side-on, facing the other way), else the forced screen facing, or None."""
    for v in tr.get('views') or []:
        if float(v['start']) <= t < float(v['end']): return 'Turned' if v.get('turned') else v.get('facing', 'North')
    return None

ANCESTOR = dict(LOW_PARENT)
def is_hidden(tr, part, t):
    """ProcAnimTimeline.IsHidden: hidden from a hide entry's t on (until its `until`, if set), with every part carried by a listed part."""
    for h in tr.get('hide') or []:
        if t < float(h['t']): continue
        if h.get('until') is not None and t >= float(h['until']): continue
        p = part
        while p:
            if p in h.get('parts', []): return True
            p = ANCESTOR.get(p)
    return False

def held_in_hands(tr, t):
    return (not tr.get('holsterHeldItems') or (tr.get('holsterAt') is not None and t < float(tr['holsterAt']))
            or (tr.get('unholsterAt') is not None and t >= float(tr['unholsterAt'])))

def prop_visible(tr, prop, t):
    return float(prop.get('start', 0)) <= t < float(prop.get('end', 1e9)) and not is_hidden(tr, prop.get('part', 'Body'), t)

def flip_scale(tr, part, t):
    """v13.2 ProcAnimTimeline.FlipScale: 1, -1 once the part is turned round, a cosine through edge-on."""
    def turn(at, blend):
        u = (1.0 if t >= at else 0.0) if blend <= 0 else min(1.0, max(0.0, (t - (at - blend / 2)) / blend))
        return math.cos(math.pi * u)
    s = 1.0
    for f in tr.get('flips') or []:
        if part not in f.get('parts', []): continue
        b = float(f.get('blend', 0.12))
        s *= turn(float(f['t']), b)
        if f.get('until') is not None: s *= turn(float(f['until']), b)
    return min(s, -0.15) if s < 0 else max(s, 0.15)  # MinFlipScale

def prop_pose(prop, part_pose, t, flip=1.0, extra=0.0):
    kx, ky, kr = evaluate(prop['keys'], t)
    kx *= flip; kr = (kr + extra) * (-1.0 if flip < 0 else 1.0)
    px, py, pr = part_pose
    c, s = math.cos(math.radians(pr)), math.sin(math.radians(pr))
    return (px + c * kx - s * ky, py + s * kx + c * ky, pr + kr)

def daze_weight(tr, t):
    dz = tr.get('daze')
    if not dz: return 0.0
    return max(0.0, evaluate(dz['weight'], t)[0]) if dz.get('weight') else _get(dz, 'amount', 1.0)

def stars(tr, t):
    """Dizzy stars orbiting over the head: [(x, y, size, alpha, front)] px relative to the stars centre."""
    dz = tr.get('daze'); st = dz.get('stars') if dz else None
    if not st: return []
    thr = _get(st, 'threshold', 0.5); w = daze_weight(tr, t)
    if w <= thr: return []
    alpha = min(1.0, (w - thr) / max(1e-3, (1 - thr) * 0.5))
    n = int(st.get('count', 3)); per = _get(st, 'period', 0.9); rad = vec(st.get('radius'), (6, 2))
    res = []
    for i in range(n):
        a = 2 * math.pi * (t / per + i / n)
        front = math.sin(a) < 0
        tw = math.sin(2 * math.pi * t * 3.1 + i * 2.1)
        res.append((rad[0] * math.cos(a), rad[1] * math.sin(a), 2 if front and tw > -0.2 else 1, alpha * (1.0 if front else 0.55), front))
    return res

def _chain(pose, pivot):
    out = {}
    pb = pose['Body']; ob = pivot_offset(pivot['Body'], pb); out['Body'] = (ob[0], ob[1], pb[2])
    for p in ('Head', 'ArmNear', 'ArmFar', 'LegNear', 'LegFar'):
        o = pivot_offset(pivot[p], pose[p]); out[p] = (o[0], o[1], pose[p][2])
    for low, up in LOW_PARENT.items():
        ou = out[up]; ol = pivot_offset(pivot[low], pose[low]); r = rot(ol, ou[2])
        out[low] = (ou[0] + r[0], ou[1] + r[1], ou[2] + pose[low][2])
    return out

def point(out, part, at):
    """Forward-frame px (relative to the entity position) of the rest-pose point `at` carried by `part`."""
    b = out['Body']
    if part == 'Body':
        r = rot(at, b[2]); return (b[0] + r[0], b[1] + r[1])
    p = out[part]; r = rot(at, p[2]); q = rot((p[0] + r[0], p[1] + r[1]), b[2])
    return (b[0] + q[0], b[1] + q[1])

# ---- blood ----
M = 0xffffffff
def _hash(x):
    x &= M
    x ^= x >> 16; x = (x * 0x7feb352d) & M
    x ^= x >> 15; x = (x * 0x846ca68b) & M
    x ^= x >> 16
    return x
def rnd(e, i, k): return _hash(e * 7919 + i * 131 + k + 1) / 4294967296.0
def lerp(a, b, u): return a + (b - a) * u

def droplets(bl, e, origin, age):
    """-> [(x, y, size, shade, alpha)] forward-frame px relative to the target entity, `age` s after the burst."""
    res = []
    n = int(bl.get('count', 12)); g = _get(bl, 'gravity', 320); life = _get(bl, 'life', 1.6)
    sp, an = vec(bl.get('speed'), (40, 110)), vec(bl.get('angle'), (30, 150))
    fl, big = _get(bl, 'floor', -14), _get(bl, 'bigChance', 0.35)
    if age > life: return res
    alpha = 1.0 if age < life * 0.55 else max(0.0, 1 - (age - life * 0.55) / (life * 0.45))
    for i in range(n):
        a = math.radians(lerp(an[0], an[1], rnd(e, i, 0))); s = lerp(sp[0], sp[1], rnd(e, i, 1))
        size = 2 if rnd(e, i, 2) < big else 1
        shade = 0.7 + 0.4 * rnd(e, i, 3)
        floor = fl + (rnd(e, i, 4) - 0.5) * _get(bl, 'floorJitter', 4)
        delay = rnd(e, i, 5) * _get(bl, 'stagger', 0.04)
        tt = age - delay
        if tt < 0: continue
        vx, vy = s * math.cos(a), s * math.sin(a)
        y0 = origin[1]
        # landing time: y0 + vy t - g/2 t^2 = floor
        disc = vy * vy + 2 * g * (y0 - floor)
        tl = (vy + math.sqrt(disc)) / g if disc >= 0 and g > 0 else 1e9
        if tt >= tl:
            res.append((origin[0] + vx * tl, floor, size + 1, shade * 0.8, alpha))
        else:
            res.append((origin[0] + vx * tt, y0 + vy * tt - 0.5 * g * tt * tt, size, shade, alpha))
    return res
