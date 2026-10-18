using System;

namespace CupRounds
{
    /// <summary>One request line from Cuphead to the ROUNDS card engine (sheets/ipc.json).</summary>
    [Serializable]
    public class Request
    {
        public int rid;
        public string op = "";
        public string version = "";
        public string[] owned = new string[0];
        public int count;
        public int seed;
        public string id = "";
        public int slot;
        // pose (ROUNDS units, +y up)
        public float vx, vy, height, ax, ay;
        public bool grounded, shoot, block, hit, dead;
    }

    /// <summary>One response line from the engine. Only the fields of the answered op are filled.</summary>
    [Serializable]
    public class Response
    {
        public int rid;
        public bool ok;
        public string error = "";
        public int cardCount;
        public string roundsVersion = "";
        public string[] mods = new string[0];
        public CardData[] cards = new CardData[0];
        public string[] ids = new string[0];
        public string png = "";
        public int width;
        public int height;
        public BuildStats stats = new BuildStats();
        public string[] partial = new string[0];
        public SkinData skin = new SkinData();
    }

    [Serializable]
    public class CardData
    {
        public string id = "";
        public string name = "";
        public string description = "";
        public string rarity = "";
        public string theme = "";
        public string colorHex = "";
        public string[] stats = new string[0];
    }

    [Serializable]
    public class SkinPart
    {
        public string png = "";
        /// <summary>Centre of the part relative to the body centre, in body diameters (+y up).</summary>
        public float x;
        public float y;
        /// <summary>Size of the part in body diameters.</summary>
        public float w;
        public float h;
        /// <summary>The part's own tint in ROUNDS, RRGGBBAA.</summary>
        public string color = "FFFFFFFF";
    }

    /// <summary>One ROUNDS noodle limb (LegRenderer): a chain of segments along a curve from start via mid to end, in body diameters.</summary>
    [Serializable]
    public class SkinLimb
    {
        public string name = "";
        public bool arm;
        public float sx, sy, mx, my, ex, ey;
        public int segments = 10;
        /// <summary>Segment size in body diameters (width across the limb, length along it).</summary>
        public float width;
        public float length;
        public string color = "";
        public string png = "";
    }

    [Serializable]
    public class SkinData
    {
        public SkinLimb[] limbs = new SkinLimb[0];
        public string color = "";
        public string shade = "";
        public SkinPart[] faceParts = new SkinPart[0];
        public SkinPart gun = new SkinPart();
        public string bulletPng = "";
    }

    public static class Proto
    {
        public const string Version = "1";
        public const string PortFileName = "engine.port";
        public const string FramesPortFileName = "frames.port";
        /// <summary>Pixels per side of each streamed ROUNDS character frame, and how many ROUNDS units it shows.</summary>
        public const int FrameSize = 256;
        public const float FrameUnits = 5.2f;
        /// <summary>Width of the ROUNDS body in ROUNDS units (the visible skin circle).</summary>
        public const float BodyUnits = 1.4f;

        public static string DataDir()
        {
            // Developer runs can keep a private data folder so they never meet a real session.
            string dev = Environment.GetEnvironmentVariable("ROUNDHEAD_DEV_DATA");
            if (!string.IsNullOrEmpty(dev)) return dev;
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local))
                local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return System.IO.Path.Combine(local, "CupRounds");
        }
    }
}
