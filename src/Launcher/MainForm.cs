using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CupRounds.Launcher
{
    /// <summary>The Roundhead launcher window: Thunderstore ROUNDS mods on the left, your profile on the right, Play at the bottom.</summary>
    public class MainForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(24, 22, 20);
        private static readonly Color Panel = Color.FromArgb(36, 33, 30);
        private static readonly Color Ink = Color.FromArgb(240, 232, 214);
        private static readonly Color Orange = Color.FromArgb(255, 140, 26);
        private static readonly Color Blue = Color.FromArgb(52, 128, 255);

        private readonly TextBox search = new TextBox();
        private readonly CheckBox cardsOnly = new CheckBox();
        private readonly ListView browse = new ListView();
        private readonly ListView installed = new ListView();
        private readonly PictureBox icon = new PictureBox();
        private readonly Label detailTitle = new Label();
        private readonly Label detailText = new Label();
        private readonly Button installBtn = new Button();
        private readonly Button removeBtn = new Button();
        private readonly Button updateBtn = new Button();
        private readonly Button importCodeBtn = new Button();
        private readonly Button importLocalBtn = new Button();
        private readonly Button playBtn = new Button();
        private readonly Label status = new Label();
        private readonly TextBox log = new TextBox();
        private bool busy;
        private bool suppressCheck;

        public MainForm()
        {
            Text = "Roundhead - ROUNDS x Cuphead";
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9.5f);
            Size = new Size(1180, 760);
            MinimumSize = new Size(980, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            if (Program.AutoPlay) { Opacity = 0; ShowInTaskbar = false; }
            CupRounds.Launcher.Log.Line += s => Ui(() => { log.AppendText(s + Environment.NewLine); });
            GameRunner.Status += s => Ui(() => status.Text = s);
            Shown += (s, e) =>
            {
                // Melty test runs (and --autoplay) never show the launcher, so the game window is what's on screen.
                if (Program.AutoPlay) Hide();
                Startup();
            };
            FormClosing += (s, e) => { if (GameRunner.CupheadProc == null) GameRunner.StopRounds(); };
        }

        private void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        private Button Btn(Button b, string text, Color back)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI Semibold", 9.5f);
            b.Height = 32;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private void Style(ListView v)
        {
            v.View = View.Details;
            v.FullRowSelect = true;
            v.HideSelection = false;
            v.BackColor = Panel;
            v.ForeColor = Ink;
            v.BorderStyle = BorderStyle.None;
            v.Dock = DockStyle.Fill;
        }

        private void BuildUi()
        {
            var title = new Label { Text = "ROUNDHEAD", Font = new Font("Segoe UI Black", 26f), ForeColor = Orange, AutoSize = true, Location = new Point(18, 10) };
            var sub = new Label { Text = "Your ROUNDS blob in Cuphead's campaign. Add ROUNDS card mods from Thunderstore, then press Play.", AutoSize = true, Location = new Point(22, 58), ForeColor = Color.FromArgb(200, 190, 170) };
            Controls.Add(title);
            Controls.Add(sub);

            var split = new SplitContainer { Location = new Point(16, 88), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Size = new Size(ClientSize.Width - 32, ClientSize.Height - 230), BackColor = Bg, SplitterWidth = 10 };
            Controls.Add(split);
            split.SplitterDistance = 640;

            // Left: Thunderstore
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            split.Panel1.Controls.Add(left);
            var leftTop = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Bg };
            var leftHead = new Label { Text = "THUNDERSTORE  -  ROUNDS mods", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Blue, AutoSize = true, Location = new Point(0, 8) };
            search.Location = new Point(250, 7);
            search.Width = 230;
            search.BackColor = Panel;
            search.ForeColor = Ink;
            search.BorderStyle = BorderStyle.FixedSingle;
            search.TextChanged += (s, e) => ApplyFilter();
            cardsOnly.Text = "Card mods only";
            cardsOnly.Checked = true;
            cardsOnly.AutoSize = true;
            cardsOnly.Location = new Point(492, 9);
            cardsOnly.CheckedChanged += (s, e) => ApplyFilter();
            leftTop.Controls.AddRange(new Control[] { leftHead, search, cardsOnly });
            Style(browse);
            browse.Columns.Add("Mod", 250);
            browse.Columns.Add("By", 130);
            browse.Columns.Add("Downloads", 90, HorizontalAlignment.Right);
            browse.Columns.Add("Rating", 60, HorizontalAlignment.Right);
            browse.SelectedIndexChanged += (s, e) => ShowDetail();
            browse.DoubleClick += (s, e) => InstallSelected();
            var detail = new Panel { Dock = DockStyle.Bottom, Height = 130, BackColor = Panel, Padding = new Padding(8) };
            icon.SizeMode = PictureBoxSizeMode.Zoom;
            icon.Size = new Size(96, 96);
            icon.Location = new Point(10, 14);
            detailTitle.Location = new Point(118, 10);
            detailTitle.Size = new Size(380, 24);
            detailTitle.Font = new Font("Segoe UI Semibold", 11f);
            detailText.Location = new Point(118, 36);
            detailText.Size = new Size(380, 84);
            Btn(installBtn, "Install", Blue).Location = new Point(510, 14);
            installBtn.Width = 110;
            installBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            installBtn.Click += (s, e) => InstallSelected();
            detail.Controls.AddRange(new Control[] { icon, detailTitle, detailText, installBtn });
            left.Controls.Add(browse);
            left.Controls.Add(detail);
            left.Controls.Add(leftTop);

            // Right: installed profile
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            split.Panel2.Controls.Add(right);
            var rightTop = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Bg };
            rightTop.Controls.Add(new Label { Text = "YOUR ROUNDS MODS  (tick = on)", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Orange, AutoSize = true, Location = new Point(0, 8) });
            Style(installed);
            installed.CheckBoxes = true;
            installed.Columns.Add("Mod", 260);
            installed.Columns.Add("Version", 80);
            installed.Columns.Add("", 90);
            installed.ItemChecked += (s, e) => ToggleChecked(e.Item);
            var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 84, BackColor = Bg, Padding = new Padding(0, 8, 0, 0) };
            Btn(removeBtn, "Remove", Color.FromArgb(120, 50, 40)).Width = 100;
            removeBtn.Click += (s, e) => RemoveSelected();
            Btn(updateBtn, "Update all", Color.FromArgb(70, 70, 70)).Width = 110;
            updateBtn.Click += (s, e) => { if (ListReady()) Work("Updating...", () => ProfileManager.UpdateAll()); };
            Btn(importCodeBtn, "Import profile code", Color.FromArgb(70, 70, 70)).Width = 160;
            importCodeBtn.Click += (s, e) => ImportCode();
            Btn(importLocalBtn, "Copy an r2modman profile", Color.FromArgb(70, 70, 70)).Width = 190;
            importLocalBtn.Click += (s, e) => ImportLocal();
            rightButtons.Controls.AddRange(new Control[] { removeBtn, updateBtn, importCodeBtn, importLocalBtn });
            right.Controls.Add(installed);
            right.Controls.Add(rightButtons);
            right.Controls.Add(rightTop);

            // Bottom: play + status + log
            Btn(playBtn, "PLAY", Orange);
            playBtn.Font = new Font("Segoe UI Black", 18f);
            playBtn.Size = new Size(220, 70);
            playBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            playBtn.Location = new Point(ClientSize.Width - 236, ClientSize.Height - 130);
            playBtn.Click += (s, e) => StartPlay();
            Controls.Add(playBtn);
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            status.Location = new Point(18, ClientSize.Height - 132);
            status.Size = new Size(ClientSize.Width - 270, 22);
            status.Font = new Font("Segoe UI Semibold", 10f);
            status.Text = "Loading...";
            Controls.Add(status);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Panel;
            log.ForeColor = Color.FromArgb(180, 172, 156);
            log.BorderStyle = BorderStyle.None;
            log.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            log.Location = new Point(18, ClientSize.Height - 104);
            log.Size = new Size(ClientSize.Width - 270, 90);
            Controls.Add(log);
        }

        public void Log(string s) => CupRounds.Launcher.Log.Write(s);

        private void Startup()
        {
            ProfileManager.Load();
            RefreshInstalled();
            // Play needs only the mods already in the profile, never the Thunderstore list, so it starts (or is
            // pressable) at once. A slow or blocked Thunderstore once kept PLAY greyed out until players gave up.
            if (Program.AutoPlay) { StartPlay(); return; }
            status.Text = "Ready. " + ProfileManager.Mods.Count + " mod(s) in your Roundhead profile. Loading the Thunderstore mod list...";
            new Thread(() =>
            {
                try { Thunderstore.LoadPackages(); }
                catch (Exception e) { Log("Could not read the Thunderstore mod list: " + e.Message); }
                Ui(() =>
                {
                    ApplyFilter();
                    if (!busy) status.Text = "Ready. " + ProfileManager.Mods.Count + " mod(s) in your Roundhead profile."
                        + (Thunderstore.Loaded ? "" : " Thunderstore is unreachable right now; you can still play.");
                });
            }) { IsBackground = true }.Start();
        }

        /// <summary>Installing, updating and importing look mods up in the Thunderstore list; say so instead of failing quietly.</summary>
        private bool ListReady()
        {
            if (Thunderstore.Loaded) return true;
            status.Text = "The Thunderstore mod list is still loading (or unreachable). You can press PLAY meanwhile.";
            return false;
        }

        public void ApplyFilter()
        {
            string q = search.Text.Trim();
            IEnumerable<TsPackage> list = Thunderstore.Packages.Where(p => !p.Deprecated && !ProfileManager.IsProvided(p.FullName));
            if (cardsOnly.Checked) list = list.Where(p => p.IsCards);
            if (q.Length > 0) list = list.Where(p => p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Owner.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Latest.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var items = list.OrderByDescending(p => p.TotalDownloads).Take(400).Select(p =>
            {
                var it = new ListViewItem(p.Name.Replace('_', ' ')) { Tag = p };
                it.SubItems.Add(p.Owner);
                it.SubItems.Add(p.TotalDownloads.ToString("N0"));
                it.SubItems.Add(p.Rating.ToString());
                if (ProfileManager.Find(p.FullName) != null) it.ForeColor = Orange;
                return it;
            }).ToArray();
            browse.BeginUpdate();
            browse.Items.Clear();
            browse.Items.AddRange(items);
            browse.EndUpdate();
        }

        private TsPackage Selected => browse.SelectedItems.Count > 0 ? browse.SelectedItems[0].Tag as TsPackage : null;

        private void ShowDetail()
        {
            var p = Selected;
            if (p == null) return;
            detailTitle.Text = p.Name.Replace('_', ' ') + "  " + p.Latest.Number;
            detailText.Text = p.Latest.Description + (p.Latest.Dependencies.Length > 0 ? "\nNeeds: " + string.Join(", ", p.Latest.Dependencies.Select(Thunderstore.PackageOf).Where(d => !ProfileManager.IsProvided(d)).Select(d => d.Split('-').Last())) : "");
            installBtn.Text = ProfileManager.Find(p.FullName) != null ? "Reinstall" : "Install";
            try { icon.LoadAsync(p.Latest.Icon); } catch { }
        }

        private void InstallSelected()
        {
            var p = Selected;
            if (p == null || busy) return;
            Work("Installing " + p.Name + "...", () => ProfileManager.Install(p.FullName));
        }

        private void RemoveSelected()
        {
            if (installed.SelectedItems.Count == 0 || busy) return;
            var m = installed.SelectedItems[0].Tag as InstalledMod;
            if (m != null) Work("Removing...", () => ProfileManager.Uninstall(m.FullName));
        }

        private void ToggleChecked(ListViewItem it)
        {
            if (suppressCheck || busy) return;
            var m = it.Tag as InstalledMod;
            if (m == null || m.Enabled == it.Checked) return;
            bool on = it.Checked;
            Work(on ? "Turning on..." : "Turning off...", () => ProfileManager.SetEnabled(m.FullName, on));
        }

        private void ImportCode()
        {
            if (!ListReady()) return;
            string code = Prompt("Paste an r2modman / Thunderstore Mod Manager profile code:", "Import profile code");
            if (string.IsNullOrWhiteSpace(code)) return;
            Work("Importing profile...", () => ProfileManager.ImportCode(code));
        }

        private void ImportLocal()
        {
            if (!ListReady()) return;
            var profiles = ProfileManager.LocalProfiles();
            if (profiles.Count == 0) { MessageBox.Show(this, "No r2modman or Thunderstore Mod Manager ROUNDS profiles were found on this PC.", "Roundhead"); return; }
            using (var f = new Form { Text = "Copy which profile?", Size = new Size(460, 320), StartPosition = FormStartPosition.CenterParent, BackColor = Bg, ForeColor = Ink })
            {
                var lb = new ListBox { Dock = DockStyle.Fill, BackColor = Panel, ForeColor = Ink };
                foreach (string p in profiles) lb.Items.Add(p);
                var ok = Btn(new Button(), "Copy its mod list", Blue);
                ok.Dock = DockStyle.Bottom;
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(lb);
                f.Controls.Add(ok);
                lb.SelectedIndex = 0;
                if (f.ShowDialog(this) != DialogResult.OK || lb.SelectedItem == null) return;
                string dir = (string)lb.SelectedItem;
                Work("Copying " + System.IO.Path.GetFileName(dir) + "...", () => ProfileManager.ImportLocal(dir));
            }
        }

        private string Prompt(string text, string caption)
        {
            using (var f = new Form { Text = caption, Size = new Size(460, 160), StartPosition = FormStartPosition.CenterParent, BackColor = Bg, ForeColor = Ink, FormBorderStyle = FormBorderStyle.FixedDialog })
            {
                var l = new Label { Text = text, Location = new Point(12, 12), AutoSize = true };
                var t = new TextBox { Location = new Point(12, 40), Width = 420 };
                var ok = Btn(new Button(), "OK", Blue);
                ok.Location = new Point(332, 72);
                ok.DialogResult = DialogResult.OK;
                f.Controls.AddRange(new Control[] { l, t, ok });
                f.AcceptButton = ok;
                return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
            }
        }

        private void RefreshInstalled()
        {
            suppressCheck = true;
            installed.BeginUpdate();
            installed.Items.Clear();
            foreach (var m in ProfileManager.Mods.OrderBy(m => m.Dependency).ThenBy(m => m.FullName))
            {
                var it = new ListViewItem(m.FullName.Contains('-') ? m.FullName.Substring(m.FullName.IndexOf('-') + 1).Replace('_', ' ') : m.FullName) { Tag = m, Checked = m.Enabled };
                it.SubItems.Add(m.Version);
                it.SubItems.Add(m.Dependency ? "library" : "");
                installed.Items.Add(it);
            }
            installed.EndUpdate();
            suppressCheck = false;
        }

        private void Work(string what, Action job, Action after = null)
        {
            if (busy) return;
            busy = true;
            status.Text = what;
            SetButtons(false);
            new Thread(() =>
            {
                string err = null;
                try { job(); }
                catch (Exception e) { err = e.Message; Log("Error: " + e.Message); }
                Ui(() =>
                {
                    busy = false;
                    SetButtons(true);
                    RefreshInstalled();
                    ApplyFilter();
                    status.Text = err == null ? "Done." : "Problem: " + err;
                    after?.Invoke();
                });
            }) { IsBackground = true }.Start();
        }

        private void SetButtons(bool on)
        {
            foreach (var b in new[] { installBtn, removeBtn, updateBtn, importCodeBtn, importLocalBtn, playBtn }) b.Enabled = on;
        }

        private void StartPlay()
        {
            if (busy) return;
            busy = true;
            SetButtons(false);
            playBtn.Text = "PLAYING";
            new Thread(() =>
            {
                string err = null;
                bool wrongRounds = false;
                try
                {
                    // While playing, the launcher steps out of the way entirely (no window, no taskbar button).
                    Ui(() => Hide());
                    GameRunner.Play();
                }
                catch (Exception e) { err = e.Message; wrongRounds = e is WrongRoundsVersion; Log("Could not play: " + e.Message); GameRunner.StopRounds(); }
                Ui(() =>
                {
                    busy = false;
                    SetButtons(true);
                    playBtn.Text = "PLAY";
                    if (err != null)
                    {
                        // Test runs (autoplay) start invisible; a problem must still be seen.
                        Opacity = 1;
                        ShowInTaskbar = true;
                        Show();
                        WindowState = FormWindowState.Normal;
                        Activate();
                        status.Text = "Problem: " + err.Replace("\n\n", " ");
                        if (wrongRounds && MessageBox.Show(this, err + "\n\nOpen ROUNDS in your Steam library now?", "Roundhead: switch ROUNDS's version",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            try { System.Diagnostics.Process.Start("steam://nav/games/details/" + GameRunner.RoundsAppId); } catch { }
                    }
                    else Close();
                });
            }) { IsBackground = true }.Start();
        }
    }
}
