## Redspace map physics (RedspaceMapSystem)
redspace-wave = A wave of redspace shoves you!
redspace-weightless = The ground lets go of you...
redspace-radio-static = Your headset hisses with static.
redspace-hallucination-1 = ...turn around...
redspace-hallucination-2 = ...you were never here...
redspace-hallucination-3 = ...it remembers your name...
redspace-hallucination-4 = ...stay. stay. stay...
redspace-hallucination-5 = ...the walls are breathing...
redspace-hallucination-6 = ...someone is walking behind you...
redspace-hallucination-7 = ...give it back...
redspace-hallucination-8 = ...the red is warm, come closer...
redspace-hallucination-9 = ...you already died here once...
redspace-hallucination-10 = ...we can hear you thinking...

## Suppressor collar
redspace-collar-full = The crystal slot is already full.
redspace-collar-inserted = You slot a bluespace crystal into the collar.
redspace-collar-depleted = The collar's crystal crumbles to dust. Redspace floods back in!
redspace-collar-examine = Suppression charge: [color=cyan]{ $minutes } min[/color] (spare crystals: { $crystals }/{ $capacity }).
redspace-collar-examine-empty = [color=red]No charge.[/color] Insert a bluespace crystal.

## Suppressor beacon
redspace-beacon-full = The beacon can't hold more than { $max } cells.
redspace-beacon-inserted = Cell loaded ({ $count }/{ $max }).
redspace-beacon-examine = Fuel left: [color=cyan]{ $minutes } min[/color] (cells: { $cells }/{ $max }).
redspace-beacon-examine-empty = [color=red]No fuel.[/color] Load a super-energy cell.
redspace-beacon-examine-unanchored = [color=yellow]Not anchored: the beacon is idle.[/color]

## Commands
cmd-redspacemap-desc = Turns redspace physics on or off for a map, or shows its state.
cmd-redspacemap-help = redspacemap <mapId> [calm|normal|storm|<preset id>|off] [field=value ...]
cmd-redspacemap-usage = Usage: redspacemap <mapId> [preset|off] [field=value ...]
cmd-redspacemap-presets = Presets: { $presets }
cmd-redspacemap-no-map = Map { $map } does not exist.
cmd-redspacemap-state-off = Map { $map }: redspace physics is off.
cmd-redspacemap-state = Map { $map }: preset { $preset }, overrides: { $overrides }.
cmd-redspacemap-off = Redspace physics disabled on map { $map }.
cmd-redspacemap-no-preset = Unknown preset "{ $preset }".
cmd-redspacemap-bad-override = Bad override "{ $arg }" (expected field=value with a known field).
cmd-redspacemap-on = Redspace physics enabled on map { $map } with preset { $preset }.
cmd-redspacemap-hint-map = <mapId>
cmd-redspacemap-hint-preset = <preset|off>
cmd-redspacemap-hint-override = [field=value]
cmd-redspacestatus-desc = Shows whether a point is exposed to redspace or suppressed.
cmd-redspacestatus-help = redspacestatus [mapId x y]  (no arguments: your own position)
cmd-redspacestatus-usage = Usage: redspacestatus [mapId x y]
cmd-redspacestatus-not-redspace = { $where }: not a redspace map.
cmd-redspacestatus-suppressed = { $where }: suppressed by { $by }.
cmd-redspacestatus-exposed = { $where }: exposed to redspace.

## Redspace anomalies

redspace-event-start-rift = Space next to you cracks open and pulls!
redspace-event-start-gravityknot = Everything around starts to slowly circle an empty point.
redspace-event-start-bloodrain = Warm red drops fall from the ceiling.
redspace-event-start-mirrorcorridor = Space is reflected: right became left.
redspace-event-start-timeloop = You feel you have been here before...
redspace-event-start-doppelganger = Someone very much like you repeats your moves.
redspace-rift-swallowed = The rift swallows you and spits you out somewhere else!
redspace-blood-rain-slip = You slip on the blood!
redspace-mirror-enter = Your legs go the wrong way!
redspace-timeloop-snap = Five seconds ago. Again.
redspace-timeloop-broken = The time loop snaps.
redspace-doppelganger-hit = The doppelganger hits you!
redspace-doppelganger-vanish = The doppelganger falls apart into static.
redspace-whispers-local = [italic][bold]{ $name }[/bold] whispers from somewhere far away: "{ $message }"[/italic]
redspace-whispers-radio = [color={ $color }]{ $channel } [bold]{ $name }[/bold] says through static, "{ $message }"[/color]
redspace-mutation-voice = Your voice sounds like someone else's.
redspace-mutation-slow = Your body grows heavy and clumsy.
redspace-mutation-eye = Something opens on your forehead and looks.
redspace-mutation-gone = The changes in your body fade.
redspace-gun-jam-start = { CAPITALIZE(THE($gun)) } jams!
redspace-gun-jammed = The gun is jammed!

redspace-event-fail-target = no target
redspace-event-fail-not-redspace = the target is not in redspace
redspace-event-fail-immune = the target is immune
redspace-event-fail-dead = the target is dead
redspace-event-fail-suppressed = redspace is suppressed next to the target
redspace-event-fail-busy = this event is already running or there are too many
redspace-event-fail-type = unknown event
redspace-event-fail-not-player = the target is not a player
redspace-event-fail-no-speech = nothing to whisper: nobody has spoken yet

cmd-redspaceevent-desc = Starts a redspace anomaly event next to a player.
cmd-redspaceevent-help = redspaceevent <rift|timeloop|doppelganger|bloodrain|mirror|whispers|gravityknot> [target]
cmd-redspaceevent-usage = Usage: redspaceevent <type> [target]. Types: { $types }
cmd-redspaceevent-bad-target = Target "{ $target }" not found.
cmd-redspaceevent-failed = Event { $type } not started: { $reason }.
cmd-redspaceevent-started = Event { $type } started for { $target } ({ $spawned }).
cmd-redspaceevent-hint-type = <type>
cmd-redspaceevent-hint-target = [target]
cmd-redspacestatus-next-event = Next event in: { $seconds } s.
cmd-redspacestatus-active-events = Running events ({ $count }): { $events }
cmd-redspacestatus-immune = You are immune to redspace.
cmd-redspacestatus-exposure = Exposure: { $seconds } s, mutations: { $mutations }
