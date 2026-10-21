using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CupRounds.Launcher
{
    /// <summary>
    /// The launcher's Faces window: each player's face (made of ROUNDS's own parts, pictured by the player's own ROUNDS) with
    /// EDIT for the face editor, and the player's eight ROUNDS faces to start from.
    /// </summary>
    public class FacesForm : Form
    {
        private const int Preview = 140, Tile = 72, Gap = 6, RowTop = 96, RowStep = 196, Left0 = 180;
        private readonly PictureBox[] previews = new PictureBox[FaceFile.Seats.Length];
        private readonly Button[] editBtns = new Button[FaceFile.Seats.Length];
        private readonly Panel[,] tiles = new Panel[FaceFile.Seats.Length, Faces.Presets];
        private readonly PictureBox[,] pics = new PictureBox[FaceFile.Seats.Length, Faces.Presets];
        private readonly Button redrawBtn = new Button();
        private readonly Button doneBtn = new Button();
        private readonly Label status = new Label();
        private FaceParts parts;
        private bool busy;

        public FacesForm()
        {
            Text = "Roundhead - Faces";
            BackColor = MainForm.Bg;
            ForeColor = MainForm.Ink;
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Left0 + Faces.Presets * (Tile + Gap) + 10, RowTop + 2 * RowStep + 52);
            Controls.Add(new Label { Text = "FACES", Font = new Font("Segoe UI Black", 20f), ForeColor = MainForm.Orange, AutoSize = true, Location = new Point(14, 8) });
            Controls.Add(new Label
            {
                Text = "Each player's face is made of ROUNDS's own eyes, mouths and details. Press EDIT to change it in the face editor, "
                    + "or start from one of your ROUNDS faces on the right.",
                Location = new Point(16, 50), Size = new Size(ClientSize.Width - 32, 40), ForeColor = Color.FromArgb(200, 190, 170)
            });
            for (int s = 0; s < FaceFile.Seats.Length; s++)
            {
                int seat = s, top = RowTop + s * RowStep;
                Color team = s == 0 ? MainForm.Orange : MainForm.Blue;
                previews[s] = new PictureBox { Location = new Point(16, top), Size = new Size(Preview, Preview), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(14, 12, 10) };
                Controls.Add(previews[s]);
                editBtns[s] = MainForm.Btn(new Button(), "EDIT " + Faces.PlayerName(s).ToUpperInvariant(), team);
                editBtns[s].Location = new Point(16, top + Preview + 6);
                editBtns[s].Width = Preview;
                editBtns[s].Click += (o, e) => Edit(seat);
                Controls.Add(editBtns[s]);
                Controls.Add(new Label { Text = Faces.PlayerName(s).ToUpperInvariant() + "  -  start from one of your ROUNDS faces:", Font = new Font("Segoe UI Semibold", 10f), ForeColor = team, AutoSize = true, Location = new Point(Left0, top) });
                for (int f = 0; f < Faces.Presets; f++)
                {
                    int preset = f;
                    var tile = new Panel { Location = new Point(Left0 + f * (Tile + Gap), top + 26), Size = new Size(Tile, Tile), BackColor = MainForm.Panel, Padding = new Padding(4), Cursor = Cursors.Hand };
                    var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 25, 22), Cursor = Cursors.Hand };
                    tile.Controls.Add(pic);
                    pic.Click += (o, e) => StartFrom(seat, preset);
                    tile.Click += (o, e) => StartFrom(seat, preset);
                    Controls.Add(tile);
                    Controls.Add(new Label { Text = (f + 1).ToString(), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(tile.Left, top + 26 + Tile + 2), Size = new Size(Tile, 18), ForeColor = Color.FromArgb(170, 160, 145) });
                    tiles[s, f] = tile;
                    pics[s, f] = pic;
                }
            }
            int bottom = RowTop + 2 * RowStep + 6;
            MainForm.Btn(redrawBtn, "Draw the pictures again", Color.FromArgb(70, 70, 70)).Location = new Point(16, bottom);
            redrawBtn.Width = 190;
            redrawBtn.Click += (o, e) => DrawPictures();
            MainForm.Btn(doneBtn, "Done", MainForm.Orange).Location = new Point(ClientSize.Width - 126, bottom);
            doneBtn.Width = 110;
            doneBtn.Click += (o, e) => Close();
            status.Location = new Point(220, bottom + 7);
            status.Size = new Size(ClientSize.Width - 360, 22);
            Controls.AddRange(new Control[] { redrawBtn, doneBtn, status });
            FormClosing += (o, e) => { if (Faces.Drawing) Faces.StopDrawing(); };
            Shown += (o, e) =>
            {
                Reload();
                if (!Faces.PicturesCurrent()) DrawPictures();
            };
        }

        private void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        /// <summary>The pictures, the parts and each player's face as they are now.</summary>
        private void Reload()
        {
            parts = FaceParts.Load();
            for (int s = 0; s < FaceFile.Seats.Length; s++)
            {
                for (int f = 0; f < Faces.Presets; f++)
                {
                    var old = pics[s, f].Image;
                    pics[s, f].Image = LoadPicture(Faces.Picture(s, f));
                    old?.Dispose();
                }
                editBtns[s].Enabled = parts != null && !busy;
            }
            ShowFaces();
        }

        private void ShowFaces()
        {
            for (int s = 0; s < FaceFile.Seats.Length; s++)
            {
                var face = Faces.Current(s, parts);
                var old = previews[s].Image;
                previews[s].Image = parts != null ? parts.Compose(s, face, Preview * 2, parts.Head) : null;
                old?.Dispose();
                for (int f = 0; f < Faces.Presets; f++)
                {
                    bool wears = parts != null && f < parts.Presets.Count && parts.Presets[f].Same(face);
                    tiles[s, f].BackColor = wears ? (s == 0 ? MainForm.Orange : MainForm.Blue) : MainForm.Panel;
                }
            }
        }

        /// <summary>A copy of the picture, so the file stays free for the next drawing.</summary>
        private static Image LoadPicture(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                using (var ms = new MemoryStream(File.ReadAllBytes(file)))
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch { return null; }
        }

        private void StartFrom(int seat, int preset)
        {
            if (parts == null || preset >= parts.Presets.Count) return;
            try { Faces.Save(seat, parts.Presets[preset]); }
            catch (Exception e) { status.Text = "Could not save: " + e.Message; return; }
            ShowFaces();
            status.Text = Faces.PlayerName(seat) + " wears your ROUNDS face " + (preset + 1) + ". EDIT changes it.";
        }

        private void Edit(int seat)
        {
            if (parts == null) { status.Text = "The face parts are still being drawn."; return; }
            using (var ed = new FaceEditorForm(seat, parts, Faces.Current(seat, parts)))
            {
                if (ed.ShowDialog(this) != DialogResult.OK) return;
                try { Faces.Save(seat, ed.Face); }
                catch (Exception e) { status.Text = "Could not save: " + e.Message; return; }
            }
            ShowFaces();
            status.Text = Faces.PlayerName(seat) + "'s face is saved; it shows from the next PLAY.";
        }

        private void DrawPictures()
        {
            if (busy) return;
            busy = true;
            redrawBtn.Enabled = false;
            foreach (var b in editBtns) b.Enabled = false;
            status.Text = "Drawing your ROUNDS faces and their parts (ROUNDS runs hidden for a few seconds)...";
            new Thread(() =>
            {
                string err = null;
                try { Faces.Draw(); }
                catch (Exception e) { err = e.Message; Log.Write("Faces: " + e.Message); }
                Ui(() =>
                {
                    busy = false;
                    redrawBtn.Enabled = true;
                    Reload();
                    status.Text = err != null ? "Problem: " + err : Faces.PicturesCurrent() ? "Your ROUNDS faces and parts are ready." : "ROUNDS did not draw the faces (see the launcher's log).";
                });
            }) { IsBackground = true }.Start();
        }
    }
}
