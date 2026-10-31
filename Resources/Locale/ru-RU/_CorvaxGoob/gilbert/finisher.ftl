cmd-gilbertfinisher-desc = Проигрывает одно из добиваний Гилберта (1-я фаза) на двух гуманоидах (подходит любой гуманоид).
cmd-gilbertfinisher-help = gilbertfinisher <uid атакующего> <uid жертвы> [вариант 1|2|3|4/rage|5/neck|6/heart|id прототипа] [keepdown=true]  |  gilbertfinisher stop <uid>
    1 = серия ударов ногами, 2 = ствол дробовика (штык + выстрел), 3 = добивание на земле (с отрыванием рук).
cmd-gilbertfinisher-usage = Использование: gilbertfinisher <uid атакующего> <uid жертвы> [вариант 1|2|3|4] [keepdown true/false] или gilbertfinisher stop <uid>
cmd-gilbertfinisher-bad-uid = { $uid } — нет такой сущности.
cmd-gilbertfinisher-bad-variant = Неизвестный вариант добивания { $variant } (1, 2, 3, 4/rage или id прототипа).
cmd-gilbertfinisher-no-proto = Прототип добивания не найден.
cmd-gilbertfinisher-same = Атакующий и жертва должны быть разными сущностями.
cmd-gilbertfinisher-not-humanoid = Обе сущности должны быть гуманоидами.
cmd-gilbertfinisher-busy = Кто-то из них уже в добивании.
cmd-gilbertfinisher-buckled = Кто-то из них пристёгнут.
cmd-gilbertfinisher-other-map = Обе сущности должны быть на одной карте.
cmd-gilbertfinisher-started = Добивание запущено: { $attacker } -> { $victim }.
cmd-gilbertfinisher-stopped = Добивание на { $uid } остановлено.
cmd-gilbertfinisher-hint-attacker = <uid атакующего>
cmd-gilbertfinisher-hint-victim = <uid жертвы>
cmd-gilbertfinisher-hint-variant = [вариант] 1 ноги, 2 ствол дробовика, 3 добивание на земле, 4 ярость: разрыв пополам, 5 ярость: отрыв головы, 6 ярость: сердце + раздавленная голова (или keepdown)
cmd-gilbertfinisher-hint-keepdown = [keepdown] оставить жертву лежать в конце

cmd-gilbertgoto-desc = Приказывает Гилберту дойти до точки (обычная навигация: шлюзы, столы, его карта) и держать / патрулировать её.
cmd-gilbertgoto-help = gilbertgoto <uid гилберта> <x> <y> [id карты | uid грида]  |  gilbertgoto <uid гилберта> here  |  gilbertgoto <uid гилберта> clear
    Координаты мировые (карты) или локальные для указанного грида. Без последнего аргумента берётся карта Гилберта.
    По пути он всё равно отвечает на угрозы, а потом продолжает выполнять приказ.
cmd-gilbertgoto-usage = Использование: gilbertgoto <uid гилберта> <x> <y> [id карты | uid грида], gilbertgoto <uid гилберта> here или gilbertgoto <uid гилберта> clear
cmd-gilbertgoto-not-gilbert = { $uid } — не Гилберт.
cmd-gilbertgoto-bad-number = { $value } — не число.
cmd-gilbertgoto-bad-map = { $value } — не id карты и не грид.
cmd-gilbertgoto-other-map = Точка должна быть на карте Гилберта.
cmd-gilbertgoto-no-player = Для "here" у админа должно быть тело.
cmd-gilbertgoto-set = { $gilbert } идёт в { $coords }.
cmd-gilbertgoto-cleared = { $gilbert }: приказ на перемещение снят.
cmd-gilbertgoto-hint-gilbert = <uid гилберта>
cmd-gilbertgoto-hint-x = <x> | here | clear
cmd-gilbertgoto-hint-y = <y>
cmd-gilbertgoto-hint-map = [id карты | uid грида]

cmd-gilberttarget-desc = Приказывает Гилберту выследить и убить цель (без предупреждений и ареста), затем вернуться к обычному поведению.
cmd-gilberttarget-help = gilberttarget <uid гилберта> <uid цели | имя игрока>  |  gilberttarget <uid гилберта> clear
    Гилберт всегда знает, где цель, преследует её и бьёт на поражение; на цель по-прежнему может сработать добивание.
cmd-gilberttarget-usage = Использование: gilberttarget <uid гилберта> <uid цели | имя игрока> или gilberttarget <uid гилберта> clear
cmd-gilberttarget-bad-target = { $value } — не id сущности и не игрок с телом.
cmd-gilberttarget-not-mob = { $target } — не живое существо.
cmd-gilberttarget-self = Гилберт не может охотиться на самого себя.
cmd-gilberttarget-set = { $gilbert } охотится на { $target }.
cmd-gilberttarget-cleared = { $gilbert }: приказ на охоту снят.
cmd-gilberttarget-hint-target = <uid цели | имя игрока> | clear
cmd-gilbertfinisher-missing-item = У Гилберта нет предмета для этого добивания (дробовик / дубинка).
