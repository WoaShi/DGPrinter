using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DGPrinter
{
    public class PenSimulator
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        private const int MOUSEEVENTF_MOVE = 0x0001;
        private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const int MOUSEEVENTF_LEFTUP = 0x0004;

        public PenSimulator() { }

        private System.Windows.Point GetCurrentPos()
        {
            if (GetCursorPos(out POINT p)) return new System.Windows.Point(p.X, p.Y);
            return new System.Windows.Point(0, 0);
        }

        /// <summary>
        /// 绘制线条
        /// </summary>
        /// <param name="stepSize">插值步长（像素）：越小越精致，越大越快</param>
        /// <param name="sleepInterval">休眠间隔（步数）：每走几步休息1ms，越大越快</param>
        public void DrawStroke(List<System.Windows.Point> points, double stepSize, int sleepInterval, CancellationToken token)
        {
            if (points == null || points.Count < 2) return;

            // 1. 快速移动到起点
            var currentPos = GetCurrentPos();
            MoveSmoothly(currentPos, points[0], token);

            // 2. 下笔
            SetCursorPos((int)points[0].X, (int)points[0].Y);
            Thread.Sleep(15);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);

            // 强力起笔抖动 (Kickstart)
            mouse_event(MOUSEEVENTF_MOVE, 2, 2, 0, 0);
            mouse_event(MOUSEEVENTF_MOVE, -2, -2, 0, 0);

            // 3. 动态插值绘制
            int stepsSinceLastSleep = 0;

            for (int i = 0; i < points.Count - 1; i++)
            {
                if (token.IsCancellationRequested)
                {
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                    return;
                }

                var p1 = points[i];
                var p2 = points[i + 1];

                double dist = Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));

                // 使用传入的 stepSize
                // 必须保证至少有1步
                int steps = (int)Math.Max(1, dist / stepSize);

                for (int j = 1; j <= steps; j++)
                {
                    double t = (double)j / steps;
                    int newX = (int)(p1.X + (p2.X - p1.X) * t);
                    int newY = (int)(p1.Y + (p2.Y - p1.Y) * t);

                    SetCursorPos(newX, newY);

                    // 使用传入的 sleepInterval 控制速度
                    stepsSinceLastSleep++;
                    if (stepsSinceLastSleep >= sleepInterval)
                    {
                        Thread.Sleep(1);
                        stepsSinceLastSleep = 0;
                    }
                }
            }

            // 4. 抬笔
            // 如果是很精致的画法，抬笔慢一点；快速画法则快抬
            int upDelay = (stepSize < 2.0) ? 10 : 2;
            Thread.Sleep(upDelay);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }

        public void Click(double x, double y)
        {
            SetCursorPos((int)x, (int)y);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }

        private void MoveSmoothly(System.Windows.Point from, System.Windows.Point to, CancellationToken token)
        {
            double dist = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            double stepSize = 15.0; // 悬停移动保持较快速度
            int steps = (int)Math.Max(1, dist / stepSize);

            for (int i = 1; i <= steps; i++)
            {
                if (token.IsCancellationRequested) return;
                double t = (double)i / steps;
                int newX = (int)(from.X + (to.X - from.X) * t);
                int newY = (int)(from.Y + (to.Y - from.Y) * t);
                SetCursorPos(newX, newY);
                if (i % 5 == 0) Thread.Sleep(1);
            }
            SetCursorPos((int)to.X, (int)to.Y);
        }
    }
}