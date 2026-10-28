cmd-procanim-desc = Plays a procedural cutscene (procAnimScene prototype) on the given humanoids, or stops one.
cmd-procanim-help = procanim play <scene id> <actor uids in role order...> [keepdown=true]  |  procanim stop [uid...]
    Without a uid, stop ends every playing scene.
cmd-procanim-usage = Usage: procanim play <scene id> <actor uids...> [keepdown true/false], or procanim stop [uid...]
cmd-procanim-bad-uid = { $uid } is not an existing entity id.
cmd-procanim-started = Scene { $scene } started: { $actors }.
cmd-procanim-stopped = Stopped.
cmd-procanim-stopped-all = Stopped every scene ({ $count } actors released).
cmd-procanim-hint-mode = play | stop
cmd-procanim-hint-scene = <scene id>
cmd-procanim-hint-uid = [uid]
cmd-procanim-hint-actor = <uid: { $role }>

procanim-error-no-proto = There is no such procAnimScene prototype.
procanim-error-actor-count = Wrong number of actors; the scene needs: { $roles }.
procanim-error-same = Every actor must be a different entity.
procanim-error-not-humanoid = Every actor must be a humanoid.
procanim-error-busy = One of them is already in a cutscene.
procanim-error-buckled = One of them is buckled.
procanim-error-other-map = Every actor must be on the same map.
procanim-error-missing-item = The actor lacks an item the scene needs.
