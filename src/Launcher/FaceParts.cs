using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CupRounds.Launcher
{
    /// <summary>
    /// The face editor's pieces, drawn by the player's own ROUNDS (the card engine's --roundhead-faces run): each player's
    /// bare body and every face part ROUNDS has, all pictured in the same square around the face, so a face is the body with
    /// its parts laid over it, each moved by its offset (pxPerOffset pixels per unit). parts.txt lists them with their drawing
    /// order and ROUNDS's own position, and ROUNDS's eight faces.
    /// </summary>
    public class FaceParts
    {
        public class Part
        {
            public string Kind;
            public int Index, Order;
            public float DefX, DefY;
            public string Name;
        }

        public static string Dir => Path.Combine(Faces.SheetDir, "parts");
        public float Frame = 3.6f, PxPerOffset = 14f, Clamp = 4.5f;
        public int Size = 256;
        public readonly Dictionary<string, List<Part>> Kinds = new Dictionary<string, List<Part>> { { "eyes", new List<Part>() }, { "mouth", new List<Part>() }, { "detail", new List<Part>() } };
        public readonly List<FaceData> Presets = new List<FaceData>();
        public readonly int[] Selected = new int[FaceFile.Seats.Length];
        private readonly Dictionary<string, Bitmap> images = new Dictionary<string, Bitmap>();

        /// <summary>The kind of part each face slot holds (eyes, mouth, then two details).</summary>
        public static string KindOf(int slot) => slot == 0 ? "eyes" : slot == 1 ? "mouth" : "detail";

        public static bool Ready => File.Exists(Path.Combine(Dir, "parts.txt"));

        public static FaceParts Load()
        {
            var p = new FaceParts();
            string file = Path.Combine(Dir, "parts.txt");
            if (!File.Exists(file)) return null;
            foreach (string line in File.ReadAllLines(file))
            {
                var t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                try
                {
                    if (t[0] == "frame")
                    {
                        p.Frame = F(t[1]);
                        p.Size = int.Parse(t[3], CultureInfo.InvariantCulture);
                        p.PxPerOffset = F(t[5]);
                        p.Clamp = F(t[7]);
                    }
                    else if (t[0] == "part" && p.Kinds.ContainsKey(t[1]))
                        p.Kinds[t[1]].Add(new Part { Kind = t[1], Index = int.Parse(t[2], CultureInfo.InvariantCulture), Order = int.Parse(t[3], CultureInfo.InvariantCulture), DefX = F(t[4]), DefY = F(t[5]), Name = t.Length > 6 ? t[6] : "" });
                    else if (t[0] == "preset") p.Presets.Add(FaceData.Parse(string.Join(" ", t.Skip(2).ToArray())) ?? new FaceData());
                    else if (t[0] == "selected")
                    {
                        int seat = int.Parse(t[1], CultureInfo.InvariantCulture);
                        if (seat >= 0 && seat < p.Selected.Length) p.Selected[seat] = int.Parse(t[2], CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception e) { Log.Write("parts.txt: " + line + ": " + e.Message); }
            }
            return p;
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public Part Find(string kind, int index) => Kinds[kind].FirstOrDefault(x => x.Index == index);

        public Bitmap Image(string name)
        {
            if (images.TryGetValue(name, out Bitmap b)) return b;
            b = null;
            string file = Path.Combine(Dir, name + ".png");
            try
            {
                if (File.Exists(file))
                    using (var ms = new MemoryStream(File.ReadAllBytes(file)))
                    using (var img = System.Drawing.Image.FromStream(ms))
                        b = new Bitmap(img);
            }
            catch (Exception e) { Log.Write("face part " + name + ": " + e.Message); }
            images[name] = b;
            return b;
        }

        public Bitmap Body(int seat) => Image("body" + seat);
        public Bitmap PartImage(string kind, int index) => Image(kind + index);

        /// <summary>The face's parts in ROUNDS's drawing order (by sorting order, then slot).</summary>
        public IEnumerable<int> DrawOrder(FaceData f) =>
            Enumerable.Range(0, FaceData.Slots).OrderBy(s => Find(KindOf(s), f.Id[s])?.Order ?? 0).ThenBy(s => s);

        /// <summary>Draws a player with a face into a square of the given size (the parts' frame scaled to it).</summary>
        public void Draw(Graphics g, Rectangle into, int seat, FaceData f)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var body = Body(seat);
            if (body != null) g.DrawImage(body, into);
            if (f == null) return;
            float k = into.Width / (float)Size;
            foreach (int s in DrawOrder(f))
            {
                var img = PartImage(KindOf(s), f.Id[s]);
                if (img == null) continue;
                g.DrawImage(img, new RectangleF(into.X + f.X[s] * PxPerOffset * k, into.Y - f.Y[s] * PxPerOffset * k, into.Width, into.Height));
            }
        }

        public Bitmap Compose(int seat, FaceData f, int size, Rectangle? crop = null)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                if (crop == null) { Draw(g, new Rectangle(0, 0, size, size), seat, f); return bmp; }
                // A close-up: the square region `crop` of the frame (in frame pixels) fills the picture.
                var c = crop.Value;
                float k = size / (float)c.Width;
                Draw(g, new Rectangle((int)(-c.X * k), (int)(-c.Y * k), (int)(Size * k), (int)(Size * k)), seat, f);
            }
            return bmp;
        }

        /// <summary>The head and what sits on it, in frame pixels (for small pictures).</summary>
        public Rectangle Head
        {
            get
            {
                int half = (int)(Size * 0.27f);
                return new Rectangle(Size / 2 - half, Size / 2 - half - Size / 20, half * 2, half * 2);
            }
        }

        /// <summary>Keeps a part within ROUNDS's editor's reach of the face's centre.</summary>
        public void ClampOffset(FaceData f, int slot)
        {
            var part = Find(KindOf(slot), f.Id[slot]);
            float dx = part?.DefX ?? 0f, dy = part?.DefY ?? 0f;
            float x = dx + f.X[slot], y = dy + f.Y[slot];
            float len = (float)Math.Sqrt(x * x + y * y);
            if (len <= Clamp || len <= 0f) return;
            f.X[slot] = x / len * Clamp - dx;
            f.Y[slot] = y / len * Clamp - dy;
        }
    }
}
