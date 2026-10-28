cmd-procanim-desc = Проигрывает процедурную катсцену (прототип procAnimScene) на указанных гуманоидах или останавливает её.
cmd-procanim-help = procanim play <id сцены> <uid актёров в порядке ролей...> [keepdown=true]  |  procanim stop [uid...]
    Без uid stop останавливает все идущие сцены.
cmd-procanim-usage = Использование: procanim play <id сцены> <uid актёров...> [keepdown true/false] или procanim stop [uid...]
cmd-procanim-bad-uid = { $uid } — нет такой сущности.
cmd-procanim-started = Сцена { $scene } запущена: { $actors }.
cmd-procanim-stopped = Остановлено.
cmd-procanim-stopped-all = Все сцены остановлены (освобождено актёров: { $count }).
cmd-procanim-hint-mode = play | stop
cmd-procanim-hint-scene = <id сцены>
cmd-procanim-hint-uid = [uid]
cmd-procanim-hint-actor = <uid: { $role }>

procanim-error-no-proto = Нет такого прототипа procAnimScene.
procanim-error-actor-count = Неверное число актёров; сцене нужны: { $roles }.
procanim-error-same = Все актёры должны быть разными сущностями.
procanim-error-not-humanoid = Все актёры должны быть гуманоидами.
procanim-error-busy = Кто-то из них уже в катсцене.
procanim-error-buckled = Кто-то из них пристёгнут.
procanim-error-other-map = Все актёры должны быть на одной карте.
procanim-error-missing-item = У актёра нет предмета, нужного сцене.
