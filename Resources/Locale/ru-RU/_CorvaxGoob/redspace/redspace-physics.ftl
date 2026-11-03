## Физика карт красного космоса (RedspaceMapSystem)
redspace-wave = Вас толкает волна красного космоса!
redspace-weightless = Пол отпускает вас...
redspace-radio-static = Гарнитура шипит помехами.
redspace-hallucination-1 = ...обернись...
redspace-hallucination-2 = ...тебя здесь никогда не было...
redspace-hallucination-3 = ...оно помнит твоё имя...
redspace-hallucination-4 = ...останься. останься. останься...
redspace-hallucination-5 = ...стены дышат...
redspace-hallucination-6 = ...кто-то идёт за тобой...
redspace-hallucination-7 = ...верни...
redspace-hallucination-8 = ...красное тёплое, подойди ближе...
redspace-hallucination-9 = ...ты уже умирал здесь однажды...
redspace-hallucination-10 = ...мы слышим, как ты думаешь...

## Ошейник-подавитель
redspace-collar-full = Слот кристалла уже занят.
redspace-collar-inserted = Вы вставляете блюспейс кристалл в ошейник.
redspace-collar-depleted = Кристалл ошейника рассыпается в пыль. Красный космос снова накрывает вас!
redspace-collar-examine = Заряд подавления: [color=cyan]{ $minutes } мин[/color] (запасных кристаллов: { $crystals }/{ $capacity }).
redspace-collar-examine-empty = [color=red]Нет заряда.[/color] Вставьте блюспейс кристалл.

## Маяк подавитель
redspace-beacon-full = Маяк не вмещает больше { $max } ячеек.
redspace-beacon-inserted = Ячейка загружена ({ $count }/{ $max }).
redspace-beacon-examine = Осталось топлива: [color=cyan]{ $minutes } мин[/color] (ячеек: { $cells }/{ $max }).
redspace-beacon-examine-empty = [color=red]Нет топлива.[/color] Загрузите сверхэнергетическую ячейку.
redspace-beacon-examine-unanchored = [color=yellow]Не закреплён: маяк бездействует.[/color]

## Команды
cmd-redspacemap-desc = Включает или выключает физику красного космоса на карте, либо показывает её состояние.
cmd-redspacemap-help = redspacemap <mapId> [calm|normal|storm|<id пресета>|off] [поле=значение ...]
cmd-redspacemap-usage = Использование: redspacemap <mapId> [пресет|off] [поле=значение ...]
cmd-redspacemap-presets = Пресеты: { $presets }
cmd-redspacemap-no-map = Карта { $map } не существует.
cmd-redspacemap-state-off = Карта { $map }: физика красного космоса выключена.
cmd-redspacemap-state = Карта { $map }: пресет { $preset }, переопределения: { $overrides }.
cmd-redspacemap-off = Физика красного космоса выключена на карте { $map }.
cmd-redspacemap-no-preset = Неизвестный пресет «{ $preset }».
cmd-redspacemap-bad-override = Неверное переопределение «{ $arg }» (нужно поле=значение с известным полем).
cmd-redspacemap-on = Физика красного космоса включена на карте { $map }, пресет { $preset }.
cmd-redspacemap-hint-map = <mapId>
cmd-redspacemap-hint-preset = <пресет|off>
cmd-redspacemap-hint-override = [поле=значение]
cmd-redspacestatus-desc = Показывает, действует ли красный космос в точке или он подавлен.
cmd-redspacestatus-help = redspacestatus [mapId x y]  (без аргументов: ваша позиция)
cmd-redspacestatus-usage = Использование: redspacestatus [mapId x y]
cmd-redspacestatus-not-redspace = { $where }: это не карта красного космоса.
cmd-redspacestatus-suppressed = { $where }: подавлено ({ $by }).
cmd-redspacestatus-exposed = { $where }: под воздействием красного космоса.

## Аномалии красного космоса

redspace-event-start-rift = Пространство рядом с вами трескается и тянет к себе!
redspace-event-start-gravityknot = Всё вокруг начинает медленно кружить вокруг пустой точки.
redspace-event-start-bloodrain = С потолка падают тёплые красные капли.
redspace-event-start-mirrorcorridor = Пространство отражается: право стало левым.
redspace-event-start-timeloop = Вам кажется, что вы здесь уже были...
redspace-event-start-doppelganger = Кто-то очень похожий на вас повторяет ваши движения.
redspace-rift-swallowed = Разлом проглатывает вас и выплёвывает в другом месте!
redspace-blood-rain-slip = Вы поскальзываетесь на крови!
redspace-mirror-enter = Ноги идут не туда!
redspace-timeloop-snap = Пять секунд назад. Снова.
redspace-timeloop-broken = Петля времени рвётся.
redspace-doppelganger-hit = Двойник бьёт вас!
redspace-doppelganger-vanish = Двойник рассыпается помехами.
redspace-whispers-local = [italic][bold]{ $name }[/bold] шепчет откуда-то издалека: "{ $message }"[/italic]
redspace-whispers-radio = [color={ $color }]{ $channel } [bold]{ $name }[/bold] говорит сквозь помехи, "{ $message }"[/color]
redspace-mutation-voice = Ваш голос звучит чужим.
redspace-mutation-slow = Тело становится тяжёлым и непослушным.
redspace-mutation-eye = На лбу что-то открывается и смотрит.
redspace-mutation-gone = Изменения в теле проходят.
redspace-gun-jam-start = { CAPITALIZE($gun) } клинит!
redspace-gun-jammed = Оружие заклинило!

redspace-event-fail-target = цели нет
redspace-event-fail-not-redspace = цель не в красном космосе
redspace-event-fail-immune = цель невосприимчива
redspace-event-fail-dead = цель мертва
redspace-event-fail-suppressed = рядом с целью действует подавление
redspace-event-fail-busy = такое событие уже идёт или их слишком много
redspace-event-fail-type = неизвестное событие
redspace-event-fail-not-player = цель не игрок
redspace-event-fail-no-speech = нечего нашёптывать: никто ещё не говорил

cmd-redspaceevent-desc = Запускает аномальное событие красного космоса рядом с игроком.
cmd-redspaceevent-help = redspaceevent <rift|timeloop|doppelganger|bloodrain|mirror|whispers|gravityknot> [цель]
cmd-redspaceevent-usage = Использование: redspaceevent <тип> [цель]. Типы: { $types }
cmd-redspaceevent-bad-target = Цель «{ $target }» не найдена.
cmd-redspaceevent-failed = Событие { $type } не запущено: { $reason }.
cmd-redspaceevent-started = Событие { $type } запущено для { $target } ({ $spawned }).
cmd-redspaceevent-hint-type = <тип>
cmd-redspaceevent-hint-target = [цель]
cmd-redspacestatus-next-event = Следующее событие через: { $seconds } с.
cmd-redspacestatus-active-events = Идущие события ({ $count }): { $events }
cmd-redspacestatus-immune = Вы невосприимчивы к красному космосу.
cmd-redspacestatus-exposure = Облучение: { $seconds } с, мутации: { $mutations }
