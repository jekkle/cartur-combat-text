# Cartur's Combat Text

World of Warcraft style combat text for Valheim, timed against retail WoW footage frame
by frame. Numbers on trees, ore, buildings, crafting and pickups are untouched - this
only changes what you see over a creature you just hit.

- Ordinary hits fling off to one side in a short arc and are gone in about half a
  second, like retail WoW.
- Crits pop: they snap to a big peak and are back to size within about 0.14s, then sit
  on the creature for about a second and a half. A crit is a backstab, a stagger, an
  elemental weakness hit, or a hit at least twice your recent average against that
  kind of enemy.
- Enemy hit numbers are 2x vanilla size, crits 3x.
- Colours are the game's own.
- Numbers are drawn in Federo, the closest free match to WoW's damage font. Set
  `General / Font` to empty for the game's font, or to any loaded font name or a
  .ttf/.otf in the mod's `fonts` folder.
- Readable from further away - raised from vanilla's own text-visibility distance
  so a bow shot from range still shows a number.

Healing and block text are exactly what vanilla already drew.

## Install

Use a mod manager (r2modman / Thunderstore / Gale) and it pulls in BepInEx for you.
Manually: drop the `CarturCombatText` folder (the DLL and its `fonts` folder) into `BepInEx/plugins`.

Client-side. The server doesn't need it and neither do the people you play with.
Federo is under the SIL Open Font License (`fonts/OFL.txt`).

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
