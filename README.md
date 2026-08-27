# Y4NGZUpgrades

Plugin source for **Y4NGZUpgrades**, a player progression overhaul for Lethal Company:
a reworked XP/level system, 27 upgrades across four skill trees purchased with XP-earned
tokens, and an Employee File that records long-term performance.

Download and play it from Thunderstore:
[Y4NGZ313/Y4NGZUpgrades](https://thunderstore.io/c/lethal-company/p/Y4NGZ313/Y4NGZUpgrades/).

## What is in this repository

The C# plugin source only. The textures, audio, and compiled Unity asset bundles the plugin
loads at runtime are not published here — they ship inside the Thunderstore package. This
tree shows how the mod works; it does not build into a playable mod on its own.

## Building

The plugin targets `netstandard2.1`. The project references
[Y4NGZ Interactions](https://github.com/y4ngz313/Y4NGZInteractions), the animation API the
first-person upgrade animations run through, via the `Y4NGZInteractionsProjectPath` MSBuild
property, and the game assemblies via `GameManagedDir`.

## Runtime dependencies

BepInEx 5.4.2305, LethalCompany InputUtils, and Y4NGZ Interactions.

## Bugs and feedback

Open an issue with the moon, whether the problem occurred for the host or a client, and the
relevant `BepInEx/LogOutput.log` excerpt.

## Licensing

No open-source license is granted; this source is published for reference.
