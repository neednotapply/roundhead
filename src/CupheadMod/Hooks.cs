using HarmonyLib;
using UnityEngine;

namespace CupRounds
{
    /// <summary>Every Harmony patch on Cuphead (sheets/hooks.json).</summary>
    [HarmonyPatch]
    public static class Hooks
    {
        [HarmonyPostfix, HarmonyPatch(typeof(LevelPlayerController), "LevelInit")]
        public static void PlayerInit(LevelPlayerController __instance)
        {
            BlobController.Attach(__instance);
        }

        /// <summary>While ROUNDS's real player stands in, Cuphead's motor stays still (the player object follows ROUNDS).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LevelPlayerMotor), "FixedUpdate")]
        public static bool MotorFixed(LevelPlayerMotor __instance)
        {
            var c = BlobController.For(__instance);
            return c == null || !(c.Arena || ArenaClient.Waiting(c));
        }

        /// <summary>A Cuphead co-op revive (after the player is placed where the ghost was parried) brings ROUNDS's player back
        /// there, even if ROUNDS is briefly not answering: the spawn is resent until ROUNDS confirms it.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(AbstractPlayerController), "OnRevive")]
        public static void StatsRevive(AbstractPlayerController __instance)
        {
            var c = ArenaSeat.For(__instance);
            if (c != null) ArenaClient.OnCupheadRevived(c);
        }

        /// <summary>A ROUNDS-driven player's death: Cuphead's ghost sprite and explosion puff stay off (ROUNDS plays its own death
        /// burst) and a ROUNDS-style ghost floats in their place around Cuphead's parry heart, so co-op revives still work
        /// (PlayerDeathEffect.cs:95-107; Init picks the sprite and switches it on). A plane level's player, drawn as the flying
        /// ROUNDS character (PlaneBlob), gets the same ghost.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerDeathEffect), "Init")]
        public static void DeathGhost(PlayerDeathEffect __instance, PlayerId playerId)
        {
            DressAnyGhost(__instance, playerId);
        }

        private static void DressAnyGhost(PlayerDeathEffect e, PlayerId id)
        {
            var p = PlayerManager.GetPlayer(id);
            var c = BlobController.For(p);
            if (c != null) PlayerSprites.DressGhost(e, c);
            else if (p != null && p.GetComponent<PlaneBlob>() != null) PlayerSprites.DressGhost(e, null, id == PlayerId.PlayerTwo ? 1 : 0);
        }

        /// <summary>The same for a ghost that never ran that Init (a scene-placed TutorialPlayerDeathEffect runs its own,
        /// TutorialPlayerDeathEffect.Start); a ghost from before the blob is dressed when the blob attaches (DressGhosts).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerDeathEffect), "Start")]
        public static void DeathGhostStart(PlayerDeathEffect __instance)
        {
            if (Reflect.Get(__instance, "playerId") is PlayerId id) DressAnyGhost(__instance, id);
        }

        /// <summary>With the Astral Cookie (or Chalice carried over) Cuphead stands a separate Cuphead/Mugman body at the spawn
        /// through the whole intro (LevelPlayerAnimationController.cs:803-817, LevelPlayerChaliceIntroAnimation.cs:11-18): it
        /// stays off; the ROUNDS character is there already.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(LevelPlayerChaliceIntroAnimation), "Create", new[] { typeof(Vector3), typeof(bool), typeof(bool) })]
        public static void ChaliceIntro(LevelPlayerChaliceIntroAnimation __result)
        {
            if (BlobController.All.Count > 0) PlayerSprites.HideAll(__result);
        }

        /// <summary>Cuphead's drop shadow under a ROUNDS-driven player sits at the ROUNDS player's feet. Cuphead casts for the
        /// floor from its own player object, which is matched to the ROUNDS body by centre and is taller, so the cast began
        /// inside the floor and the shadow drew a body below the feet (LevelPlayerShadow.cs:28-55). The cast now starts at the
        /// ROUNDS body, and the shadow shows while standing too (ROUNDS's players always have one): its size follows the height
        /// of the body's underside over the floor, as Cuphead's does with its feet.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(LevelPlayerShadow), "Update")]
        public static void PlayerShadow(LevelPlayerShadow __instance)
        {
            var c = BlobController.For(__instance);
            if (c == null || !c.Arena) return;
            Vector2 centre;
            float radius;
            if (!ArenaClient.RoundsBody(c, out centre, out radius)) return;
            var shadow = Reflect.Get<Transform>(__instance, "shadow");
            var sr = Reflect.Get<SpriteRenderer>(__instance, "spriteRenderer");
            var sprites = Reflect.Get<Sprite[]>(__instance, "shadowSprites");
            if (shadow == null || sr == null || sprites == null || sprites.Length == 0) return;
            if (c.player.IsDead) { sr.enabled = false; return; }
            float maxDistance = Mathf.Max(1, Reflect.Get<int>(__instance, "maxDistance"));
            bool up = c.player.motor != null && c.player.motor.GravityReversed;
            int mask = up ? 524288 : 1048576;   // Cuphead's ceiling / ground layers (LevelPlayerShadow.cs:13-14)
            RaycastHit2D hit = Physics2D.BoxCast(centre, new Vector2(radius * 2f, 1f), 0f, up ? Vector2.up : Vector2.down, maxDistance + radius, mask);
            if (hit.collider == null) { sr.enabled = false; return; }
            var platform = hit.collider.GetComponent<LevelPlatform>();
            if (platform != null && !platform.AllowShadows) { sr.enabled = false; return; }
            float underside = up ? centre.y + radius : centre.y - radius;
            shadow.position = new Vector3(centre.x, hit.point.y, shadow.position.z);
            int n = (int)(Mathf.Abs(underside - hit.point.y) / maxDistance * sprites.Length);
            if (n < 0 || n >= sprites.Length) { sr.enabled = false; return; }
            sr.enabled = true;
            sr.sprite = sprites[n];
        }

        /// <summary>Leaving the tutorial house: the Cuphead/Mugman running out of the door stays off (HouseLevelExit.cs:37-50).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(HouseLevelExit), "SwitchToRun")]
        public static void HouseExit(HouseLevelExit __instance)
        {
            if (BlobController.All.Count == 0) return;
            foreach (string f in new[] { "cupheadRunning", "mugmanRunning", "chaliceRunning" })
            {
                var go = Reflect.Get<GameObject>(__instance, f);
                if (go != null) PlayerSprites.HideAll(go.transform);
            }
        }

        /// <summary>The game-over card's boss timeline marks how far each player got with a Cuphead/Mugman head
        /// (LevelGameOverGUI.cs:52-66, timelineIcon_cr animates the Image's colour): a dot in the player's ROUNDS colour instead.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(LevelGameOverGUI.TimelineObjects), "Setup")]
        public static void GameOverIcons(LevelGameOverGUI.TimelineObjects __instance)
        {
            if (ArenaSeat.Seats.Count == 0) return;
            int first = PlayerManager.player1IsMugman ? 1 : 0; // the cuphead icon is player 2's when player 1 is Mugman
            Mark(__instance.cuphead, first);
            Mark(__instance.mugman, 1 - first);
            Mark(__instance.chalice, PlayerManager.Multiplayer && PlayerManager.GetPlayer(PlayerId.PlayerTwo).stats.isChalice ? 1 : 0);
        }

        /// <summary>The head Image goes dark and a dot in the player's colour rides on its rect (the card's own animation and
        /// timelineIcon_cr keep moving and fading the Image; the dot copies its alpha, UiAlphaFollower).</summary>
        private static void Mark(UnityEngine.UI.Image img, int index)
        {
            if (img == null) return;
            Color col, shade;
            BlobBody.Colors(index, Plugin.Skins[index], out col, out shade);
            img.enabled = false;
            Transform old = img.transform.Find("RoundsMark");
            if (old != null) Object.Destroy(old.gameObject);
            // The head sprite sits small inside a much larger, mostly empty rect (201 px for a head about 35 px tall): the dot takes 0.17 of it.
            var go = new GameObject("RoundsMark", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(img.rectTransform, false);
            Rect pr = img.rectTransform.rect;
            float size = Mathf.Max(8f, Mathf.Min(pr.width, pr.height) * 0.17f);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(size, size);
            var dot = go.AddComponent<UnityEngine.UI.Image>();
            dot.sprite = Gfx.TintedDisc(col);
            dot.raycastTarget = false;
            go.AddComponent<UiAlphaFollower>().Init(img, dot);
            Plugin.L.LogInfo("game-over timeline: ROUNDS dot for player " + (index + 1) + " (icon rect " + pr.width.ToString("0") + "x" + pr.height.ToString("0") + ", sprite " + (img.sprite != null ? img.sprite.rect.width.ToString("0") + "x" + img.sprite.rect.height.ToString("0") : "none") + ")");
        }

        /// <summary>The character-shaped death explosion and its shadow stay off (LevelPlayerDeathEffect.cs:14-42; a leaving
        /// player's explosion too): ROUNDS's burst is the death.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(LevelPlayerDeathEffect), "Init", new[] { typeof(Vector2), typeof(PlayerId), typeof(bool) })]
        public static void DeathExplosion(LevelPlayerDeathEffect __instance, PlayerId id)
        {
            if (BlobController.For(PlayerManager.GetPlayer(id)) != null) PlayerSprites.HideAll(__instance);
        }

        /// <summary>The Cuphead/Mugman jump-in of a mid-level join stays off (PlayerJoinEffect.cs:30-46); ROUNDS's revive pop-in
        /// shows the player arriving.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerJoinEffect), "Init")]
        public static void JoinEffect(PlayerJoinEffect __instance, PlayerId playerId)
        {
            if (BlobController.For(PlayerManager.GetPlayer(playerId)) != null) PlayerSprites.HideAll(__instance);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(LevelPlayerMotor), "Start")]
        public static void MotorStart(LevelPlayerMotor __instance)
        {
            var c = BlobController.For(__instance);
            if (c == null) return;
            c.motor.ApplyMove();
            c.motor.ApplyJump();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(LevelPlayerMotor), "HandleJumping")]
        public static bool MotorJump(LevelPlayerMotor __instance)
        {
            var c = BlobController.For(__instance);
            return c == null || c.motor.Jump();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(LevelPlayerMotor), "HandleWalking")]
        public static void MotorWalk(LevelPlayerMotor __instance)
        {
            var c = BlobController.For(__instance);
            if (c != null) c.motor.Walk();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(LevelPlayerMotor), "HandleFalling")]
        public static bool MotorFalling(LevelPlayerMotor __instance)
        {
            var c = BlobController.For(__instance);
            return c == null || c.motor.Fall();
        }

        /// <summary>Parry starts from the ROUNDS block instead of a second jump press (which is an air jump now).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LevelPlayerMotor), "HandleParry")]
        public static bool MotorParry(LevelPlayerMotor __instance)
        {
            var c = BlobController.For(__instance);
            if (c == null) return true;
            if (!c.block.WantsParry || __instance.IsHit || __instance.Parrying) return false;
            object parryManager = Reflect.Get(__instance, "parryManager");
            if (Reflect.EnumName(parryManager, "state") != "Ready") return false;
            c.block.ConsumeParry();
            Reflect.Call(__instance, "ClearBufferedInput");
            Reflect.Set(Reflect.Get(__instance, "hitManager"), "state", "Inactive");
            Reflect.Set(parryManager, "state", "NotReady");
            Reflect.Set(__instance, "Parrying", true);
            Reflect.Raise(__instance, "OnParryEvent");
            return false;
        }

        /// <summary>The blob has no dash: Cuphead's dash button is the ROUNDS block.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LevelPlayerMotor), "HandleDash")]
        public static bool MotorDash(LevelPlayerMotor __instance, ref bool __result)
        {
            if (BlobController.For(__instance) == null) return true;
            __result = false;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(LevelPlayerWeaponManager), "HandleWeaponFiring")]
        public static bool WeaponFiring(LevelPlayerWeaponManager __instance)
        {
            return BlobController.For(__instance) == null;
        }

        /// <summary>The blob has no Cuphead weapons; the switch button reaches ROUNDS as a press counter instead
        /// (ArenaClient.WriteSlot2, PCE Laser), counted per frame because this method runs in FixedUpdate.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LevelPlayerWeaponManager), "HandleWeaponSwitch")]
        public static bool WeaponSwitch(LevelPlayerWeaponManager __instance)
        {
            return BlobController.For(__instance) == null;
        }

        /// <summary>Hearts from ROUNDS max health; Cuphead charms that add hearts still add them.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerStatsManager), "CalculateHealthMax")]
        public static void HealthMax(PlayerStatsManager __instance)
        {
            var c = BlobController.For(__instance);
            if (c == null) return;
            int charmBonus = Mathf.Max(0, __instance.HealthMax - 3);
            Reflect.Set(__instance, "HealthMax", Mathf.Clamp(c.health.Hearts + charmBonus, 1, (int)Tuning.maxHearts));
        }

        /// <summary>A hit inside the block window (or caught by auto block, or in decay grace) does nothing. A plane level's
        /// player without the arena takes Cuphead's own hit.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerStatsManager), "OnDamageTaken")]
        public static bool DamageTaken(PlayerStatsManager __instance, DamageDealer.DamageInfo info)
        {
            var seat = ArenaSeat.For(__instance);
            if (seat == null || info == null || info.damage <= 0f) return true;
            // ROUNDS's player takes the hit (its block, health and death); Cuphead's own health never moves.
            if (seat.Arena) return ArenaClient.OnCupheadDamage(seat, info);
            var c = seat as BlobController;
            if (c == null) return true;
            // Hidden and held at the spawn point until ROUNDS's player arrives: nothing hurts it meanwhile.
            if (ArenaClient.Waiting(c)) return false;
            if (info.damageSource == DamageDealer.DamageSource.Pit) return true;
            if (c.block.IsBlocking || c.health.InDecayGrace || c.block.AutoBlock() || TestMode.Immune)
            {
                c.body.FlashBlock(false);
                return false;
            }
            c.health.lastHitTime = Time.time;
            c.body.MarkHit();
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerStatsManager), "OnStatsDeath")]
        public static bool StatsDeath(PlayerStatsManager __instance)
        {
            var c = BlobController.For(__instance);
            return c == null || c.Arena || !c.health.TryRespawn();
        }

        /// <summary>A won level ends the match: both decks are wiped (sheets/progression.json, level_won).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(Level), "zHack_OnWin")]
        public static void LevelWin(Level __instance)
        {
            Progress.OnLevelWon(__instance);
        }

        /// <summary>A lost level: the game-over card comes in (LevelGameOverGUI.In) and each player gets a loss pick on it
        /// (sheets/progression.json, loss).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(LevelGameOverGUI), "In")]
        public static void GameOverIn()
        {
            Progress.OnLevelLost(Level.Current);
        }

        /// <summary>The game-over card's menu (Retry, Exit to Map, Quit) waits while the loss picks are made on it
        /// (LevelGameOverGUI.cs:196-237 reads the menu input in Update).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LevelGameOverGUI), "Update")]
        public static bool GameOverMenu()
        {
            return PickOverlay.I == null || !PickOverlay.I.HoldsGameOver();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Map), "Start")]
        public static void MapStart()
        {
            Progress.RefreshBuilds();
        }

        /// <summary>Erasing a Cuphead save slot erases its ROUNDS decks too.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerData), "ClearSlot")]
        public static void SlotCleared(int slot)
        {
            Progress.ClearSlot(slot);
        }

        /// <summary>Plane levels: the player flies as their ROUNDS player (PlaneBlob); the plane controls stay Cuphead's.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(PlanePlayerController), "LevelInit")]
        public static void PlaneInit(PlanePlayerController __instance)
        {
            PlaneBlob.Attach(__instance);
        }

        /// <summary>While ROUNDS's player flies with the plane, the ROUNDS gun is the weapon: the plane's shots, EX, super and
        /// weapon switch stay off (PlanePlayerWeaponManager.FixedUpdate runs CheckBasic, CheckEx and HandleWeaponSwitch), and
        /// a shot it was firing when ROUNDS took over ends. Without the arena the plane shoots as Cuphead's.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlanePlayerWeaponManager), "FixedUpdate")]
        public static bool PlaneWeapons(PlanePlayerWeaponManager __instance)
        {
            var c = ArenaSeat.For(__instance);
            if (c == null || !c.Arena) return true;
            if (__instance.IsShooting) Reflect.Call(__instance, "EndBasic");
            return false;
        }

        /// <summary>The results screen after a win shows the players' ROUNDS characters instead of Cuphead and Mugman, and
        /// without their names (ResultsLook).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(WinScreen), "Awake")]
        public static void ResultsScreen(WinScreen __instance)
        {
            ResultsLook.Attach(__instance);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(MapPlayerController), "Start")]
        public static void MapPlayerStart(MapPlayerController __instance)
        {
            MapBlob.Attach(__instance);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ShopScene), "Awake")]
        public static void ShopStart()
        {
            ShopPackOverlay.ShopOpened();
        }

        /// <summary>One save slot: the title's Start goes straight into slot 1 (GameFlow).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(SlotSelectScreen), "UpdateMainMenu")]
        public static void SingleSlot(SlotSelectScreen __instance)
        {
            GameFlow.EnterSingleSlot(__instance);
        }

        /// <summary>Drop-in two-player play: player 2 joins on their first press, without the "join?" prompt (GameFlow).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerManager), "SetPlayerCanJoin")]
        public static void DropIn(ref bool promptBeforeJoin)
        {
            GameFlow.DropIn(ref promptBeforeJoin);
        }

        /// <summary>No storybook cutscenes: each is swapped for the scene it leads to (GameFlow).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(SceneLoader), "LoadScene", new[] { typeof(Scenes), typeof(SceneLoader.Transition), typeof(SceneLoader.Transition), typeof(SceneLoader.Icon), typeof(SceneLoader.Context) })]
        public static void SkipCutscene(ref Scenes scene)
        {
            GameFlow.SkipCutscene(ref scene);
        }

        /// <summary>Test runs never write the player's Cuphead save.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerData), "SaveCurrentFile")]
        public static bool TestNoSave()
        {
            return !TestMode.Active;
        }

        /// <summary>Per-enemy coroutine time (CupheadTime.cs:107-125): boss and minion attack timing yields
        /// CupheadTime.WaitForSeconds(this, t), so a slowed or stunned enemy's waits run on its ROUNDS speed, with a hard
        /// cap. Costs one counter increment while no enemy shows a status.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(CupheadTime), "WaitForSeconds", new[] { typeof(MonoBehaviour), typeof(float) })]
        public static bool EnemyWait(MonoBehaviour m, float time, ref Coroutine __result)
        {
            return EnemyStatus.Dilate(m, time, CupheadTime.Layer.Default, ref __result);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CupheadTime), "WaitForSeconds", new[] { typeof(MonoBehaviour), typeof(float), typeof(CupheadTime.Layer) })]
        public static bool EnemyWaitLayer(MonoBehaviour m, float time, CupheadTime.Layer layer, ref Coroutine __result)
        {
            return EnemyStatus.Dilate(m, time, layer, ref __result);
        }

        /// <summary>Silence and confuse: a projectile that appears at a silenced enemy becomes a harmless dud
        /// (AbstractProjectile.cs:230; parryable ones stay live).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(AbstractProjectile), "Start")]
        public static void SilencedShot(AbstractProjectile __instance)
        {
            EnemyStatus.SilencedShot(__instance);
        }

        /// <summary>The same test for pooled projectiles, which come back through ObjectPool.Spawn with SetActive(true) and no
        /// new Start (ObjectPool.cs:161-200; every Spawn overload ends here). Costs one check while no enemy shows a status.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(ObjectPool), "Spawn", new[] { typeof(GameObject), typeof(Transform), typeof(Vector3), typeof(Quaternion) })]
        public static void SilencedSpawn(GameObject __result)
        {
            EnemyStatus.SilencedSpawn(__result);
        }
    }
}
