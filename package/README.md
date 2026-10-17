# Cartur's Combat Text

World of Warcraft style combat text for Valheim. The motion was timed against retail WoW footage, frame by frame, and it only changes the numbers you see over an enemy you just hit.

![Damage numbers popping off a greydwarf](https://raw.githubusercontent.com/jekkle/cartur-combat-text/master/docs/images/ingame-combat.jpg)

## What it does

Ordinary hits fling off to one side in a short arc and are gone in about half a second, as in retail WoW. Crits pop. They snap to a big peak and are back to size within about 0.14 seconds, then sit on the creature for around a second and a half. Enemy hit numbers are drawn at 2x vanilla size and crits at 3x. Colours are the game's own.

A hit counts as a crit in four cases: a backstab, a stagger hit, a hit that triggers the game's weakness text (a creature taking extra damage from that damage type), or a hit at least twice the average of your last 20 normal hits. The average needs at least five hits before it starts counting, so the first swings of a session are not all crits. It is one running average across every enemy, not one per creature type.

Numbers are drawn in Federo, the closest free match to WoW's damage font. Federo is under the SIL Open Font License (`fonts/OFL.txt`). Hit numbers on enemies are also readable from further away. The mod raises the game's text-visibility distance to 80 metres for an enemy hit, so a bow shot from range still shows a number. The raise applies only to that one hit, so distant chopping, mining and other players' numbers are not pulled in.

Only hits on enemies are changed. Numbers on trees, ore, buildings, crafting and pickups, and over the player or tamed creatures, are left as vanilla draws them.

## Settings

Config file: `BepInEx/config/com.jekkle.valheim.carturcombattext.cfg`.

| Setting | Default | What it does |
| --- | --- | --- |
| `General / Font` | `Federo-Regular.ttf` | Font for the numbers on enemies. Empty uses the game's own. Otherwise either the name of a font the game has loaded, or the file name of a .ttf or .otf in the `fonts` folder next to the DLL. Takes effect on the next hit. |

The names of the game's loaded fonts, for example `Valheim-Norse`, are written to the BepInEx log when you enter a world. A config from before 1.2.0 keeps whatever `Font` it already had. Set it to `Federo-Regular.ttf` to switch. If a font file cannot be loaded, a warning is written to the log.

## Install

Use a mod manager (r2modman, Thunderstore or Gale) and it pulls in BepInEx for you. To install by hand, drop the `CarturCombatText` folder, which holds the DLL and its `fonts` folder, into `BepInEx/plugins`.

Client-side. The server does not need it and neither do the people you play with.

---

*Free, and always will be. If it improved your game you can [tip me on Patreon](https://www.patreon.com/c/cartur).*

**[Discord](https://discord.gg/nd5RqpwNkz)**: bug reports, install help, and mod requests.
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
[Feeding Trough](https://thunderstore.io/c/valheim/p/Cartur/Carturs_Feeding_Trough/)
