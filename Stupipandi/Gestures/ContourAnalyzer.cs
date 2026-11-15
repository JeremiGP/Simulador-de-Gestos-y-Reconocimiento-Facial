using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Gestures;

public sealed record DefectInfo(
    Point StartPoint,
    Point EndPoint,
    Point FarthestPoint,
    double DepthPixels,
    double AngleDegrees);

public sealed class ContourAnalysis
{
    public Point[] Contour { get; }
    public Point[] ConvexHull { get; }
    public Vec4i[] ConvexityDefects { get; }
    public double Area { get; }
    public double HullArea { get; }
    public double Solidity { get; }
    public Rect BoundingBox { get; }
    public Point2f Centroid { get; }
    public double AspectRatio => BoundingBox.Width > 0 ? (double)BoundingBox.Height / BoundingBox.Width : 1.0;
    public IReadOnlyList<DefectInfo> ValidDefects { get; }
    public Point? IndexFingerTip { get; }
    public Point? ThumbTip { get; }

    internal ContourAnalysis(
        Point[] contour,
        Point[] convexHull,
        Vec4i[] convexityDefects,
        double area,
        double hullArea,
        Rect boundingBox,
        Point2f centroid,
        IReadOnlyList<DefectInfo> validDefects,
        Point? indexFingerTip,
        Point? thumbTip)
    {
        Contour = contour;
        ConvexHull = convexHull;
        ConvexityDefects = convexityDefects;
        Area = area;
        HullArea = Math.Max(area, hullArea);
        Solidity = HullArea > 0 ? Math.Clamp(area / HullArea, 0.0, 1.0) : 1.0;
        BoundingBox = boundingBox;
        Centroid = centroid;
        ValidDefects = validDefects;
        IndexFingerTip = indexFingerTip;
        ThumbTip = thumbTip;
    }

    public int FingerDefectCount => ValidDefects.Count;
}

public static class ContourAnalyzer
{
    public static ContourAnalysis? Analyze(
        Mat binaryMask,
        double minDefectDepthPixels = 12.0,
        double maxDefectAngleDegrees = 95.0)
    {
        ValidateMask(binaryMask);

        double minAreaStrict = Math.Max(8_500.0, 0.015 * binaryMask.Cols * binaryMask.Rows);

        using var workingMask = binaryMask.Clone();
        Cv2.FindContours(
            workingMask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return null;

        Point[]? largestContour = null;
        double maxArea = 0;

        foreach (var c in contours)
        {
            double a = Cv2.ContourArea(c);
            if (a >= minAreaStrict && a > maxArea)
            {
                maxArea = a;
                largestContour = c;
            }
        }

        if (largestContour is null || largestContour.Length < 5)
            return null;

        Rect boundingBox = Cv2.BoundingRect(largestContour);

        if (boundingBox.Width > binaryMask.Cols * 0.68 && boundingBox.Height > binaryMask.Rows * 0.68)
            return null;

        Point[] convexHullPoints = Cv2.ConvexHull(largestContour);
        int[] convexHullIndices = Cv2.ConvexHullIndices(largestContour);

        double hullArea = Cv2.ContourArea(convexHullPoints);

        Moments m = Cv2.Moments(largestContour);
        Point2f centroid = m.M00 > 0
            ? new Point2f((float)(m.M10 / m.M00), (float)(m.M01 / m.M00))
            : new Point2f(boundingBox.X + boundingBox.Width / 2f, boundingBox.Y + boundingBox.Height / 2f);

        Vec4i[] convexityDefects = convexHullIndices.Length >= 3
            ? Cv2.ConvexityDefects(largestContour, convexHullIndices) ?? Array.Empty<Vec4i>()
            : Array.Empty<Vec4i>();

        List<DefectInfo> validDefects = new();

        foreach (Vec4i d in convexityDefects)
        {
            Point start = largestContour[d.Item0];
            Point end = largestContour[d.Item1];
            Point farthest = largestContour[d.Item2];
            double depth = d.Item3 / 256.0;

            if (depth < minDefectDepthPixels) continue;

            double angle = CalculateAngle(start, farthest, end);
            if (angle <= maxDefectAngleDegrees)
            {
                validDefects.Add(new DefectInfo(start, end, farthest, depth, angle));
            }
        }

        (Point? indexTip, Point? thumbTip) = EstimateFingertips(largestContour, convexHullPoints, centroid, boundingBox);

        return new ContourAnalysis(
            largestContour,
            convexHullPoints,
            convexityDefects,
            maxArea,
            hullArea,
            boundingBox,
            centroid,
            validDefects,
            indexTip,
            thumbTip);
    }

    /// <summary>
    /// Detecta ambas manos colocadas a los lados o encima de la cabeza (Trigger 2: Hands on Head).
    /// </summary>
    public static bool CheckHandsOnHead(Mat binaryMask, Rect faceBounds)
    {
        if (binaryMask.Empty() || faceBounds.Width <= 0 || faceBounds.Height <= 0) return false;

        int sideReach = (int)(faceBounds.Width * 0.95);
        int topReach = Math.Max(0, faceBounds.Y - (int)(faceBounds.Height * 0.45));
        int zoneHeight = Math.Min((int)(faceBounds.Height * 1.35), binaryMask.Rows - topReach);

        // Zona izquierda de la cabeza
        int lx = Math.Max(0, faceBounds.X - sideReach);
        int lw = Math.Min(sideReach + (int)(faceBounds.Width * 0.08), binaryMask.Cols - lx);

        // Zona derecha de la cabeza
        int rx = Math.Max(0, faceBounds.Right - (int)(faceBounds.Width * 0.08));
        int rw = Math.Min(sideReach + (int)(faceBounds.Width * 0.08), binaryMask.Cols - rx);

        if (lw <= 10 || rw <= 10 || zoneHeight <= 10) return false;

        using var leftRoi = binaryMask[new Rect(lx, topReach, lw, zoneHeight)];
        using var rightRoi = binaryMask[new Rect(rx, topReach, rw, zoneHeight)];

        int leftSkin = Cv2.CountNonZero(leftRoi);
        int rightSkin = Cv2.CountNonZero(rightRoi);

        double minSkinPixels = faceBounds.Width * faceBounds.Height * 0.032;
        return leftSkin >= minSkinPixels && rightSkin >= minSkinPixels;
    }

    public static bool CheckMilitarySalute(ContourAnalysis? handAnalysis, Rect faceBounds)
    {
        if (handAnalysis is null || faceBounds.Width <= 0) return false;

        var rightTemple = new Rect(
            faceBounds.Right - (int)(faceBounds.Width * 0.35),
            Math.Max(0, faceBounds.Y - (int)(faceBounds.Height * 0.25)),
            (int)(faceBounds.Width * 0.85),
            (int)(faceBounds.Height * 0.65));

        var leftTemple = new Rect(
            Math.Max(0, faceBounds.X - (int)(faceBounds.Width * 0.50)),
            Math.Max(0, faceBounds.Y - (int)(faceBounds.Height * 0.25)),
            (int)(faceBounds.Width * 0.85),
            (int)(faceBounds.Height * 0.65));

        int pointsInRightTemple = 0;
        int pointsInLeftTemple = 0;

        foreach (var pt in handAnalysis.Contour)
        {
            if (rightTemple.Contains(pt)) pointsInRightTemple++;
            if (leftTemple.Contains(pt)) pointsInLeftTemple++;
        }

        bool isLateralSalute = handAnalysis.BoundingBox.Width >= handAnalysis.BoundingBox.Height * 0.75;
        return (pointsInRightTemple >= 12 || pointsInLeftTemple >= 12) && isLateralSalute;
    }

    private static (Point? IndexTip, Point? ThumbTip) EstimateFingertips(
        Point[] contour,
        Point[] hull,
        Point2f centroid,
        Rect bbox)
    {
        if (hull.Length == 0) return (null, null);

        Point highestPoint = hull.OrderBy(p => p.Y).First();

        Point? sidePoint = null;
        double bestSideScore = 0;

        foreach (var p in hull)
        {
            double dx = Math.Abs(p.X - highestPoint.X);
            double distFromCentroid = Distance(p, centroid);
            if (dx > bbox.Width * 0.30 && distFromCentroid > bbox.Width * 0.32)
            {
                double score = dx + distFromCentroid * 0.5;
                if (score > bestSideScore)
                {
                    bestSideScore = score;
                    sidePoint = p;
                }
            }
        }

        return (highestPoint, sidePoint);
    }

    private static double Distance(Point a, Point2f b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double CalculateAngle(Point start, Point farthest, Point end)
    {
        double v1X = start.X - farthest.X;
        double v1Y = start.Y - farthest.Y;
        double v2X = end.X - farthest.X;
        double v2Y = end.Y - farthest.Y;

        double dot = (v1X * v2X) + (v1Y * v2Y);
        double mag1 = Math.Sqrt(v1X * v1X + v1Y * v1Y);
        double mag2 = Math.Sqrt(v2X * v2X + v2Y * v2Y);

        if (mag1 <= double.Epsilon || mag2 <= double.Epsilon) return 180.0;
        double cosine = Math.Clamp(dot / (mag1 * mag2), -1.0, 1.0);
        return Math.Acos(cosine) * 180.0 / Math.PI;
    }

    private static void ValidateMask(Mat binaryMask)
    {
        ArgumentNullException.ThrowIfNull(binaryMask);
        if (binaryMask.IsDisposed)
            throw new ObjectDisposedException(nameof(binaryMask));

        if (binaryMask.Empty() || binaryMask.Channels() != 1)
            throw new ArgumentException("La máscara debe ser binaria monocanal de 8 bits.", nameof(binaryMask));
    }
}