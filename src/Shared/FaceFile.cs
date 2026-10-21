using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CupRounds
{
    /// <summary>
    /// One ROUNDS face as Roundhead keeps it: ROUNDS's PlayerFace values (a part id and an offset for the eyes, the mouth and
    /// two details). Written by the launcher's face editor, read by the card engine; no Unity types, so both build it.
    /// </summary>
    public class FaceData
    {
        public const int Slots = 4;
        public static readonly string[] SlotNames = { "eyes", "mouth", "detail", "detail2" };
        public int[] Id = new int[Slots];
        public float[] X = new float[Slots];
        public float[] Y = new float[Slots];

        public FaceData Copy() => new FaceData { Id = (int[])Id.Clone(), X = (float[])X.Clone(), Y = (float[])Y.Clone() };

        public bool Same(FaceData o) => o != null && Id.SequenceEqual(o.Id) && X.SequenceEqual(o.X) && Y.SequenceEqual(o.Y);

        /// <summary>"eyes mouth detail detail2", each "id x y".</summary>
        public string Format() => string.Join("  ", Enumerable.Range(0, Slots).Select(i => Id[i].ToString(CultureInfo.InvariantCulture) + " "
            + X[i].ToString("0.####", CultureInfo.InvariantCulture) + " " + Y[i].ToString("0.####", CultureInfo.InvariantCulture)).ToArray());

        public static FaceData Parse(string s)
        {
            var t = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < Slots * 3) return null;
            var f = new FaceData();
            for (int i = 0; i < Slots; i++)
            {
                if (!int.TryParse(t[i * 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out f.Id[i])) return null;
                if (!float.TryParse(t[i * 3 + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out f.X[i])) return null;
                if (!float.TryParse(t[i * 3 + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out f.Y[i])) return null;
            }
            return f;
        }
    }

    /// <summary>
    /// %LOCALAPPDATA%\CupRounds\faces.txt: player 1's and player 2's faces, one line each ("player1=14 0 0  33 0 0.1  5 0 0
    /// 0 0 0"). A player with no line wears ROUNDS's own face for its player 1 or 2.
    /// </summary>
    public static class FaceFile
    {
        public static readonly string[] Seats = { "player1", "player2" };

        public static FaceData[] Read(string file)
        {
            var faces = new FaceData[Seats.Length];
            if (!File.Exists(file)) return faces;
            foreach (string line in File.ReadAllLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                int seat = Array.IndexOf(Seats, line.Substring(0, eq).Trim().ToLowerInvariant());
                if (seat >= 0) faces[seat] = FaceData.Parse(line.Substring(eq + 1));
            }
            return faces;
        }

        public static void Write(string file, FaceData[] faces)
        {
            var lines = new List<string>();
            for (int s = 0; s < Seats.Length && s < faces.Length; s++)
                if (faces[s] != null) lines.Add(Seats[s] + "=" + faces[s].Format());
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllLines(file, lines.ToArray());
        }
    }
}
