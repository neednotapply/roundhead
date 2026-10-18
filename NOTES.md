# Roundhead dev notes

- Melty: slug `roundhead`, public page https://melty.gg/m/roundhead (0.1.0 live since 2026-10-18).
- Games (default paths): Cuphead `D:\SteamLibrary\steamapps\common\Cuphead` (Unity 2017.4.9, old Mono), ROUNDS `D:\SteamLibrary\steamapps\common\ROUNDS` (Unity 2018.4.34, old-rounds-for-mods branch).

## Build
1. Edit `sheets/*.json` first (source of truth), then `python -I tools/sheets.py gen` and `python -I tools/sheets.py preflight --code` (must say clean).
2. `python -I tools/package.py <version>` builds the three projects, assembles `dist/Roundhead-<version>.zip` and checks it against `sheets/components.json`.
   Build references: the games' Managed folders (paths in the .csproj files) and BepInEx 5.4.23.2 from `%USERPROFILE%\cuprounds-work\dl\bepinex`.

## How it fits together
- Melty starts `{managed}/CupRoundsLauncher.exe --cuphead {game} --rounds {game:custom-rounds} --managed {managed}`.
- The launcher syncs BepInEx core + card engine into `%LOCALAPPDATA%\CupRounds\rounds-profile` (Thunderstore mods live there too), starts ROUNDS with
  doorstop 4 args (`--doorstop-enabled true --doorstop-target-assembly ...`), waits for `%LOCALAPPDATA%\CupRounds\engine.port`, then starts Cuphead the same way.
  Both games are started directly with `SteamAppId` set; starting Cuphead without it makes it hand itself to Steam, which then asks the user about the arguments.
  The hidden ROUNDS never connects to Steam (src/CardEngine/SteamQuiet.cs skips SteamManager.Awake's SteamAPI.Init under
  `--roundhead-engine`), so Steam shows the player in Cuphead only; with both connected, friends saw them flip between the
  two games. ROUNDS already plays on after a failed Init, and the offline sandbox needs nothing from Steam.
- Doorstop 4.3 ships as `version.dll` in each game folder (not winhttp.dll), so it never clashes with an r2modman/TMM winhttp doorstop. Without our arguments it does nothing.
- Cuphead plugin <-> ROUNDS engine: localhost TCP, one JSON object per line (`src/Shared/Json.cs`; Unity's JsonUtility drops nested classes from plugin assemblies).

## The ROUNDS arena (real ROUNDS players)
- The hidden ROUNDS builds a Map from Cuphead's level as soon as Cuphead connects; a Map appearing while ROUNDS is not
  playing starts ROUNDS's own sandbox (Map.Awake -> GM_Test, offline Photon). Players are made with PlayerAssigner.CreatePlayer,
  so movement, gun, bullets, block, health, death and every card effect are ROUNDS's own code (mod cards included).
- Link: shared memory `Local\CupRoundsArena3` (src/Shared/ArenaLink.cs; name and magic are bumped whenever the layout changes, so an older build never shares the section). Cuphead writes camera, per-player input (press
  counters so no click is lost), level colliders (layers 18/19/20 + enemy DamageReceivers + Level.Left/Right/Ceiling as walls)
  and events (damage, revive, teleport, pit). ROUNDS writes players (Cuphead units), health, block, bullet hits on targets,
  picked cards, and its picture as two 1280x720 RGBA frames per publish (3 buffer pairs, un-premultiplied, AsyncGPUReadback +
  GL.Flush, 60 fps): the world (players, bullets, effects) and the game UI (card bar), rendered by two cameras into two
  render textures. Cuphead draws the world frame on its "Player" sorting layer (order 2, just over its own hidden player),
  so the level's foreground covers the ROUNDS player exactly as it covers Cuphead's, and the UI frame over everything.
  The UI is rendered twice, over clear black and over white, and its exact coverage is the difference (ArenaRenderer.UiFrame):
  Unity's UI shader leaves a·a in the target's alpha, text blends premultiplied and the card bar's fill is additive, so the
  stored alpha alone is wrong for all of them. ROUNDS draws that bar for its dark screen (a glow fill, a 76% border), so the
  coverage is raised with 1 - (1 - a)^uiOpacityBoost (tuning) before Cuphead draws it; colours stay as ROUNDS draws them.
- Mapping: ROUNDS = Cuphead / 37 (moveWorldScale), arena 1000 ROUNDS units below the menu. Cuphead floors are one-way per
  player: each player has its own copy on layer 6/7, enabled only while its feet are above the surface under it (or were
  last physics step); bullets get a floor copy on layer 17; enemy targets are triggers on layer 15 with a Damagable
  (CupheadTarget) that reports hits.
- Cuphead's player object follows ROUNDS's player (motor FixedUpdate skipped), so Cuphead enemies, hits, camera and co-op work.
  Cuphead damage -> ROUNDS health (34 per hit, 1 s cooldown, blocked while ROUNDS's block is up); ROUNDS death -> Cuphead death;
  Cuphead revive -> ROUNDS revive. ROUNDS block also starts Cuphead's parry for 0.35 s (pink objects). A parry that lands
  (LevelPlayerMotor.OnParrySuccess, after the hit-stop) posts EvParry: ROUNDS refills the block (Block.ResetCD, its own
  recharge sound and spark) and bounces the player with its own jump (PlayerJump.Jump, tuning parryBounce), which does not
  spend a jump. Cuphead's own parry bounce is lost on a ROUNDS-driven player, whose place comes from ROUNDS.
- Plane levels keep Cuphead's plane controls, hitbox, shots and super; the player is drawn as the real ROUNDS character
  flying (src/CupheadMod/PlaneBlob.cs): the plane's sprites stay off and the ROUNDS puppet hovers on the plane sprite with
  Request.fly, gun forward and legs dangling behind (ROUNDS puts an airborne foot at rest + body velocity x 0.08, so the
  puppet is posed with a backward, downward velocity: tuning flyLegTrail, flyLegDrop), sized by flySize. It follows the plane's shrink move,
  alpha (invincibility blink) and visibility (supers), turns parry-pink while parrying, and dies as the ROUNDS ghost.
- The results screen after a win (WinScreen) shows the ROUNDS characters on the portrait spot (src/CupheadMod/ResultsLook.cs)
  and switches the hand-lettered name animator off.
- Cuphead's drop shadow stays and is cast from the ROUNDS body (Hooks.PlayerShadow), so it sits at the feet and shows while
  standing; Cuphead's own cast started at its player object, placed a body lower (the two are matched by centre), inside the floor.
- Cuphead's own drawings of a driven player stay off (src/CupheadMod/PlayerSprites.cs): the player object's sprites are
  switched off in Camera.onPreCull every frame (no script order or animation key can show a hit or death pose), and the
  separate objects Cuphead spawns with the character on them are hidden by postfixes on their Init (PlayerDeathEffect,
  LevelPlayerDeathEffect, PlayerJoinEffect). ROUNDS's death burst and revive pop-in are the visuals; the co-op death ghost
  becomes a RoundsGhost (the blob, see-through, in a parry-pink ring) on Cuphead's PlayerDeathEffect, whose parry switch,
  float and revive flow stay Cuphead's. Dev scenario ROUNDHEAD_DEV=revive checks the ghost and the revive.
- Card picks use ROUNDS's CardChoice (DoPlayerSelect patched to read Cuphead's keys/stick/mouse), shown full screen.
- The card loop is ROUNDS's (sheets/progression.json, src/CupheadMod/Progress.cs): a level is a match. The game-over card
  (LevelGameOverGUI.In) gives each player a loss pick, made on the card before its menu answers (Hooks.GameOverMenu holds the
  menu while PickOverlay.HoldsGameOver); the deck is saved per slot and re-applied to ROUNDS's players (ArenaClient.SyncCards),
  so it survives Retry, Exit to Map and quitting; a won level (Level.zHack_OnWin) wipes both decks and resends them empty, which
  ROUNDS answers with its own FullReset (Arena.ResetCards: plain gun, empty card bar). Dice Palace mini bosses keep the deck,
  King Dice himself wipes; plane levels and the tutorial have no ROUNDS players, so neither pick nor wipe. Porkrind's pack puts
  one card into the current deck. Saves from the first-clear rules (SlotSave.rules < 2) start over empty; erasing a Cuphead slot
  erases its decks. A pick Cuphead gives up (ROUNDS away, or its player for the slot missing: PickOverlay falls back to its own
  card screen) is cancelled on ROUNDS's side too (a pick request for 0 cards, Arena.CheckPick), so no stale pick comes up over
  a level; a pick that yields nothing is retried once, then falls back, then the card's menu goes on. ROUNDS parks its players
  while a pick runs; for a pick in the middle of a level (the game-over card) they come back to where they stood.
- Exit: ROUNDS's Unity teardown throws in its sound system and reports a crash, so the launcher kills the background ROUNDS and
  the engine kills itself when Cuphead's process is gone; Cuphead closes its sockets and the shared memory on quit.

## Card effects on Cuphead enemies
- Every ROUNDS effect that lands on an opponent lands on Cuphead enemies with ROUNDS's own rules. CupheadTarget
  (src/CardEngine/ArenaVictims.cs) is a full ROUNDS victim: damage, damage over time (DamageOverTime maths), slow
  (RPCA_AddSlow max rule, 0.9 cap, 0.3/s and 2/s decay), stun, silence, forces (a 100-mass body) and gravity, kept per
  Cuphead DamageReceiver ("group", so a boss with several hit areas is one victim). Every damage path ends in Hit, which
  posts to Cuphead and calls the attacker's DealtDamage, so lifesteal and on-damage cards fire.
- Hooks (src/CardEngine/ArenaEffectHooks.cs, ArenaTargetHooks.cs, ArenaModHooks.cs; sheets/hooks.json) replace the
  places where ROUNDS or a card mod only looks for players: explosions (Explosion.DoExplosionEffects, with scale flags,
  sight, per-name immunity and the stronger-hit override), Dazzle, Homing, Target Bounce, Radar Shot, Chase, Saw and
  Abyssal Countdown, Radiance, the aura cards, Shield Charge, Thruster, Remote, Teleport, screen-edge cards (the Cuphead
  view), and PCE / ModdingUtils cards (Glare, Low Ground, King Midas, Discombobulate, Laser, Super Jump, Demonic
  Possession). Murder's stacks are kept in the Roundhead save and spent by Cuphead (one boss phase per stack, next level).
- ROUNDS -> Cuphead: summed hits (kind and colour), an effect ring and a per-frame status table (ArenaLink: serial-last
  ring entries, a sequence lock on the table). Cuphead (src/CupheadMod/EnemyStatus.cs) shows them on real enemies:
  slower animations and per-enemy CupheadTime.WaitForSeconds, hit-pause for stun, dud projectiles while silenced, tints,
  pushes for small moving enemies (bosses flinch), boss phase-change immunity.
- Test bench (dev only, not shipped): cardprobe.py drives a private dev engine through the same shared memory and checks
  each card, 27 combinations and co-op against the ROUNDS / mod source values.

## The ROUNDS character (puppet, world map only)
- The card engine spawns ROUNDS's own player prefab per co-op slot far from the menu (Puppet.cs), switches off its gameplay scripts and keeps the
  animation ones (particle skin, face, IK legs and arms, wobble, block orb). Cuphead sends a pose ~60/s (velocity, height, aim, grounded, shot,
  block, hit); ROUNDS renders the puppet with its own camera (256 px, 5.2 ROUNDS units) and streams raw RGBA frames on frames.port.
- The gun is drawn solid in gun black (GunLook.Blacken, tuning gunShade), as for the arena's players: ROUNDS keeps the gun's own
  sprites off and shows the gun as its soft shadow (SFPolygon under its soft-shadow renderer), which our frames have no pass for, and
  its masks would otherwise fill with the team-coloured skin particles. It follows the hand with ROUNDS's Holding spring (force 500,
  drag 25).
- Movement uses ROUNDS physics read from the prefab (force 17000, extraDrag 200, airControl 0.8, upForce 5500, gravityForce 1000, exponent 1.5),
  scaled 37 Cuphead units per ROUNDS unit.
- The ROUNDS engine runs with -batchmode (no window) and starts after Cuphead, so Melty records Cuphead's window; test runs start Cuphead windowed.

## Test mode
While `%LOCALAPPDATA%\Melty\test\roundhead.json` exists the launcher auto-plays and the Cuphead plugin: loads save data, switches on 2-player mode, visits the map
puts Cold Bullets in player 1's deck (a head start, so a card effect shows on the boss from the first attempt), then loads Botanic Panic where a bot aims,
shoots and blocks. The bots take Cuphead's hits and die like anyone (no test immunity); the game-over card gives each its loss pick (picked by itself
after 3.5 s: the cards-picked check), then the level is retried a few seconds later (TestMode.TestRetry), so a recording keeps showing the loop. Checks: engine-cards, cards-picked, cards-applied, blob-spawned, coop-blobs,
blob-hits-boss, enemy-status, enemy-wait-dilation. Cuphead's own save is never written in test runs. Dev scenarios (never for players,
ROUNDHEAD_DEV=retry | revive | statuses | win, plus ROUNDHEAD_DEV_CARDS and ROUNDHEAD_DEV_SCENE) skip the forced card and the test retry; the status
bench holds the bots quiet while it measures the boss and is the one scenario that keeps them immune to Cuphead's hits (TestMode.Immune),
since they could neither dodge nor block. ROUNDHEAD_DEV_DUMP logs how ROUNDS draws each player and gun ("arena look"). ROUNDHEAD_DEV_SCENE takes a Levels name (Platforming_Level_1_1) or a raw scene name. The test retry also fires on a level lost with everyone alive (Mausoleum).

## Seen once
- 2026-10-17, a Melty test run: Cuphead went black with a busy cursor right after the studio logo and never reached the title;
  its Unity log shows an unhandled `ObjectDisposedException` on the finalizer thread inside Rewired (Cuphead's input library:
  `ThreadPoolJob.SharedThread.ThreadQueue.Finalize -> Stop -> EventWaitHandle.Set`) at that moment and nothing after. The
  plugin had only just loaded. The same build passed every dev run and the next Melty run; nothing in the plugin runs at
  that point of the title load. The plugin logs a heartbeat every 15 s in test runs (scene, engine link, arena, window) so
  a recording with no checks still says where it stood.

## Known limits (v0.1.1)
- Plane levels have no ROUNDS cards: the plane's shots and super are Cuphead's (no picks, no wipe), only the look is ROUNDS's.
- The title screen is Cuphead's own art (Cuphead and Mugman).
- Co-op is Cuphead's local 2-player; there is no online play.

## Melty listing wording (2026-10-17)

Melty's publish check flags the words "co-op" and "multiplayer" anywhere in the listing description as a claim of online play that the recipe must set up (recipe.multiplayer), even in a sentence saying there is none. Roundhead's two players share one PC, so the listing says "two players on one PC" and "no online play or matchmaking" instead. Keep those words out of the description unless online play is ever added. The README follows the same wording.
