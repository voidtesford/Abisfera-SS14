# Reference implementation of the ProcAnim "puppet" split (mirrored 1:1 by ProcAnimPuppet.cs on the client).
# Input: the visible sprite layers of one humanoid in draw order, each already resolved to its displayed RSI frame
# (RGBA, layer colour applied) and its map key. Output: one composited image per animated group plus mask-derived
# joint pivots. Coordinates: image px (y down) inside a C x C canvas centred on the sprite origin.
import numpy as np

# puppet draw order (back to front): arms and legs are two segments (upper arm / forearm + hand, thigh / shin + foot;
# the *Hand / *Foot groups stay empty since v10);
# Held* = in-hand items (carried by the hand until holstered), Slung = the holstered item on the back
ORDER = ['ArmFar', 'ArmFarLow', 'ArmFarHand', 'HeldFar', 'LegFar', 'LegFarLow', 'LegFarFoot', 'Torso', 'LegNear',
         'LegNearLow', 'LegNearFoot', 'TorsoTop', 'Slung', 'Head', 'ArmNear', 'ArmNearLow', 'ArmNearHand', 'HeldNear']
LOW = {'ArmNear': 'ArmNearLow', 'ArmFar': 'ArmFarLow', 'LegNear': 'LegNearLow', 'LegFar': 'LegFarLow'}
TIP = {'ArmNear': 'ArmNearHand', 'ArmFar': 'ArmFarHand', 'LegNear': 'LegNearFoot', 'LegFar': 'LegFarFoot'}
HELD = {'ArmNear': 'HeldNear', 'ArmFar': 'HeldFar'}
HEAD_KEYS = {'Head', 'Snout', 'Eyes', 'HeadSide', 'HeadTop', 'Face', 'Hair', 'FacialHair'}
HEAD_SLOTS = {'head', 'mask', 'maskalt', 'eyes', 'ears'}
TOP_SLOTS = {'belt', 'back', 'neck', 'id', 'pocket1', 'pocket2', 'suitstorage'}
FAR_DARKEN = 0.82
NEAREST_RADIUS = 3

def slot_of(key):
    return key.split('-')[0] if isinstance(key, str) else None

def classify(key, near_right):
    """-> (kind, side) kind: part:<Group> | whole:<Group> | gloves | shoes | split"""
    n, f = ('R', 'L') if near_right else ('L', 'R')
    if key in ('Chest', 'Groin', 'Tail'): return 'part', 'Torso'
    if key == 'Head': return 'part', 'Head'
    if key == 'back-slung': return 'whole', 'Slung'
    if key in HEAD_KEYS: return 'whole', 'Head'
    for s, near in ((n, 'Near'), (f, 'Far')):
        if key in (s + 'Arm', s + 'Hand'): return 'part', 'Arm' + near
        if key in (s + 'Leg', s + 'Foot'): return 'part', 'Leg' + near
    if isinstance(key, str) and key.startswith('inhand-'):
        right = key.startswith('inhand-right')
        return 'whole', 'ArmNear' if right == near_right else 'ArmFar'
    s = slot_of(key)
    if s in HEAD_SLOTS: return 'whole', 'Head'
    if s in TOP_SLOTS: return 'whole', 'TorsoTop'
    if s == 'gloves': return 'gloves', None
    if s == 'shoes': return 'shoes', None
    return 'split', None

def _label(masks, allowed, y, x, C):
    hit = [g for g in allowed if masks[g][y, x]]
    if hit: return hit
    best, found = None, []
    for dy in range(-NEAREST_RADIUS, NEAREST_RADIUS + 1):
        for dx in range(-NEAREST_RADIUS, NEAREST_RADIUS + 1):
            yy, xx = y + dy, x + dx
            if not (0 <= yy < C and 0 <= xx < C): continue
            d = dx * dx + dy * dy
            if d > NEAREST_RADIUS * NEAREST_RADIUS: continue
            gs = [g for g in allowed if masks[g][yy, xx]]
            if not gs: continue
            if best is None or d < best: best, found = d, list(gs)
            elif d == best: found += [g for g in gs if g not in found]
    return found

def _over(dst, src, sel=None):
    a = src[..., 3:4].astype(np.float32) / 255.0
    if sel is not None: a = a * sel[..., None]
    da = dst[..., 3:4].astype(np.float32) / 255.0
    oa = a + da * (1 - a)
    rgb = np.where(oa > 0, (src[..., :3] * a + dst[..., :3] * da * (1 - a)) / np.maximum(oa, 1e-6), 0)
    dst[..., :3] = rgb.round().clip(0, 255); dst[..., 3:4] = (oa * 255).round().clip(0, 255)

def _shift(m, d):
    r = np.roll(m, d, axis=1)
    if d > 0: r[:, :d] = 0
    elif d < 0: r[:, d:] = 0
    return r

def _bbox(m):
    ys, xs = np.nonzero(m)
    return (ys.min(), ys.max(), xs.min(), xs.max()) if len(ys) else None

def _fill_under_arm(torso, arm):
    """Torso pixels hidden under the near arm (sleeve copies, bare skin under the hand) get the colour of the nearest
    torso pixel outside the arm in the same row (else column), so the body behind a swung arm reads as plain cloth.
    Silhouette pixels (an opaque pixel with a transparent 4-neighbour) are kept: they are the outline."""
    C = torso.shape[0]
    op = torso[..., 3] > 0
    region = arm & op
    def boundary(y, x):
        for yy, xx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
            if not (0 <= yy < C and 0 <= xx < C) or not op[yy, xx]: return True
        return False
    src = torso.copy()
    ok = op & ~region
    for y, x in zip(*np.nonzero(region)):
        if boundary(y, x): continue
        best = None
        for d in range(1, C):
            for yy, xx in ((y, x - d), (y, x + d)):
                if 0 <= xx < C and ok[yy, xx] and not boundary(yy, xx): best = (yy, xx); break
            if best: break
        if best is None:
            for d in range(1, C):
                for yy, xx in ((y - d, x), (y + d, x)):
                    if 0 <= yy < C and ok[yy, xx] and not boundary(yy, xx): best = (yy, xx); break
                if best: break
        if best: torso[y, x] = src[best]

def build(layers, near_right):
    """layers: [(key, img HxWx4 uint8 already placed on a CxC canvas)] in draw order."""
    C = layers[0][1].shape[0]
    kinds = [classify(k, near_right) for k, _ in layers]
    masks = {g: np.zeros((C, C), bool) for g in ORDER}
    hands = {'ArmNear': np.zeros((C, C), bool), 'ArmFar': np.zeros((C, C), bool)}
    armonly = {'ArmNear': np.zeros((C, C), bool), 'ArmFar': np.zeros((C, C), bool)}
    legonly = {'LegNear': np.zeros((C, C), bool), 'LegFar': np.zeros((C, C), bool)}
    feet = {'LegNear': np.zeros((C, C), bool), 'LegFar': np.zeros((C, C), bool)}
    for (key, img), (kind, g) in zip(layers, kinds):
        if kind != 'part': continue
        a = img[..., 3] > 0
        masks[g] |= a
        if g in hands: (hands if 'Hand' in key else armonly)[g][...] |= a
        if g in legonly: (legonly if 'Leg' in key else feet)[g][...] |= a
    near_n = int(masks['ArmNear'].sum()); far_n = int(masks['ArmFar'].sum())
    synth = near_n > 0 and far_n < 0.6 * near_n
    dx = 0
    if synth:
        bf, bn = _bbox(hands['ArmFar']), _bbox(hands['ArmNear'])
        if bf and bn: dx = int(bf[3] - bn[3]) if near_right else int(bf[2] - bn[2])
    split_allowed = ['ArmNear', 'Head', 'LegNear', 'Torso', 'LegFar', 'ArmFar']
    arms, legs = ['ArmNear', 'ArmFar'], ['LegNear', 'LegFar']
    lab_cache = {}
    def label(allowed, fallback, y, x):
        k = (tuple(allowed), y, x)
        if k not in lab_cache:
            gs = _label(masks, allowed, y, x, C)
            lab_cache[k] = gs if gs else [fallback]
        return lab_cache[k]
    out = {g: np.zeros((C, C, 4), np.uint8) for g in ORDER}
    held = {'ArmNear': np.zeros((C, C, 4), np.uint8), 'ArmFar': np.zeros((C, C, 4), np.uint8)}
    near_bare = np.zeros((C, C, 4), np.uint8)   # near arm without held items: source of the synthesized far arm
    glove = {'ArmNear': np.zeros((C, C), bool), 'ArmFar': np.zeros((C, C), bool)}   # glove pixels ride on the hand
    for (key, img), (kind, g) in zip(layers, kinds):
        if kind == 'whole' and g in held:   # in-hand items: carried by the hand (lower arm segment)
            _over(held[g], img); continue
        if kind in ('part', 'whole'):
            if synth and g == 'ArmFar' and kind == 'part': continue   # far-arm slivers: replaced by the synthesized arm
            _over(out[g], img)
            if g == 'ArmNear' and kind == 'part': _over(near_bare, img)
            continue
        allowed, fb = {'split': (split_allowed, 'Torso'), 'gloves': (arms, 'ArmNear'), 'shoes': (legs, 'LegNear')}[kind]
        sel = {grp: np.zeros((C, C), np.float32) for grp in ORDER}
        ys, xs = np.nonzero(img[..., 3] > 0)
        for y, x in zip(ys, xs):
            for grp in label(allowed, fb, y, x):
                if synth and grp == 'ArmFar': continue
                sel[grp][y, x] = 1
        for grp in ORDER:
            if sel[grp].any():
                _over(out[grp], img, sel[grp])
                if grp == 'ArmNear': _over(near_bare, img, sel[grp])
                if kind == 'gloves' and grp in glove: glove[grp] |= sel[grp] > 0
    _fill_under_arm(out['Torso'], masks['ArmNear'])
    if synth:
        far = np.zeros((C, C, 4), np.uint8)
        sh = np.roll(near_bare, dx, axis=1)
        if dx > 0: sh[:, :dx] = 0
        elif dx < 0: sh[:, dx:] = 0
        sh = sh.copy(); sh[..., :3] = (sh[..., :3].astype(np.float32) * FAR_DARKEN).round().astype(np.uint8)
        _over(far, sh); _over(far, out['ArmFar']); out['ArmFar'] = far
        glove['ArmFar'] = _shift(glove['ArmNear'], dx)
    # joints (centred, y up)
    def top_joint(m):
        b = _bbox(m)
        if not b: return None
        y0 = b[0]; xs = np.nonzero(m[y0:y0 + 2].any(axis=0))[0]
        return (float(xs.mean()) + 0.5 - C / 2, C / 2 - (y0 + 1))
    piv = {}
    j = top_joint(armonly['ArmNear'] if armonly['ArmNear'].any() else masks['ArmNear'])
    if j: piv['ArmNear'] = j
    if synth and j: piv['ArmFar'] = (j[0] + dx, j[1])
    elif not synth:
        jf = top_joint(armonly['ArmFar'] if armonly['ArmFar'].any() else masks['ArmFar'])
        if jf: piv['ArmFar'] = jf
    for g in legs:
        jl = top_joint(legonly[g] if legonly[g].any() else masks[g])
        if jl: piv[g] = jl
    b = _bbox(masks['Head'])
    if b:
        y1 = b[1]; xs = np.nonzero(masks['Head'][y1])[0]
        piv['Head'] = (float(xs.mean()) + 0.5 - C / 2, C / 2 - (y1 + 1))
    # three-segment limbs, cut along rows taken from the masks: the wrist / ankle at the top of the hand / foot mask,
    # the elbow / knee half-way between the shoulder / hip joint and it. Each lower segment keeps one overlap row
    # above its cut so no seam opens while bending; glove pixels always ride on the hand, held items on their own
    # layer carried by it.
    def shifted(m, d): return _shift(m, d) if d else m
    seg = {}
    for g in ('ArmNear', 'ArmFar', 'LegNear', 'LegFar'):
        if g.startswith('Arm'):
            src = 'ArmNear' if (g == 'ArmFar' and synth) else g
            sdx = dx if (g == 'ArmFar' and synth) else 0
            jm = shifted(armonly[src] if armonly[src].any() else masks[src], sdx)
            mm, tm = shifted(masks[src], sdx), shifted(hands[src], sdx)
        else:
            jm = legonly[g] if legonly[g].any() else masks[g]; mm, tm = masks[g], feet[g]
        jb, mb = _bbox(jm), _bbox(mm)
        if not jb or not mb or g not in piv: continue
        y0, y1 = int(jb[0]), int(mb[1])
        L = y1 - y0 + 1
        if L < 6: continue
        if g.startswith('Leg'):   # two-segment legs (thigh, shin + foot): knee half-way between the hip and the sole
            k = y0 + (L + 1) // 2
            xs = np.nonzero(mm[k])[0]
            if not len(xs): continue
            img = out[g]; low = np.zeros_like(img); low[k - 1:] = img[k - 1:]
            up = img.copy(); up[k:] = 0
            out[g], out[LOW[g]], out[TIP[g]] = up, low, np.zeros_like(img)
            piv[LOW[g]] = (float(xs.mean()) + 0.5 - C / 2, C / 2 - k)
            piv[TIP[g]] = (float(xs.mean()) + 0.5 - C / 2, C / 2 - (y1 + 1))
            seg[g] = (y0 + 1, k, y1 + 1, y1 + 1)
            continue
        # v10 (t279u): two-segment arms as well (upper arm, forearm + hand + glove, rigid like the shin with its shoe):
        # elbow cut from the shoulder and finger-tip rows (like the knee from the hip and the sole), so the arm keeps its full
        # length and the forearm carries the hand. The hand group stays empty; its pivot is the wrist (grip reference).
        # Elbow at 2/5 of shoulder -> finger tips (upper arm : forearm + hand ~ 30 : 44 cm).
        tb = _bbox(tm)
        w = int(tb[0]) if tb and y0 + 3 <= tb[0] <= y1 else y0 + int(round(0.7 * L))
        e = y0 + 1 + (2 * (L - 1)) // 5
        def cx(m, row):
            xs = np.nonzero(m[row])[0]
            return float(xs.mean()) if len(xs) else None
        xe = cx(mm, e); xw = cx(jm, w - 1)
        if xw is None: xw = cx(mm, w)
        if xe is None or xw is None: continue
        img = out[g]; gl = glove.get(g)
        gm = gl[..., None] if gl is not None else np.zeros((C, C, 1), bool)
        low = np.zeros_like(img); low[e - 1:] = img[e - 1:]
        if gl is not None: low = np.where(gm, img, low).astype(np.uint8)
        up = img.copy(); up[e:] = 0
        if gl is not None: up = np.where(gm, 0, up).astype(np.uint8)
        out[g], out[LOW[g]], out[TIP[g]] = up, low, np.zeros_like(img)
        piv[LOW[g]] = (xe + 0.5 - C / 2, C / 2 - e)
        piv[TIP[g]] = (xw + 0.5 - C / 2, C / 2 - w)
        seg[g] = (y0 + 1, e, w, y1 + 1)
    for g, hg in HELD.items():
        if not held[g][..., 3].any(): continue
        out[hg] = held[g]
        src = 'ArmNear' if (g == 'ArmFar' and synth) else g
        hb = _bbox(shifted(hands[src], dx if (g == 'ArmFar' and synth) else 0))
        if hb: piv[hg] = ((hb[2] + hb[3] + 1) / 2 - C / 2, C / 2 - (hb[0] + hb[1] + 1) / 2)
        elif TIP[g] in piv: piv[hg] = piv[TIP[g]]
    return out, piv, {'synth': synth, 'dx': dx, 'seg': seg}
