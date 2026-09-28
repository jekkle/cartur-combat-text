# Cartur's Combat Text

World of Warcraft style combat text for Valheim - bigger numbers on enemies, and a
fast crit pop instead of a flat vanilla rise. Numbers on trees, ore, buildings,
crafting and pickups are untouched - this only changes what you see over a
creature you just hit.

![A weak-elemental hit in red, next to the normal white hit number](https://raw.githubusercontent.com/jekkle/cartur-combat-text/master/docs/images/combat-text-hit.png)

- Enemy hit numbers are 2x vanilla size. A crit is 3x, with a snap-to-peak,
  undershoot, settle pop rather than a smooth swell.
- **Red** text for an elemental weakness hit. **Yellow** for everything else that
  counts as a crit: a backstab, a Valheim "Bonus" hit, or a hit at least twice the
  size of your recent average against that kind of enemy. Crits are also bold and
  clear the screen faster.
- The number rides up from the creature as it moves, instead of hanging in the air
  where the blow landed.
- Readable from further away - raised from vanilla's own text-visibility distance
  so a bow shot from range still shows a number.

Everything else - the font, the fade, the rise speed on an ordinary hit, healing
and block text - is exactly what vanilla already drew.

## Install

Use a mod manager (r2modman / Thunderstore / Gale) and it pulls in BepInEx for you.
Manually: drop `CarturCombatText.dll` into `BepInEx/plugins`.

Client-side. The server doesn't need it and neither do the people you play with.
No settings to configure - it just changes the numbers.

---

*Free, and always will be. If it improved your game you can [tip me on Patreon](https://www.patreon.com/c/cartur).*

**[Discord](https://discord.gg/nd5RqpwNkz)** — bug reports, install help, and mod requests.
Bug reports get their own thread so nothing is lost in a chat scroll, and requests are voted on.

**More from Cartur:**
[HD Blood](https://thunderstore.io/c/valheim/p/Cartur/Carturs_HD_Blood/) ·
[Map Pins](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Map_Pins/) ·
[Compass and Clock](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Compass_and_Clock/) ·
[Safe Stamina](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Safe_Stamina/) ·
[Follow Command](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Follow_Command/) ·
[Flooring](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Flooring/) ·
[UI HUD](https://thunderstore.io/c/valheim/p/Cartur/Carturs_UI_HUD/) ·
[Waste Management](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Waste_Management/) ·
[Feeding Trough](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Feeding_Trough/) ·
[Build Camera](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Build_Camera/)
