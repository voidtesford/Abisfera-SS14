cmd-gilbertfinisher-desc = Plays one of Gilbert's phase-1 finisher cutscenes on two humanoids (works for any humanoids).
cmd-gilbertfinisher-help = gilbertfinisher <attacker uid> <victim uid> [variant 1|2|3|4/rage|prototype id] [keepdown=true]  |  gilbertfinisher stop <uid>
    1 = kick combo, 2 = shotgun barrel (bayonet + shot), 3 = ground and pound (arms torn off).
cmd-gilbertfinisher-usage = Usage: gilbertfinisher <attacker uid> <victim uid> [variant 1|2|3|4] [keepdown true/false], or gilbertfinisher stop <uid>
cmd-gilbertfinisher-bad-uid = { $uid } is not an existing entity id.
cmd-gilbertfinisher-bad-variant = Unknown finisher variant { $variant } (use 1, 2, 3, 4/rage or a finisher prototype id).
cmd-gilbertfinisher-no-proto = The finisher prototype is missing.
cmd-gilbertfinisher-same = The attacker and the victim must be different entities.
cmd-gilbertfinisher-not-humanoid = Both entities must be humanoids.
cmd-gilbertfinisher-busy = One of them is already in a finisher.
cmd-gilbertfinisher-buckled = One of them is buckled.
cmd-gilbertfinisher-other-map = Both entities must be on the same map.
cmd-gilbertfinisher-started = Finisher started: { $attacker } -> { $victim }.
cmd-gilbertfinisher-stopped = Finisher stopped on { $uid }.
cmd-gilbertfinisher-hint-attacker = <attacker uid>
cmd-gilbertfinisher-hint-victim = <victim uid>
cmd-gilbertfinisher-hint-variant = [variant] 1 kick combo, 2 shotgun barrel, 3 ground and pound, 4 rage: torn in half (or keepdown)
cmd-gilbertfinisher-hint-keepdown = [keepdown] leave the victim lying at the end

cmd-gilbertgoto-desc = Orders Gilbert to walk to a point (normal navigation: airlocks, tables, his map) and hold / patrol there.
cmd-gilbertgoto-help = gilbertgoto <gilbert uid> <x> <y> [map id | grid uid]  |  gilbertgoto <gilbert uid> here  |  gilbertgoto <gilbert uid> clear
    Coordinates are world (map) coordinates, or local to the grid when a grid uid is given. Without the last argument Gilbert's own map is used.
    He still fights anyone who threatens him on the way, then goes on with the order.
cmd-gilbertgoto-usage = Usage: gilbertgoto <gilbert uid> <x> <y> [map id | grid uid], gilbertgoto <gilbert uid> here, or gilbertgoto <gilbert uid> clear
cmd-gilbertgoto-not-gilbert = { $uid } is not Gilbert.
cmd-gilbertgoto-bad-number = { $value } is not a number.
cmd-gilbertgoto-bad-map = { $value } is neither a map id nor a grid.
cmd-gilbertgoto-other-map = The point must be on Gilbert's map.
cmd-gilbertgoto-no-player = "here" needs an admin with a body.
cmd-gilbertgoto-set = { $gilbert } goes to { $coords }.
cmd-gilbertgoto-cleared = { $gilbert }: movement order cleared.
cmd-gilbertgoto-hint-gilbert = <gilbert uid>
cmd-gilbertgoto-hint-x = <x> | here | clear
cmd-gilbertgoto-hint-y = <y>
cmd-gilbertgoto-hint-map = [map id | grid uid]

cmd-gilberttarget-desc = Orders Gilbert to hunt one target down and kill it (no warnings, no arrest), then return to normal.
cmd-gilberttarget-help = gilberttarget <gilbert uid> <target uid | player name>  |  gilberttarget <gilbert uid> clear
    Gilbert always knows where the target is, chases it and attacks to kill; the target can still get a finisher.
cmd-gilberttarget-usage = Usage: gilberttarget <gilbert uid> <target uid | player name>, or gilberttarget <gilbert uid> clear
cmd-gilberttarget-bad-target = { $value } is neither an entity id nor a player with a body.
cmd-gilberttarget-not-mob = { $target } is not a living mob.
cmd-gilberttarget-self = Gilbert can't hunt himself.
cmd-gilberttarget-set = { $gilbert } hunts { $target }.
cmd-gilberttarget-cleared = { $gilbert }: hunt order cleared.
cmd-gilberttarget-hint-target = <target uid | player name> | clear
cmd-gilbertfinisher-missing-item = Gilbert lacks an item this finisher needs (shotgun / truncheon).
