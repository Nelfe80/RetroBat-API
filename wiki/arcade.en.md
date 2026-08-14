# Arcade and panels

APIExpose exposes **arcade data** for cabinets, LEDs and themes: plugins like [LedManager](https://nelfe80.github.io/RetroBat-Led-Manager/) use it to color your buttons, and [MarqueeManager](https://nelfe80.github.io/RetroBat-Marquee-Manager/) to animate lamps and scores.

## Panels (dynpanels)

The `resources\dynpanels\` folder holds control-panel definitions: buttons, colors, control functions, CPO layouts — per system and per game. This is what lets your buttons take the colors of the selected game's real controls.

## The drawn panel, for themes

For every selected game, APIExpose **draws your control panel** and writes it as SVG, ready for an EmulationStation theme to show:

```
resources\theme\panels\<system>\<game>.svg        top view
resources\theme\panels\<system>\<game>-3d.svg     front view
resources\theme\panels\<system>\default.svg       system fallback
```

The same files are mirrored under `\.emulationstation\themes\.panels\`, the only place a theme can reliably read from. The folder starts with a dot so it never mixes with installed themes.

What the drawing shows:

- **the buttons your cabinet really has** — not the game's. You see your eight holes, and which of them this game speaks to;
- buttons the game **uses** carry their colour and their function (`Fire`, `Loop`…); the others stay drawn, faded;
- the joystick is drawn when there is one, in the colour the game's definition gives it.

It is vector artwork: a theme scales it to whatever slot it has, and it stays sharp on a 4K marquee as on a small card.

!!! note "Atomic write"
    The file is written aside, then moved into place. A theme reading it while a game launches — the exact moment the panel is rewritten — sees either the old drawing or the new one, never half of one.

## Checking your wiring

The `/ws/panel` stream publishes the cabinet's **physical presses**, already resolved to panel slots (`panel.input.pressed` / `panel.input.released`): never a raw button index, but the slot and what it does.

That is what makes a wiring check possible **with no LED hardware at all**: press the bottom-left button, and the bottom-left slot lights up. If another one does, the wiring is not what the cabinet declares — and it shows in a second. [MarqueeManager](https://nelfe80.github.io/RetroBat-Marquee-Manager/) uses this for its panel layer.

START and SELECT are reported as **system inputs** rather than as "no slot": they are wired on their own pins, outside the numbered slots.

## RAM definitions

The `resources\ram\` folder holds per-game memory definitions (`.MEM` files): they detect game events in real time — score, lives, power-ups — straight from the game's RAM. You can write your own: see [Creating .MEM files](mem.md).

!!! note "The Data Pack"
    `dynpanels`, `ram`, gamelists and the other `resources\` data form the **APIExpose Data Pack**, the result of long curation work. It ships in the `full` release archive and is protected by its own license (`DATA-LICENSE.md`) — see [Licensing](licences.md).

## The RetroArch wrapper

To read game RAM, APIExpose relies on `wrapper\wrapper.dll`, a libretro proxy DLL that sits between RetroArch and the emulation core, without modifying RetroArch.

Every published version ships with its fingerprint in `wrapper\WRAPPER_VERSION.txt`:

```powershell
Get-FileHash wrapper\wrapper.dll -Algorithm SHA256
```

The hash must match the one in `WRAPPER_VERSION.txt` and in the release notes. If it differs, do not use the DLL.

## High scores and MAME outputs

APIExpose also exposes:

- arcade **high scores** (via hi2txt);
- **native MAME outputs** (`READY_LAMP`, `TORP_LAMP_1`…) on the `/ws/arcade` stream, so lamps and LEDs live again like on the original cabinet;
- the current game context and runtime events, for overlays, themes or external tools.

Real-time streams are detailed in [Local API](api.md).
