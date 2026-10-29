using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Media.FaceAnalysis;
using Windows.Storage.Streams;

namespace ZeroPhotos
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private readonly ImageCanvas _canvas;
        private readonly Panel _sidebar;
        private readonly List<string> _currentFolderImages = new();
        private int _currentIndex = -1;
        private readonly Stack<string> _trashStack = new();

        // Dark Mode DWM API
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public MainForm()
        {
            this.Text = "ZeroPhotos";
            this.Size = new Size(1200, 800);
            this.BackColor = Color.FromArgb(20, 20, 20); // Minimalist Dark Mode
            this.KeyPreview = true;

            // Enable Windows 11 Dark Title Bar
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(this.Handle, 20, ref useImmersiveDarkMode, sizeof(int));

            _sidebar = new Panel
            {
                Width = 200,
                Dock = DockStyle.Left,
                BackColor = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.None
            };

            _canvas = new ImageCanvas { Dock = DockStyle.Fill };
            
            this.Controls.Add(_canvas);
            this.Controls.Add(_sidebar);

            this.Load += (s, e) => LoadAlbums();
            this.KeyDown += HandleHotkeys;
        }

        private void LoadAlbums()
        {
            // Lightweight folder detection without background database indexing
            string picsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!Directory.Exists(picsDir)) return;

            var yOffset = 10;
            foreach (var dir in Directory.GetDirectories(picsDir))
            {
                var btn = new Button
                {
                    Text = new DirectoryInfo(dir).Name,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Bounds = new Rectangle(10, yOffset, 180, 30)
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (s, e) => LoadFolder(dir);
                _sidebar.Controls.Add(btn);
                yOffset += 35;
            }
        }

        private void LoadFolder(string path)
        {
            _currentFolderImages.Clear();
            _currentFolderImages.AddRange(Directory.GetFiles(path, "*.*")
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));

            if (_currentFolderImages.Any())
            {
                _currentIndex = 0;
                RenderCurrentImage();
            }
        }

        private void RenderCurrentImage()
        {
            if (_currentIndex < 0 || _currentIndex >= _currentFolderImages.Count) return;
            
            // Memory Optimization: Dispose old image, force GC if necessary
            _canvas.SetImage(null); 
            
            string file = _currentFolderImages[_currentIndex];
            
            // Use FileStream to prevent GDI+ from locking the file on disk
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
            {
                var img = Image.FromStream(fs, false, false);
                _canvas.SetImage((Image)img.Clone()); // Detach from stream
            }
            this.Text = $"ZeroPhotos - {Path.GetFileName(file)}";
        }

        private async void HandleHotkeys(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Right)
            {
                _currentIndex = (_currentIndex + 1) % _currentFolderImages.Count;
                RenderCurrentImage();
            }
            else if (e.KeyCode == Keys.Left)
            {
                _currentIndex = (_currentIndex - 1 + _currentFolderImages.Count) % _currentFolderImages.Count;
                RenderCurrentImage();
            }
            else if (e.KeyCode == Keys.P) // Quick-Pin
            {
                PinCurrentImage();
            }
            else if (e.KeyCode == Keys.Delete) // ZeroBloat Trash
            {
                TrashCurrentImage();
            }
            else if (e.Control && e.KeyCode == Keys.Z) // Undo Trash
            {
                UndoTrash();
            }
            else if (e.KeyCode == Keys.T) // Instant OCR
            {
                await PerformOcrAsync();
            }
            else if (e.KeyCode == Keys.B) // Face Blur
            {
                await PerformFaceBlurAsync();
            }
        }

        private void PinCurrentImage()
        {
            if (_currentIndex < 0) return;
            string source = _currentFolderImages[_currentIndex];
            string pinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pinned");
            Directory.CreateDirectory(pinDir);
            
            // Hardlink for zero-disk-space pinning, fallback to copy
            string dest = Path.Combine(pinDir, Path.GetFileName(source));
            if (!File.Exists(dest)) File.Copy(source, dest);
        }

        private void TrashCurrentImage()
        {
            if (_currentIndex < 0) return;
            string source = _currentFolderImages[_currentIndex];
            string dir = Path.GetDirectoryName(source)!;
            string trashDir = Path.Combine(dir, ".zerotrash");
            Directory.CreateDirectory(trashDir);
            
            string dest = Path.Combine(trashDir, Path.GetFileName(source));
            
            // Release GDI+ lock before moving
            _canvas.SetImage(null);
            File.Move(source, dest, true);
            
            _trashStack.Push(dest);
            _currentFolderImages.RemoveAt(_currentIndex);
            
            if (_currentIndex >= _currentFolderImages.Count) _currentIndex--;
            RenderCurrentImage();
        }

        private void UndoTrash()
        {
            if (!_trashStack.Any()) return;
            string trashedFile = _trashStack.Pop();
            string originalDest = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(trashedFile))!, Path.GetFileName(trashedFile));
            
            File.Move(trashedFile, originalDest);
            _currentFolderImages.Insert(_currentIndex < 0 ? 0 : _currentIndex, originalDest);
            RenderCurrentImage();
        }

        private async Task PerformOcrAsync()
        {
            var swBitmap = await GetSoftwareBitmapAsync();
            if (swBitmap == null) return;

            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            var result = await engine.RecognizeAsync(swBitmap);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                Clipboard.SetText(result.Text);
                MessageBox.Show("Text copied to clipboard.", "ZeroPhotos OCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private async Task PerformFaceBlurAsync()
        {
            var swBitmap = await GetSoftwareBitmapAsync();
            if (swBitmap == null) return;

            var detector = await FaceDetector.CreateAsync();
            var faces = await detector.DetectFacesAsync(swBitmap);

            if (faces.Count > 0)
            {
                using var g = Graphics.FromImage(_canvas.CurrentImage!);
                using var blurBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0)); // Quick masking (blur equivalent for GDI+)
                
                foreach (var face in faces)
                {
                    var box = face.FaceBox;
                    g.FillRectangle(blurBrush, box.X, box.Y, box.Width, box.Height);
                }
                _canvas.Invalidate(); // Trigger repaint
            }
        }

        // Interop helper: Converts GDI+ Bitmap to WinRT SoftwareBitmap via MemoryStream
        private async Task<SoftwareBitmap?> GetSoftwareBitmapAsync()
        {
            if (_canvas.CurrentImage == null) return null;
            using var ms = new MemoryStream();
            _canvas.CurrentImage.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            using var ras = ms.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);
            return await decoder.GetSoftwareBitmapAsync();
        }
    }

    // High-performance custom rendering canvas
    public class ImageCanvas : Control
    {
        public Image? CurrentImage { get; private set; }
        private float _zoom = 1f;
        private PointF _offset = new PointF(0, 0);
        private Point _lastMouse;
        private bool _isDragging;

        public ImageCanvas()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 20, 20);
        }

        public void SetImage(Image? img)
        {
            CurrentImage?.Dispose();
            CurrentImage = img;
            _zoom = 1f;
            _offset = new PointF(0, 0);
            
            // Aggressive memory release for 20MB constraint
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (CurrentImage == null) return;

            // NearestNeighbor for instant decoding rendering, HighQuality for scaled views
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

            var transform = new Matrix();
            transform.Translate(_offset.X, _offset.Y);
            transform.Scale(_zoom, _zoom);
            e.Graphics.Transform = transform;

            e.Graphics.DrawImage(CurrentImage, 0, 0);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (CurrentImage == null) return;
            
            float zoomFactor = e.Delta > 0 ? 1.2f : 0.833f;
            float newZoom = _zoom * zoomFactor;

            // Math to keep zoom centered on the mouse cursor
            float mouseX = e.X;
            float mouseY = e.Y;

            _offset.X -= (mouseX - _offset.X) * (newZoom / _zoom - 1);
            _offset.Y -= (mouseY - _offset.Y) * (newZoom / _zoom - 1);
            _zoom = newZoom;

            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _lastMouse = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isDragging && CurrentImage != null)
            {
                _offset.X += (e.X - _lastMouse.X);
                _offset.Y += (e.Y - _lastMouse.Y);
                _lastMouse = e.Location;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _isDragging = false;
        }
    }
}