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
        // Layout Panels
        private readonly Panel _sidebar;
        private readonly Panel _sidebarHeader;
        private readonly FlowLayoutPanel _folderListPanel;
        private readonly GalleryControl _gallery;
        private readonly ViewerContainer _viewer;

        // Startup target
        private readonly string? _startupFilePath;

        // Data State
        private readonly HashSet<string> _watchedDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MediaItem> _allMediaItems = new();
        private List<MediaItem> _currentViewItems = new();
        private int _currentIndex = -1;
        private CancellationTokenSource? _scanCts;

        // Supported Extensions
        private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".ico", ".tiff" };
        private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".wmv", ".mov", ".m4v", ".webm" };

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public MainForm(string? startupFilePath = null)
        {
            _startupFilePath = startupFilePath;

            this.Text = "ZeroPhotos - Ultra-Lightweight Media Suite";
            this.Size = new Size(1300, 850);
            this.MinimumSize = new Size(850, 600);
            this.BackColor = Color.FromArgb(14, 14, 16);
            this.ForeColor = Color.White;
            this.KeyPreview = true;

            int dark = 1;
            DwmSetWindowAttribute(this.Handle, 20, ref dark, sizeof(int));

            // Sidebar
            _sidebar = new Panel { Width = 230, Dock = DockStyle.Left, BackColor = Color.FromArgb(20, 20, 24) };
            _sidebarHeader = new Panel { Dock = DockStyle.Top, Height = 115, BackColor = Color.FromArgb(18, 18, 22), Padding = new Padding(14) };

            var lblApp = new Label { Text = "ZeroPhotos", Font = new Font("Segoe UI", 13, FontStyle.Bold), ForeColor = Color.White, Location = new Point(14, 14), AutoSize = true };
            var btnAddFolder = new Button { Text = "+ Add Folder", Font = new Font("Segoe UI", 9), ForeColor = Color.FromArgb(180, 215, 255), BackColor = Color.FromArgb(32, 34, 42), FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(14, 48, 202, 30), Cursor = Cursors.Hand };
            btnAddFolder.FlatAppearance.BorderSize = 0;
            btnAddFolder.Click += (s, e) => PickCustomFolder();

            var btnBackToGallery = new Button { Text = "← Back to Grid", Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.FromArgb(255, 180, 80), BackColor = Color.FromArgb(38, 30, 24), FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(14, 82, 202, 26), Visible = false, Cursor = Cursors.Hand };
            btnBackToGallery.FlatAppearance.BorderSize = 0;
            btnBackToGallery.Click += (s, e) => ShowGalleryView();

            _sidebarHeader.Controls.AddRange(new Control[] { lblApp, btnAddFolder, btnBackToGallery });

            _folderListPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6) };
            _sidebar.Controls.Add(_folderListPanel);
            _sidebar.Controls.Add(_sidebarHeader);

            // Workspace
            _gallery = new GalleryControl { Dock = DockStyle.Fill };
            _gallery.ItemClicked += (idx) => OpenViewerAt(idx);

            _viewer = new ViewerContainer { Dock = DockStyle.Fill, Visible = false };
            _viewer.FilmstripSelected += (idx) => OpenViewerAt(idx);

            this.Controls.Add(_viewer);
            this.Controls.Add(_gallery);
            this.Controls.Add(_sidebar);

            _viewer.VisibleChanged += (s, e) => btnBackToGallery.Visible = _viewer.Visible;
            this.KeyDown += HandleGlobalKeys;
            this.Load += async (s, e) => await InitializeEngineAsync();
        }

        private async Task InitializeEngineAsync()
        {
            // If launched with a specific file, prioritize its parent folder
            if (!string.IsNullOrEmpty(_startupFilePath))
            {
                string? parentDir = Path.GetDirectoryName(_startupFilePath);
                if (!string.IsNullOrEmpty(parentDir)) AddPathIfExists(parentDir);
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddPathIfExists(Path.Combine(userProfile, "Pictures"));
            AddPathIfExists(Path.Combine(userProfile, "Videos"));
            AddPathIfExists(Path.Combine(userProfile, "Downloads"));
            AddPathIfExists(Path.Combine(userProfile, "Desktop"));

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                if (!drive.RootDirectory.FullName.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                {
                    AddPathIfExists(drive.RootDirectory.FullName);
                }
            }

            RebuildSidebarUI();
            await RunBackgroundScannerAsync();
        }

        private void AddPathIfExists(string path)
        {
            if (Directory.Exists(path) && !_watchedDirectories.Contains(path))
            {
                _watchedDirectories.Add(path);
            }
        }

        private void RebuildSidebarUI()
        {
            _folderListPanel.SuspendLayout();
            _folderListPanel.Controls.Clear();

            AddSidebarPill("⚡ All Photos & Videos", () => SwitchView(_allMediaItems), true);
            AddSidebarPill("📌 Pinned Photos", () => SwitchToPinnedFolder());

            var sep = new Panel
            {
                Height = 1,
                Width = 202,
                BackColor = Color.FromArgb(36, 36, 44),
                Margin = new Padding(6, 6, 6, 6)
            };
            _folderListPanel.Controls.Add(sep);

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
                Text = "  " + label,
                Width = 202,
                Height = 34,
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, isDefault ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = isDefault ? Color.White : Color.FromArgb(180, 180, 190),
                BackColor = isDefault ? Color.FromArgb(38, 40, 52) : Color.Transparent,
                Margin = new Padding(0, 2, 0, 2),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 32, 40);

            btn.Click += (s, e) =>
            {
                foreach (Control c in _folderListPanel.Controls)
                {
                    if (c is Button b)
                    {
                        b.BackColor = Color.Transparent;
                        b.ForeColor = Color.FromArgb(180, 180, 190);
                        b.Font = new Font("Segoe UI", 9, FontStyle.Regular);
                    }
                }
                btn.BackColor = Color.FromArgb(38, 40, 52);
                btn.ForeColor = Color.White;
                btn.Font = new Font("Segoe UI", 9, FontStyle.Bold);
                action();
            };

            _folderListPanel.Controls.Add(btn);
        }

        private void PickCustomFolder()
        {
            using var fbd = new FolderBrowserDialog();
            fbd.Description = "Select a folder for ZeroPhotos to index";
            if (fbd.ShowDialog() == DialogResult.OK && Directory.Exists(fbd.SelectedPath))
            {
                AddPathIfExists(fbd.SelectedPath);
                RebuildSidebarUI();
                _ = RunBackgroundScannerAsync();
            }
        }

        private async Task RunBackgroundScannerAsync()
        {
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            await Task.Run(() =>
            {
                var discovered = new List<MediaItem>();

                foreach (var dir in _watchedDirectories.ToList())
                {
                    if (token.IsCancellationRequested) return;
                    ScanDirRecursive(dir, discovered, token, depth: 3);
                }

                var sorted = discovered.OrderByDescending(x => x.DateModified).ToList();

                this.Invoke((Action)(() =>
                {
                    _allMediaItems.Clear();
                    _allMediaItems.AddRange(sorted);

                    // Case A: App launched via file click -> Open viewer directly on target
                    if (!string.IsNullOrEmpty(_startupFilePath))
                    {
                        string targetPath = Path.GetFullPath(_startupFilePath);
                        string? parentDir = Path.GetDirectoryName(targetPath);

                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            _currentViewItems = _allMediaItems
                                .Where(i => i.FilePath.StartsWith(parentDir, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                        }

                        if (_currentViewItems.Count == 0) _currentViewItems = _allMediaItems;

                        int matchIdx = _currentViewItems.FindIndex(i => string.Equals(i.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
                        if (matchIdx >= 0)
                        {
                            OpenViewerAt(matchIdx);
                            return;
                        }
                    }

                    // Case B: Normal launcher -> Show gallery grid
                    if (_currentViewItems.Count == 0 && _allMediaItems.Count > 0)
                    {
                        SwitchView(_allMediaItems);
                    }
                    else
                    {
                        _gallery.SetItems(_currentViewItems);
                    }
                }));
            }, token);
        }

        private void ScanDirRecursive(string path, List<MediaItem> results, CancellationToken token, int depth)
        {
            if (depth < 0 || token.IsCancellationRequested) return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    string ext = Path.GetExtension(file);
                    bool isImg = ImageExts.Contains(ext);
                    bool isVid = VideoExts.Contains(ext);

                    if (isImg || isVid)
                    {
                        var info = new FileInfo(file);
                        results.Add(new MediaItem
                        {
                            FilePath = file,
                            FileName = info.Name,
                            IsVideo = isVid,
                            DateModified = info.LastWriteTime,
                            FileSizeBytes = info.Length
                        });
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(path))
                {
                    var dirInfo = new DirectoryInfo(sub);
                    if ((dirInfo.Attributes & FileAttributes.Hidden) != 0 ||
                        (dirInfo.Attributes & FileAttributes.System) != 0 ||
                        dirInfo.Name.StartsWith(".")) continue;

                    ScanDirRecursive(sub, results, token, depth - 1);
                }
            }
            catch { }
        }

        private void SwitchView(List<MediaItem> items)
        {
            _currentViewItems = items;
            _gallery.SetItems(_currentViewItems);
            ShowGalleryView();
        }

        private void SwitchToFolderPath(string path)
        {
            var subset = _allMediaItems.Where(i => i.FilePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList();
            SwitchView(subset);
        }

        private void SwitchToPinnedFolder()
        {
            string pinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pinned");
            Directory.CreateDirectory(pinDir);
            SwitchToFolderPath(pinDir);
        }

        private void ShowGalleryView()
        {
            _viewer.StopMediaPlayback();
            _viewer.Visible = false;
            _gallery.Visible = true;
            _currentIndex = -1;
        }

        private void OpenViewerAt(int index)
        {
            if (index < 0 || index >= _currentViewItems.Count) return;
            _currentIndex = index;
            _gallery.Visible = false;
            _viewer.Visible = true;
            _viewer.LoadMedia(_currentViewItems, _currentIndex);
        }

        private void NavigateViewer(int direction)
        {
            if (_currentViewItems.Count == 0) return;
            int next = (_currentIndex + direction + _currentViewItems.Count) % _currentViewItems.Count;
            OpenViewerAt(next);
        }

        private void PinCurrentMedia()
        {
            if (_currentIndex < 0 || _currentIndex >= _currentViewItems.Count) return;
            var item = _currentViewItems[_currentIndex];
            string pinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pinned");
            Directory.CreateDirectory(pinDir);
            string dest = Path.Combine(pinDir, item.FileName);

            if (!File.Exists(dest)) File.Copy(item.FilePath, dest);
            MessageBox.Show($"Pinned '{item.FileName}' to your Pinned Album!", "ZeroPhotos", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void TrashCurrentMedia()
        {
            if (_currentIndex < 0 || _currentIndex >= _currentViewItems.Count) return;
            var item = _currentViewItems[_currentIndex];
            string dir = Path.GetDirectoryName(item.FilePath)!;
            string trashDir = Path.Combine(dir, ".zerotrash");
            Directory.CreateDirectory(trashDir);

            string dest = Path.Combine(trashDir, item.FileName);
            _viewer.StopMediaPlayback();

            File.Move(item.FilePath, dest, true);

            _allMediaItems.Remove(item);
            _currentViewItems.Remove(item);

            if (_currentViewItems.Count == 0)
            {
                ShowGalleryView();
            }
            else
            {
                if (_currentIndex >= _currentViewItems.Count) _currentIndex = _currentViewItems.Count - 1;
                OpenViewerAt(_currentIndex);
            }
        }

        private void HandleGlobalKeys(object? sender, KeyEventArgs e)
        {
            if (!_viewer.Visible) return;

            if (e.KeyCode == Keys.Right) { NavigateViewer(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { NavigateViewer(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { ShowGalleryView(); e.Handled = true; }
            else if (e.KeyCode == Keys.P) { PinCurrentMedia(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { TrashCurrentMedia(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { _viewer.TogglePlayPause(); e.Handled = true; }
            else if (e.KeyCode == Keys.I) { _viewer.ToggleInfoHud(); e.Handled = true; }
        }
    }

    public class MediaItem
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool IsVideo { get; set; }
        public DateTime DateModified { get; set; }
        public long FileSizeBytes { get; set; }
    }

    // --- GRID GALLERY COMPONENT ---
    public class GalleryControl : Control
    {
        private List<MediaItem> _items = new();
        private readonly ConcurrentDictionary<string, Image> _thumbs = new();
        private int _thumbSize = 170;
        private int _gap = 14;
        private int _hoverIndex = -1;
        private int _scrollY = 0;

        public event Action<int>? ItemClicked;

        public GalleryControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(14, 14, 16);
            Cursor = Cursors.Hand;
            this.MouseWheel += (s, e) =>
            {
                _scrollY = Math.Max(0, _scrollY - (e.Delta / 2));
                Invalidate();
            };
        }

        public void SetItems(List<MediaItem> items)
        {
            _items = items;
            _scrollY = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            if (_items.Count == 0)
            {
                using var font = new Font("Segoe UI", 12);
                string empty = "No photos or videos found. Click '+ Add Folder' to index media.";
                var sz = g.MeasureString(empty, font);
                g.DrawString(empty, font, Brushes.Gray, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;

            int cols = Math.Max(1, (Width - _gap) / (_thumbSize + _gap));
            int startRow = _scrollY / (_thumbSize + _gap);
            int endRow = startRow + (Height / (_thumbSize + _gap)) + 2;

            for (int r = startRow; r <= endRow; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int index = r * cols + c;
                    if (index >= _items.Count) return;

                    int x = _gap + c * (_thumbSize + _gap);
                    int y = _gap + r * (_thumbSize + _gap) - _scrollY;

                    var rect = new Rectangle(x, y, _thumbSize, _thumbSize);
                    DrawCard(g, rect, _items[index], index == _hoverIndex);
                }
            }
        }

        private void DrawCard(Graphics g, Rectangle rect, MediaItem item, bool isHovered)
        {
            using var path = GetRoundedRect(rect, 8);
            g.SetClip(path);

            if (_thumbs.TryGetValue(item.FilePath, out var bmp))
            {
                g.DrawImage(bmp, rect);
            }
            else
            {
                using var phBrush = new SolidBrush(Color.FromArgb(28, 28, 34));
                g.FillRectangle(phBrush, rect);
                Task.Run(() => GenerateThumbnail(item));
            }

            if (item.IsVideo)
            {
                using var grad = new LinearGradientBrush(new Point(rect.X, rect.Bottom - 32), new Point(rect.X, rect.Bottom),
                    Color.Transparent, Color.FromArgb(210, 0, 0, 0));
                g.FillRectangle(grad, rect.X, rect.Bottom - 32, rect.Width, 32);

                using var vidFont = new Font("Segoe UI", 8, FontStyle.Bold);
                g.DrawString("▶ VIDEO", vidFont, Brushes.White, rect.X + 8, rect.Bottom - 22);
            }

            g.ResetClip();

            if (isHovered)
            {
                using var pen = new Pen(Color.FromArgb(0, 140, 255), 2.5f);
                g.DrawPath(pen, path);
            }
        }

        private void GenerateThumbnail(MediaItem item)
        {
            try
            {
                if (!item.IsVideo)
                {
                    using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read);
                    using var orig = Image.FromStream(fs, false, false);
                    var thumb = orig.GetThumbnailImage(_thumbSize, _thumbSize, () => false, IntPtr.Zero);
                    _thumbs[item.FilePath] = thumb;
                    this.Invalidate();
                }
                else
                {
                    // Video placeholder icon card
                    var bmp = new Bitmap(_thumbSize, _thumbSize);
                    using var bg = Graphics.FromImage(bmp);
                    bg.Clear(Color.FromArgb(22, 24, 30));
                    using var f = new Font("Segoe UI", 26);
                    bg.DrawString("▶", f, Brushes.DimGray, (_thumbSize / 2) - 18, (_thumbSize / 2) - 25);
                    _thumbs[item.FilePath] = bmp;
                    this.Invalidate();
                }
            }
            catch { }
        }

        private GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int cols = Math.Max(1, (Width - _gap) / (_thumbSize + _gap));
            int c = (e.X - _gap) / (_thumbSize + _gap);
            int r = (e.Y + _scrollY - _gap) / (_thumbSize + _gap);

            int newHover = (c >= 0 && c < cols) ? (r * cols + c) : -1;
            if (newHover >= _items.Count) newHover = -1;

            if (_hoverIndex != newHover)
            {
                _hoverIndex = newHover;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (_hoverIndex >= 0 && _hoverIndex < _items.Count)
            {
                ItemClicked?.Invoke(_hoverIndex);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    // --- FULL FEATURED VIEWER CONTAINER ---
    public class ViewerContainer : Panel
    {
        private readonly CanvasControl _canvas;
        private readonly NativeVideoHost _videoHost;
        private readonly FilmstripControl _filmstrip;
        private readonly Label _hudInfo;

        public event Action? BackRequested;
        public event Action? NavNextRequested;
        public event Action? NavPrevRequested;
        public event Action? PinRequested;
        public event Action? TrashRequested;
        public event Action<int>? FilmstripSelected;

        private MediaItem? _activeMedia;
        private bool _infoVisible = true;

        public ViewerContainer()
        {
            this.BackColor = Color.FromArgb(10, 10, 12);

            _canvas = new CanvasControl { Dock = DockStyle.Fill };
            _videoHost = new NativeVideoHost { Dock = DockStyle.Fill, Visible = false };

            _filmstrip = new FilmstripControl { Dock = DockStyle.Bottom, Height = 74 };
            _filmstrip.ItemSelected += (idx) => FilmstripSelected?.Invoke(idx);

            // Translucent Glass Info HUD
            _hudInfo = new Label
            {
                AutoSize = true,
                BackColor = Color.FromArgb(190, 18, 18, 22),
                ForeColor = Color.FromArgb(235, 235, 240),
                Font = new Font("Segoe UI", 9),
                Padding = new Padding(12, 8, 12, 8),
                Location = new Point(20, 20)
            };

            this.Controls.Add(_hudInfo);
            this.Controls.Add(_videoHost);
            this.Controls.Add(_canvas);
            this.Controls.Add(_filmstrip);

            _canvas.Controls.Add(_hudInfo);
            _videoHost.Controls.Add(_hudInfo);

            _canvas.MouseDoubleClick += (s, e) => _canvas.ResetZoom();
        }

        public void LoadMedia(List<MediaItem> context, int index)
        {
            _activeMedia = context[index];
            _filmstrip.SetItems(context, index);

            if (_activeMedia.IsVideo)
            {
                _canvas.Visible = false;
                _videoHost.Visible = true;
                _videoHost.Play(_activeMedia.FilePath);
                UpdateHudMetadata(null);
            }
            else
            {
                _videoHost.Stop();
                _videoHost.Visible = false;
                _canvas.Visible = true;
                _canvas.LoadImage(_activeMedia.FilePath);
                UpdateHudMetadata(_canvas.LoadedImage);
            }
        }

        private void UpdateHudMetadata(Image? img)
        {
            if (_activeMedia == null) return;

            var sb = new StringBuilder();
            sb.AppendLine(_activeMedia.IsVideo ? "▶ VIDEO FILE" : "📷 IMAGE FILE");
            sb.AppendLine($"Name: {_activeMedia.FileName}");
            sb.AppendLine($"Size: {(_activeMedia.FileSizeBytes / (1024.0 * 1024.0)):F2} MB");

            if (img != null)
            {
                double mp = (img.Width * img.Height) / 1000000.0;
                sb.AppendLine($"Resolution: {img.Width} x {img.Height} ({mp:F1} MP)");
                sb.AppendLine($"Zoom: {Math.Round(_canvas.Zoom * 100)}%");
            }

            sb.Append("[Space] Play/Pause | [P] Pin | [Del] Trash | [I] Toggle Info");
            _hudInfo.Text = sb.ToString();
            _hudInfo.Visible = _infoVisible;
        }

        public void TogglePlayPause()
        {
            if (_activeMedia != null && _activeMedia.IsVideo)
                _videoHost.TogglePlayPause();
        }

        public void ToggleInfoHud()
        {
            _infoVisible = !_infoVisible;
            _hudInfo.Visible = _infoVisible;
        }

        public void StopMediaPlayback()
        {
            _videoHost.Stop();
        }
    }

    // --- HIGH-PERFORMANCE IMAGE CANVAS (ZOOM & PAN) ---
    public class CanvasControl : Control
    {
        public Image? LoadedImage { get; private set; }
        public float Zoom { get; private set; } = 1.0f;
        private PointF _panOffset = new PointF(0, 0);

        private Point _dragStart;
        private bool _isDragging;

        // Transition Alpha
        private System.Windows.Forms.Timer _fadeTimer;
        private float _fadeAlpha = 0f;

        public CanvasControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(10, 10, 12);
            Cursor = Cursors.Cross;

            _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _fadeTimer.Tick += (s, e) =>
            {
                _fadeAlpha -= 0.15f;
                if (_fadeAlpha <= 0f) { _fadeAlpha = 0f; _fadeTimer.Stop(); }
                Invalidate();
            };
        }

        public void LoadImage(string path)
        {
            LoadedImage?.Dispose();
            LoadedImage = null;
            Zoom = 1.0f;
            _panOffset = new PointF(0, 0);

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var orig = Image.FromStream(fs, false, false);
                LoadedImage = (Image)orig.Clone();
            }
            catch { }

            _fadeAlpha = 1.0f;
            _fadeTimer.Start();
            GC.Collect();
            Invalidate();
        }

        public void ResetZoom()
        {
            Zoom = 1.0f;
            _panOffset = new PointF(0, 0);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            if (LoadedImage == null) return;

            g.InterpolationMode = Zoom > 1.2f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // Compute "Fit To Screen" Base Size
            float fitScale = Math.Min((float)Width / LoadedImage.Width, (float)Height / LoadedImage.Height);
            float baseW = LoadedImage.Width * fitScale;
            float baseH = LoadedImage.Height * fitScale;

            float drawW = baseW * Zoom;
            float drawH = baseH * Zoom;

            float drawX = ((Width - drawW) / 2) + _panOffset.X;
            float drawY = ((Height - drawH) / 2) + _panOffset.Y;

            g.DrawImage(LoadedImage, drawX, drawY, drawW, drawH);

            if (_fadeAlpha > 0f)
            {
                using var brush = new SolidBrush(Color.FromArgb((int)(_fadeAlpha * 255), 10, 10, 12));
                g.FillRectangle(brush, ClientRectangle);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (LoadedImage == null) return;

            float factor = e.Delta > 0 ? 1.25f : 0.8f;
            float newZoom = Math.Clamp(Zoom * factor, 0.1f, 30.0f);

            // Anchor zoom to cursor
            _panOffset.X -= (e.X - (Width / 2f) - _panOffset.X) * (newZoom / Zoom - 1);
            _panOffset.Y -= (e.Y - (Height / 2f) - _panOffset.Y) * (newZoom / Zoom - 1);

            Zoom = newZoom;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _dragStart = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isDragging)
            {
                _panOffset.X += (e.X - _dragStart.X);
                _panOffset.Y += (e.Y - _dragStart.Y);
                _dragStart = e.Location;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) => _isDragging = false;
    }

    // --- HARDWARE-ACCELERATED NATIVE DIRECTSHOW VIDEO PLAYER ---
    public class NativeVideoHost : Control
    {
        private DirectShowPlayer? _player;
        private bool _isPlaying;

        public NativeVideoHost()
        {
            this.DoubleBuffered = false;
            this.BackColor = Color.Black;
        }

        public void Play(string path)
        {
            Stop();
            _player = new DirectShowPlayer();
            _player.OpenFile(path, this.Handle, this.Width, this.Height);
            _player.Play();
            _isPlaying = true;
        }

        public void TogglePlayPause()
        {
            if (_player == null) return;
            if (_isPlaying) { _player.Pause(); _isPlaying = false; }
            else { _player.Play(); _isPlaying = true; }
        }

        public void Stop()
        {
            if (_player != null)
            {
                _player.Stop();
                _player.Dispose();
                _player = null;
                _isPlaying = false;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _player?.ResizeVideo(this.Width, this.Height);
        }
    }

    // DirectShow Low-Level COM Pipeline
    public class DirectShowPlayer : IDisposable
    {
        private IGraphBuilder? _graph;
        private IMediaControl? _control;
        private IVideoWindow? _window;

        public void OpenFile(string path, IntPtr hostHwnd, int width, int height)
        {
            _graph = (IGraphBuilder)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770"))!)!;
            _control = (IMediaControl)_graph;
            _window = (IVideoWindow)_graph;

            _graph.RenderFile(path, null);

            _window.put_Owner(hostHwnd);
            _window.put_WindowStyle(0x40000000 | 0x10000000); // WS_CHILD | WS_VISIBLE
            _window.SetWindowPosition(0, 0, width, height);
        }

        public void Play() => _control?.Run();
        public void Pause() => _control?.Pause();
        public void Stop() => _control?.Stop();
        public void ResizeVideo(int w, int h) => _window?.SetWindowPosition(0, 0, w, h);

        public void Dispose()
        {
            Stop();
            if (_window != null)
            {
                _window.put_Visible(0);
                _window.put_Owner(IntPtr.Zero);
            }
            if (_control != null) Marshal.ReleaseComObject(_control);
            if (_window != null) Marshal.ReleaseComObject(_window);
            if (_graph != null) Marshal.ReleaseComObject(_graph);
        }

        [ComImport, Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphBuilder
        {
            [PreserveSig] int AddFilter(object pFilter, string pName);
            [PreserveSig] int RemoveFilter(object pFilter);
            [PreserveSig] int EnumFilters(out object ppEnum);
            [PreserveSig] int FindFilterByName(string pName, out object ppFilter);
            [PreserveSig] int ConnectDirect(object ppinOut, object ppinIn, object pmt);
            [PreserveSig] int Reconnect(object ppin);
            [PreserveSig] int Disconnect(object ppin);
            [PreserveSig] int SetDefaultSyncSource();
            [PreserveSig] int Connect(object ppinOut, object ppinIn);
            [PreserveSig] int Render(object ppinOut);
            [PreserveSig] int RenderFile(string lpcwstrFile, string? lpcwstrPlayList);
        }

        [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IMediaControl
        {
            void Run();
            void Pause();
            void Stop();
        }

        [ComImport, Guid("56A868B4-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IVideoWindow
        {
            void put_Owner(IntPtr Owner);
            void put_WindowStyle(int WindowStyle);
            void SetWindowPosition(int Left, int Top, int Width, int Height);
            void put_Visible(int Visible);
        }
    }

    // --- FILMSTRIP CONTROL ---
    public class FilmstripControl : Control
    {
        private List<MediaItem> _items = new();
        private int _selectedIndex = -1;
        private readonly ConcurrentDictionary<string, Image> _cache = new();
        public event Action<int>? ItemSelected;

        public FilmstripControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(16, 16, 20);
            Cursor = Cursors.Hand;
        }

        public void SetItems(List<MediaItem> items, int selected)
        {
            _items = items;
            _selectedIndex = selected;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            if (_items.Count == 0) return;

            int tw = 60, th = 50, gap = 8;
            int maxVisible = Width / (tw + gap);
            int startIdx = Math.Max(0, _selectedIndex - (maxVisible / 2));
            int endIdx = Math.Min(_items.Count, startIdx + maxVisible);

            for (int i = startIdx; i < endIdx; i++)
            {
                int x = 12 + ((i - startIdx) * (tw + gap));
                int y = (Height - th) / 2;
                var rect = new Rectangle(x, y, tw, th);

                if (i == _selectedIndex)
                {
                    using var pen = new Pen(Color.FromArgb(0, 140, 255), 2);
                    g.DrawRectangle(pen, rect.X - 2, rect.Y - 2, tw + 4, th + 4);
                }

                var item = _items[i];
                if (!item.IsVideo)
                {
                    if (_cache.TryGetValue(item.FilePath, out var bmp))
                    {
                        g.DrawImage(bmp, rect);
                    }
                    else
                    {
                        using var bg = new SolidBrush(Color.FromArgb(28, 28, 34));
                        g.FillRectangle(bg, rect);
                        Task.Run(() =>
                        {
                            try
                            {
                                using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read);
                                using var orig = Image.FromStream(fs, false, false);
                                _cache[item.FilePath] = orig.GetThumbnailImage(tw, th, () => false, IntPtr.Zero);
                                Invalidate();
                            }
                            catch { }
                        });
                    }
                }
                else
                {
                    using var vidBrush = new SolidBrush(Color.FromArgb(32, 34, 42));
                    g.FillRectangle(vidBrush, rect);
                    using var f = new Font("Segoe UI", 8, FontStyle.Bold);
                    g.DrawString("VID", f, Brushes.White, rect.X + 16, rect.Y + 16);
                }
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int tw = 60, gap = 8;
            int maxVisible = Width / (tw + gap);
            int startIdx = Math.Max(0, _selectedIndex - (maxVisible / 2));
            int clicked = startIdx + ((e.X - 12) / (tw + gap));

            if (clicked >= 0 && clicked < _items.Count)
            {
                ItemSelected?.Invoke(clicked);
            }
        }
    }
}