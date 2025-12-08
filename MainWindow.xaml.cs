using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Media;
using System.IO; // 需要引用 IO

using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WinBitmap = System.Drawing.Bitmap;
using WinGraphics = System.Drawing.Graphics;
using WinColor = System.Drawing.Color;
using WinPoint = System.Drawing.Point;

namespace DGPrinter
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        // 用于移开鼠标
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        private const int VK_F10 = 0x79;

        private Rect _canvasRect = Rect.Empty;
        private Rect _btnRect = Rect.Empty;
        private Rect _pickerRect = Rect.Empty;
        private Rect _closePickerRect = Rect.Empty;

        private string? _sourceImagePath;
        private PenSimulator? _pen;
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            _pen = new PenSimulator();
        }

        #region UI Events
        private void BtnImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.png;*.bmp" };
            if (dlg.ShowDialog() == true)
            {
                _sourceImagePath = dlg.FileName;
                BtnImage.Content = System.IO.Path.GetFileName(_sourceImagePath);
                // 重置一下截图按钮的状态文本，避免混淆
                BtnSnapshot.Content = "Snapshot from Screen";
            }
        }

        // --- 新增：截图作为源图像 ---
        private async void BtnSnapshot_Click(object sender, RoutedEventArgs e)
        {
            // 1. 选择截图区域
            Rect shotRect = await PickRegion();
            if (shotRect.IsEmpty) return;

            try
            {
                // 2. 截取屏幕
                using (var bmp = new WinBitmap((int)shotRect.Width, (int)shotRect.Height))
                using (var g = WinGraphics.FromImage(bmp))
                {
                    g.CopyFromScreen((int)shotRect.X, (int)shotRect.Y, 0, 0, bmp.Size);

                    // 3. 保存到临时文件
                    string tempPath = Path.Combine(Path.GetTempPath(), "dgprinter_snapshot.png");
                    bmp.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);

                    // 4. 设置为源图像
                    _sourceImagePath = tempPath;
                    BtnImage.Content = "Use Snapshot"; // 更新文件按钮文字
                    BtnSnapshot.Content = "Snapshot Captured!";
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to capture screen: " + ex.Message);
            }
        }

        private async void BtnCanvas_Click(object sender, RoutedEventArgs e) => await SetRegion(r => { _canvasRect = r; TxtStatusCanvas.Text = "Set"; }, TxtStatusCanvas);
        private async void BtnColorBtn_Click(object sender, RoutedEventArgs e) => await SetRegion(r => { _btnRect = r; TxtStatusBtn.Text = "Set"; }, TxtStatusBtn);
        private async void BtnPicker_Click(object sender, RoutedEventArgs e) => await SetRegion(r => { _pickerRect = r; TxtStatusPicker.Text = "Set"; }, TxtStatusPicker);
        private async void BtnClosePicker_Click(object sender, RoutedEventArgs e) => await SetRegion(r => { _closePickerRect = r; TxtStatusClosePicker.Text = "Set"; }, TxtStatusClosePicker);

        private async Task SetRegion(Action<Rect> setAction, TextBlock statusBlock)
        {
            var r = await PickRegion();
            setAction(r);
            statusBlock.Text = r.IsEmpty ? "Not Set" : "OK";
            statusBlock.Foreground = r.IsEmpty ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.Green;
        }

        private Task<Rect> PickRegion()
        {
            var tcs = new TaskCompletionSource<Rect>();
            this.WindowState = WindowState.Minimized;
            Task.Delay(300).ContinueWith(_ => Dispatcher.Invoke(() => {
                var win = new SelectionWindow();
                win.Closed += (s, args) => {
                    this.WindowState = WindowState.Normal;
                    if (win.IsConfirmed) tcs.SetResult(win.SelectedRegion);
                    else tcs.SetResult(Rect.Empty);
                };
                win.Show();
            }));
            return tcs.Task;
        }
        #endregion

        #region Core Logic
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            // --- 修改：校验逻辑 ---
            int mode = ComboMode.SelectedIndex;
            bool isColorMode = (mode != 0); // 模式0是黑白，其他是彩色

            // 基础检查
            if (_pen == null || string.IsNullOrEmpty(_sourceImagePath) || _canvasRect.IsEmpty)
            {
                System.Windows.MessageBox.Show("Please select an Image (or Snapshot) and set Canvas Area.");
                return;
            }

            // 只有在彩色模式下，才检查取色器区域
            if (isColorMode)
            {
                if (_btnRect.IsEmpty || _pickerRect.IsEmpty)
                {
                    System.Windows.MessageBox.Show("For Color Mode, you must set 'Color Button Area' and 'Picker Region'.");
                    return;
                }
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();

            BtnStart.Content = "Processing...";
            BtnStart.IsEnabled = false;
            BtnCancel.Visibility = Visibility.Visible;
            TxtPrediction.Text = "Calculating...";

            string path = _sourceImagePath;

            StartHotkeyListener(_cts.Token);

            try
            {
                await Task.Run(() => RunTask(mode, path, _cts.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Windows.MessageBox.Show("Error: " + ex.Message); }
            finally { ResetUI(); }
        }

        private void StartHotkeyListener(CancellationToken token)
        {
            Task.Factory.StartNew(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    if ((GetAsyncKeyState(VK_F10) & 0x8000) != 0)
                    {
                        SystemSounds.Hand.Play();
                        Dispatcher.Invoke(() => CancelDrawing());
                        break;
                    }
                    Thread.Sleep(50);
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void CancelDrawing()
        {
            if (_cts != null && !_cts.IsCancellationRequested) _cts.Cancel();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => CancelDrawing();

        private void ResetUI()
        {
            _isRunning = false;
            BtnStart.Content = "Start Painting";
            BtnStart.IsEnabled = true;
            BtnCancel.Visibility = Visibility.Collapsed;
            TxtPrediction.Text = "Finished / Stopped";
        }

        private void RunTask(int mode, string imgPath, CancellationToken token)
        {
            if (_pen == null) return;

            var allBatches = new List<(WinColor? Color, List<List<System.Windows.Point>> Paths)>();

            if (mode == 0) // Binary
            {
                var paths = ImageProcessor.GetBinaryPaths(imgPath, _canvasRect.Width, _canvasRect.Height);
                allBatches.Add((null, paths));
            }
            else if (mode == 1) // Raster Colored Edges
            {
                var edges = ImageProcessor.GetRasterColoredEdges(imgPath, _canvasRect.Width, _canvasRect.Height);
                var groups = edges.GroupBy(x => x.Color).ToList();
                foreach (var g in groups)
                    allBatches.Add((g.Key, g.Select(x => x.Path).ToList()));
            }
            else // 256 Colors
            {
                var blocks = ImageProcessor.GetQuantized256Blocks(imgPath, _canvasRect.Width, _canvasRect.Height);
                var groups = blocks.GroupBy(x => x.Color).ToList();
                foreach (var g in groups)
                    allBatches.Add((g.Key, g.Select(x => x.Path).ToList()));
            }

            token.ThrowIfCancellationRequested();

            // 预测时间
            int totalColorChanges = allBatches.Count;
            double totalPixels = 0;
            foreach (var batch in allBatches)
            {
                foreach (var path in batch.Paths)
                {
                    if (path.Count >= 2)
                    {
                        double dx = path.Last().X - path.First().X;
                        totalPixels += Math.Abs(dx);
                    }
                }
            }

            // 黑白模式没有换色耗时
            double estimatedSeconds = (totalColorChanges * 2.5) + (totalPixels / 400.0);

            Dispatcher.Invoke(() =>
            {
                string info = mode == 0 ? "B&W Mode" : $"{totalColorChanges} Colors";
                TxtPrediction.Text = $"~ {estimatedSeconds / 60.0:F1} Min ({info})";
            });

            // 执行绘制
            foreach (var batch in allBatches)
            {
                // 只有当有颜色且不是黑白模式时，才执行取色
                if (batch.Color.HasValue && mode != 0)
                {
                    PickColor(batch.Color.Value, token);
                }

                DrawBatch(batch.Paths, 5.0, 50, token);
            }
        }

        private void DrawBatch(List<List<System.Windows.Point>> paths, double stepSize, int sleepInterval, CancellationToken token)
        {
            if (paths.Count == 0 || _pen == null) return;

            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var absPath = path.Select(p => new System.Windows.Point(_canvasRect.X + p.X, _canvasRect.Y + p.Y)).ToList();
                _pen.DrawStroke(absPath, stepSize, sleepInterval, token);
            }
        }

        private void PickColor(WinColor target, CancellationToken token)
        {
            if (_btnRect.IsEmpty || _pickerRect.IsEmpty || _pen == null) return;
            token.ThrowIfCancellationRequested();

            // 1. 点开颜色面板
            _pen.Click(_btnRect.X + _btnRect.Width / 2, _btnRect.Y + _btnRect.Height / 2);
            Thread.Sleep(800);

            token.ThrowIfCancellationRequested();

            // 2. 移开鼠标
            SetCursorPos(0, 0);
            Thread.Sleep(50);

            WinPoint best = new WinPoint((int)_pickerRect.X, (int)_pickerRect.Y);
            double min = double.MaxValue;

            // 3. 截屏找色
            using (var bmp = new WinBitmap((int)_pickerRect.Width, (int)_pickerRect.Height))
            using (var g = WinGraphics.FromImage(bmp))
            {
                g.CopyFromScreen((int)_pickerRect.X, (int)_pickerRect.Y, 0, 0, bmp.Size);

                for (int y = 0; y < bmp.Height; y += 4)
                {
                    for (int x = 0; x < bmp.Width; x += 4)
                    {
                        var px = bmp.GetPixel(x, y);
                        double dist = Math.Pow(px.R - target.R, 2) + Math.Pow(px.G - target.G, 2) + Math.Pow(px.B - target.B, 2);
                        if (dist < min)
                        {
                            min = dist;
                            best = new WinPoint((int)_pickerRect.X + x, (int)_pickerRect.Y + y);
                        }
                    }
                }
            }

            _pen.Click(best.X, best.Y);
            Thread.Sleep(150);

            // 4. 点击关闭
            if (!_closePickerRect.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                _pen.Click(_closePickerRect.X + _closePickerRect.Width / 2, _closePickerRect.Y + _closePickerRect.Height / 2);
                Thread.Sleep(500);
            }
        }
        #endregion
    }
}