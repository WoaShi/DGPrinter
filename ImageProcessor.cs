using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;

// 显式指定别名
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;
using SysColor = System.Drawing.Color;
using WinPoint = System.Windows.Point;

namespace DGPrinter
{
    public static class ImageProcessor
    {
        private static bool IsWhite(byte r, byte g, byte b) => r > 230 && g > 230 && b > 230;

        // 辅助：比较两种颜色是否足够接近（用于合并短横线）
        private static bool AreColorsSimilar(SysColor c1, SysColor c2, int threshold = 30)
        {
            return Math.Abs(c1.R - c2.R) < threshold &&
                   Math.Abs(c1.G - c2.G) < threshold &&
                   Math.Abs(c1.B - c2.B) < threshold;
        }

        // 模式 1: 二值化横向填充 (保持不变)
        public static List<List<WinPoint>> GetBinaryPaths(string path, double w, double h)
        {
            using var src = Cv2.ImRead(path, ImreadModes.Grayscale);
            ResizeMat(src, w, h, out var resized);
            Cv2.Threshold(resized, resized, 128, 255, ThresholdTypes.Binary);

            var paths = new List<List<WinPoint>>();
            int stepY = 2;

            for (int y = 0; y < resized.Height; y += stepY)
            {
                bool isDrawing = false;
                int startX = 0;
                for (int x = 0; x < resized.Width; x++)
                {
                    bool isBlack = resized.At<byte>(y, x) < 128;
                    if (isBlack)
                    {
                        if (!isDrawing) { startX = x; isDrawing = true; }
                    }
                    else
                    {
                        if (isDrawing)
                        {
                            paths.Add(new List<WinPoint> { new WinPoint(startX, y), new WinPoint(x - 1, y) });
                            isDrawing = false;
                        }
                    }
                }
                if (isDrawing) paths.Add(new List<WinPoint> { new WinPoint(startX, y), new WinPoint(resized.Width - 1, y) });
            }
            return paths;
        }

        // --- 重写：模式 2 (彩色边缘 - 横向短线扫描) ---
        public static List<(SysColor Color, List<WinPoint> Path)> GetRasterColoredEdges(string path, double w, double h)
        {
            using var src = Cv2.ImRead(path, ImreadModes.Color);
            ResizeMat(src, w, h, out var resized);

            // 1. 生成边缘掩码
            using var edges = new Mat();
            // 适当降低阈值以捕捉更多细节，因为现在是扫描线模式，不怕碎点
            Cv2.Canny(resized, edges, 80, 180);

            // 2. 准备结果容器 (为了合并同色，我们使用临时 Map)
            // 这里的 Key 是量化后的颜色 Int 值
            var colorMap = new Dictionary<int, List<List<WinPoint>>>();

            // 3. 逐行扫描
            int stepY = 2; // 隔行扫描，效率优先

            for (int y = 0; y < resized.Height; y += stepY)
            {
                int currentRGB = -1;
                int startX = -1;

                for (int x = 0; x < resized.Width; x++)
                {
                    // 检查边缘掩码 (255 表示边缘)
                    byte edgeVal = edges.At<byte>(y, x);
                    bool isEdge = edgeVal > 200;

                    if (!isEdge)
                    {
                        // 遇到非边缘，中断当前线条
                        if (currentRGB != -1)
                        {
                            AddSegment(colorMap, currentRGB, startX, y, x - 1);
                            currentRGB = -1;
                        }
                        continue;
                    }

                    // 是边缘，获取原图颜色并加深
                    var px = resized.At<Vec3b>(y, x);

                    // 过滤白色背景
                    if (IsWhite(px.Item2, px.Item1, px.Item0))
                    {
                        if (currentRGB != -1)
                        {
                            AddSegment(colorMap, currentRGB, startX, y, x - 1);
                            currentRGB = -1;
                        }
                        continue;
                    }

                    // 颜色加深 (Darker)
                    byte r = (byte)(px.Item2 * 0.6);
                    byte g = (byte)(px.Item1 * 0.6);
                    byte b = (byte)(px.Item0 * 0.6);

                    // 简单量化一下，以便相邻像素能连成线
                    // 步长 20
                    r = (byte)((r / 20) * 20);
                    g = (byte)((g / 20) * 20);
                    b = (byte)((b / 20) * 20);

                    int rgb = (r << 16) | (g << 8) | b;

                    if (rgb != currentRGB)
                    {
                        // 颜色变了，断开并开始新线
                        if (currentRGB != -1) AddSegment(colorMap, currentRGB, startX, y, x - 1);
                        currentRGB = rgb;
                        startX = x;
                    }
                }
                // 行尾闭合
                if (currentRGB != -1) AddSegment(colorMap, currentRGB, startX, y, resized.Width - 1);
            }

            // 4. 转换结果
            var result = new List<(SysColor, List<WinPoint>)>();
            foreach (var kvp in colorMap)
            {
                var c = SysColor.FromArgb((kvp.Key >> 16) & 0xFF, (kvp.Key >> 8) & 0xFF, kvp.Key & 0xFF);
                foreach (var p in kvp.Value) result.Add((c, p));
            }
            return result;
        }

        // 模式 3: 256色阶量化
        public static List<(SysColor Color, List<WinPoint> Path)> GetQuantized256Blocks(string path, double w, double h)
        {
            using var src = Cv2.ImRead(path, ImreadModes.Color);
            ResizeMat(src, w, h, out var resized);

            var colorMap = new Dictionary<int, List<List<WinPoint>>>();
            int stepY = 2;

            for (int y = 0; y < resized.Height; y += stepY)
            {
                int currentRGB = -1;
                int startX = -1;

                for (int x = 0; x < resized.Width; x++)
                {
                    var px = resized.At<Vec3b>(y, x);
                    if (IsWhite(px.Item2, px.Item1, px.Item0))
                    {
                        if (currentRGB != -1)
                        {
                            AddSegment(colorMap, currentRGB, startX, y, x - 1);
                            currentRGB = -1;
                        }
                        continue;
                    }

                    // 256 色阶量化 (Level 42)
                    byte r = (byte)((px.Item2 / 42) * 42);
                    byte g = (byte)((px.Item1 / 42) * 42);
                    byte b = (byte)((px.Item0 / 42) * 42);
                    int rgb = (r << 16) | (g << 8) | b;

                    if (rgb != currentRGB)
                    {
                        if (currentRGB != -1) AddSegment(colorMap, currentRGB, startX, y, x - 1);
                        currentRGB = rgb;
                        startX = x;
                    }
                }
                if (currentRGB != -1) AddSegment(colorMap, currentRGB, startX, y, resized.Width - 1);
            }

            var result = new List<(SysColor, List<WinPoint>)>();
            foreach (var kvp in colorMap)
            {
                var c = SysColor.FromArgb((kvp.Key >> 16) & 0xFF, (kvp.Key >> 8) & 0xFF, kvp.Key & 0xFF);
                foreach (var p in kvp.Value) result.Add((c, p));
            }
            return result;
        }

        private static void AddSegment(Dictionary<int, List<List<WinPoint>>> map, int rgb, int startX, int y, int endX)
        {
            if (!map.ContainsKey(rgb)) map[rgb] = new List<List<WinPoint>>();
            // 即使是 1px 的点也添加，保证细节，但为了效率可以过滤极短的
            // 这里为了“短横线”效果，允许短线
            map[rgb].Add(new List<WinPoint> { new WinPoint(startX, y), new WinPoint(endX, y) });
        }

        private static void ResizeMat(Mat src, double targetW, double targetH, out Mat dst)
        {
            dst = new Mat();
            double scale = Math.Min(targetW / src.Width, targetH / src.Height);
            Cv2.Resize(src, dst, new CvSize(src.Width * scale, src.Height * scale));
        }
    }
}