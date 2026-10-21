using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// Player 1's and player 2's faces (sheets/launcher.json "faces"). The launcher's face editor keeps each player's face in
    /// %LOCALAPPDATA%\CupRounds\faces.txt (FaceFile: ROUNDS's PlayerFace values); the engine puts them into ROUNDS's in-memory
    /// face selection (CharacterCreatorHandler.selectedPlayerFaces), which the arena's players, ROUNDS's pick screen, the
    /// world-map puppets and the look sent to Cuphead (SkinExport) all read, so ROUNDS's own saved faces are left as they are.
    /// A player with no face there wears ROUNDS's own.
    /// For the launcher, a run with --roundhead-faces draws ROUNDS's eight faces on each player, each player's bare body and
    /// every face part ROUNDS has (the editor's pieces, all in one frame around the face), writes their catalogue
    /// (faces/parts/parts.txt) and ends.
    /// </summary>
    public static class Faces
    {
        public const string SheetFlag = "--roundhead-faces";
        public const int ThumbSize = 128;
        /// <summary>The faces ROUNDS shows and edits (its handler holds ten; the last two never appear in its menus).</summary>
        public const int Presets = 8;
        /// <summary>The editor's pictures: a square this many player-scales wide around the face, at PartSize px.</summary>
        public const float PartFrame = 3f;
        public const int PartSize = 512;
        /// <summary>A layer only the part pictures use (the puppet is on 30).</summary>
        private const int PartLayer = 31;
        /// <summary>ROUNDS's face editor keeps a part within this distance of the face's centre (CharacterCreatorDragging).</summary>
        public const float Clamp = 4.5f;
        public static string ChoiceFile => Path.Combine(Proto.DataDir(), "faces.txt");
        public static string SheetDir => Path.Combine(Proto.DataDir(), "faces");
        public static string PartDir => Path.Combine(SheetDir, "parts");
        public static string ThumbFor(int seat, int preset) => Path.Combine(SheetDir, "seat" + seat + "_face" + preset + ".png");
        public static bool SheetMode => Environment.GetCommandLineArgs().Any(a => a == SheetFlag);
        private static readonly string[] Kinds = { "eyes", "mouth", "detail" };

        /// <summary>Before the engine serves Cuphead or makes its players: the launcher's faces into ROUNDS's in-memory selection.</summary>
        public static void ApplyChoice()
        {
            var h = CharacterCreatorHandler.instance;
            if (h == null || h.selectedPlayerFaces == null) { EnginePlugin.L.LogWarning("faces: ROUNDS's face handler is not there; faces stay ROUNDS's own"); return; }
            FaceData[] faces;
            try { faces = FaceFile.Read(ChoiceFile); }
            catch (Exception e) { EnginePlugin.L.LogWarning("faces: " + ChoiceFile + ": " + e.Message); return; }
            for (int seat = 0; seat < faces.Length && seat < h.selectedPlayerFaces.Length; seat++)
            {
                var f = faces[seat];
                if (f == null) continue;
                h.selectedPlayerFaces[seat] = new PlayerFace
                {
                    eyeID = f.Id[0], eyeOffset = new Vector2(f.X[0], f.Y[0]),
                    mouthID = f.Id[1], mouthOffset = new Vector2(f.X[1], f.Y[1]),
                    detailID = f.Id[2], detailOffset = new Vector2(f.X[2], f.Y[2]),
                    detail2ID = f.Id[3], detail2Offset = new Vector2(f.X[3], f.Y[3]),
                };
                EnginePlugin.L.LogInfo("faces: player " + (seat + 1) + " wears the launcher's face " + f.Format());
            }
        }

        private static FaceData Data(PlayerFace p)
        {
            var f = new FaceData();
            if (p == null) return f;
            f.Id = new[] { p.eyeID, p.mouthID, p.detailID, p.detail2ID };
            f.X = new[] { p.eyeOffset.x, p.mouthOffset.x, p.detailOffset.x, p.detail2Offset.x };
            f.Y = new[] { p.eyeOffset.y, p.mouthOffset.y, p.detailOffset.y, p.detail2Offset.y };
            return f;
        }

        private static bool Ready()
        {
            try { return CharacterCreatorHandler.instance != null && CharacterCreatorItemLoader.instance != null && PlayerSkinBank.GetPlayerSkinColors(1) != null; }
            catch { return false; }
        }

        /// <summary>The launcher's pictures and the editor's parts, then the engine ends.</summary>
        public static IEnumerator DrawSheet()
        {
            float giveUp = Time.realtimeSinceStartup + 60f;
            while (!Ready())
            {
                if (Time.realtimeSinceStartup > giveUp) { EnginePlugin.Exit("faces: ROUNDS's face editor never loaded"); yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            var h = CharacterCreatorHandler.instance;
            var loader = CharacterCreatorItemLoader.instance;
            var catalogue = new List<string>();
            int drawn = 0, parts = 0;
            Directory.CreateDirectory(PartDir);
            for (int seat = 0; seat < FaceFile.Seats.Length; seat++)
            {
                // The world map's puppet, standing still and aiming right (its default pose), is the posed player.
                var pose = new Puppet(seat);
                bool made = false;
                try { made = pose.Create(); }
                catch (Exception e) { EnginePlugin.L.LogWarning("faces: puppet " + seat + ": " + e); }
                if (!made) continue;
                for (int f = 0; f < 30; f++) { pose.Tick(1f / 60f); yield return null; }
                pose.ShowGun(false);
                float s = pose.Scale;
                for (int preset = 0; preset < Presets && preset < h.playerFaces.Length; preset++)
                {
                    try { pose.EquipFace(h.GetFacePreset(preset)); }
                    catch (Exception e) { EnginePlugin.L.LogWarning("faces: face " + preset + ": " + e.Message); continue; }
                    // The face's parts appear and take their places over the next frames.
                    for (int f = 0; f < 3; f++) { pose.Tick(1f / 60f); pose.Relayer(); yield return null; }
                    try
                    {
                        // The body and what sits on it (hats rise above it): a square around the head, a bit above its centre.
                        pose.SavePng(ThumbFor(seat, preset), pose.Center + new Vector3(0f, 0.3f * s, 0f), 1.8f * s, ThumbSize);
                        drawn++;
                    }
                    catch (Exception e) { EnginePlugin.L.LogWarning("faces: picture " + seat + "/" + preset + ": " + e.Message); }
                }
                // The editor's pieces: the bare body, then (once, they are the same on both players) every part alone.
                pose.ClearFace();
                for (int f = 0; f < 3; f++) { pose.Tick(1f / 60f); pose.Relayer(); yield return null; }
                Vector3 center = pose.FaceParent.position;
                float frame = PartFrame * s;
                try { pose.SavePng(Path.Combine(PartDir, "body" + seat + ".png"), center, frame, PartSize); }
                catch (Exception e) { EnginePlugin.L.LogWarning("faces: body " + seat + ": " + e.Message); }
                if (seat == 0)
                {
                    float pxPerOffset = PartSize / frame * Mathf.Abs(pose.FaceParent.lossyScale.x) * pose.FaceScaleM;
                    catalogue.Add("frame " + F(frame) + " size " + PartSize + " pxPerOffset " + F(pxPerOffset) + " clamp " + F(Clamp));
                    var lists = new[] { loader.eyes, loader.mouths, loader.accessories };
                    var types = new[] { CharacterItemType.Eyes, CharacterItemType.Mouth, CharacterItemType.Detail };
                    for (int k = 0; k < lists.Length; k++)
                    {
                        if (lists[k] == null) continue;
                        for (int i = 0; i < lists[k].Length; i++)
                        {
                            var item = lists[k][i];
                            if (item == null) continue;
                            try
                            {
                                pose.EquipPart(item, types[k], PartLayer);
                                pose.SavePng(Path.Combine(PartDir, Kinds[k] + i + ".png"), center, frame, PartSize, PartLayer);
                                var sr = item.GetComponent<SpriteRenderer>();
                                catalogue.Add("part " + Kinds[k] + " " + i + " " + (sr != null ? sr.sortingOrder : 0) + " " + F(item.offset.x) + " " + F(item.offset.y) + " " + item.name.Replace(' ', '_'));
                                parts++;
                            }
                            catch (Exception e) { EnginePlugin.L.LogWarning("faces: part " + Kinds[k] + " " + i + ": " + e.Message); }
                            if (parts % 8 == 0) yield return null;
                        }
                    }
                    pose.ClearFace();
                    for (int preset = 0; preset < Presets && preset < h.playerFaces.Length; preset++)
                        catalogue.Add("preset " + preset + " " + Data(h.GetFacePreset(preset)).Format());
                    for (int p = 0; p < FaceFile.Seats.Length && p < h.selectedFaceID.Length; p++)
                        catalogue.Add("selected " + p + " " + h.selectedFaceID[p]);
                }
                pose.Destroy();
            }
            try { File.WriteAllLines(Path.Combine(PartDir, "parts.txt"), catalogue.ToArray()); }
            catch (Exception e) { EnginePlugin.L.LogWarning("faces: catalogue: " + e.Message); }
            EnginePlugin.L.LogInfo("faces: " + drawn + " face pictures and " + parts + " face parts drawn into " + SheetDir);
            try { File.WriteAllText(Path.Combine(SheetDir, "done.txt"), DateTime.Now.ToString("o")); } catch { }
            EnginePlugin.Exit("faces drawn");
        }

        private static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
