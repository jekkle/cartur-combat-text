# Changelog

## 1.1.1

- An error while drawing a damage number can no longer cancel the hit's damage.
- The list of recent hits no longer grows without limit where the text is never shown (hidden HUD, dedicated server).
- Stagger hits count as critical.
- Less garbage per frame, and a guard against the last entry being shown with the wrong label.

## 1.1.0

- **Choose your own font.** `General / Font` takes either the name of a font the game already has
  (the list is written to the BepInEx log when you enter a world, e.g. `Valheim-Norse`) or the file
  name of a .ttf/.otf you put in a `fonts` folder next to this mod's DLL. Empty keeps the game's.

## 1.0.0

First release.

- Enemy hit numbers scaled up (2x normal, 3x crit) with a fast crit pop -
  everything else the game draws is left alone.
- Crit on a backstab, a weak-elemental hit, or a hit at least 2x your recent
  average against that kind of enemy.
- Red for elemental weakness, yellow for every other crit.
- Numbers follow the creature instead of hanging where the blow landed.
- Visibility distance raised so a ranged hit still shows a number.
