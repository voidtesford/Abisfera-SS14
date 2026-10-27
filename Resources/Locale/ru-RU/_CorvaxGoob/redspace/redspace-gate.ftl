redspace-gate-window-title = Консоль Евклидовых врат
redspace-gate-window-status-open = Портал ОТКРЫТ
redspace-gate-window-status-closed = Портал закрыт
redspace-gate-window-target-zone = Целевая зона:
redspace-gate-window-map-unknown = недоступна
redspace-gate-window-disk = Диск координат:
redspace-gate-window-disk-none = нет (текущая карта)
redspace-gate-window-disk-ok = вставлен
redspace-gate-window-disk-invalid = вставлен, карта недоступна
redspace-gate-window-eject = Извлечь
redspace-gate-window-gate-position = Позиция врат:
redspace-gate-window-use-gate-position = Взять позицию врат
redspace-gate-window-bluespace-rack = Блюспейс стойка:
redspace-gate-window-redspace-sockets = Редспейс гнёзда:
redspace-gate-window-bluespace = Блюспейс кристаллы: {$count}
redspace-gate-window-redspace = Редспейс кристаллы: {$count}
redspace-gate-window-open = Открыть портал
redspace-gate-window-close-portal = Закрыть портал
redspace-gate-window-close = Закрыть

redspace-gate-examine-fuel = Стойка: [color=#5FA8FF]{$bluespace}/{$bluespaceCapacity}[/color] блюспейс. Врата: [color=#E0304A]{$redspace}/{$redspaceCapacity}[/color] редспейс.

redspace-gate-fuel-inserted-bluespace = Загружено блюспейс кристаллов в стойку: {$amount} ({$count}/{$capacity}).
redspace-gate-fuel-inserted-redspace = Вставлено редспейс кристаллов во врата: {$amount} ({$count}/{$capacity}).
redspace-gate-fuel-full-bluespace = Блюспейс стойка заполнена.
redspace-gate-fuel-full-redspace = Все редспейс гнёзда врат заняты.
redspace-gate-fuel-depleted = У врат кончилось топливо, и портал схлопывается!

redspace-gate-fail-already-open = Портал уже открыт.
redspace-gate-fail-invalid-coordinates = Неверные координаты.
redspace-gate-fail-no-map = Целевая карта не существует.
redspace-gate-fail-map-unavailable = Целевая карта недоступна.
redspace-gate-fail-too-close = Цель слишком близко к вратам.
redspace-gate-fail-obstructed = Целевая точка заблокирована.
redspace-gate-fail-no-fuel = У врат нет топлива.

redspace-gate-flatpack-examine = Используйте [color=yellow]мультитул[/color], чтобы развернуть врата.
redspace-gate-flatpack-examine-unfolding = Разворачивается!
redspace-gate-flatpack-unfolding = Плоскоблок начинает разворачиваться...
redspace-gate-flatpack-in-container = Сначала положите плоскоблок.
redspace-gate-flatpack-no-grid = Для разворачивания нужна твёрдая поверхность.
redspace-gate-flatpack-no-room = Здесь недостаточно места для разворачивания.

redspace-gate-window-disk-coordinates = вставлен, координаты {$x}, {$y}
redspace-gate-disk-examine = На диске записаны координаты Евклидовых врат: [color=#E0304A]X {$x}, Y {$y}[/color], карта «{$map}» (ID {$mapId}).
redspace-gate-disk-printed = Врата с треском печатают диск координат.

cmd-ftldiskgate-desc = Заставляет Евклидовы врата распечатать диск координат (как ftldisk) с записанными координатами: при активации врат с ним портал сразу открывается в этой точке.
cmd-ftldiskgate-help = ftldiskgate <id врат> <id карты> <x> <y>  (координаты можно и так: x,y)
cmd-ftldiskgate-usage = Использование: ftldiskgate <id врат> <id карты> <x> <y>  (или <x,y>)
cmd-ftldiskgate-no-gate = { $gate } — это не Евклидовы врата (нужен id сущности врат).
cmd-ftldiskgate-no-map = Карта { $map } не существует (нужен MapId).
cmd-ftldiskgate-map-not-init = Карта { $map } не инициализирована. Сначала инициализируйте её.
cmd-ftldiskgate-map-paused = Карта { $map } на паузе. Сначала снимите её с паузы.
cmd-ftldiskgate-bad-coordinates = Неверные координаты «{ $coords }». Укажите два числа: x y или x,y.
cmd-ftldiskgate-out-of-range = Координаты вне допустимого диапазона (не больше { $max } по модулю).
cmd-ftldiskgate-success = Врата { $gate } распечатали диск { $disk }: карта { $map }, координаты { $x }, { $y }.
cmd-ftldiskgate-hint-gate = id врат
cmd-ftldiskgate-hint-map = id карты (MapId)
cmd-ftldiskgate-hint-x = x (или x,y)
cmd-ftldiskgate-hint-y = y
