redspace-gate-window-title = Euclidean gate console
redspace-gate-window-status-open = Portal is OPEN
redspace-gate-window-status-closed = Portal is closed
redspace-gate-window-target-zone = Target zone:
redspace-gate-window-map-unknown = unavailable
redspace-gate-window-disk = Coordinates disk:
redspace-gate-window-disk-none = none (current map)
redspace-gate-window-disk-ok = inserted
redspace-gate-window-disk-invalid = inserted, map unavailable
redspace-gate-window-eject = Eject
redspace-gate-window-gate-position = Gate position:
redspace-gate-window-use-gate-position = Use gate position
redspace-gate-window-bluespace-rack = Bluespace rack:
redspace-gate-window-redspace-sockets = Redspace sockets:
redspace-gate-window-bluespace = Bluespace crystals: {$count}
redspace-gate-window-redspace = Redspace crystals: {$count}
redspace-gate-window-open = Open portal
redspace-gate-window-close-portal = Close portal
redspace-gate-window-close = Close

redspace-gate-examine-fuel = Rack: [color=#5FA8FF]{$bluespace}/{$bluespaceCapacity}[/color] bluespace. Gate: [color=#E0304A]{$redspace}/{$redspaceCapacity}[/color] redspace.

redspace-gate-fuel-inserted-bluespace = Loaded {$amount} bluespace {$amount ->
    [one] crystal
   *[other] crystals
} into the rack ({$count}/{$capacity}).
redspace-gate-fuel-inserted-redspace = Socketed {$amount} redspace {$amount ->
    [one] crystal
   *[other] crystals
} into the gate ({$count}/{$capacity}).
redspace-gate-fuel-full-bluespace = The bluespace rack is full.
redspace-gate-fuel-full-redspace = All of the gate's redspace sockets are full.
redspace-gate-fuel-depleted = The gate sputters out of fuel and the portal collapses!

redspace-gate-fail-already-open = The portal is already open.
redspace-gate-fail-invalid-coordinates = Invalid coordinates.
redspace-gate-fail-no-map = The target map does not exist.
redspace-gate-fail-map-unavailable = The target map is unreachable.
redspace-gate-fail-too-close = The target is too close to the gate.
redspace-gate-fail-obstructed = The target point is obstructed.
redspace-gate-fail-no-fuel = The gate has no fuel.

redspace-gate-flatpack-examine = Use a [color=yellow]multitool[/color] on it to unfold the gate.
redspace-gate-flatpack-examine-unfolding = It is unfolding!
redspace-gate-flatpack-unfolding = The flatpack starts to unfold...
redspace-gate-flatpack-in-container = Put the flatpack down first.
redspace-gate-flatpack-no-grid = It needs solid ground to unfold on.
redspace-gate-flatpack-no-room = There's no room to unfold it here.

redspace-gate-window-disk-coordinates = inserted, coordinates {$x}, {$y}
redspace-gate-disk-examine = The disk stores Euclidean gate coordinates: [color=#E0304A]X {$x}, Y {$y}[/color], map "{$map}" (ID {$mapId}).
redspace-gate-disk-printed = The gate crackles and prints a coordinates disk.

cmd-ftldiskgate-desc = Makes a Euclidean gate print a coordinates disk (like ftldisk) with stored coordinates: activating the gate with it opens the portal right there.
cmd-ftldiskgate-help = ftldiskgate <gate id> <map id> <x> <y>  (coordinates may also be x,y)
cmd-ftldiskgate-usage = Usage: ftldiskgate <gate id> <map id> <x> <y>  (or <x,y>)
cmd-ftldiskgate-no-gate = { $gate } is not a Euclidean gate (expected the gate's entity id).
cmd-ftldiskgate-no-map = Map { $map } does not exist (expected a MapId).
cmd-ftldiskgate-map-not-init = Map { $map } is not initialized. Initialize it first.
cmd-ftldiskgate-map-paused = Map { $map } is paused. Unpause it first.
cmd-ftldiskgate-bad-coordinates = Invalid coordinates "{ $coords }". Give two numbers: x y or x,y.
cmd-ftldiskgate-out-of-range = Coordinates out of range (at most { $max } in absolute value).
cmd-ftldiskgate-success = Gate { $gate } printed disk { $disk }: map { $map }, coordinates { $x }, { $y }.
cmd-ftldiskgate-hint-gate = gate id
cmd-ftldiskgate-hint-map = map id (MapId)
cmd-ftldiskgate-hint-x = x (or x,y)
cmd-ftldiskgate-hint-y = y
