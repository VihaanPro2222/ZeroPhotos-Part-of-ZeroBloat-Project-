using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZeroPhotos
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string? initialFilePath = (args.Length > 0 && File.Exists(args[0])) ? args[0] : null;
            Application.Run(new MainForm(initialFilePath));
        }
    }

    public class MainForm : Form
    {
        private readonly Panel _sidebar;
        private readonly Panel _sidebarHeader;
        private readonly FlowLayoutPanel _folderListPanel;
        private readonly GalleryControl _gallery;
        private readonly ViewerContainer _viewer;
        
        private readonly RoundedTextBoxContainer _searchBoxContainer;
        private readonly RoundedComboBoxContainer _sortBoxContainer;

        private readonly string? _startupFilePath;
        private readonly HashSet<string> _watchedDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MediaItem> _allMediaItems = new();
        private List<MediaItem> _currentContextItems = new();
        private List<MediaItem> _currentViewItems = new();
        private int _currentIndex = -1;
        private CancellationTokenSource? _scanCts;

        private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".ico", ".tiff" };
        private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".wmv", ".mov", ".m4v", ".webm" };

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static readonly string AppFont = "Segoe UI Variable Display";

        public MainForm(string? startupFilePath = null)
        {
            _startupFilePath = startupFilePath;
            this.Text = "ZeroPhotos - Ultra-Lightweight Media Suite";
            this.Size = new Size(1300, 850);
            this.MinimumSize = new Size(850, 600);
            this.BackColor = Color.FromArgb(14, 14, 16);
            this.ForeColor = Color.White;
            this.KeyPreview = true;
            this.FormClosing += (s, e) => { _scanCts?.Cancel(); _viewer.StopMediaPlayback(); };

            int dark = 1; DwmSetWindowAttribute(this.Handle, 20, ref dark, sizeof(int));

            _sidebar = new Panel { Width = 240, Dock = DockStyle.Left, BackColor = Color.FromArgb(20, 20, 24) };
            _sidebarHeader = new Panel { Dock = DockStyle.Top, Height = 190, BackColor = Color.FromArgb(18, 18, 22), Padding = new Padding(14) };

            var lblApp = new Label { Text = "ZeroPhotos", Font = new Font(AppFont, 14, FontStyle.Bold), ForeColor = Color.White, Location = new Point(14, 16), AutoSize = true };
            
            var btnAddFolder = new Button { Text = "+ Add Folder", Font = new Font(AppFont, 9, FontStyle.Bold), ForeColor = Color.FromArgb(200, 225, 255), BackColor = Color.FromArgb(32, 34, 42), FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(14, 52, 210, 32), Cursor = Cursors.Hand };
            btnAddFolder.FlatAppearance.BorderSize = 0; btnAddFolder.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 50, 60);
            btnAddFolder.Click += (s, e) => PickCustomFolder();

            var btnBackToGallery = new Button { Text = "← Back to Grid", Font = new Font(AppFont, 9, FontStyle.Bold), ForeColor = Color.FromArgb(255, 190, 90), BackColor = Color.FromArgb(42, 32, 24), FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(14, 88, 210, 30), Visible = false, Cursor = Cursors.Hand };
            btnBackToGallery.FlatAppearance.BorderSize = 0; btnBackToGallery.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 42, 30);
            btnBackToGallery.Click += (s, e) => ShowGalleryView();

            _searchBoxContainer = new RoundedTextBoxContainer { Bounds = new Rectangle(14, 122, 210, 32) };
            _searchBoxContainer.InnerBox.TextChanged += (s, e) => ApplySearchAndSort();

            _sortBoxContainer = new RoundedComboBoxContainer { Bounds = new Rectangle(14, 158, 210, 32) };
            _sortBoxContainer.InnerBox.Items.AddRange(new[] { "Sort by Date", "Sort by Name", "Sort by Size" });
            _sortBoxContainer.InnerBox.SelectedIndex = 0;
            _sortBoxContainer.InnerBox.SelectedIndexChanged += (s, e) => ApplySearchAndSort();

            _sidebarHeader.Controls.AddRange(new Control[] { lblApp, btnAddFolder, btnBackToGallery, _searchBoxContainer, _sortBoxContainer });
            _folderListPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6) };
            _sidebar.Controls.Add(_folderListPanel); _sidebar.Controls.Add(_sidebarHeader);

            _gallery = new GalleryControl { Dock = DockStyle.Fill };
            _gallery.ItemClicked += (idx) => OpenViewerAt(idx);

            _viewer = new ViewerContainer { Dock = DockStyle.Fill, Visible = false };
            _viewer.FilmstripSelected += (idx) => OpenViewerAt(idx);

            this.Controls.Add(_viewer); this.Controls.Add(_gallery); this.Controls.Add(_sidebar);

            _viewer.VisibleChanged += (s, e) =>
            {
                btnBackToGallery.Visible = _viewer.Visible;
                _searchBoxContainer.Visible = !_viewer.Visible;
                _sortBoxContainer.Visible = !_viewer.Visible;
                btnAddFolder.Visible = !_viewer.Visible;
            };

            this.KeyDown += HandleGlobalKeys;
            this.Load += async (s, e) => await InitializeEngineAsync();
        }

        private async Task InitializeEngineAsync()
        {
            if (!string.IsNullOrEmpty(_startupFilePath))
            {
                string? parentDir = Path.GetDirectoryName(_startupFilePath);
                if (!string.IsNullOrEmpty(parentDir)) AddPathIfExists(parentDir);
            }
            
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddPathIfExists(Path.Combine(userProfile, "Pictures"));
            AddPathIfExists(Path.Combine(userProfile, "Videos"));
            AddPathIfExists(Path.Combine(userProfile, "Downloads"));
            AddPathIfExists(Path.Combine(userProfile, "Documents"));
            AddPathIfExists(Path.Combine(userProfile, "Desktop"));

            // Safely scan C drive excluding heavy system/hidden folders
            var cDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase));
            if (cDrive != null)
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(cDrive.RootDirectory.FullName))
                    {
                        var dInfo = new DirectoryInfo(dir);
                        if ((dInfo.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                        {
                            string name = dInfo.Name.ToLower();
                            if (name != "windows" && name != "program files" && name != "program files (x86)" && name != "programdata" && name != "perflogs")
                            {
                                AddPathIfExists(dir);
                            }
                        }
                    }
                }
                catch { }
            }

            RebuildSidebarUI(); await RunBackgroundScannerAsync();
        }

        private void AddPathIfExists(string path) { if (Directory.Exists(path) && !_watchedDirectories.Contains(path)) _watchedDirectories.Add(path); }

        private void RebuildSidebarUI()
        {
            _folderListPanel.SuspendLayout(); _folderListPanel.Controls.Clear();
            AddSidebarPill("⚡ All Media", () => SwitchView(_allMediaItems), true);
            AddSidebarPill("📌 Pinned", () => SwitchToPinnedFolder());
            var sep = new Panel { Height = 1, Width = 210, BackColor = Color.FromArgb(36, 36, 44), Margin = new Padding(6) }; _folderListPanel.Controls.Add(sep);
            foreach (var path in _watchedDirectories)
            {
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = path;
                AddSidebarPill("📁 " + name, () => SwitchToFolderPath(path));
            }
            _folderListPanel.ResumeLayout();
        }

        private void AddSidebarPill(string label, Action action, bool isDefault = false)
        {
            var btn = new Button
            {
                Text = "  " + label, Width = 210, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat, Font = new Font(AppFont, 9, isDefault ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = isDefault ? Color.White : Color.FromArgb(180, 180, 190),
                BackColor = isDefault ? Color.FromArgb(38, 40, 52) : Color.Transparent,
                Margin = new Padding(0, 2, 0, 2), Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0; btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 36, 44);
            btn.Click += (s, e) =>
            {
                foreach (Control c in _folderListPanel.Controls) if (c is Button b) { b.BackColor = Color.Transparent; b.ForeColor = Color.FromArgb(180, 180, 190); b.Font = new Font(AppFont, 9, FontStyle.Regular); }
                btn.BackColor = Color.FromArgb(38, 40, 52); btn.ForeColor = Color.White; btn.Font = new Font(AppFont, 9, FontStyle.Bold); action();
            };
            _folderListPanel.Controls.Add(btn);
        }

        private void PickCustomFolder()
        {
            using var fbd = new FolderBrowserDialog { Description = "Select a folder to index" };
            if (fbd.ShowDialog() == DialogResult.OK && Directory.Exists(fbd.SelectedPath)) { AddPathIfExists(fbd.SelectedPath); RebuildSidebarUI(); _ = RunBackgroundScannerAsync(); }
        }

        private async Task RunBackgroundScannerAsync()
        {
            _scanCts?.Cancel(); _scanCts = new CancellationTokenSource(); var token = _scanCts.Token;
            await Task.Run(() =>
            {
                var discovered = new List<MediaItem>();
                foreach (var dir in _watchedDirectories.ToList()) { if (token.IsCancellationRequested) return; ScanDirRecursive(dir, discovered, token, 3); }
                var sorted = discovered.OrderByDescending(x => x.DateModified).ToList();
                this.Invoke((Action)(() =>
                {
                    _allMediaItems.Clear(); _allMediaItems.AddRange(sorted);
                    if (!string.IsNullOrEmpty(_startupFilePath))
                    {
                        string targetPath = Path.GetFullPath(_startupFilePath); string? parentDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            var targetSubset = _allMediaItems.Where(i => i.FilePath.StartsWith(parentDir, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (targetSubset.Count > 0) { SwitchView(targetSubset); int matchIdx = _currentViewItems.FindIndex(i => string.Equals(i.FilePath, targetPath, StringComparison.OrdinalIgnoreCase)); if (matchIdx >= 0) OpenViewerAt(matchIdx); return; }
                        }
                    }
                    SwitchView(_allMediaItems);
                }));
            }, token);
        }

        private void ScanDirRecursive(string path, List<MediaItem> results, CancellationToken token, int depth)
        {
            if (depth < 0 || token.IsCancellationRequested) return;
            try
            {
                int folderCount = 0;
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    if (folderCount++ > 800) break; // Hard limit per folder to prevent RAM/CPU hangs
                    
                    string ext = Path.GetExtension(file); bool isImg = ImageExts.Contains(ext); bool isVid = VideoExts.Contains(ext);
                    if (isImg || isVid) { var info = new FileInfo(file); results.Add(new MediaItem { FilePath = file, FileName = info.Name, IsVideo = isVid, DateModified = info.LastWriteTime, FileSizeBytes = info.Length }); }
                }
                foreach (var sub in Directory.EnumerateDirectories(path))
                {
                    var d = new DirectoryInfo(sub); if ((d.Attributes & FileAttributes.Hidden) != 0 || (d.Attributes & FileAttributes.System) != 0 || d.Name.StartsWith(".")) continue;
                    ScanDirRecursive(sub, results, token, depth - 1);
                }
            } catch { }
        }

        private void SwitchView(List<MediaItem> items) { _currentContextItems = items; ApplySearchAndSort(); }
        private void SwitchToFolderPath(string path) { SwitchView(_allMediaItems.Where(i => i.FilePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList()); }
        private void SwitchToPinnedFolder() { string pinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pinned"); Directory.CreateDirectory(pinDir); SwitchToFolderPath(pinDir); }

        private void ApplySearchAndSort()
        {
            string query = _searchBoxContainer.InnerBox.Text.ToLower();
            var filtered = string.IsNullOrWhiteSpace(query) ? _currentContextItems.ToList() : _currentContextItems.Where(i => i.FileName.ToLower().Contains(query)).ToList();
            if (_sortBoxContainer.InnerBox.SelectedIndex == 0) filtered = filtered.OrderByDescending(i => i.DateModified).ToList();
            else if (_sortBoxContainer.InnerBox.SelectedIndex == 1) filtered = filtered.OrderBy(i => i.FileName).ToList();
            else if (_sortBoxContainer.InnerBox.SelectedIndex == 2) filtered = filtered.OrderByDescending(i => i.FileSizeBytes).ToList();
            _currentViewItems = filtered; _gallery.SetItems(_currentViewItems);
            if (_viewer.Visible && _currentViewItems.Count > 0) { _currentIndex = 0; _viewer.LoadMedia(_currentViewItems, _currentIndex); }
        }

        private void ShowGalleryView() { _viewer.StopMediaPlayback(); _viewer.Visible = false; _gallery.Visible = true; _currentIndex = -1; }
        private void OpenViewerAt(int index) { if (index < 0 || index >= _currentViewItems.Count) return; _currentIndex = index; _gallery.Visible = false; _viewer.Visible = true; _viewer.LoadMedia(_currentViewItems, _currentIndex); }
        private void NavigateViewer(int dir) { if (_currentViewItems.Count == 0) return; OpenViewerAt((_currentIndex + dir + _currentViewItems.Count) % _currentViewItems.Count); }

        private void PinCurrentMedia()
        {
            if (_currentIndex < 0 || _currentIndex >= _currentViewItems.Count) return; var item = _currentViewItems[_currentIndex];
            string pinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pinned"); Directory.CreateDirectory(pinDir);
            string dest = Path.Combine(pinDir, item.FileName); if (!File.Exists(dest)) File.Copy(item.FilePath, dest);
            MessageBox.Show($"Pinned '{item.FileName}'!", "ZeroPhotos");
        }

        private void TrashCurrentMedia()
        {
            if (_currentIndex < 0 || _currentIndex >= _currentViewItems.Count) return; var item = _currentViewItems[_currentIndex];
            string trashDir = Path.Combine(Path.GetDirectoryName(item.FilePath)!, ".zerotrash"); Directory.CreateDirectory(trashDir);
            _viewer.StopMediaPlayback(); File.Move(item.FilePath, Path.Combine(trashDir, item.FileName), true);
            _allMediaItems.Remove(item); _currentContextItems.Remove(item); ApplySearchAndSort();
            if (_currentViewItems.Count == 0) ShowGalleryView(); else { if (_currentIndex >= _currentViewItems.Count) _currentIndex = _currentViewItems.Count - 1; OpenViewerAt(_currentIndex); }
        }

        private void HandleGlobalKeys(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S && _viewer.Visible) { _viewer.SaveEdits(); e.Handled = true; return; }
            if (_searchBoxContainer.InnerBox.Focused) return; 
            if (!_viewer.Visible) return;
            if (e.KeyCode == Keys.Right) { NavigateViewer(1); e.Handled = true; } else if (e.KeyCode == Keys.Left) { NavigateViewer(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { ShowGalleryView(); e.Handled = true; } else if (e.KeyCode == Keys.P) { PinCurrentMedia(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { TrashCurrentMedia(); e.Handled = true; } else if (e.KeyCode == Keys.Space) { _viewer.TogglePlayPause(); e.Handled = true; }
            else if (e.KeyCode == Keys.I) { _viewer.ToggleInfoHud(); e.Handled = true; } else if (e.KeyCode == Keys.R) { _viewer.RotateImage(); e.Handled = true; }
            else if (e.KeyCode == Keys.C) { _viewer.ToggleCropMode(); e.Handled = true; } else if (e.KeyCode == Keys.Enter) { _viewer.ApplyCrop(); e.Handled = true; }
        }
    }

    public class MediaItem
    {
        public string FilePath { get; set; } = ""; public string FileName { get; set; } = ""; public bool IsVideo { get; set; } public DateTime DateModified { get; set; } public long FileSizeBytes { get; set; }
    }

    // --- ROUNDED UI CONTROLS ---
    public class RoundedTextBoxContainer : Panel
    {
        public TextBox InnerBox { get; }
        public RoundedTextBoxContainer()
        {
            DoubleBuffered = true; BackColor = Color.FromArgb(32, 34, 42); Padding = new Padding(12, 6, 12, 6);
            InnerBox = new TextBox { BorderStyle = BorderStyle.None, BackColor = BackColor, ForeColor = Color.White, Dock = DockStyle.Fill, Font = new Font(MainForm.AppFont, 10), PlaceholderText = "Search media..." };
            Controls.Add(InnerBox);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var path = new GraphicsPath(); int d = 16;
            path.AddArc(0, 0, d, d, 180, 90); path.AddArc(Width - d, 0, d, d, 270, 90); path.AddArc(Width - d, Height - d, d, d, 0, 90); path.AddArc(0, Height - d, d, d, 90, 90); path.CloseFigure();
            using var brush = new SolidBrush(BackColor); e.Graphics.Clear(Parent.BackColor); e.Graphics.FillPath(brush, path);
        }
    }

    public class RoundedComboBoxContainer : Panel
    {
        public ComboBox InnerBox { get; }
        public RoundedComboBoxContainer()
        {
            DoubleBuffered = true; BackColor = Color.FromArgb(32, 34, 42); Padding = new Padding(10, 4, 10, 4);
            InnerBox = new ComboBox { FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BackColor, ForeColor = Color.White, Dock = DockStyle.Fill, Font = new Font(MainForm.AppFont, 9) };
            Controls.Add(InnerBox);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var path = new GraphicsPath(); int d = 16;
            path.AddArc(0, 0, d, d, 180, 90); path.AddArc(Width - d, 0, d, d, 270, 90); path.AddArc(Width - d, Height - d, d, d, 0, 90); path.AddArc(0, Height - d, d, d, 90, 90); path.CloseFigure();
            using var brush = new SolidBrush(BackColor); e.Graphics.Clear(Parent.BackColor); e.Graphics.FillPath(brush, path);
        }
    }

    // --- GRID GALLERY (DECOUPLED 60FPS BATCHED REDRAWS) ---
    public class GalleryControl : Control
    {
        private List<MediaItem> _items = new(); private readonly ConcurrentDictionary<string, Image> _thumbs = new();
        private readonly ConcurrentQueue<MediaItem> _thumbQueue = new();
        private bool _isProcessingQueue = false;
        private bool _needsRedraw = false;
        
        private int _thumbSize = 170; private int _gap = 16; private int _hoverIndex = -1;
        private float _targetScrollY = 0; private float _currentScrollY = 0;
        private System.Windows.Forms.Timer _animTimer;
        public event Action<int>? ItemClicked;

        public GalleryControl()
        {
            DoubleBuffered = true; BackColor = Color.FromArgb(14, 14, 16); Cursor = Cursors.Hand;
            
            // Centralized 60Hz UI Update Loop
            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += (s, e) => 
            {
                bool moving = Math.Abs(_targetScrollY - _currentScrollY) > 0.5f;
                if (moving) { _currentScrollY += (_targetScrollY - _currentScrollY) * 0.35f; }
                else { _currentScrollY = _targetScrollY; }
                
                // Only invalidate if scrolling OR if the background queue loaded new thumbnails
                if (moving || _needsRedraw) {
                    _needsRedraw = false;
                    Invalidate();
                } else if (!moving && !_needsRedraw) {
                    _animTimer.Stop(); // Rest CPU when idle
                }
            };
            this.MouseWheel += (s, e) => { _targetScrollY = Math.Max(0, _targetScrollY - (e.Delta)); _animTimer.Start(); };
        }

        public void SetItems(List<MediaItem> items) { _items = items; _targetScrollY = 0; _currentScrollY = 0; _thumbQueue.Clear(); _animTimer.Start(); Invalidate(); }
        private void TrimMemoryCache() { if (_thumbs.Count > 150) { foreach (var img in _thumbs.Values) img.Dispose(); _thumbs.Clear(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            TrimMemoryCache(); var g = e.Graphics; g.Clear(BackColor);
            if (_items.Count == 0) { using var font = new Font(MainForm.AppFont, 12); g.DrawString("No media found.", font, Brushes.Gray, (Width - 100) / 2, Height / 2); return; }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cols = Math.Max(1, (Width - _gap) / (_thumbSize + _gap));
            int startRow = (int)_currentScrollY / (_thumbSize + _gap);
            int endRow = startRow + (Height / (_thumbSize + _gap)) + 2;

            for (int r = startRow; r <= endRow; r++)
                for (int c = 0; c < cols; c++)
                {
                    int index = r * cols + c; if (index >= _items.Count) return;
                    int x = _gap + c * (_thumbSize + _gap); int y = _gap + r * (_thumbSize + _gap) - (int)_currentScrollY;
                    DrawCard(g, new Rectangle(x, y, _thumbSize, _thumbSize), _items[index], index == _hoverIndex);
                }
        }

        private void DrawCard(Graphics g, Rectangle rect, MediaItem item, bool isHovered)
        {
            if (isHovered) rect.Inflate(4, 4);

            using var path = GetRoundedRect(rect, 10); g.SetClip(path);
            if (_thumbs.TryGetValue(item.FilePath, out var bmp)) g.DrawImage(bmp, rect);
            else 
            { 
                using var b = new SolidBrush(Color.FromArgb(24, 26, 32)); g.FillRectangle(b, rect); 
                QueueThumbnail(item);
            }

            if (item.IsVideo)
            {
                using var grad = new LinearGradientBrush(new Point(rect.X, rect.Bottom - 36), new Point(rect.X, rect.Bottom), Color.Transparent, Color.FromArgb(220, 0, 0, 0));
                g.FillRectangle(grad, rect.X, rect.Bottom - 36, rect.Width, 36);
                using var f = new Font(MainForm.AppFont, 8, FontStyle.Bold); g.DrawString("▶ VIDEO", f, Brushes.White, rect.X + 8, rect.Bottom - 24);
            }
            g.ResetClip();
            if (isHovered) { using var p = new Pen(Color.FromArgb(50, 150, 255), 3f); g.DrawPath(p, path); }
        }

        private void QueueThumbnail(MediaItem item)
        {
            if (!_thumbQueue.Contains(item)) _thumbQueue.Enqueue(item);
            if (!_isProcessingQueue) ProcessQueueAsync();
        }

        private async void ProcessQueueAsync()
        {
            _isProcessingQueue = true;
            await Task.Run(() =>
            {
                while (_thumbQueue.TryDequeue(out var item))
                {
                    if (_thumbs.ContainsKey(item.FilePath)) continue;
                    try {
                        if (!item.IsVideo) { using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var orig = Image.FromStream(fs, false, false); _thumbs[item.FilePath] = orig.GetThumbnailImage(_thumbSize, _thumbSize, () => false, IntPtr.Zero); }
                        else { var bmp = new Bitmap(_thumbSize, _thumbSize); using var bg = Graphics.FromImage(bmp); bg.Clear(Color.FromArgb(22, 24, 30)); using var f = new Font("Segoe UI", 26); bg.DrawString("▶", f, Brushes.DimGray, (_thumbSize / 2) - 18, (_thumbSize / 2) - 25); _thumbs[item.FilePath] = bmp; }
                        
                        // Flag the 60hz timer to safely invalidate the UI on the next frame
                        _needsRedraw = true; 
                        this.Invoke((Action)_animTimer.Start);
                    } catch { }
                }
            });
            _isProcessingQueue = false;
        }

        private GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath(); int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int cols = Math.Max(1, (Width - _gap) / (_thumbSize + _gap));
            int c = (e.X - _gap) / (_thumbSize + _gap); int r = (e.Y + (int)_currentScrollY - _gap) / (_thumbSize + _gap);
            int newHover = (c >= 0 && c < cols) ? (r * cols + c) : -1;
            if (newHover >= _items.Count) newHover = -1;
            if (_hoverIndex != newHover) { _hoverIndex = newHover; Invalidate(); }
        }
        protected override void OnMouseClick(MouseEventArgs e) { if (_hoverIndex >= 0 && _hoverIndex < _items.Count) ItemClicked?.Invoke(_hoverIndex); }
        protected override void OnMouseLeave(EventArgs e) { _hoverIndex = -1; Invalidate(); }
    }

    // --- SEAMLESS ASYNC LOADER (No Flicker/Blinking) ---
    public class ViewerContainer : Panel
    {
        private readonly CanvasControl _canvas; private readonly NativeVideoHost _videoHost; private readonly FilmstripControl _filmstrip;
        private readonly Label _hudInfo; private readonly FlowLayoutPanel _actionBar;
        private readonly Button _btnApplyCrop, _btnRotate, _btnCrop, _btnSave, _btnPlay, _btnPin, _btnTrash, _btnInfo;
        private MediaItem? _activeMedia; private bool _infoVisible = true;
        private int _loadId = 0; // Prevents race conditions when spamming Arrow Keys
        
        public event Action<int>? FilmstripSelected; public event Action? PinRequested; public event Action? TrashRequested;

        public ViewerContainer()
        {
            this.BackColor = Color.FromArgb(10, 10, 12);
            _canvas = new CanvasControl { Dock = DockStyle.Fill }; _videoHost = new NativeVideoHost { Dock = DockStyle.Fill, Visible = false };
            _filmstrip = new FilmstripControl { Dock = DockStyle.Bottom, Height = 74 }; _filmstrip.ItemSelected += (idx) => FilmstripSelected?.Invoke(idx);
            _hudInfo = new Label { AutoSize = true, BackColor = Color.FromArgb(190, 18, 18, 22), ForeColor = Color.FromArgb(235, 235, 240), Font = new Font(MainForm.AppFont, 9), Padding = new Padding(12, 8, 12, 8), Location = new Point(20, 12) };

            _actionBar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            _btnApplyCrop = CreateBtn("✔ Apply", ApplyCrop, Color.FromArgb(40, 160, 80)); _btnApplyCrop.Visible = false;
            _btnRotate = CreateBtn("⟳ Rotate", RotateImage); _btnCrop = CreateBtn("✂ Crop", ToggleCropMode); _btnSave = CreateBtn("💾 Save", SaveEdits); _btnPlay = CreateBtn("⏯ Play", TogglePlayPause); _btnPin = CreateBtn("📌 Pin", () => PinRequested?.Invoke());
            _btnTrash = CreateBtn("🗑 Trash", () => TrashRequested?.Invoke(), Color.FromArgb(160, 40, 50)); _btnTrash.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 40, 40);
            _btnInfo = CreateBtn("ℹ Info", ToggleInfoHud);

            _actionBar.Controls.AddRange(new Control[] { _btnApplyCrop, _btnRotate, _btnCrop, _btnSave, _btnPlay, _btnPin, _btnTrash, _btnInfo });
            this.Controls.Add(_videoHost); this.Controls.Add(_canvas); this.Controls.Add(_filmstrip); _videoHost.Controls.Add(_hudInfo);
            _canvas.MouseDoubleClick += (s, e) => _canvas.ResetZoom();
            _canvas.CropModeChanged += () => { _btnApplyCrop.Visible = _canvas.IsCropMode; _btnRotate.Visible = !_canvas.IsCropMode; _btnCrop.Text = _canvas.IsCropMode ? "✖ Cancel" : "✂ Crop"; RepositionActionBar(); };
        }

        private Button CreateBtn(string text, Action onClick, Color? backColor = null)
        {
            var btn = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = backColor ?? Color.FromArgb(40, 42, 50), Font = new Font(MainForm.AppFont, 9, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(4, 0, 4, 0), Padding = new Padding(4, 2, 4, 2) };
            btn.FlatAppearance.BorderSize = 0; btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 130, 255);
            btn.Click += (s, e) => { onClick(); _canvas.Focus(); }; return btn;
        }

        public async void LoadMedia(List<MediaItem> context, int index)
        {
            _activeMedia = context[index]; _filmstrip.SetItems(context, index);
            int currentLoadId = ++_loadId;

            if (_activeMedia.IsVideo)
            {
                _canvas.Visible = false; _videoHost.Visible = true; _videoHost.Play(_activeMedia.FilePath); UpdateVideoHud(); _actionBar.Parent = _videoHost; _btnApplyCrop.Visible = false; _btnRotate.Visible = false; _btnCrop.Visible = false; _btnSave.Visible = false; _btnPlay.Visible = true; RepositionActionBar();
            }
            else
            {
                _videoHost.Stop(); _videoHost.Visible = false; _canvas.Visible = true; _actionBar.Parent = _canvas; _btnApplyCrop.Visible = _canvas.IsCropMode; _btnRotate.Visible = !_canvas.IsCropMode; _btnCrop.Visible = true; _btnSave.Visible = true; _btnPlay.Visible = false; RepositionActionBar();
                
                // Keep the old image on screen while loading the new one to prevent black blinking
                var img = await Task.Run(() => {
                    try { using var fs = new FileStream(_activeMedia.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var temp = Image.FromStream(fs, false, false); return new Bitmap(temp); } catch { return null; }
                });

                // Only apply if the user hasn't spammed the arrow keys past this photo
                if (_loadId == currentLoadId) _canvas.SetImage(img, _activeMedia);
                else img?.Dispose();
            }
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); RepositionActionBar(); }
        private void RepositionActionBar() { if (_actionBar.Parent != null) _actionBar.Location = new Point(_actionBar.Parent.Width - _actionBar.PreferredSize.Width - 16, 12); }
        private void UpdateVideoHud() { if (_activeMedia == null) return; _hudInfo.Text = $"▶ VIDEO FILE\nName: {_activeMedia.FileName}\nSize: {(_activeMedia.FileSizeBytes / 1048576.0):F2} MB"; _hudInfo.Visible = _infoVisible; }
        public void TogglePlayPause() { if (_activeMedia != null && _activeMedia.IsVideo) _videoHost.TogglePlayPause(); }
        public void ToggleInfoHud() { _infoVisible = !_infoVisible; _hudInfo.Visible = _infoVisible; _canvas.ShowHud = _infoVisible; _actionBar.Visible = _infoVisible; _canvas.Invalidate(); }
        public void StopMediaPlayback() => _videoHost.Stop();
        public void RotateImage() => _canvas.RotateImage(); public void ToggleCropMode() => _canvas.ToggleCropMode(); public void ApplyCrop() => _canvas.ApplyCrop(); public void SaveEdits() => _canvas.SaveEdits();
    }

    // --- HIGH-PERFORMANCE CANVAS (DYNAMIC RENDERING LOD) ---
    public class CanvasControl : Control
    {
        public Image? LoadedImage { get; private set; } public float Zoom { get; private set; } = 1.0f; public bool ShowHud { get; set; } = true;
        private MediaItem? _activeMedia; private string _exifData = ""; private PointF _panOffset = new PointF(0, 0); private Point _dragStart; private bool _isPanning;
        private bool _isCropMode = false; public bool IsCropMode => _isCropMode; public event Action? CropModeChanged;
        private bool _isDrawingCrop = false; private Point _cropStart; private Point _cropEnd;

        private System.Windows.Forms.Timer _animTimer;
        private System.Windows.Forms.Timer _qualityTimer;
        private float _animScale = 1.0f; 
        private float _fadeAlpha = 0f;
        private bool _isInteracting = false; 

        public CanvasControl()
        {
            DoubleBuffered = true; BackColor = Color.FromArgb(10, 10, 12); Cursor = Cursors.Cross;
            
            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += (s, e) => 
            {
                _animScale += (1.0f - _animScale) * 0.35f; _fadeAlpha += (0.0f - _fadeAlpha) * 0.35f;
                if (Math.Abs(_animScale - 1.0f) < 0.005f) { _animScale = 1.0f; _fadeAlpha = 0f; _animTimer.Stop(); }
                Invalidate();
            };

            _qualityTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _qualityTimer.Tick += (s, e) => { _isInteracting = false; _qualityTimer.Stop(); Invalidate(); };
        }

        public void SetImage(Image? img, MediaItem media)
        {
            var old = LoadedImage; LoadedImage = img; old?.Dispose(); 
            _activeMedia = media; Zoom = 1.0f; _panOffset = new PointF(0, 0); _isCropMode = false; _exifData = ""; CropModeChanged?.Invoke();
            
            if (img != null) { try { var model = img.GetPropertyItem(0x0110); if (model != null) _exifData = "Shot on: " + Encoding.ASCII.GetString(model.Value).Replace("\0", ""); } catch { } }
            
            _animScale = 0.96f; _fadeAlpha = 1.0f; _animTimer.Start(); Invalidate();
        }

        public void ResetZoom() { Zoom = 1.0f; _panOffset = new PointF(0, 0); TriggerInteraction(); Invalidate(); }
        public void RotateImage() { if (LoadedImage == null || _activeMedia == null || _activeMedia.IsVideo) return; LoadedImage.RotateFlip(RotateFlipType.Rotate90FlipNone); TriggerInteraction(); Invalidate(); }
        public void ToggleCropMode() { if (LoadedImage == null || _activeMedia == null || _activeMedia.IsVideo) return; _isCropMode = !_isCropMode; _cropStart = Point.Empty; _cropEnd = Point.Empty; CropModeChanged?.Invoke(); Invalidate(); }
        public void ApplyCrop() { if (!_isCropMode || LoadedImage == null || _cropStart == _cropEnd) return; var p1 = ScreenToImage(_cropStart); var p2 = ScreenToImage(_cropEnd); int x = (int)Math.Max(0, Math.Min(p1.X, p2.X)); int y = (int)Math.Max(0, Math.Min(p1.Y, p2.Y)); int w = (int)Math.Min(LoadedImage.Width - x, Math.Abs(p1.X - p2.X)); int h = (int)Math.Min(LoadedImage.Height - y, Math.Abs(p1.Y - p2.Y)); if (w <= 0 || h <= 0) { _isCropMode = false; CropModeChanged?.Invoke(); Invalidate(); return; } var newBmp = new Bitmap(w, h); using (var g = Graphics.FromImage(newBmp)) { g.DrawImage(LoadedImage, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel); } var old = LoadedImage; LoadedImage = newBmp; old?.Dispose(); _isCropMode = false; CropModeChanged?.Invoke(); ResetZoom(); }
        public void SaveEdits() { if (LoadedImage == null || _activeMedia == null) return; try { LoadedImage.Save(_activeMedia.FilePath); MessageBox.Show("Changes saved successfully!", "ZeroPhotos", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch (Exception ex) { MessageBox.Show("Failed to save: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
        
        private void TriggerInteraction() { _isInteracting = true; _qualityTimer.Stop(); _qualityTimer.Start(); }
        private PointF ScreenToImage(Point sp) { if (LoadedImage == null) return new PointF(0, 0); float fitScale = Math.Min((float)Width / LoadedImage.Width, (float)Height / LoadedImage.Height); float drawW = (LoadedImage.Width * fitScale) * Zoom; float drawH = (LoadedImage.Height * fitScale) * Zoom; float drawX = ((Width - drawW) / 2) + _panOffset.X; float drawY = ((Height - drawH) / 2) + _panOffset.Y; return new PointF((sp.X - drawX) / (fitScale * Zoom), (sp.Y - drawY) / (fitScale * Zoom)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            if (LoadedImage != null)
            {
                g.InterpolationMode = _isInteracting ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic; 
                g.PixelOffsetMode = PixelOffsetMode.Half;
                
                float fitScale = Math.Min((float)Width / LoadedImage.Width, (float)Height / LoadedImage.Height);
                float drawW = (LoadedImage.Width * fitScale) * Zoom * _animScale; 
                float drawH = (LoadedImage.Height * fitScale) * Zoom * _animScale;
                float drawX = ((Width - drawW) / 2) + _panOffset.X; float drawY = ((Height - drawH) / 2) + _panOffset.Y;
                g.DrawImage(LoadedImage, drawX, drawY, drawW, drawH);

                if (_isCropMode)
                {
                    using var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)); g.FillRectangle(dimBrush, ClientRectangle);
                    if (_cropStart != _cropEnd) { var rect = new Rectangle(Math.Min(_cropStart.X, _cropEnd.X), Math.Min(_cropStart.Y, _cropEnd.Y), Math.Abs(_cropStart.X - _cropEnd.X), Math.Abs(_cropStart.Y - _cropEnd.Y)); g.CompositingMode = CompositingMode.SourceCopy; g.FillRectangle(Brushes.Transparent, rect); g.CompositingMode = CompositingMode.SourceOver; using var dashPen = new Pen(Color.White, 2) { DashStyle = DashStyle.Dash }; g.DrawRectangle(dashPen, rect); }
                    g.DrawString("CROP MODE: Drag to select area. Click '✔ Apply' or press Enter.", new Font(MainForm.AppFont, 12, FontStyle.Bold), Brushes.LimeGreen, 20, 70);
                }
            }

            if (_fadeAlpha > 0f) { using var brush = new SolidBrush(Color.FromArgb((int)(_fadeAlpha * 255), 10, 10, 12)); g.FillRectangle(brush, ClientRectangle); }
            if (ShowHud && _activeMedia != null)
            {
                using var glassBrush = new SolidBrush(Color.FromArgb(170, 18, 22, 28)); using var borderPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);
                g.FillRectangle(glassBrush, 0, 0, Width, 48); g.DrawLine(borderPen, 0, 48, Width, 48);
                using var boldFont = new Font(MainForm.AppFont, 10, FontStyle.Bold); using var font = new Font(MainForm.AppFont, 9);
                string meta = $"{(_activeMedia.FileSizeBytes / 1048576.0):F2} MB"; if (LoadedImage != null) meta += $"  •  {LoadedImage.Width} x {LoadedImage.Height}  •  Zoom: {Math.Round(Zoom * 100)}%  •  {_exifData}";
                g.DrawString(_activeMedia.FileName, boldFont, Brushes.White, 16, 14); g.DrawString(meta, font, Brushes.LightGray, 300, 15);
            }
        }
        protected override void OnMouseWheel(MouseEventArgs e) { if (LoadedImage == null || _isCropMode) return; float factor = e.Delta > 0 ? 1.25f : 0.8f; float newZoom = Math.Clamp(Zoom * factor, 0.1f, 30.0f); _panOffset.X -= (e.X - (Width / 2f) - _panOffset.X) * (newZoom / Zoom - 1); _panOffset.Y -= (e.Y - (Height / 2f) - _panOffset.Y) * (newZoom / Zoom - 1); Zoom = newZoom; TriggerInteraction(); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { if (_isCropMode) { _isDrawingCrop = true; _cropStart = e.Location; _cropEnd = e.Location; } else { _isPanning = true; _dragStart = e.Location; TriggerInteraction(); } } }
        protected override void OnMouseMove(MouseEventArgs e) { if (_isDrawingCrop && _isCropMode) { _cropEnd = e.Location; Invalidate(); } else if (_isPanning) { _panOffset.X += (e.X - _dragStart.X); _panOffset.Y += (e.Y - _dragStart.Y); _dragStart = e.Location; TriggerInteraction(); Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { _isPanning = false; _isDrawingCrop = false; }
    }

    // --- HARDWARE-ACCELERATED NATIVE DIRECTSHOW VIDEO PLAYER ---
    public class NativeVideoHost : Control
    {
        private DirectShowPlayer? _player; private bool _isPlaying;
        public NativeVideoHost() { this.DoubleBuffered = false; this.BackColor = Color.Black; }
        public void Play(string path) { Stop(); _player = new DirectShowPlayer(); _player.OpenFile(path, this.Handle, this.Width, this.Height); _player.Play(); _isPlaying = true; }
        public void TogglePlayPause() { if (_player == null) return; if (_isPlaying) { _player.Pause(); _isPlaying = false; } else { _player.Play(); _isPlaying = true; } }
        public void Stop() { if (_player != null) { _player.Stop(); _player.Dispose(); _player = null; _isPlaying = false; } }
        protected override void OnResize(EventArgs e) { base.OnResize(e); _player?.ResizeVideo(this.Width, this.Height); }
    }

    public class DirectShowPlayer : IDisposable
    {
        private IGraphBuilder? _graph; private IMediaControl? _control; private IVideoWindow? _window;
        public void OpenFile(string path, IntPtr hostHwnd, int width, int height) { _graph = (IGraphBuilder)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770"))!)!; _control = (IMediaControl)_graph; _window = (IVideoWindow)_graph; _graph.RenderFile(path, null); _window.put_Owner(hostHwnd.ToInt32()); _window.put_WindowStyle(0x40000000 | 0x10000000); _window.SetWindowPosition(0, 0, width, height); }
        public void Play() => _control?.Run(); public void Pause() => _control?.Pause(); public void Stop() => _control?.Stop(); public void ResizeVideo(int w, int h) => _window?.SetWindowPosition(0, 0, w, h);
        public void Dispose() { Stop(); if (_window != null) { _window.put_Visible(0); } if (_control != null) Marshal.ReleaseComObject(_control); if (_window != null) Marshal.ReleaseComObject(_window); if (_graph != null) Marshal.ReleaseComObject(_graph); }
        [ComImport, Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IGraphBuilder { [PreserveSig] int AddFilter(object pFilter, string pName); [PreserveSig] int RemoveFilter(object pFilter); [PreserveSig] int EnumFilters(out object ppEnum); [PreserveSig] int FindFilterByName(string pName, out object ppFilter); [PreserveSig] int ConnectDirect(object ppinOut, object ppinIn, object pmt); [PreserveSig] int Reconnect(object ppin); [PreserveSig] int Disconnect(object ppin); [PreserveSig] int SetDefaultSyncSource(); [PreserveSig] int Connect(object ppinOut, object ppinIn); [PreserveSig] int Render(object ppinOut); [PreserveSig] int RenderFile(string lpcwstrFile, string? lpcwstrPlayList); }
        [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)] private interface IMediaControl { void Run(); void Pause(); void Stop(); }
        [ComImport, Guid("56A868B4-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)] private interface IVideoWindow { void put_Owner(int Owner); void put_WindowStyle(int WindowStyle); void SetWindowPosition(int Left, int Top, int Width, int Height); void put_Visible(int Visible); }
    }

    // --- FILMSTRIP CONTROL ---
    public class FilmstripControl : Control
    {
        private List<MediaItem> _items = new(); private int _selectedIndex = -1; private readonly ConcurrentDictionary<string, Image> _cache = new();
        public event Action<int>? ItemSelected;
        public FilmstripControl() { DoubleBuffered = true; BackColor = Color.FromArgb(16, 16, 20); Cursor = Cursors.Hand; }
        public void SetItems(List<MediaItem> items, int selected) { _items = items; _selectedIndex = selected; Invalidate(); }
        private void TrimMemoryCache() { if (_cache.Count > 100) { foreach (var img in _cache.Values) img.Dispose(); _cache.Clear(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            TrimMemoryCache(); var g = e.Graphics;
            using var glassBrush = new SolidBrush(Color.FromArgb(170, 14, 16, 20)); using var borderPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);
            g.FillRectangle(glassBrush, 0, 0, Width, Height); g.DrawLine(borderPen, 0, 0, Width, 0);
            if (_items.Count == 0) return;
            int tw = 60, th = 50, gap = 8; int maxVisible = Width / (tw + gap);
            int startIdx = Math.Max(0, _selectedIndex - (maxVisible / 2)); int endIdx = Math.Min(_items.Count, startIdx + maxVisible);
            for (int i = startIdx; i < endIdx; i++)
            {
                int x = 12 + ((i - startIdx) * (tw + gap)); int y = (Height - th) / 2; var rect = new Rectangle(x, y, tw, th);
                if (i == _selectedIndex) { using var pen = new Pen(Color.FromArgb(0, 140, 255), 2); g.DrawRectangle(pen, rect.X - 2, rect.Y - 2, tw + 4, th + 4); }
                var item = _items[i];
                if (!item.IsVideo) { if (_cache.TryGetValue(item.FilePath, out var bmp)) g.DrawImage(bmp, rect); else { using var bg = new SolidBrush(Color.FromArgb(28, 28, 34)); g.FillRectangle(bg, rect); Task.Run(() => { try { using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read); using var orig = Image.FromStream(fs, false, false); _cache[item.FilePath] = orig.GetThumbnailImage(tw, th, () => false, IntPtr.Zero); Invalidate(); } catch { } }); } }
                else { using var vidBrush = new SolidBrush(Color.FromArgb(32, 34, 42)); g.FillRectangle(vidBrush, rect); using var f = new Font(MainForm.AppFont, 8, FontStyle.Bold); g.DrawString("VID", f, Brushes.White, rect.X + 16, rect.Y + 16); }
            }
        }
        protected override void OnMouseClick(MouseEventArgs e) { int tw = 60, gap = 8; int maxVisible = Width / (tw + gap); int startIdx = Math.Max(0, _selectedIndex - (maxVisible / 2)); int clicked = startIdx + ((e.X - 12) / (tw + gap)); if (clicked >= 0 && clicked < _items.Count) ItemSelected?.Invoke(clicked); }
    }
}