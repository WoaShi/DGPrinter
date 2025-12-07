using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DGPrinter
{
    public partial class SelectionWindow : Window
    {
        private System.Windows.Point _startPoint;
        public Rect SelectedRegion { get; private set; }
        public bool IsConfirmed { get; private set; } = false;

        public SelectionWindow()
        {
            InitializeComponent();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(this);
            SelRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelRect, _startPoint.X);
            Canvas.SetTop(SelRect, _startPoint.Y);
            SelRect.Width = 0;
            SelRect.Height = 0;
        }

        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(this);
                var x = Math.Min(pos.X, _startPoint.X);
                var y = Math.Min(pos.Y, _startPoint.Y);
                var w = Math.Abs(pos.X - _startPoint.X);
                var h = Math.Abs(pos.Y - _startPoint.Y);

                Canvas.SetLeft(SelRect, x);
                Canvas.SetTop(SelRect, y);
                SelRect.Width = w;
                SelRect.Height = h;
            }
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // --- 核心修复：使用 PointToScreen 获取绝对物理坐标 ---
            // 这会自动处理 DPI 缩放和多显示器偏移，消除错位
            if (SelRect.Width > 0 && SelRect.Height > 0)
            {
                // 获取矩形左上角在屏幕上的物理坐标
                System.Windows.Point p1 = SelRect.PointToScreen(new System.Windows.Point(0, 0));
                // 获取矩形右下角在屏幕上的物理坐标
                System.Windows.Point p2 = SelRect.PointToScreen(new System.Windows.Point(SelRect.Width, SelRect.Height));

                // 构造绝对坐标矩形
                double w = Math.Abs(p2.X - p1.X);
                double h = Math.Abs(p2.Y - p1.Y);

                SelectedRegion = new Rect(p1.X, p1.Y, w, h);
                IsConfirmed = true;
            }
            else
            {
                IsConfirmed = false;
            }

            this.Close();
        }
    }
}