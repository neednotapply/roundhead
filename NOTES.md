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
- Plane levels keep Cuphead's plane controls, hitbox, shrink and parry, and ROUNDS's real player is the player the other way
  round from the ground: Cuphead writes the plane's centre and U_Fly into each slot's Slot2 block, and the engine holds the
  ROUNDS player there (Arena.HoldFlyers, in Update and FixedUpdate before ROUNDS's own scripts) with PlayerVelocity.isKinematic
  on, so ROUNDS's gravity, movement, jumps, recoil, knockback and block dashes do nothing while its gun, bullets, block, health
  and cards work as on the ground. Ground and plane players are both ArenaSeats (src/CupheadMod/ArenaSeat.cs: BlobController,
  PlaneBlob); ArenaClient.Follow moves a ground player to ROUNDS, ArenaClient.Lead only follows a plane player's life, death and
  deck. The plane's shots, EX, super and weapon switch stay off while ROUNDS drives (Hooks.PlaneWeapons on
  PlanePlayerWeaponManager.FixedUpdate). Controls: Dash and Lock shrink the plane (PlanePlayerAnimationController), so a flying
  player blocks with the EX button and right mouse (BlobInput.BlockDown); Jump is the plane's parry, so no ROUNDS jumps are sent
  and Space stays Cuphead's jump (InputPatches.InLevelArena); aim is the mouse or right stick, else forward. A plane parry that
  lands (PlanePlayerParryController.OnParrySuccessEvent) refills the ROUNDS block with no bounce (EvParry with 0).
  Without the arena (ROUNDS starting or away) the plane plays as Cuphead's, drawn as the ROUNDS puppet hovering on the plane
  sprite (src/CupheadMod/PlaneBlob.cs) with
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
  King Dice himself wipes; plane levels take part like any other level; the tutorial has no ROUNDS players, so neither pick nor wipe. Porkrind's pack puts
  one card into the current deck. Saves from the first-clear rules (SlotSave.rules < 2) start over empty; erasing a Cuphead slot
  erases its decks. A pick Cuphead gives up (ROUNDS away, or its player for the slot missing: PickOverlay falls back to its own
  card screen) is cancelled on ROUNDS's side too (a pick request for 0 cards, Arena.CheckPick), so no stale pick comes up over
  a level; a pick that yields nothing is retried once, then falls back, then the card's menu goes on. ROUNDS parks its players
  while a pick runs; for a pick in the middle of a level (the game-over card) they come back to where they stood.
- Logs: the hidden engine flushes its BepInEx disk log every 5 s and once its cards are ready, and when Cuphead closes the launcher
  gives it 6 s to end itself (it checks Cuphead's pid every 2 s and flushes on Exit) before killing it, so a player's report always
  carries the engine's lines. The "Fallback handler could not load library .../data-<address>.dll" lines at the top of ROUNDS's
  Player.log are Unity's own (Mono naming in-memory assemblies) and appear without any mod. 0.1.0's engine also signed into Steam and
  ROUNDS printed a Steam session ticket into Player.log; SteamQuiet (0.1.1) keeps the hidden ROUNDS off Steam, so that line is gone.
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
- Sound: Unity's audio still runs in a batch-mode player (EnginePlugin.LogAudio logs the DSP clock moving and the output), so ROUNDS's
  sounds play from the hidden ROUNDS itself. Arena.Sound sets AudioListener.volume to Cuphead's sound-effects gain (C_Volume, from
  AudioManager.masterVolume + sfxOptionsVolume in dB), 0 on the map and menus (mode 0, where only the puppets move), pauses with
  Cuphead (AudioListener.pause), and keeps ROUNDS's music and ambience off (SoundVolumeManager.SetAudioMixerVolumes(1, 0, 1) every
  second, ROUNDS's own defaults for master and effects).

## Faces (the launcher's face editor, ported from Vampire Rounds)
- ROUNDS's faces: CharacterCreatorHandler.playerFaces[10] presets (PlayerPrefs eyeID-<n>, mouthID-<n>, detailID-<n>,
  detail2ID-<n> + offsets; registry HKCU\Software\Landfall Games\ROUNDS, names with _h<hash>), selectedPlayerFaces[4] from
  SelectedFace<player>. ROUNDS's menus show only the first 8, the editable ones.
- faces.txt (`%LOCALAPPDATA%\CupRounds`, src/Shared/FaceFile.cs, also compiled into the launcher): "player1=<eyes id x y>
  <mouth ...>  <detail ...>  <detail2 ...>"; a player with no line wears ROUNDS's own face for its player 1 or 2. The engine's
  Faces.ApplyChoice (EnginePlugin.WaitForCards, before the EngineServer starts) puts them into selectedPlayerFaces in memory
  only (no SelectFace, so ROUNDS's PlayerPrefs are untouched); the arena's players (GetFaceOffline), ROUNDS's pick screen
  (CardChoiceVisuals.RPCA_SetFace), the world-map puppets (Puppet.Dress) and the look sent to Cuphead (SkinExport ->
  FaceRenderer, drawn by Blob, MapBlob and the co-op ghost) all read it. An engine still running from before keeps the faces
  it started with.
- Colours: on a ROUNDS player the face parts use material SFUnshadowed (shader Sprites/SFSoftShadow), which shows colour only
  under ROUNDS's soft-shadow light pass; the engine's cameras have none, so the parts came out near-black. FaceLook swaps it
  for Unity's Sprites/Default on every equip (Harmony postfix on CharacterCreatorItemEquipper.Equip, hooks e_face_colours),
  on the puppets and on FaceRenderer's copies. FaceRenderer also draws the parts in ROUNDS's order (sorting order, then slot)
  instead of slot order, and Cuphead's blob no longer darkens white parts to the shade colour: the face arrives in ROUNDS's
  own colours (dark features, coloured hats and details).
- Pictures: the launcher (Faces.Draw) syncs the profile (GameRunner.SyncProfile, so the face mode is the shipped engine's)
  and runs ROUNDS hidden with `--roundhead-engine --roundhead-faces`, its window kept off screen from the first frame
  (GameRunner.KeepOutOfSight). The engine (Faces.DrawSheet; Awake returns right after starting it: no link, server or arena)
  poses a Puppet per player, standing still and aiming right, hides the gun, clears the face parts and equips each of ROUNDS's
  8 faces (ROUNDS's Equip skips a missing part and would leave the last face's), renders a 1.8-unit square around the head at
  128 px into faces/seat<s>_face<n>.png; then the bare body (faces/parts/body<s>.png) and every CharacterCreatorItemLoader part
  alone at offset 0 on layer 31 in the same frame (3 player scales around the equipper's itemParent, 512 px). parts.txt:
  "frame F size N pxPerOffset P clamp 4.5", "part <kind> <i> <sortingOrder> <defX> <defY> <name>", "preset <i> <face>",
  "selected <p> <n>"; pxPerOffset = size / frame x |itemParent.lossyScale.x| x scaleM (28.672 at 512). The launcher keeps a
  signature of ROUNDS's face registry values and draws again when it changes; PLAY stops a drawing run first;
  `CupRoundsLauncher.exe --draw-faces` draws without the window.
- Launcher (FacesForm, FaceEditorForm, FaceParts): FaceParts lays a face together (body, then the parts by sorting order, each
  moved by offset x pxPerOffset, y up). FACES shows each player's face with EDIT and the 8 ROUNDS faces to start from. The
  editor has EYES / MOUTH / DETAIL 1 / DETAIL 2 grids (each part on the player's head) and drags the topmost part with paint
  under the mouse; a newly chosen part starts at its own place (ROUNDS's SpawnItem) and offsets stay within 4.5 of the face's
  centre (ROUNDS's CharacterCreatorDragging clamp). Reset positions, Undo changes, Cancel, Save.
- FaceRenderer builds each face at the same spot and now removes it with DestroyImmediate: both players' looks are exported
  in one EngineServer pump, and with Destroy (end of frame) player 2's picture also held player 1's parts (unseen while
  both players wore the same ROUNDS face).
- Verified 2026-10-20 on the real games (dev install, 12 Thunderstore mods in the profile): `--draw-faces` drew 16 face pictures
  and 197 parts in 21 s, in colour (star, horns, top hat, blue sweat drop); the FACES window and the editor driven by posted
  clicks and drags (a new mouth, the "R" hat and blue tears as details, the sunglasses dragged, Save wrote faces.txt; player 2
  started from ROUNDS face 7; "Draw the pictures again" redrew in 17 s); a Melty-style test run on Botanic Panic passed every
  check, and the world-map puppets, both ROUNDS players in the level, player 1's ghost and the pick screen wore their faces in
  colour; the engine's look export for both players (skin op) gave each only its own face.

## Campaign flow (src/CupheadMod/GameFlow.cs)
- One save slot: the title's Start goes straight into slot 1 (SlotSelectScreen.EnterGame) with the character the slot was saved
  with; slots 2 and 3 stay on disk and are never loaded, and there is no erase from inside Roundhead.
- Drop-in: PlayerManager.SetPlayerCanJoin always gets promptBeforeJoin false, so player 2 joins on their first press wherever
  Cuphead allows joining (map, a level under way) instead of the interrupting prompt; the mid-level join is the ROUNDS player
  popping in (PlayerJoinEffect stays off).
- No storybook cutscenes: SceneLoader.LoadScene swaps a scene_cutscene_* for its destination (Cutscene.Load's stored target;
  the opening one goes to the world map instead of Elder Kettle's house, whose tutorial stays on the map; the Devil's offer to
  his fight, as refusing does; King Dice's welcome to his palace with every contract, else back to the map). Test runs never
  pass the title or a cutscene.

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

## Known limits (v0.1.2)
- In plane levels the ROUNDS player is held at the plane: cards that move the player (jumps, recoil, knockback, block dashes) do nothing there, and the ROUNDS picture of the player trails the plane by a frame or two.
- The title screen is Cuphead's own art (Cuphead and Mugman).
- Co-op is Cuphead's local 2-player; there is no online play.

## Melty listing wording (2026-10-17)

Melty's publish check flags the words "co-op" and "multiplayer" anywhere in the listing description as a claim of online play that the recipe must set up (recipe.multiplayer), even in a sentence saying there is none. Roundhead's two players share one PC, so the listing says "two players on one PC" and "no online play or matchmaking" instead. Keep those words out of the description unless online play is ever added. The README follows the same wording.
