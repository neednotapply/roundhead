using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CupRounds
{
    /// <summary>
    /// The live link between Cuphead and the ROUNDS arena (sheets/ipc.json, "arena" rows): one block of shared memory.
    /// Cuphead writes the control part every frame (camera, each player's input, the level's colliders, events such as
    /// damage); ROUNDS writes the result part every frame (where its players are, health, block, bullet hits on Cuphead
    /// targets, card picks) and its rendered layer (players, bullets, effects, ROUNDS UI) into one of three frame buffers.
    /// Counters only ever go up, so a reader never misses a press, a hit or an event.
    /// </summary>
    public unsafe sealed class ArenaLink : IDisposable
    {
        public const string DefaultMapName = "Local\\CupRoundsArena3"; // bumped with the layout (6 frame buffers): an older build never shares it
        /// <summary>Developer runs can use a private link (ROUNDHEAD_DEV_ARENA) so they never meet a real session.</summary>
        public static readonly string MapName = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROUNDHEAD_DEV_ARENA")) ? DefaultMapName : Environment.GetEnvironmentVariable("ROUNDHEAD_DEV_ARENA");
        public const int Magic = 0x43524133; // "CRA3"
        public const int MaxW = 1280, MaxH = 720;
        public const int FrameBytes = MaxW * MaxH * 4;
        // Frames: three world buffers (players, bullets, effects: drawn at the Cuphead player's depth, under Cuphead's
        // foreground) and three UI buffers (card bar, pick texts: drawn over everything), filled as a pair per published frame.
        public const int ControlOff = 0, ResultOff = 65536, FramesOff = 131072;
        public const long UiFramesOff = FramesOff + 3L * FrameBytes;
        public const long Size = FramesOff + 6L * FrameBytes;

        // control (Cuphead -> ROUNDS)
        public const int C_Magic = 0, C_Beat = 4, C_Pid = 8, C_Mode = 12, C_CamX = 16, C_CamY = 20, C_CamOrtho = 24, C_Aspect = 28,
            C_FrameW = 32, C_FrameH = 36, C_Scale = 40, C_Reading = 44, C_Level = 48, C_ColCount = 52, C_EventWrite = 56, C_Paused = 60,
            C_Slots = 64, SlotSize = 64, C_Events = 192, EventSize = 32, EventRing = 64, C_Pick = 2304, C_Cols = 4096, ColSize = 160, MaxCols = 256;
        // per slot input
        public const int S_Present = 0, S_MoveX = 4, S_MoveY = 8, S_AimX = 12, S_AimY = 16, S_JumpHeld = 20, S_JumpPresses = 24,
            S_ShootHeld = 28, S_BlockPresses = 32, S_AcceptPresses = 36, S_MouseX = 40, S_MouseY = 44, S_NavPresses = 48, S_NavDir = 52, S_ShootPresses = 56;
        // pick request
        public const int P_Serial = 0, P_Slot = 4, P_Count = 8;
        // collider entry
        public const int K_Id = 0, K_Kind = 4, K_Shape = 8, K_Points = 12, K_Data = 16; // 32 floats from K_Data
        /// <summary>Collider entry: which Cuphead enemy (DamageReceiver) owns it, and its flags.</summary>
        public const int K_Group = 144, K_Flags = 148;
        public const int TfLight = 1, TfBoss = 2, TfNoStatus = 4;
        public const int KindSolid = 0, KindGround = 1, KindPlatform = 2, KindTarget = 3;
        public const int ShapeBox = 0, ShapeCircle = 1, ShapePoly = 2, ShapeOff = 3;
        // events
        public const int E_Serial = 0, E_Type = 4, E_Slot = 8, E_A = 12, E_B = 16, E_C = 20, E_D = 24, E_Life = 28;
        public const int EvDamage = 1, EvRevive = 2, EvTeleport = 3, EvKill = 4, EvPit = 5, EvPark = 6, EvSpawn = 7, EvBlockedHit = 8;
        /// <summary>Extra per-player input block (cursor in Cuphead world units, keys that only some cards use).</summary>
        public const int C_Slot2 = 2560, Slot2Size = 32, U_CursorX = 0, U_CursorY = 4, U_CursorOn = 8, U_DownHeld = 12, U_SwitchPresses = 16;

        // result (ROUNDS -> Cuphead)
        public const int R_Magic = 0, R_Beat = 4, R_Pid = 8, R_State = 12, R_FrameSerial = 16, R_Ready = 20, R_FrameCamX = 24, R_FrameCamY = 28,
            R_FrameOrtho = 32, R_FrameW = 36, R_FrameH = 40, R_HitWrite = 44, R_PickDone = 48, R_PickedCount = 52, R_Level = 56,
            R_Slots = 64, R_Picked = 256, PickedSize = 64, MaxPicked = 8, R_Hits = 1024, HitSize = 32, HitRing = 128;
        public const int StateOff = 0, StateArena = 1, StatePicking = 2;
        // per slot result
        public const int Q_Present = 0, Q_X = 4, Q_Y = 8, Q_VX = 12, Q_VY = 16, Q_Hp = 20, Q_MaxHp = 24, Q_Dead = 28, Q_Blocking = 32,
            Q_Grounded = 36, Q_Blocks = 40, Q_AimX = 44, Q_AimY = 48, Q_Radius = 52, Q_Cards = 56, Q_Life = 60;
        // hit entry
        public const int H_Serial = 0, H_Target = 4, H_Damage = 8, H_X = 12, H_Y = 16, H_Slot = 20, H_Kind = 24, H_Color = 28;
        public const int HitBullet = 0, HitDot = 1, HitArea = 2, HitContinuous = 3, HitContact = 4, HitAura = 5;

        // effect ring (ROUNDS -> Cuphead): one-shot status events, coalesced per enemy per type per ROUNDS frame
        public const int R_FxWrite = 5120, R_Fx = 5124, FxSize = 32, FxRing = 256;
        public const int FX_Serial = 0, FX_Target = 4, FX_Type = 8, FX_A = 12, FX_B = 16, FX_C = 20, FX_D = 24, FX_Slot = 28;
        public const int FxSlow = 1, FxStun = 2, FxSilence = 3, FxDot = 4, FxForce = 5, FxPull = 6, FxGravity = 7, FxGold = 8, FxConfuse = 9, FxMurder = 10, FxShake = 11;

        // status table (ROUNDS -> Cuphead): every enemy's current statuses under ROUNDS's rules, rewritten each ROUNDS frame.
        // R_StatusFrame is a sequence lock: ROUNDS makes it odd before writing the count and rows and even after. A reader takes
        // a copy only when it read the same even value before and after copying (otherwise it keeps the last good copy).
        public const int R_StatusFrame = 13320, R_StatusCount = 13324, R_Status = 13328, StatusSize = 32, MaxStatus = 256;
        public const int T_Group = 0, T_Slow = 4, T_Stun = 8, T_Silence = 12, T_Dot = 16, T_DotRgba = 20, T_Flags = 24, T_Aux = 28;
        public const int StGold = 1, StConfused = 2, StHeavy = 4, StFlipped = 8;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attr, uint protect, uint sizeHigh, uint sizeLow, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr addr);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr addr, out MemoryBasicInformation info, UIntPtr len);
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public UIntPtr RegionSize; public uint State, Protect, Type; }

        private IntPtr handle, view;
        public byte* Base { get; private set; }

        /// <summary>What the last Open did (for the callers' logs): the section's name and whether it was made or found.</summary>
        public static string LastOpenNote = "";

        public static ArenaLink Open()
        {
            var l = new ArenaLink();
            l.handle = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, (uint)(Size >> 32), (uint)(Size & 0xFFFFFFFF), MapName);
            int err = Marshal.GetLastWin32Error();
            if (l.handle == IntPtr.Zero) { LastOpenNote = MapName + ": CreateFileMapping failed (error " + err + ")"; return null; }
            bool existed = err == 183; // ERROR_ALREADY_EXISTS
            l.view = MapViewOfFile(l.handle, 0x0002 | 0x0004, 0, 0, UIntPtr.Zero);
            if (l.view == IntPtr.Zero) { LastOpenNote = MapName + ": MapViewOfFile failed (error " + Marshal.GetLastWin32Error() + ")"; CloseHandle(l.handle); return null; }
            // An existing section comes at its creator's size, whatever was asked for: one made by a build with a smaller layout
            // would be read past its end, so it is refused (the callers log "no shared memory" and go without the arena).
            MemoryBasicInformation info;
            long region = -1;
            if (VirtualQuery(l.view, out info, (UIntPtr)(ulong)Marshal.SizeOf(typeof(MemoryBasicInformation))) != UIntPtr.Zero) region = (long)(ulong)info.RegionSize;
            if (region >= 0 && region < Size)
            {
                LastOpenNote = MapName + " exists with a smaller layout (" + (region >> 20) + " MB, this build needs " + (Size >> 20) + " MB): refused";
                UnmapViewOfFile(l.view);
                CloseHandle(l.handle);
                return null;
            }
            LastOpenNote = MapName + (existed ? " found" : " made") + " (" + (Size >> 20) + " MB, view region " + (region >> 20) + " MB)";
            l.Base = (byte*)l.view.ToPointer();
            return l;
        }

        public void Dispose()
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            if (handle != IntPtr.Zero) CloseHandle(handle);
            view = handle = IntPtr.Zero;
            Base = null;
        }

        public int I(int off) => *(int*)(Base + off);
        public float F(int off) => *(float*)(Base + off);
        public void I(int off, int v) => *(int*)(Base + off) = v;
        public void F(int off, float v) => *(float*)(Base + off) = v;
        public void Inc(int off) { Thread.MemoryBarrier(); *(int*)(Base + off) += 1; }
        public IntPtr Frame(int i) => new IntPtr(Base + FramesOff + (long)i * FrameBytes);
        public IntPtr UiFrame(int i) => new IntPtr(Base + UiFramesOff + (long)i * FrameBytes);

        public string Str(int off, int max)
        {
            var b = new char[max];
            int n = 0;
            while (n < max && Base[off + n] != 0) { b[n] = (char)Base[off + n]; n++; }
            return new string(b, 0, n);
        }

        public void Str(int off, int max, string s)
        {
            int n = Math.Min(max - 1, s == null ? 0 : s.Length);
            for (int i = 0; i < n; i++) Base[off + i] = (byte)(s[i] < 128 ? s[i] : '?');
            Base[off + n] = 0;
        }

        public static int Slot(int slot) => C_Slots + slot * SlotSize;
        public static int Slot2(int slot) => C_Slot2 + slot * Slot2Size;
        public static int RSlot(int slot) => ResultOff + R_Slots + slot * SlotSize;

        /// <summary>Cuphead: queue an event for ROUNDS (ring of 64; ROUNDS reads every frame).</summary>
        public void PostEvent(int type, int slot, float a = 0f, float b = 0f, float c = 0f, float d = 0f, int life = 0)
        {
            int n = I(C_EventWrite);
            int e = C_Events + (n % EventRing) * EventSize;
            I(e + E_Serial, n + 1);
            I(e + E_Type, type);
            I(e + E_Slot, slot);
            F(e + E_A, a); F(e + E_B, b); F(e + E_C, c); F(e + E_D, d);
            I(e + E_Life, life);
            Inc(C_EventWrite);
        }

        /// <summary>ROUNDS: record a hit on a Cuphead target (ring of 128; Cuphead reads every frame). rgba 0 = no tint.</summary>
        public void PostHit(int target, float damage, float x, float y, int slot, int kind = HitBullet, int rgba = 0)
        {
            int n = I(ResultOff + R_HitWrite);
            int h = ResultOff + R_Hits + (n % HitRing) * HitSize;
            // The serial goes last (0 while the entry is written): a reader that sees serial == its index + 1 before and after
            // copying the fields has the whole entry, never a half-written or lapped one.
            I(h + H_Serial, 0);
            I(h + H_Target, target);
            F(h + H_Damage, damage);
            F(h + H_X, x); F(h + H_Y, y);
            I(h + H_Slot, slot);
            I(h + H_Kind, kind);
            I(h + H_Color, rgba);
            Thread.MemoryBarrier();
            I(h + H_Serial, n + 1);
            Inc(ResultOff + R_HitWrite);
        }

        /// <summary>ROUNDS: a one-shot status event for a Cuphead enemy group (ring of 256; Cuphead reads every frame).</summary>
        public void PostFx(int group, int type, float a, float b = 0f, float c = 0f, float d = 0f, int slot = -1)
        {
            int n = I(ResultOff + R_FxWrite);
            int e = ResultOff + R_Fx + (n % FxRing) * FxSize;
            I(e + FX_Serial, 0); // serial last, as in PostHit
            I(e + FX_Target, group);
            I(e + FX_Type, type);
            F(e + FX_A, a); F(e + FX_B, b); F(e + FX_C, c); F(e + FX_D, d);
            I(e + FX_Slot, slot);
            Thread.MemoryBarrier();
            I(e + FX_Serial, n + 1);
            Inc(ResultOff + R_FxWrite);
        }

        /// <summary>A colour as one int, r&lt;&lt;24 | g&lt;&lt;16 | b&lt;&lt;8 | a (0 = none).</summary>
        public static int Rgba(float r, float g, float b, float a)
        {
            if (a <= 0f || (r <= 0f && g <= 0f && b <= 0f)) return 0;
            int R8 = (int)(Math.Max(0f, Math.Min(1f, r)) * 255f), G8 = (int)(Math.Max(0f, Math.Min(1f, g)) * 255f), B8 = (int)(Math.Max(0f, Math.Min(1f, b)) * 255f), A8 = (int)(Math.Max(0f, Math.Min(1f, a)) * 255f);
            return (R8 << 24) | (G8 << 16) | (B8 << 8) | A8;
        }

        public static void FromRgba(int v, out float r, out float g, out float b, out float a)
        {
            r = ((v >> 24) & 0xFF) / 255f; g = ((v >> 16) & 0xFF) / 255f; b = ((v >> 8) & 0xFF) / 255f; a = (v & 0xFF) / 255f;
        }
    }
}
