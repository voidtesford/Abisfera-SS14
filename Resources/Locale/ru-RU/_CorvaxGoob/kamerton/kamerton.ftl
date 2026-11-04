
## Преобразователь кристаллов

kamerton-converter-cell-slot = ячейка
kamerton-converter-busy = Преобразователь занят.
kamerton-converter-full = Лоток кристаллов полон.
kamerton-converter-inserted = Загружено кристаллов: { $count }.
kamerton-converter-redspace-needs-destroy = Редспейс кристаллы принимаются только в режиме «Уничтожение».
kamerton-converter-eject-redspace-first = Сначала извлеките редспейс кристаллы.
kamerton-converter-mode-create = Режим: создание.
kamerton-converter-mode-destroy = Режим: уничтожение.
kamerton-converter-verb-create = Создание
kamerton-converter-verb-destroy = Уничтожение
kamerton-converter-need-crystals = Нужно { $count } блюспейс кристаллов.
kamerton-converter-empty = Нечего перемалывать.
kamerton-converter-no-cell = Нет ячейки.
kamerton-converter-cell-already-charged = Ячейка уже заряжена.
kamerton-converter-not-enough-stable = Недостаточно стабильной энергии: нужно { $need }, есть { $have }.
kamerton-converter-not-enough-unstable = Недостаточно нестабильной энергии: нужно { $need }, есть { $have }.
kamerton-converter-cell-charged = Ячейка заряжена.
kamerton-converter-not-enough-power = Недостаточно энергии машины: нужно { $need }, есть { $have }.
kamerton-converter-not-enough-materials = Нужно по { $count } листов плазмы и урана.
kamerton-converter-examine-mode-create = Режим: [color=#39C6FF]создание[/color].
kamerton-converter-examine-mode-destroy = Режим: [color=#E0304A]уничтожение[/color].
kamerton-converter-examine-crystals = Кристаллы: блюспейс { $bluespace }, редспейс { $redspace } из { $max }.
kamerton-converter-examine-energy = Стабильная энергия: { $stable }, нестабильная: { $unstable }.

kamerton-converter-window-title = Преобразователь кристаллов
kamerton-converter-window-mode = Режим:
kamerton-converter-window-mode-create = Создание
kamerton-converter-window-mode-destroy = Уничтожение
kamerton-converter-window-status = Состояние:
kamerton-converter-window-work-idle = ожидание
kamerton-converter-window-work-creating = синтез редспейс кристалла…
kamerton-converter-window-work-grinding = перемалывание кристаллов…
kamerton-converter-window-work-printing = печать блюспейс кристалла…
kamerton-converter-window-crystals = Кристаллы:
kamerton-converter-window-crystal-count = { $bluespace } BS / { $redspace } RS из { $max }
kamerton-converter-window-stable = Стабильная энергия:
kamerton-converter-window-unstable = Нестабильная энергия:
kamerton-converter-window-power = Энергия машины:
kamerton-converter-window-materials = Плазма / уран (листы):
kamerton-converter-window-materials-value = { $plasma } / { $uranium } (печать: по { $cost })
kamerton-converter-window-cell = Ячейка:
kamerton-converter-window-cell-none = нет
kamerton-converter-window-cell-charged = { $name } (заряжена)
kamerton-converter-window-cell-empty = { $name } (пустая)
kamerton-converter-window-start = Старт
kamerton-converter-window-charge = Зарядить ячейку
kamerton-converter-window-print = Печать кристалла
kamerton-converter-window-eject = Извлечь

## Реактор «Камертон»

kamerton-reactor-slot-cell = Ячейка
kamerton-reactor-popup-collapsed = Реактор схлопнулся.
kamerton-reactor-popup-scram-locked = Аварийная блокировка: запустить реактор пока нельзя.
kamerton-reactor-popup-not-assembled = Реактор не собран: рядом с ядром нет синхронизатора.
kamerton-reactor-popup-scram = АВАРИЙНАЯ ОСТАНОВКА КАМЕРТОНА!
kamerton-reactor-popup-leak = Кожу покалывает странным красным статическим зудом.
kamerton-reactor-popup-collapse = Реактор «Камертон» схлопывается!
kamerton-reactor-examine-temperature = Температура ядра: [color=orange]{ $temp } K[/color] (схлопывание при { $max } K).
kamerton-reactor-examine-power = Выработка: [color=#39C6FF]{ $power } кВт[/color], активных пар: { $pairs }.
kamerton-reactor-examine-running = Реактор [color=lime]работает[/color].
kamerton-reactor-examine-stopped = Реактор [color=gray]остановлен[/color].
kamerton-reactor-examine-ring-ok = Кольцо подавителей [color=lime]замкнуто[/color].
kamerton-reactor-examine-ring-broken = Кольцо подавителей [color=red]разомкнуто[/color]: не хватает { $missing } сегм.
kamerton-reactor-examine-part-linked = Подключено к ядру реактора.
kamerton-reactor-examine-part-unlinked = Не подключено ни к одному ядру реактора.

kamerton-reactor-window-title = Управление реактором «Камертон»
kamerton-reactor-window-status = Состояние:
kamerton-reactor-window-unlinked = нет ядра
kamerton-reactor-window-running = работает
kamerton-reactor-window-stopped = остановлен
kamerton-reactor-window-scrammed = аварийная блокировка ({ $seconds } с)
kamerton-reactor-window-not-assembled = не собран
kamerton-reactor-window-temperature = Температура ядра:
kamerton-reactor-window-power = Общая мощность:
kamerton-reactor-window-pairs = Активные пары:
kamerton-reactor-window-pairs-value = { $pairs } (синхронизаторов: { $syncs })
kamerton-reactor-window-output = Выход ВВ:
kamerton-reactor-window-output-value = отдаётся { $kw } кВт
kamerton-reactor-window-output-none = в кольце нет силового блока
kamerton-reactor-window-ring = Кольцо подавителей:
kamerton-reactor-window-ring-ok = замкнуто
kamerton-reactor-window-ring-broken = разомкнуто, не хватает { $missing }
kamerton-reactor-window-arrays = Топливные массивы
kamerton-reactor-window-array-stable = Стабильный: заряжено { $charged }/{ $slots }, пустых { $empty }, минимум { $minutes } мин
kamerton-reactor-window-array-unstable = Нестабильный: заряжено { $charged }/{ $slots }, пустых { $empty }, минимум { $minutes } мин
kamerton-reactor-window-cooling = Охлаждение (радиаторов на теплолиниях: { $radiators })
kamerton-reactor-window-sink = Теплоотвод { $index }: { $flow } моль/с, { $tin } K → { $tout } K, { $kw } кВт
kamerton-reactor-window-no-sinks = Нет подключённых теплоотводов.
kamerton-reactor-window-hint-unlinked = Поставьте консоль в кольцо подавителей вокруг ядра реактора.
kamerton-reactor-window-hint-hot = Опасно: ядро близко к схлопыванию!
kamerton-reactor-window-hint-ring = Кольцо разомкнуто: вокруг реактора сочится красный космос.
kamerton-reactor-window-start = Пуск
kamerton-reactor-window-stop = Стоп
kamerton-reactor-window-scram = АЗ
