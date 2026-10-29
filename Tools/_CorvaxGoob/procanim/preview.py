#!/usr/bin/env python3
# ProcAnim offline preview: reads a procAnimScene from the real prototypes and the real RSI layers, splits each actor
# into the same puppet as the client (puppet.py = ProcAnimPuppet.cs) and poses it with the same maths (solve.py =
# ProcAnimTimeline.cs): easings, procedural layers, joint limits, IK, look-at, props, hit-stops, shake, flash, blood.
# Side view; the first actor faces screen right when its `facing` is 1.
#
#   python3 Tools/_CorvaxGoob/procanim/preview.py <scene-id> [--fps 30] [--crop t0-t1] [--out DIR] [--tag SUFFIX]
#       [--actor ROLE=PRESET ...] [--empty-hands] [--no-video] [--res Resources] [--list]
#
# Writes <out>/<scene-id><tag>-preview.mp4, -preview.gif and -contact-sheet.png (one frame per `sheet: true` event,
# plus the holster intro and the end). --crop limits the video / sheet to real time t0..t1 (seconds, hit-stops
# included; negative = the holster intro). Presets: gilbert (the Gilbert loadout, shotgun in hand unless
# --empty-hands), human (grey jumpsuit). Needs python3 with numpy, pillow, pyyaml; ffmpeg for the mp4.
import sys, math, json, re, glob, subprocess, os, argparse, functools, tempfile, shutil
import numpy as np, yaml
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import solve as SV
import fx as FX
import puppet

ap = argparse.ArgumentParser(description='Offline preview of a procAnimScene (mp4 / gif / contact sheet).')
ap.add_argument('scene', nargs='?')
ap.add_argument('--fps', type=int, default=30)
ap.add_argument('--crop', help='real-time range t0-t1, e.g. 0.5-3 or -0.6-1')
ap.add_argument('--out', default='.')
ap.add_argument('--tag', default='')
ap.add_argument('--actor', action='append', default=[], help='ROLE=PRESET (gilbert | human)')
ap.add_argument('--empty-hands', action='store_true', help='no held items (no holster intro)')
ap.add_argument('--no-video', action='store_true', help='contact sheet only')
ap.add_argument('--res', default=os.environ.get('PROCANIM_RES', os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../../Resources'))))
ap.add_argument('--list', action='store_true', help='list the procAnimScene ids')
args = ap.parse_args()
R = args.res
if args.list or not args.scene:
    print('\n'.join(SV.list_scenes(R))); sys.exit(0 if args.list else 1)

S = 4            # screen pixels per sprite pixel
FPS = args.fps
X0, X1, Y0, Y1 = -1.5, 5.6, -1.2, 2.4   # tiles
W, H = int((X1 - X0) * 32 * S), int((Y1 - Y0) * 32 * S)
proto, SRC = SV.load_scene(R, args.scene)
ACT = proto['actors']; N = len(ACT)
TOTAL = SV.total_length(proto)
HELD = not args.empty_hands
DIRS = {'South': 0, 'North': 1, 'East': 2, 'West': 3}
PRESET = {}
for a in args.actor:
    role, _, p = a.partition('='); PRESET[role.lower()] = p
def preset_of(i):
    role = str(ACT[i]['role']).lower()
    if role in PRESET: return PRESET[role]
    return 'gilbert' if i == 0 and str(proto['id']).startswith('GilbertFinisher') else 'human'
PRESETS = [preset_of(i) for i in range(N)]
# the holster intro plays only when an actor that holsters actually holds something
INTRO = float(proto.get('intro', 0) or 0) if HELD and any(
    a.get('holsterHeldItems') and PRESETS[i] == 'gilbert' for i, a in enumerate(ACT)) else 0.0
# ---- sprites ----
blocks = {}
for f in glob.glob(R + '/Prototypes/**/*.yml', recursive=True):
    txt = open(f, errors='ignore').read()
    for b in re.split(r'\n(?=- type: )', txt):
        m = re.search(r'^\s*id:\s*(\S+)', b, re.M)
        if m and b.lstrip().startswith('- type: entity'): blocks.setdefault(m.group(1), b)
def rsi_of(eid, depth=0):
    b = blocks.get(eid)
    if not b: return None
    for comp in ('Clothing', 'Sprite', 'Item'):
        m = re.search(r'- type: ' + comp + r'\n((?:\s{4,}.*\n?)*)', b)
        if m:
            s = re.search(r'^\s+sprite:\s*(\S+)', m.group(1), re.M)
            if s: return s.group(1)
    p = re.search(r'^\s*parent:\s*\[?\s*(\w+)', b, re.M)
    return rsi_of(p.group(1), depth + 1) if p and depth < 15 else None
def state(rsi, name, d):
    path = R + '/Textures/' + rsi
    meta = json.load(open(path + '/meta.json', encoding='utf-8-sig'))
    st = [s for s in meta['states'] if s['name'] == name]
    if not st: return None
    img = Image.open(path + '/' + name + '.png').convert('RGBA')
    w, h = meta['size']['x'], meta['size']['y']
    nd = st[0].get('directions', 1); delays = st[0].get('delays'); per = len(delays[0]) if delays else 1
    idx = d * per if nd > 1 else 0; cols = img.width // w
    x, y = (idx % cols) * w, (idx // cols) * h
    return img.crop((x, y, x + w, y + h))
def tint(im, c):
    a = np.asarray(im).astype(np.float32); a[..., :3] *= np.array(c[:3]) / 255.0
    return Image.fromarray(a.clip(0, 255).astype(np.uint8), 'RGBA')
def hexc(h): h = h.lstrip('#'); return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))
SKIN = (255, 225, 204)
import puppet

# Layers in the real draw order (base.yml BaseMobSpeciesOrganic + clothing slot bookmarks + in-hand / Gilbert eye
# layers appended), keyed like the game's sprite layer map.
def human(d):
    P = 'Mobs/Species/Human/parts.rsi'
    st = {'Groin': 'groin_m', 'Chest': 'chest_m', 'Head': 'head_m', 'RArm': 'r_arm', 'LArm': 'l_arm', 'RLeg': 'r_leg',
          'LLeg': 'l_leg', 'LFoot': 'l_foot', 'RFoot': 'r_foot', 'LHand': 'l_hand', 'RHand': 'r_hand'}
    return {k: tint(state(P, v, d), SKIN) for k, v in st.items()}
DRAW_ORDER = ['Groin', 'Chest', 'Head', 'Snout', 'Eyes', 'RArm', 'LArm', 'RLeg', 'LLeg', 'Underwear', 'Undershirt',
              'jumpsuit', 'LFoot', 'RFoot', 'LHand', 'RHand', 'Handcuffs', 'Face', 'gloves', 'shoes', 'ears', 'eyes', 'id',
              'outerClothing', 'belt', 'back', 'neck', 'FacialHair', 'HeadSide', 'maskalt', 'Hair', 'mask', 'HeadTop', 'Tail',
              'head', 'pocket1', 'pocket2', 'inhand-left', 'back-slung', 'inhand-right', 'EyesHalo', 'GilbertEyes']
def ordered(d):
    return [(k, d[k]) for k in DRAW_ORDER if k in d and d[k] is not None]
def gilbert(d):
    L = human(d)
    L['Eyes'] = tint(state('Mobs/Customization/eyes.rsi', 'eyes', d), (200, 20, 20))
    for eid, st, slot in [('ClothingShoesBootsShogkk', 'equipped-FEET', 'shoes'), ('ClothingHandsGlovesCombat', 'equipped-HAND', 'gloves'),
                          ('ClothingOuterArmorBulletproof', 'equipped-OUTERCLOTHING', 'outerClothing'), ('ClothingBeltShogkkWebbingFilled', 'equipped-BELT', 'belt'),
                          ('ClothingBackpackDuffelMilitaryShogkkFilled', 'equipped-BACKPACK', 'back'), ('ClothingMaskGasSwat', 'equipped-MASK', 'mask'),
                          ('ClothingHeadHelmetShogkk', 'equipped-HELMET', 'head'), ('ClothingNeckDogtagShogkk', 'equipped-NECK', 'neck')]:
        rsi = rsi_of(eid)
        im = state(rsi, st, d) if rsi else None
        if im is None: print('missing', eid, st, file=sys.stderr); continue
        L[slot] = im
    im = Image.new('RGBA', (32, 32))   # layered, colour-tinted jumpsuit (as in its prototype)
    for ls, col in [('equipped-base-bottom', '#1c1c22'), ('equipped-base-top-jumpsuit-tac', '#1c1c22'), ('equipped-decor-contractor', '#34343c')]:
        x = state('_NF/Clothing/Uniforms/Jumpsuit/military.rsi', ls, d)
        if x is not None: im.alpha_composite(tint(x, hexc(col)))
    L['jumpsuit'] = im
    # loadout shotgun held in the right hand: drawn in hand (HeldNear) until the holster swing, then its on-back
    # state baked as 'back-slung' (holsterHeldItems / holsterAt)
    G = '_CorvaxGoob/Objects/Weapons/Guns/Shotguns/m8_shotgun.rsi'
    if HELD:
        L['inhand-right'] = state(G, 'inhand-right', d)
        L['back-slung'] = state(G, 'equipped-BACKPACK', d)
    return ordered(L), {'GilbertEyes': state('_CorvaxGoob/Mobs/Gilbert/eyes.rsi', 'glow', d)}   # unshaded -> leftover layer
def human_preset(d, i=1):
    """A plain crew member: grey jumpsuit, black shoes; the hair colour varies per actor."""
    L = human(d)
    hair = [(90, 60, 30), (40, 30, 25), (160, 120, 60), (120, 40, 20)][(i - 1) % 4]
    L['Eyes'] = tint(state('Mobs/Customization/eyes.rsi', 'eyes', d), (60, 90, 160))
    L['Hair'] = tint(state('Mobs/Customization/human_hair.rsi', 'business3', d), hair)
    L['jumpsuit'] = tint(state('Clothing/Uniforms/Jumpsuit/color.rsi', 'equipped-INNERCLOTHING', d), hexc('#b3b3b3'))
    sh = tint(state('Clothing/Shoes/color.rsi', 'equipped-FEET', d), hexc('#1d1d1d'))
    sh.alpha_composite(state('Clothing/Shoes/color.rsi', 'soles-equipped-FEET', d)); L['shoes'] = sh
    return ordered(L), {}
def appearance(i, d):
    return gilbert(d) if PRESETS[i] == 'gilbert' else human_preset(d, i)

def make_actor(layers, leftovers, near_right):
    out, piv, info = puppet.build([(k, np.asarray(im.convert('RGBA')).copy()) for k, im in layers], near_right)
    groups = [(g, Image.fromarray(out[g], 'RGBA')) for g in puppet.ORDER if out[g][..., 3].any()]
    # leftover (shader) layers follow the group most of their pixels belong to, as in the client
    left = []
    for k, im in leftovers.items():
        if im is None: continue
        a = np.asarray(im)[..., 3] > 0
        best, bn = 'Torso', 0
        for g in puppet.ORDER:
            n = int((a & (out[g][..., 3] > 0)).sum())
            if n > bn: best, bn = g, n
        left.append((best, im))
    print('puppet', info, {k: tuple(round(float(v), 1) for v in p) for k, p in piv.items()}, [g for g, _ in left], file=sys.stderr)
    return groups + left, piv

# facing 1: drawn facing east (dir 2, right arm near); facing -1: west (dir 3, left arm near)
ACTORS = {}
VIEW_ACTORS = {}
for _i, _a in enumerate(ACT):
    _right = _a['facing'] > 0
    ACTORS[_i] = make_actor(*appearance(_i, 2 if _right else 3), near_right=_right)
    # views: e.g. North = back to the camera, its screen-right side is the near side when facing right;
    # turned views: side-on facing the other way
    for _v in {('Turned' if w.get('turned') else w.get('facing', 'North')) for w in _a.get('views') or []}:
        VIEW_ACTORS[(_i, _v)] = make_actor(*appearance(_i, 3 if _right else 2), near_right=not _right) if _v == 'Turned' \
            else make_actor(*appearance(_i, DIRS[_v]), near_right=_right)   # client: forcedNearRight = facingRight

def rotm(deg):
    r = math.radians(deg); c, s = math.cos(r), math.sin(r)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])
def trans(x, y): return np.array([[1, 0, x], [0, 1, y], [0, 0, 1.0]])

def floor():
    im = Image.new('RGBA', (W, H), (52, 54, 60, 255)); d = ImageDraw.Draw(im)
    for i in range(int(X1 - X0) + 2):
        x = int((math.floor(X0) + i + 0.5 - X0) * 32 * S); d.line([(x, 0), (x, H)], fill=(66, 68, 76, 255), width=2)
    for j in range(int(Y1 - Y0) + 2):
        y = int((Y1 - (math.floor(Y0) + j + 0.5)) * 32 * S); d.line([(0, y), (W, y)], fill=(66, 68, 76, 255), width=2)
    return im
FLOOR = floor()
FLASH = (1.0, 0.55, 0.55)
EVENTS = sorted(proto['events'], key=lambda e: e['t'])

def snap(v): return round(v)
PIVS = {i: ACTORS[i][1] for i in ACTORS}
def draw_actor(frame, role, t, shake, real):
    tr = ACT[role]
    mx, my, _ = SV.evaluate(tr['move'], t)
    ex, ey = (mx * 32, my * 32)   # entity position in px (forward = east on screen, so sx = +1 for both roles)
    view = SV.view_at(tr, t)
    layers, piv = VIEW_ACTORS[(role, view)] if view else ACTORS[role]
    pose = SV.solve(proto, role, t, piv, pivs_all=PIVS)
    inh = SV.held_in_hands(tr, t)
    def xf(name):
        o = pose.get(name, (0, 0, 0))
        if name == 'Body': return trans(o[0], o[1]) @ rotm(o[2])
        return trans(snap(o[0]), snap(o[1])) @ rotm(o[2])   # whole-pixel offsets, as the client snaps them
    body = xf('Body')
    flash = any(e.get('flash') and e['_target'] == role and SV.real_time(proto, e['t']) <= real < SV.real_time(proto, e['t']) + 0.12 for e in EVENTS)
    ash = FX.shake(proto, role, real); fl = FX.view_flip_scale(tr, t)   # v12 per-actor impact shake, view-flip squash
    scr = np.array([[S * fl, 0, (round(ex - X0 * 32 + shake[0]) + ash[0]) * S], [0, -S, (round(Y1 * 32 - ey + shake[1]) - ash[1]) * S], [0, 0, 1.0]])
    for g, im in layers:
        if g in ('HeldNear', 'HeldFar') and not inh: continue
        if g == 'Slung' and inh: continue
        if SV.is_hidden(tr, g, t) or SV.is_hidden(tr, 'Body', t): continue   # v13: Body hidden = whole actor (client: alpha 0)
        lay = xf(g)
        C = im.width
        pix = np.array([[1, 0, -C / 2], [0, -1, C / 2], [0, 0, 1.0]])   # image px -> centred y-up
        F = scr @ body @ lay @ pix
        inv = np.linalg.inv(F)
        src = tint(im, tuple(int(c * 255) for c in FLASH)) if flash else im
        out = src.transform((W, H), Image.AFFINE, tuple(inv[:2].flatten()), resample=Image.NEAREST)
        draw_props(frame, tr, role, g, t, pose, scr, body, under=True)   # v12 under: below the part's layers
        frame.alpha_composite(out)
        draw_props(frame, tr, role, g, t, pose, scr, body, under=False)

def draw_props(frame, tr, role, g, t, pose, scr, body, under):
        for pr in tr.get('props') or []:
            if bool(pr.get('under', False)) != under: continue
            if pr.get('part', 'Body') != g and not (g == 'Torso' and pr.get('part', 'Body') == 'Body'): continue
            if not SV.prop_visible(tr, pr, t): continue
            base = (0, 0, 0) if pr.get('part', 'Body') == 'Body' else pose.get(g, (0, 0, 0))
            px_, py_, pr_ = SV.prop_pose(pr, base, t)
            pim, sc, extra = prop_image(pr, role)
            pr_ += extra
            C = pim.width
            pix = np.array([[sc, 0, -C / 2 * sc], [0, -sc, C / 2 * sc], [0, 0, 1.0]])
            F = scr @ body @ trans(round(px_), round(py_)) @ rotm(pr_) @ pix
            inv = np.linalg.inv(F)
            frame.alpha_composite(pim.transform((W, H), Image.AFFINE, tuple(inv[:2].flatten()), resample=Image.NEAREST))

_PROP_IM = {}
def _load(sprite, st):
    k = (sprite, st)
    if k not in _PROP_IM:
        _PROP_IM[k] = Image.open(f"{R}/Textures/{sprite.lstrip('/').removeprefix('Textures/')}/{st}.png").convert('RGBA')
    return _PROP_IM[k]

def prop_item(pr, role):
    """v11 propFromItem: the actor item a prop is bound to ("id" on its own actor, or "role.id")."""
    ref = pr.get('item')
    if not ref: return None
    owner, iid = ref.split('.', 1) if '.' in ref else (None, ref)
    actors = proto['actors']
    cand = [a for a in actors if a.get('role') == owner] if owner else [actors[role] if isinstance(role, int) else
                                                                        next(a for a in actors if a.get('role') == role)]
    for a in cand:
        for it in a.get('items') or []:
            if it.get('id') == iid: return it
    return None

def prop_image(pr, role):
    """(image, scale, extra rotation): a bound item is drawn with its previewSprite (the game uses the real entity's
    sprite), else the authored fallback sprite."""
    it = prop_item(pr, role)
    if it and it.get('previewSprite'):
        return (_load(it['previewSprite'], pr.get('itemState', 'icon')), float(pr.get('itemScale', pr.get('scale', 1))),
                float(pr.get('itemRotation', 0)))
    return _load(pr['sprite'], pr.get('state', 'icon')), float(pr.get('scale', 1)), 0.0

BLOOD_DEFAULT = (128, 0, 0)   # Blood reagent colour (#800000)
def draw_blood(frame, real, shake):
    d = ImageDraw.Draw(frame)
    for i, e in enumerate(EVENTS):
        bl = e.get('blood')
        if not bl: continue
        t0 = SV.real_time(proto, e['t'])
        if real < t0: continue
        role = e['_target']
        if not 0 <= role < N: continue
        te = e['t']
        m = SV.evaluate(ACT[role]['move'], te)
        pose = SV.solve(proto, role, te, PIVS[role], pivs_all=PIVS)
        o = SV.point(pose, bl.get('from', 'Head'), SV.vec(bl.get('at'), (0, 9)))
        col = hexc(bl['color']) if bl.get('color') else BLOOD_DEFAULT
        for x, y, size, shade, alpha in SV.droplets(bl, i, o, real - t0):
            wx, wy = m[0] * 32 + x, m[1] * 32 + y
            px = round(wx - X0 * 32 + shake[0]); py = round(Y1 * 32 - wy + shake[1])
            c = tuple(int(min(255, ch * shade)) for ch in col) + (int(255 * alpha),)
            layer = Image.new('RGBA', (size * S, size * S), c)
            frame.alpha_composite(layer, (int((px - size / 2) * S), int((py - size / 2) * S)))

def _dot(frame, m, x, y, w, h, c, shake, snap, ash=(0, 0), fl=1.0):
    x = x * fl + ash[0]; y = y + ash[1]
    if snap: x, y = round(x), round(y)
    px = m[0] * 32 + x - X0 * 32 + shake[0]; py = Y1 * 32 - (m[1] * 32 + y) + shake[1]
    x0, y0 = int(round((px - w / 2) * S)), int(round((py - h / 2) * S))
    x1, y1 = int(round((px + w / 2) * S)), int(round((py + h / 2) * S))
    if x1 <= x0 or y1 <= y0: return
    col = tuple(int(255 * max(0, min(1, ch))) for ch in c)
    if col[3] <= 0: return
    frame.alpha_composite(Image.new('RGBA', (x1 - x0, y1 - y0), col), (x0, y0)) if 0 <= x0 and 0 <= y0 and x1 <= frame.width and y1 <= frame.height else None

BLOOD_F = tuple(c / 255 for c in BLOOD_DEFAULT) + (1.0,)
def draw_fx(frame, real, at, shake):
    """v12 hit effects (ProcAnimBloodOverlay mirror): anchored spatter / pools / dust, attached bruises / drips, faces."""
    for i, e in enumerate(EVENTS):
        role = e['_target']
        if not e.get('effects') or not 0 <= role < N: continue
        t0 = SV.real_time(proto, e['t'])
        anchored = [(j, fx) for j, fx in enumerate(e['effects']) if fx['type'] in FX.ANCHORED]
        if real < t0 or not anchored: continue
        m = SV.evaluate(ACT[role]['move'], e['t'])
        pose = SV.solve(proto, role, e['t'], PIVS[role], pivs_all=PIVS)
        for j, fx in anchored:
            if real - t0 > FX.life(fx): continue
            o = FX.origin(fx, pose)
            for x, y, w, h, c in FX.particles(fx, i * 16 + j, o, real - t0, BLOOD_F):
                _dot(frame, m, x, y, w, h, c, shake, True)
    for role in range(N):
        tr = ACT[role]; m = SV.evaluate(tr['move'], at)
        pose = None; ash = FX.shake(proto, role, real); fl = FX.view_flip_scale(tr, at)
        for i, e in enumerate(EVENTS):
            if e['t'] > at: break
            if e['_target'] != role or not e.get('effects'): continue
            for j, fx in enumerate(e['effects']):
                if fx['type'] not in ('Bruise', 'Drips'): continue
                if SV.is_hidden(tr, fx.get('part', 'Head'), at): continue
                if fx['type'] == 'Drips' and at - e['t'] > FX.life(fx) + 2: continue
                pose = pose or SV.solve(proto, role, at, PIVS[role], pivs_all=PIVS)
                for x, y, w, h, c in FX.particles(fx, i * 16 + j, FX.origin(fx, pose), at - e['t'], BLOOD_F):
                    _dot(frame, m, x, y, w, h, c, shake, True, ash, fl)
        face, eyes = SV.expression_at(tr, at)
        cfg = FX.face_cfg(tr)
        if cfg['disabled'] or ((face in (None, 'neutral')) and (eyes in (None, 'open'))) or SV.is_hidden(tr, 'Head', at): continue
        fs = 1.0 if tr['facing'] >= 0 else -1.0; front = False
        v = FX._view(tr, at)
        if v and v[0]: fs = -fs
        elif v:
            if v[1] != 'South': continue
            front = True
        pose = pose or SV.solve(proto, role, at, PIVS[role], pivs_all=PIVS)
        for x, y, w, h, c in FX.face_dots(cfg, (face, eyes), pose, fs, front):
            _dot(frame, m, x, y, w, h, c, shake, False, ash, fl)

def draw_stars(frame, at, shake):
    """Dizzy stars (ProcAnimBloodOverlay mirror): pixel crosses orbiting over a dazed actor's head."""
    for role in reversed(range(N)):
        tr = ACT[role]; st = (tr.get('daze') or {}).get('stars')
        if not st: continue
        sts = SV.stars(tr, at)
        if not sts: continue
        m = SV.evaluate(tr['move'], at)
        pose = SV.solve(proto, role, at, PIVS[role], pivs_all=PIVS)
        c0 = SV.point(pose, st.get('from', 'Head'), SV.vec(st.get('at'), (0, 13)))
        col = hexc(st.get('color', '#ffe066'))
        for x, y, size, alpha, front in sts:
            px = round(m[0] * 32 + c0[0] + x - X0 * 32 + shake[0]); py = round(Y1 * 32 - (m[1] * 32 + c0[1] + y) + shake[1])
            k = 1.0 if front else 0.7
            c = tuple(int(ch * k) for ch in col) + (int(255 * alpha),)
            cells = [(0, 0)] + ([(1, 0), (-1, 0), (0, 1), (0, -1)] if size > 1 else [])
            for dx, dy in cells:
                frame.alpha_composite(Image.new('RGBA', (S, S), c), ((px + dx) * S, (py + dy) * S))

def shake_at(t):
    sx = sy = 0.0
    for e in EVENTS:
        s = float(e.get('shake', 0) or 0)
        te = SV.real_time(proto, e['t'])
        if s > 0 and te <= t:
            k = math.exp(-(t - te) * 9) * 0.35 * s * 32
            sx += k * math.cos((t - te) * 60); sy += k * 0.4 * math.sin((t - te) * 47)
    return (sx, sy)

def beat_at(t):
    b = ''
    for e in EVENTS:
        if e.get('beat') and e['t'] <= t + 1e-6: b = e['beat']
    return b
FONT = ImageFont.load_default()
try: FONT = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf', 22)
except Exception: pass

def render(t, label=True):
    """t = real time; poses use the hit-stop warped animation time."""
    fr = FLOOR.copy(); sh = shake_at(t); at = SV.anim_time(proto, min(max(t, -INTRO), TOTAL))
    # y-sorted like the game: the actor further north (higher entity y) is drawn first; equal y: the later actor
    # first (the first actor walks over / in front of the others)
    ys = [SV.evaluate(a['move'], at)[1] for a in ACT]
    def cmp(a, b):
        if abs(ys[a] - ys[b]) <= 1e-6: return b - a
        return -1 if ys[a] > ys[b] else 1
    for role in sorted(range(N), key=functools.cmp_to_key(cmp)):
        draw_actor(fr, role, at, sh, t)
    draw_blood(fr, t, sh)
    draw_fx(fr, t, at, sh)
    draw_stars(fr, at, sh)
    if label:
        d = ImageDraw.Draw(fr)
        d.text((12, 10), f't = {min(t, TOTAL):4.2f} s (anim {at:4.2f})   {beat_at(at)}', fill=(235, 235, 235, 255), font=FONT)
    return fr

def parse_crop(s):
    m = re.fullmatch(r'\s*(-?[\d.]+)\s*-\s*(-?[\d.]+)\s*', s or '')
    if not m: raise SystemExit(f'--crop: expected t0-t1, got {s!r}')
    return float(m.group(1)), float(m.group(2))

if __name__ == '__main__':
    os.makedirs(args.out, exist_ok=True)
    base = os.path.join(args.out, f"{proto['id']}{args.tag}")
    i0, i1 = 0, int((TOTAL + INTRO + 1.2) * FPS)
    c0, c1 = -INTRO, TOTAL + 1.2
    if args.crop:
        c0, c1 = parse_crop(args.crop)
        i0, i1 = max(0, round((c0 + INTRO) * FPS)), min(i1, round((c1 + INTRO) * FPS) + 1)
    if not args.no_video:
        tmp = tempfile.mkdtemp(prefix='procanim-frames-')
        frames = []
        for n, i in enumerate(range(i0, i1)):
            im = render(i / FPS - INTRO); im.save(f'{tmp}/{n:04d}.png'); frames.append(im.convert('RGB').quantize(colors=128, method=Image.MEDIANCUT))
        frames[0].save(base + '-preview.gif', save_all=True, append_images=frames[1:], duration=int(1000 / FPS), loop=0, optimize=True)
        if shutil.which('ffmpeg'):
            subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-framerate', str(FPS), '-i', tmp + '/%04d.png', '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
                            '-vf', 'pad=ceil(iw/2)*2:ceil(ih/2)*2', base + '-preview.mp4'], check=True)
        else: print('ffmpeg not found: no mp4', file=sys.stderr)
        shutil.rmtree(tmp, ignore_errors=True)
    # contact sheet: the events flagged `sheet: true` (anim time -> real time), plus the end
    beats = [(SV.real_time(proto, float(e['t'])) + float(e.get('sheetDelay', 0)), e['beat']) for e in EVENTS if e.get('sheet')]
    if INTRO > 0: beats.insert(0, (-INTRO + 0.25, 'holster'))
    beats.append((TOTAL, 'end'))
    if args.crop: beats = [b for b in beats if c0 - 1e-6 <= b[0] <= c1 + 1e-6] or [(c0, 'start'), (c1, 'end')]
    cols, sc = 2, 0.5
    tw, th = int(W * sc), int(H * sc)
    rows = (len(beats) + cols - 1) // cols
    sheet = Image.new('RGBA', (cols * tw + (cols + 1) * 8, rows * (th + 28) + 8), (24, 24, 28, 255)); d = ImageDraw.Draw(sheet)
    small = FONT
    for i, (t, name) in enumerate(beats):
        im = render(t, label=False).resize((tw, th), Image.NEAREST)
        x, y = 8 + (i % cols) * (tw + 8), 8 + (i // cols) * (th + 28)
        sheet.alpha_composite(im, (x, y + 24)); d.text((x, y), f'{i + 1}. {name}  (t={t:.2f}s)', fill=(235, 235, 235, 255), font=small)
    sheet.save(base + '-contact-sheet.png')
    print('scene', proto['id'], 'from', os.path.relpath(SRC, R), 'frames', i1 - i0, '->', base + '-*')
