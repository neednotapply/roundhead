using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CupRounds.Launcher
{
    /// <summary>
    /// The face editor, as ROUNDS's own: pick the eyes, the mouth and two details from ROUNDS's parts (EYES, MOUTH, DETAIL 1,
    /// DETAIL 2), and drag them on the face to move them (as far as ROUNDS's editor lets them go). Save gives the player the face.
    /// </summary>
    public class FaceEditorForm : Form
    {
        private const int View = 420, TileSize = 64, TileGap = 6, Cols = 7;
        private readonly int seat;
        private readonly FaceParts parts;
        private readonly FaceData original;
        public FaceData Face { get; private set; }
        private readonly Canvas canvas = new Canvas();
        private readonly Button[] tabs = new Button[FaceData.Slots];
        private readonly FlowLayoutPanel grid = new FlowLayoutPanel();
        private readonly Label hint = new Label();
        private readonly Dictionary<string, Bitmap> tileCache = new Dictionary<string, Bitmap>();
        private int slot;
        private int dragSlot = -1;
        private Point dragFrom;
        private float dragX, dragY;
        private readonly Color team;
        private readonly ToolTip tips = new ToolTip();

        private class Canvas : Panel
        {
            public Canvas() { DoubleBuffered = true; ResizeRedraw = true; }
        }

        public FaceEditorForm(int seat, FaceParts parts, FaceData face)
        {
            this.seat = seat;
            this.parts = parts;
            original = face.Copy();
            Face = face.Copy();
            team = seat == 0 ? MainForm.Orange : MainForm.Blue;
            Text = "Roundhead - " + Faces.PlayerName(seat) + "'s face";
            BackColor = MainForm.Bg;
            ForeColor = MainForm.Ink;
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            int gridW = Cols * (TileSize + TileGap) + SystemInformation.VerticalScrollBarWidth + 8;
            ClientSize = new Size(16 + View + 16 + gridW + 16, 64 + View + 70);

            Controls.Add(new Label { Text = Faces.PlayerName(seat).ToUpperInvariant() + "'S FACE", Font = new Font("Segoe UI Black", 18f), ForeColor = team, AutoSize = true, Location = new Point(14, 10) });
            canvas.Location = new Point(16, 64);
            canvas.Size = new Size(View, View);
            canvas.BackColor = Color.FromArgb(14, 12, 10);
            canvas.Paint += (o, e) => parts.Draw(e.Graphics, new Rectangle(0, 0, View, View), seat, Face);
            canvas.MouseDown += CanvasDown;
            canvas.MouseMove += CanvasMove;
            canvas.MouseUp += (o, e) => { dragSlot = -1; canvas.Cursor = Cursors.Default; };
            Controls.Add(canvas);
            hint.Text = "Drag the eyes, mouth and details on the face to move them.";
            hint.ForeColor = Color.FromArgb(200, 190, 170);
            hint.AutoSize = true;
            hint.Location = new Point(16, 64 + View + 8);
            Controls.Add(hint);

            int gx = 16 + View + 16;
            string[] names = { "EYES", "MOUTH", "DETAIL 1", "DETAIL 2" };
            for (int i = 0; i < tabs.Length; i++)
            {
                int s = i;
                var b = MainForm.Btn(new Button(), names[i], Color.FromArgb(70, 64, 58));
                b.Location = new Point(gx + i * ((gridW - 18) / 4 + 6), 64);
                b.Width = (gridW - 18) / 4;
                b.Click += (o, e) => ShowSlot(s);
                tabs[i] = b;
                Controls.Add(b);
            }
            grid.Location = new Point(gx, 104);
            grid.Size = new Size(gridW, View - 40);
            grid.AutoScroll = true;
            grid.BackColor = MainForm.Panel;
            grid.Padding = new Padding(4);
            Controls.Add(grid);

            int by = 64 + View + 30;
            var reset = MainForm.Btn(new Button(), "Reset positions", Color.FromArgb(70, 70, 70));
            reset.Location = new Point(16, by);
            reset.Width = 150;
            reset.Click += (o, e) => { for (int i = 0; i < FaceData.Slots; i++) { Face.X[i] = 0f; Face.Y[i] = 0f; } canvas.Invalidate(); };
            var undo = MainForm.Btn(new Button(), "Undo changes", Color.FromArgb(70, 70, 70));
            undo.Location = new Point(176, by);
            undo.Width = 140;
            undo.Click += (o, e) => { Face = original.Copy(); canvas.Invalidate(); ShowSlot(slot); };
            var save = MainForm.Btn(new Button(), "Save", team);
            save.Location = new Point(ClientSize.Width - 16 - 120, by);
            save.Width = 120;
            save.DialogResult = DialogResult.OK;
            var cancel = MainForm.Btn(new Button(), "Cancel", Color.FromArgb(120, 50, 40));
            cancel.Location = new Point(ClientSize.Width - 16 - 120 - 10 - 110, by);
            cancel.Width = 110;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.AddRange(new Control[] { reset, undo, save, cancel });
            AcceptButton = save;
            CancelButton = cancel;
            FormClosed += (o, e) => { foreach (var b in tileCache.Values) b?.Dispose(); };
            Shown += (o, e) => ShowSlot(0);
        }

        /// <summary>The parts for one slot, each shown on the player's head; the one the face wears is framed.</summary>
        private void ShowSlot(int s)
        {
            slot = s;
            for (int i = 0; i < tabs.Length; i++) tabs[i].BackColor = i == s ? team : Color.FromArgb(70, 64, 58);
            string kind = FaceParts.KindOf(s);
            grid.SuspendLayout();
            foreach (Control c in grid.Controls) c.Dispose();
            grid.Controls.Clear();
            foreach (var part in parts.Kinds[kind])
            {
                var p = part;
                var tile = new PictureBox
                {
                    Size = new Size(TileSize, TileSize),
                    Margin = new Padding(TileGap / 2),
                    Padding = new Padding(3),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Face.Id[s] == p.Index ? team : Color.FromArgb(28, 25, 22),
                    Image = Tile(kind, p.Index),
                    Cursor = Cursors.Hand,
                    Tag = p.Index
                };
                tips.SetToolTip(tile, (kind == "detail" && p.Index == 0 ? "None" : kind + " " + (p.Index + 1)));
                tile.Click += (o, e) => Choose(p.Index);
                grid.Controls.Add(tile);
            }
            grid.ResumeLayout();
            var on = grid.Controls.Cast<Control>().FirstOrDefault(c => (int)c.Tag == Face.Id[s]);
            if (on != null) grid.ScrollControlIntoView(on);
        }

        /// <summary>A part on the bare head, at ROUNDS's own place for it.</summary>
        private Bitmap Tile(string kind, int index)
        {
            string key = kind + index;
            if (tileCache.TryGetValue(key, out Bitmap b)) return b;
            var f = new FaceData();
            // Only this part: the others point at nothing (no picture for id -1).
            for (int i = 0; i < FaceData.Slots; i++) f.Id[i] = -1;
            f.Id[kind == "eyes" ? 0 : kind == "mouth" ? 1 : 2] = index;
            b = parts.Compose(seat, f, TileSize * 2, parts.Head);
            tileCache[key] = b;
            return b;
        }

        private void Choose(int index)
        {
            Face.Id[slot] = index;
            // ROUNDS puts a newly chosen part at its own place (CharacterCreatorItemEquipper.SpawnItem).
            Face.X[slot] = 0f;
            Face.Y[slot] = 0f;
            foreach (Control c in grid.Controls) c.BackColor = (int)c.Tag == index ? team : Color.FromArgb(28, 25, 22);
            canvas.Invalidate();
        }

        private float Zoom => View / (float)parts.Size;

        /// <summary>The topmost part with paint under the mouse.</summary>
        private int PartAt(Point p)
        {
            foreach (int s in parts.DrawOrder(Face).Reverse())
            {
                var img = parts.PartImage(FaceParts.KindOf(s), Face.Id[s]);
                if (img == null) continue;
                int x = (int)(p.X / Zoom - Face.X[s] * parts.PxPerOffset), y = (int)(p.Y / Zoom + Face.Y[s] * parts.PxPerOffset);
                // a little slack around thin parts
                for (int dy = -4; dy <= 4; dy += 4)
                    for (int dx = -4; dx <= 4; dx += 4)
                    {
                        int px = x + dx, py = y + dy;
                        if (px >= 0 && py >= 0 && px < img.Width && py < img.Height && img.GetPixel(px, py).A > 40) return s;
                    }
            }
            return -1;
        }

        private void CanvasDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragSlot = PartAt(e.Location);
            if (dragSlot < 0) return;
            dragFrom = e.Location;
            dragX = Face.X[dragSlot];
            dragY = Face.Y[dragSlot];
            canvas.Cursor = Cursors.SizeAll;
            if (dragSlot != slot) ShowSlot(dragSlot);
        }

        private void CanvasMove(object sender, MouseEventArgs e)
        {
            if (dragSlot < 0)
            {
                canvas.Cursor = PartAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
                return;
            }
            float per = parts.PxPerOffset * Zoom;
            Face.X[dragSlot] = dragX + (e.X - dragFrom.X) / per;
            Face.Y[dragSlot] = dragY - (e.Y - dragFrom.Y) / per;
            parts.ClampOffset(Face, dragSlot);
            canvas.Invalidate();
        }
    }
}
