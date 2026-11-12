# Combat stats

A combat meter for the pack, based on Muindor CombatStats 1.0.3. Install the matching version on the server and every client, replacing Muindor-CombatStats. The original MIT license is included.

- F10: cycle compact meter visibility.
- F11: open Current, Last, Overall and recent fights, with damage, DPS, damage taken and healing received.

Pets and summons count for their owner. DPS uses the whole fight, ending when the last engaged enemy dies or disengages. Overkill is separate from effective damage; resource gathering does not count. Environmental damage counts only when taken during a fight.

Change the layout in the settings panel, and the keys and colours in `tenemo.CombatStats.cfg`. The compact meter's offset is its distance inward from the chosen corner. Personal settings and completed fights are stored in `BepInEx/combat-stats-state/`.
