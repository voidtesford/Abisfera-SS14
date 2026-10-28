# ProcAnim — procedural cutscene library

## Кратко (RU)

ProcAnim — переиспользуемая библиотека процедурных катсцен для гуманоидов (не привязана к Гилберту).
Сцена описывается прототипом `procAnimScene` в YAML: список **актёров** (ролей) с ключами движения корня,
поворотами частей тела (рука/предплечье, бедро/голень, голова, торс), IK-целями (кисть/стопа тянется к кости
другого актёра), ограничениями суставов, взглядом (look-at), реквизитом (props) и **событиями** на таймлайне
(урон, ампутация, убийство, звук, кровь, декаль, тряска, хит-стоп, попап, убрать/достать оружие).

* Сервер (`ProcAnimSystem`) блокирует актёров, проверяет условия, держит их живыми, применяет события, в конце
  наносит `finalDamage` и снимает блокировки (в т.ч. при `Stop`, удалении актёра или выходе за карту).
* Клиент (`ProcAnimVisualsSystem`) каждый кадр считает позу (`ProcAnimTimeline.Solve`) и двигает слои
  «марионетки», нарезанной из реальных спрайтов (любая раса, любая одежда; `ProcAnimPuppet`).
* Админ-команды: `procanim play <scene> <uid...>` (uid в порядке ролей), `procanim stop [uid]`.
* Превью без игры: `python3 Tools/_CorvaxGoob/procanim/preview.py <scene-id> [--fps 30] [--crop t0-t1]` — тот же
  YAML и та же математика, на выходе mp4, gif и контакт-лист.
* Реальные предметы (v11): `items` привязывают сцену к настоящим сущностям актёра (дубинка с пояса / из сумки,
  дробовик) — реквизит `props` с `item:` рисуется спрайтом самого предмета, предмет берётся в руку в `draw` и
  возвращается туда, откуда взят, в `return`; без обязательного предмета сцена не стартует
  (`procanim-error-missing-item`). `stowHands` перед сценой убирает содержимое рук (быстрый жест на каждый
  предмет: на спину / пояс / в сумку, в крайнем случае на пол) и после сцены возвращает всё в те же руки.
* Сцена строится в экранной системе координат: на повёрнутом гриде / с повёрнутой камерой «вперёд» — это экранное
  «вправо», так что катсцена всегда выглядит как задумано.
* Пример сцены — `example_punch.yml` (удар в лицо), добивания Гилберта — `../Gilbert/finisher.yml`.

## Architecture

| Layer | File | Role |
|---|---|---|
| Data | `Content.Shared/_CorvaxGoob/ProcAnim/ProcAnimScenePrototype.cs` | `procAnimScene` prototype, actors, tracks, events; resolves role names to actor indices |
| Math | `Content.Shared/_CorvaxGoob/ProcAnim/ProcAnimTimeline.cs` | easing, key evaluation, procedural layers, joint limits, 2-bone IK, look-at, hit-stop time warp, props, blood droplets — pure functions shared by server, client, tests and (mirrored 1:1) by the preview |
| Validation | `Content.Shared/_CorvaxGoob/ProcAnim/ProcAnimValidation.cs` | `ProcAnimValidation.Validate(scene)` → list of authoring errors (also printed by `procanim play`, enforced by tests) |
| State | `ProcAnimComponent.cs`, `SharedProcAnimSystem.cs`, `ProcAnimEvents.cs` | networked per-actor state (scene, actor index, all actors, start time, forward frame…) |
| Server | `Content.Server/_CorvaxGoob/ProcAnim/ProcAnimSystem.cs`, `ProcAnimCommand.cs` | `TryPlay(scene, actors, out error)`, `Stop(uid)`, `StopAll()`; locks movement/actions, holds alive/crit, applies events, raises `ProcAnimEventFiredEvent` (on the target) and `ProcAnimEndedEvent` (on every actor) |
| Client | `Content.Client/_CorvaxGoob/ProcAnim/*` | puppet split (`ProcAnimPuppet`), per-frame posing, props, shake/flash, blood/stars overlay, `ProcAnimExpressionChangedEvent` |
| Preview | `Tools/_CorvaxGoob/procanim/{preview,solve,puppet}.py` | offline renderer: mp4 / gif / contact sheet |

Game code uses scenes through the server API (e.g. `GilbertFinisherSystem` is a thin wrapper that picks a
`GilbertFinisher*` scene and calls `ProcAnimSystem.TryPlay`). Gameplay hooks beyond the built-in event types:
subscribe to `ProcAnimEventFiredEvent` (filter by `Scene` / `Beat`) and `ProcAnimEndedEvent`.

**Frame and units.** Every actor is authored in a shared *forward frame*: +x points from the first actor towards
the second one (chosen at start: the free direction with `requiredRoom` tiles of floor), +y is up. `move` keys are
in tiles relative to the first actor's start position; part offsets / pivots / IK targets are in sprite px
(32 px = 1 tile). Rotations are degrees, counter-clockwise in the forward frame; joint limits are expressed
"+ = swung towards the actor's facing". An actor with `facing: -1` faces −x (towards the first actor).
The layout is chosen in *screen space*: on a rotated grid / camera the forward axis is a screen direction (the
lead player's eye rotation at start is stored as `View`), so the side view always reads left↔right on screen.

**Real items (v11).** `items` bind an actor to entities it carries; `props` with `item:` draw that entity's own
sprite; `stowHands` empties the hands first. Order at start: bind items (a missing `required` item refuses the
scene) → *pre-beat* (one `stowHands.duration` reach per item put away or drawn, before `intro`) → intro → timeline.

**Solve order per actor and frame:** keys → walk cycle → drag (secondary motion) → daze → idle → joint limits →
chain (child segments carried by their parents) → IK (then limits + chain again) → look-at.

## Authoring

```yaml
- type: procAnimScene
  id: MyScene
  length: 1.6          # s, hit-stops come on top
  intro: 0             # s of holster beat (anim time -intro..0), played only if a holstering actor holds something
  requiredRoom: 1.2    # tiles of free floor needed in front of the first actor
  actors:
  - role: attacker     # roles are referenced by events / look / IK (case-insensitive)
    facing: 1
    move: [{t: 0, x: 0}, {t: 0.5, x: 0.2, ease: In}]
    parts:
      ArmNear:
        pivot: 0, 5    # YAML pivot; at runtime the mask-derived puppet joint wins (except Body)
        keys: [{t: 0, rot: 10}, {t: 0.3, rot: -30, ease: BackOut}]
    ik:
    - {limb: ArmNear, relativeTo: victim.Head, at: '0, 3', weights: [{t: 0.3, x: 0}, {t: 0.48, x: 1}]}
  - role: victim
    facing: -1
    move: [{t: 0, x: 0.7}]
  events:
  - {t: 0.48, target: victim, beat: punch, hitStop: 0.06, damage: [{part: Head, damage: {types: {Blunt: 8}}}]}
```

See `example_punch.yml` for a complete scene. Workflow: edit YAML → `preview.py <id>` → look at the contact
sheet / gif → in game `procanim play <id> <uid1> <uid2>` (validation warnings are printed first).

### Keys

A key is `{t, x, y, rot, ease}` (missing = 0, `ease` defaults to `InOut`); the ease applies to the segment that
*ends* at the key. `smooth: true` on an actor turns its default (`InOut`) part segments into a monotone cubic
spline through the keys. Times must be ascending (validated).

### Easings

`Linear`, `In` (cubic), `Out` (cubic), `InOut` (cubic, default), `Step` (jumps at the key), `BackIn`, `BackOut`,
`BackInOut` (overshoot), `ElasticIn`, `ElasticOut`, `HitStop` (frozen for the first 35 % of the segment, then
snaps in with a cubic out).

### Bones

`Body Head ArmNear ArmFar LegNear LegFar ArmNearLow ArmFarLow LegNearLow LegFarLow ArmNearHand ArmFarHand
LegNearFoot LegFarFoot HeldNear HeldFar`. *Near* = the side towards the camera. Limbs have two segments (upper
arm / forearm + hand, thigh / shin + shoe): `*Hand` / `*Foot` stay rigid (no wrist / ankle), `Held*` carry the
in-hand items.

### Scene keys

| Key | Meaning |
|---|---|
| `id`, `length`, `intro`, `requiredRoom` | see above |
| `actors` | list of actors, in the order the entities are passed to `TryPlay` / `procanim play` |
| `events` | timeline events (ascending `t`) |

### Actor keys

| Key | Meaning |
|---|---|
| `role` | unique name (required) |
| `facing` | `1` faces +x, `-1` faces −x |
| `move` | root position keys (tiles, forward frame); `rot` turns the whole sprite |
| `parts` | `{Bone: {pivot, keys}}` per-bone offsets (px) / rotations |
| `joints` | joint limit override: `knee`, `elbow`, `hip`, `shoulder` as `'min, max'` (deg), `backViewBend` |
| `ik` | 2-bone IK effectors: `limb` (ArmNear/ArmFar/LegNear/LegFar), `relativeTo` (`role.Bone`, `role` or omitted = own sprite frame), `at` (rest-pose point on that bone), `keys` (extra px offset), `weight` / `weights` (0..1 blend over keyed pose), `tip` (end-effector point, default the hand/foot pivot), `bend` (`Auto`/`Positive`/`Negative`) |
| `look` | head look-at: `actor` (default: the other actor), `target` bone, `at` / `targetAt`, `max`, `neck`, `weight` or `keys` (x = weight) |
| `props` | sprites attached to a bone: `sprite` (rsi), `state`, `start`, `end`, `part`, `keys` (px/deg relative to the bone), `scale` |
| `props[].item` | v11 `propFromItem`: `id` of an own `items` entry or `role.id` of another actor's; drawn with the real item's RSI (`itemState`, default `icon`, else its first layer state), `itemRotation` (deg, added) and `itemScale` (replaces `scale`); `sprite` / `state` stay as the fallback (item gone, or no sprite) |
| `items` | real items: `{id, match: {prototypes, tags, components}, from: [hands, belt, back...] (default anywhere: hands, worn slots, storages 3 deep), required (true), draw: t, return: t, wield, previewSprite}`. Bound at start. Without `draw` the item is taken into the hand during the pre-beat (if not held); `draw` takes it into the prop's hand at `t`, `return` puts it back in its source slot / container (re-wielding what had to be unwielded); any not returned go back at the end. `ProcAnimSystem.HasRequiredItems(scene, role, uid)` checks without playing |
| `stowHands` | empty the hands before the scene: `keep` (matches left in hand), `rules` (`{match, slots}` first match wins), `slots` (default `back, belt, pocket1, pocket2`; a worn storage takes the item, an empty slot is equipped), `duration` (0.32 s per item), `grab` (0.15 s: when in the reach the item moves), `keys` (the reach pose: `Bone: [keys]` over `0..duration`, mirrored onto the hand used), `sound`, `restore` (true: put back into the same hands afterwards, toggles such as a raised shield re-activated). Items nothing takes are dropped at the feet as the last resort; bound items without `draw` and `keep` matches stay in hand |
| `views` | `{start, end, facing: North/South/East/West}` or `turned: true` — draw another direction for a while |
| `hide` | `{t, parts: [...]}` hide bones (and everything they carry) from `t` |
| `walk`, `idle`, `drag`, `daze` | procedural layers (walk cycle from the `move` speed, breathing, secondary lag, concussion sway; `daze.stars` draws dizzy stars) |
| `smooth` | spline the default-eased part keys |
| `expressions` | STUB face / eye layer: `{t, face, eyes}` step keys → `ProcAnimExpressionChangedEvent` on the client (nothing draws them yet) |
| `holsterHeldItems`, `holsterStates`, `holsterAt`, `unholsterAt` | holster held items onto the back during the scene (the `holster` / `unholster` events set the times) |
| `flight` | `{start, end}` — the root flies through the air: the landing spot is checked for walls |
| `standUp` | stand the actor up at the start |
| `knockDown` | leave the actor lying after the scene |
| `holdAlive` / `holdCrit` | the actor can't die / leave crit while the scene plays |
| `finalDamage` | damage applied when the scene completes (topped up so the actor ends dead if it was lethal) |

### Events

`t` (anim time), `target` (role; default: the second actor), `beat` (name, shown in previews / raised to
gameplay), plus any combination of:

| Key | Effect |
|---|---|
| `damage` | `[{part, damage, ignoreArmor}]` damage to a body part |
| `amputate` | `[TargetBodyPart...]` cut off parts |
| `kill` | kill the target |
| `sound` | sound played at the target |
| `blood` | droplet burst (`from` bone, `at`, `count`, `speed`, `angle`, `gravity`, `floor`, `life`, `color`…) |
| `decal` | blood decal (`id`, `from`, `at`, `color`) |
| `shake` | camera shake strength |
| `flash` | red flash of the target sprite |
| `hitStop` | freeze the whole scene for this many seconds (time warp, all actors) |
| `popup` | `{text: <locale id>, type}` popup over the target |
| `holster` / `unholster` | move held items to the back / back to the hands |
| `sheet`, `sheetDelay` | include this beat in the preview contact sheet |

Final damage is not an event: set `finalDamage` on the actor.

## Validation

`ProcAnimValidation.Validate(scene)` checks lengths, roles (unique, non-empty), facing, ascending key times in every
track, prop / view intervals, look / IK references (`role.Bone`, limbs), event targets and order, event times inside
`-intro..length`. `procanim play` prints the problems as warnings; `ProcAnimLibraryTest.ValidationOfAllScenes`
fails on any problem in a shipped scene.

## Preview

```
python3 Tools/_CorvaxGoob/procanim/preview.py --list
python3 Tools/_CorvaxGoob/procanim/preview.py ProcAnimExamplePunch --out /tmp/prev
python3 Tools/_CorvaxGoob/procanim/preview.py GilbertFinisherPhase1 --fps 30 --crop 0.5-3 --out /tmp/prev
```

Options: `--actor ROLE=gilbert|human` (appearance preset), `--empty-hands`, `--no-video` (sheet only),
`--tag`, `--res`. Needs python3 + numpy + pillow + pyyaml, and ffmpeg for mp4. The preview mirrors the client
1:1 (same puppet split, pose maths, pixel snapping, draw order), so a scene that looks right there looks the same
in game; the only approximations are the clothing presets and the floor.

## Limitations

* Real items: the server lays the scene out in the lead player's camera frame (other viewers with a different eye
  rotation see the same world motion); a two-handed item is unwielded while a `draw` item takes its hand and
  re-wielded on `return`; `match.prototypes` matches parents via `EnumerateParents`, which skips abstract parents;
  the preview draws `items` with `previewSprite` and doesn't show the stow pre-beat.

* Humanoid rig only (head, torso, two-segment limbs split from the humanoid sprite layers); side view with
  optional forced N/S/E/W views. Non-humanoids are refused (`procanim-error-not-humanoid`).
* The expression / eye layer is a stub (keys and event only).
* Actors are placed relative to the first actor's start position; scenes don't path-find, they only pick the
  direction with enough free floor and clamp the motion to it.
