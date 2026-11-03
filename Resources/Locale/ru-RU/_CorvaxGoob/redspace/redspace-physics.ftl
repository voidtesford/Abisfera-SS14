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
