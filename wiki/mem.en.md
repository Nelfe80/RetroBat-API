# Creating .MEM files

A `.MEM` file teaches APIExpose to **read a game's memory while you play**: where the lives, the score, the character state live… It is what makes your LEDs flash when Sonic loses his rings and the score display live on the marquee — without modifying the game or the emulator.

This page teaches you how to write your own. No special tool required: a `.MEM` is a plain text file.

!!! tip "Share your .MEM with the community"
    Got a `.MEM` that works for a game? **Submit it on NelfePlay**: it gets validated, tested, then — if it's good — integrated into the official RAM set and deployed to every cabinet, credited to you.
    → **[Contribute a .MEM](https://nelfeplay.com/en/mem/contribute)** (NelfePlay account required).

## How it works

```text
RetroArch runs the game
   → the APIExpose wrapper reads the RAM described by the .MEM
      → changes become events ("Player loses a life")
         → LedManager, MarqueeManager and your tools react to them
```

## Finding addresses: MEM Explorer

Writing a `.MEM` means knowing **where** a game keeps its lives, its score, its state. **MEM Explorer** is the desktop tool that discovers them for you, installed alongside APIExpose:

```text
plugins\APIExpose\tools\mem-explorer\MemExplorer.exe
```

It launches a game **through APIExpose**, watches memory while you play, and helps you isolate the address that changes when the value changes on screen. Two engines depending on the platform, picked automatically: the **RetroArch wrapper** for consoles, the **MAME Lua bridge** for arcade.

The method is the one every address hunter uses: play, tell the tool what you just saw (“I lost a life”, “the score went up”), and the candidate list narrows on every pass until only the right address is left.

The tool then writes the definition in the **curator's format** — the same vocabulary the official `.MEM` files use — and you can test it in game straight away, before offering it to the community.

!!! note "Discovery mode only runs when you ask for it"
    Reading RAM continuously costs machine time. MEM Explorer turns discovery on for the session and releases it afterwards: a cabinet simply playing pays nothing for it.

## Where to put the file

```text
plugins\APIExpose\resources\ram\<system>\<game-name>.MEM
```

For instance `resources\ram\nes\super-mario-bros.MEM`. `<system>` is the **RetroBat folder name** (`nes`, `snes`, `megadrive`, `mame`…). Look at the system's existing files to follow the same naming style. An `alias.json` file in the system folder can point several ROM names (regions, romhacks) to the same `.MEM`.

!!! tip "Start from an existing file"
    The Data Pack already contains thousands of `.MEM` files. Open one from a game close to yours: it is the best starting template.

## The structure: three blocks

A `.MEM` is a Lua table with three sections, always in this order:

```lua
return {
  game = { ... },      -- which game this is
  rom = { ... },       -- which ROMs match
  events = { ... }     -- the events watched in game
}
```

### 1. `game` — the game's identity

```lua
game = {
  title = "Super Mario Bros.",
  system = "nes",                 -- RetroBat folder name, required
  system_name = "NES/Famicom",    -- human-readable name, recommended
  game_id = 1446                  -- database id, optional
}
```

### 2. `rom` — the compatible ROMs

```lua
rom = {
  name = "super-mario-bros",      -- lowercase kebab-case = the .MEM filename
  hashes = {
    { hash = "8e3630186e35d477231bf8fd50e54cdd",
      label = "Super Mario Bros. (World).nes",
      tags = { "nointro" } }
  }
}
```

Hashes let APIExpose recognize variants (regions, versions) without duplicating the file; `alias.json` in the system folder maps ROM names/hashes to the canonical `.MEM` file.

**Where do addresses come from?** RetroArch's cheat engine, an emulator debugger, or community databases (Data Crystal, romhacking guides). Existing cheat-code addresses are often an excellent starting point.

### 3. `events` — what triggers the effects

An event = an address + a type + a **condition** + a nomenclature **action** + a description, stored under its `category.subfamily` family:

```lua
events = {
  resources = {
    lives = {
      { address=0X75A, type="u8", condition="decrease", action="LOSE_LIFE", desc="Player loses a life" }
    }
  },
  scoring = {
    points = {
      { address=0X840, type="u24be", condition="change", action="SCORE_STATE", desc="Score" }
    }
  }
}
```

## Types

| Type | Meaning |
|---|---|
| `u8` | 1 byte — **the default choice** |
| `u16le` / `u16be` | 2 bytes, little / big endian |
| `u24le` / `u24be` | 3 bytes (common for scores) |
| `u32le` / `u32be` | 4 bytes |

Only these seven unsigned types are recognized by the runtime; anything else is read as `u8`.

Do not guess the endianness of a multi-byte value: when in doubt, stay on `u8`.

## Conditions

| Condition | When to use it | Examples |
|---|---|---|
| `decrease` | The value meaningfully drops | lives, health, timer, ammo |
| `increase` | The value meaningfully rises | score, rings, experience, combo |
| `change` | It changes, no particular direction | current level, room, player state |
| `equal` | A precise value is reached (with `min`/`max`) | title screen active, invincibility, boss defeated |
| `any` | Last resort, non-directional observation | |

## The eight event families

Each event belongs to a family, with normalized sub-keys (lowercase, `snake_case`):

| Family | Contents | Typical sub-keys |
|---|---|---|
| `flow` | Where the game is | `title_screen`, `in_game`, `pause`, `game_over`, `credits` |
| `progression` | Advancement | `world`, `level`, `stage`, `room`, `lap`, `checkpoint` |
| `resources` | What is gained/lost | `lives`, `health`, `ammo`, `oxygen`, `timer` |
| `inventory` | Objects | `items`, `keys`, `weapon`, `held_object` |
| `combat` | Fights | `boss_hit`, `damage_taken`, `enemy_state` |
| `scoring` | Performance | `score`, `coins_rings`, `currency`, `experience`, `combo` |
| `state` | Forms and effects | `player_state`, `powerup_state`, `temporary_state`, `status_effect` |
| `system` | Useful technicals | `memory`, `prng`, `flags` |

Use the canonical names: `rings` and `coins` become `coins_rings`, `gold`/`rupees` become `currency`, `XP` becomes `experience`. A valid but unclassifiable value goes into `system.memory` — nothing gets thrown away.

## Naming what the player is holding

Two actions read differently from the rest: their `desc` carries **a name**, not a description.

| Action | Family | What `desc` holds |
|---|---|---|
| `CHARACTER_SELECTED` | `state.player` | the character being played — `"Cody"`, `"Ryu"` |
| `WEAPON_SELECTED` | `inventory.weapon` | the weapon in hand — `"Fire Water"`, `"Shotgun"` |

That is what lets an instruction card show itself: the game announces *Cody*, the screen shows Cody's card.

One entry per value, with `condition="eq"` — it only fires when the value is **entered**, so once, at the moment of the choice:

```lua
state = {
  player = {
    { address=0X857D, type="u8", condition="eq", value=0X00, action="CHARACTER_SELECTED", player=1, desc="Guy" },
    { address=0X857D, type="u8", condition="eq", value=0X01, action="CHARACTER_SELECTED", player=1, desc="Cody" },
    { address=0X857D, type="u8", condition="eq", value=0X02, action="CHARACTER_SELECTED", player=1, desc="Haggar" },
  },
},
```

Two rules make these entries usable:

- **`player` is mandatory.** A card is shown for *one* player, and a cabinet has several. Without it, the event has nowhere to go.
- **The name must be the one of the content it designates**, not the one from your source. If your table says `Torch` while the game's own card shows `FIRE WATER`, write `Fire Water` — otherwise the event points at a card that does not exist.

## Describing a score: one entry, or pieces that never overlap

A score is often scattered in memory — one digit per byte, a BCD pair, an upper and a lower half. The aggregator therefore **adds the pieces back together**, each weighted by what its description or its `score_mask` says.

But **nothing in a `.MEM` says "this entry IS the whole score"** rather than a piece of it. So pick one of two ways, never both:

- **one entry covering the whole value** — a wide type reads it in one go (`u24be`, `u32be`), and `score_mask` / `score_encoding` say how to read it;
- **several entries, one per piece**, whose byte ranges are **disjoint**, each carrying its weight.

Mixing them breaks the score, and it happens on its own: someone adds the complete entry without removing the pieces it replaces. Sonic 1 read **110 for a score of 100** that way — a `u24be` at `0xFE26` (covering `FE26` to `FE28`) sitting next to the RA note's halves at `0xFE26` and `0xFE28`.

The runtime defends itself: when two parts cover a common byte, **only the widest is kept**, and the other is dropped with a warning naming both. The file is still wrong, though — two descriptions of the same number will mislead the next reader.

## Translating values: `map`

```lua
powerup_state = {
  { address=0x0756, type="u8", condition="change", desc="Player powerup state",
    map={ [0]="small", [1]="big", [2]="fire" } }
}
```

`map` turns a raw number into a stable word — this is what light effects exploit ("fire" → red panel).

## Driving effects: `action` and `action_map`

The runtime automatically translates your families into universal commands: a `lives` drop emits `ACTION: DEAD`, a `scoring` rise emits `ACTION: SCORE`, a `flow` transition to title emits `STATE: TITLE_SCREEN`… To force a precise behavior, use the official generic verbs in `action`/`action_map`: `INVINCIBILITY_START`/`STOP`, `SPEED_START`/`STOP`, `SHIELD_GAIN`/`LOST`, `RING_GAIN`/`LOSE`, `TREASURE`, `BOSS_DEFEATED`, `LAP_COMPLETE`, `TURBO_BOOST`, `CRASH`, `DOOR_OPENED`, `SECRET_REVEALED`, `NIGHT_TIME`… That way Sonic's Speed Shoes and Mario's Star light up the same effects on every cabinet.

## Avoiding spam: `no_log` and `no_survey`

- `no_log=true` or `no_survey=true`: the runtime **skips the entry at load time** — the address is not watched and costs nothing in game.
- These entries are deliberately kept in the Data Pack files: to re-enable an address, flip its flag to `false` (or remove it — absence means `false`), no tooling required.
- An automatic anti-spam guard protects the runtime anyway: a non-score event firing in a tight loop is permanently muted for the session.

## The golden rules for descriptions

English, short, gameplay-oriented, no trailing period, no address in the text:

- ✅ `Player lives`, `Collected rings`, `Invincibility active`
- ❌ `ram address for number of lives`, `0x075A - Lives`

## Checklist before sharing

- [ ] The three blocks in order `game` → `rom` → `events`
- [ ] `game.system` = RetroBat folder name
- [ ] Canonical families only (`flow.lifecycle`, `scoring.points`…), `desc` as the last field, without `=` or the word "address"
- [ ] Recognized `condition`: `change`, `eq`, `neq`, `increase`, `decrease`, `bit_true`, `bit_false`
- [ ] `no_log=true` on values that change every frame (re-enable by flipping to `false`)
- [ ] Tested in game: events show up on `ws://127.0.0.1:12345/ws/ingame` (with their `family`, plus `color` for arcade score deltas)

!!! question "In doubt?"
    The commented template is `resources\ram\<system>\template.MEM` when present, and the Data Pack files are all compliant examples. `.MEM` files are covered by the [DATA-LICENSE](licences.md) — your personal creations remain yours, community sharing is welcome.
