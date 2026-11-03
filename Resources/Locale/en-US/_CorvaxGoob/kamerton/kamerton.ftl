
## Crystal converter

kamerton-converter-cell-slot = cell
kamerton-converter-busy = The converter is busy.
kamerton-converter-full = The crystal rack is full.
kamerton-converter-inserted = Loaded { $count } crystals.
kamerton-converter-redspace-needs-destroy = Redspace crystals are only accepted in Destroy mode.
kamerton-converter-eject-redspace-first = Eject the redspace crystals first.
kamerton-converter-mode-create = Mode: create.
kamerton-converter-mode-destroy = Mode: destroy.
kamerton-converter-verb-create = Create
kamerton-converter-verb-destroy = Destroy
kamerton-converter-need-crystals = Needs { $count } bluespace crystals.
kamerton-converter-empty = Nothing to grind.
kamerton-converter-no-cell = No cell inserted.
kamerton-converter-cell-already-charged = The cell is already charged.
kamerton-converter-not-enough-stable = Not enough stable energy: need { $need }, have { $have }.
kamerton-converter-not-enough-unstable = Not enough unstable energy: need { $need }, have { $have }.
kamerton-converter-cell-charged = Cell charged.
kamerton-converter-not-enough-power = Not enough machine power: need { $need }, have { $have }.
kamerton-converter-not-enough-materials = Needs { $count } sheets each of plasma and uranium.
kamerton-converter-examine-mode-create = Mode: [color=#39C6FF]create[/color].
kamerton-converter-examine-mode-destroy = Mode: [color=#E0304A]destroy[/color].
kamerton-converter-examine-crystals = Crystals: bluespace { $bluespace }, redspace { $redspace } of { $max }.
kamerton-converter-examine-energy = Stable energy: { $stable }, unstable: { $unstable }.

kamerton-converter-window-title = Crystal converter
kamerton-converter-window-mode = Mode:
kamerton-converter-window-mode-create = Create
kamerton-converter-window-mode-destroy = Destroy
kamerton-converter-window-status = Status:
kamerton-converter-window-work-idle = idle
kamerton-converter-window-work-creating = synthesising a redspace crystal…
kamerton-converter-window-work-grinding = grinding crystals…
kamerton-converter-window-work-printing = printing a bluespace crystal…
kamerton-converter-window-crystals = Crystals:
kamerton-converter-window-crystal-count = { $bluespace } BS / { $redspace } RS of { $max }
kamerton-converter-window-stable = Stable energy:
kamerton-converter-window-unstable = Unstable energy:
kamerton-converter-window-power = Machine power:
kamerton-converter-window-materials = Plasma / uranium (sheets):
kamerton-converter-window-materials-value = { $plasma } / { $uranium } (print: { $cost } each)
kamerton-converter-window-cell = Cell:
kamerton-converter-window-cell-none = none
kamerton-converter-window-cell-charged = { $name } (charged)
kamerton-converter-window-cell-empty = { $name } (empty)
kamerton-converter-window-start = Start
kamerton-converter-window-charge = Charge cell
kamerton-converter-window-print = Print crystal
kamerton-converter-window-eject = Eject

## Kamerton reactor

kamerton-reactor-slot-cell = Cell
kamerton-reactor-popup-collapsed = The reactor has collapsed.
kamerton-reactor-popup-scram-locked = Emergency lockout: the reactor cannot be started yet.
kamerton-reactor-popup-not-assembled = The reactor is not assembled: no synchronizer next to the core.
kamerton-reactor-popup-scram = EMERGENCY SHUTDOWN OF THE KAMERTON!
kamerton-reactor-popup-leak = Your skin prickles with a strange red static.
kamerton-reactor-popup-collapse = The Kamerton reactor collapses!
kamerton-reactor-examine-temperature = Core temperature: [color=orange]{ $temp } K[/color] (collapse at { $max } K).
kamerton-reactor-examine-power = Output: [color=#39C6FF]{ $power } kW[/color], active pairs: { $pairs }.
kamerton-reactor-examine-running = The reactor is [color=lime]running[/color].
kamerton-reactor-examine-stopped = The reactor is [color=gray]stopped[/color].
kamerton-reactor-examine-ring-ok = The suppressor ring is [color=lime]complete[/color].
kamerton-reactor-examine-ring-broken = The suppressor ring is [color=red]broken[/color]: { $missing } segments missing.
kamerton-reactor-examine-part-linked = It is connected to a reactor core.
kamerton-reactor-examine-part-unlinked = It is not connected to any reactor core.

kamerton-reactor-window-title = Kamerton reactor control
kamerton-reactor-window-status = Status:
kamerton-reactor-window-unlinked = no core
kamerton-reactor-window-running = running
kamerton-reactor-window-stopped = stopped
kamerton-reactor-window-scrammed = emergency lockout ({ $seconds } s)
kamerton-reactor-window-not-assembled = not assembled
kamerton-reactor-window-temperature = Core temperature:
kamerton-reactor-window-power = Total power:
kamerton-reactor-window-pairs = Active pairs:
kamerton-reactor-window-pairs-value = { $pairs } (synchronizers: { $syncs })
kamerton-reactor-window-output = HV output:
kamerton-reactor-window-output-value = { $kw } kW supplied
kamerton-reactor-window-output-none = no power unit in the ring
kamerton-reactor-window-ring = Suppressor ring:
kamerton-reactor-window-ring-ok = complete
kamerton-reactor-window-ring-broken = broken, { $missing } missing
kamerton-reactor-window-arrays = Fuel arrays
kamerton-reactor-window-array-stable = Stable: { $charged }/{ $slots } charged, { $empty } empty, lowest { $minutes } min
kamerton-reactor-window-array-unstable = Unstable: { $charged }/{ $slots } charged, { $empty } empty, lowest { $minutes } min
kamerton-reactor-window-cooling = Cooling (radiators: { $radiators })
kamerton-reactor-window-sink = Heat sink { $index }: { $flow } mol/s, { $tin } K → { $tout } K, { $kw } kW
kamerton-reactor-window-no-sinks = No heat sinks in the corners.
kamerton-reactor-window-hint-unlinked = Place the console in the suppressor ring of a reactor core.
kamerton-reactor-window-hint-hot = Danger: the core is close to collapse!
kamerton-reactor-window-hint-ring = The ring is broken: redspace is leaking around the reactor.
kamerton-reactor-window-start = Start
kamerton-reactor-window-stop = Stop
kamerton-reactor-window-scram = SCRAM
