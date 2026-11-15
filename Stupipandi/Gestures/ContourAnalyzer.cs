using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Detalle geométrico de un defecto de convexidad individual interdigital.
/// </summary>
public sealed record DefectInfo(
    Point StartPoint,
    Point EndPoint,
    Point FarthestPoint,
    double DepthPixels,
    double AngleDegrees);

/// <summary>
/// Descriptor geométrico enriquecido de un contorno de mano y sus apéndices dactilares.
/// </summary>
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

    /// <summary>
    /// Coordenada 2D de la punta del dedo índice (estimada en posturas de apuntado o pistola).
    /// </summary>
    public Point? IndexFingerTip { get; }

    /// <summary>
    /// Coordenada 2D de la punta del pulgar.
    /// </summary>
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

/// <summary>
/// Analizador geométrico de contornos de mano con umbrales mínimos estrictos (> 2.5% del fotograma)
/// y extracción de poses complejas (Finger-Gun, Palmas orando, Manos en ojiva, Saludo militar).
/// </summary>
public static class ContourAnalyzer
{
    /// <summary>
    /// Analiza la máscara binaria aplicando la restricción estricta de área mínima (> 2.5% del área total del lienzo).
    /// </summary>
    public static ContourAnalysis? Analyze(
        Mat binaryMask,
        double minDefectDepthPixels = 12.0,
        double maxDefectAngleDegrees = 92.0)
    {
        ValidateMask(binaryMask);

        // Umbral estricto: al menos 2.5% de los píxeles del fotograma (ej. > 23,000 px² en 1280x720)
        double minAreaStrict = Math.Max(12_000.0, 0.025 * binaryMask.Cols * binaryMask.Rows);

        using var workingMask = binaryMask.Clone();
        Cv2.FindContours(
            workingMask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return null;

        // Buscar el contorno mayor que supere el umbral estricto
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

        Point[] convexHullPoints = Cv2.ConvexHull(largestContour);
        int[] convexHullIndices = Cv2.ConvexHullIndices(largestContour);

        double hullArea = Cv2.ContourArea(convexHullPoints);
        Rect boundingBox = Cv2.BoundingRect(largestContour);

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

        // ── Extracción de puntas de dedos (Index & Thumb) ────────────────────
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
    /// Detecta si hay dos contornos de piel simétricos en las sienes/orejas del rostro (Trigger 2: Hands on Head).
    /// </summary>
    public static bool CheckHandsOnHead(Mat binaryMask, Rect faceBounds)
    {
        if (binaryMask.Empty() || faceBounds.Width <= 0 || faceBounds.Height <= 0) return false;

        // Zona lateral izquierda (oreja/sien izquierda)
        int lx = Math.Max(0, faceBounds.X - (int)(faceBounds.Width * 0.70));
        int ly = Math.Max(0, faceBounds.Y - (int)(faceBounds.Height * 0.15));
        int lw = Math.Min(faceBounds.X - lx + (int)(faceBounds.Width * 0.15), binaryMask.Cols - lx);
        int lh = Math.Min((int)(faceBounds.Height * 0.90), binaryMask.Rows - ly);

        // Zona lateral derecha (oreja/sien derecha)
        int rx = Math.Max(0, faceBounds.Right - (int)(faceBounds.Width * 0.15));
        int ry = ly;
        int rw = Math.Min((int)(faceBounds.Width * 0.70), binaryMask.Cols - rx);
        int rh = lh;

        if (lw <= 10 || rw <= 10 || lh <= 10) return false;

        using var leftRoi = binaryMask[new Rect(lx, ly, lw, lh)];
        using var rightRoi = binaryMask[new Rect(rx, ry, rw, rh)];

        int leftSkin = Cv2.CountNonZero(leftRoi);
        int rightSkin = Cv2.CountNonZero(rightRoi);

        double minSkinPixels = faceBounds.Width * faceBounds.Height * 0.08;
        return leftSkin >= minSkinPixels && rightSkin >= minSkinPixels;
    }

    /// <summary>
    /// Detecta si la mano se encuentra en la sien/frente en postura de saludo militar (Trigger 6: Military Salute).
    /// </summary>
    public static bool CheckMilitarySalute(ContourAnalysis? handAnalysis, Rect faceBounds)
    {
        if (handAnalysis is null || faceBounds.Width <= 0) return false;

        // Zona de la sien derecha o izquierda superior
        int templeMargin = (int)(faceBounds.Width * 0.40);
        var rightTemple = new Rect(
            faceBounds.Right - (int)(faceBounds.Width * 0.25),
            faceBounds.Y - (int)(faceBounds.Height * 0.20),
            templeMargin,
            (int)(faceBounds.Height * 0.55));

        var leftTemple = new Rect(
            faceBounds.X - (int)(faceBounds.Width * 0.35),
            faceBounds.Y - (int)(faceBounds.Height * 0.20),
            templeMargin,
            (int)(faceBounds.Height * 0.55));

        Point? tip = handAnalysis.IndexFingerTip ?? handAnalysis.Centroid.ToPoint();

        // Si la punta de la mano está dentro de la zona de la sien y la mano tiene buena solidez
        bool inRightTemple = rightTemple.Contains(tip.Value);
        bool inLeftTemple = leftTemple.Contains(tip.Value);

        return (inRightTemple || inLeftTemple) && handAnalysis.Solidity >= 0.65;
    }

    private static (Point? IndexTip, Point? ThumbTip) EstimateFingertips(
        Point[] contour,
        Point[] hull,
        Point2f centroid,
        Rect bbox)
    {
        if (hull.Length == 0) return (null, null);

        // Puntos de la envolvente convexa que están a mayor distancia del centroide
        var extremeHull = hull
            .Select(p => new { Point = p, Dist = Distance(p, centroid) })
            .Where(p => p.Dist > bbox.Height * 0.32)
            .OrderByDescending(p => p.Dist)
            .ToList();

        if (extremeHull.Count == 0) return (null, null);

        Point indexTip = extremeHull[0].Point;
        Point? thumbTip = null;

        // El pulgar es el segundo extremo más alejado que forme un ángulo amplio (> 30°) con el índice
        for (int i = 1; i < extremeHull.Count; i++)
        {
            Point p = extremeHull[i].Point;
            if (Distance(indexTip, p) > bbox.Width * 0.28)
            {
                thumbTip = p;
                break;
            }
        }

        return (indexTip, thumbTip);
    }

    private static double Distance(Point a, Point2f b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Distance(Point a, Point b)
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
        {
            throw new ArgumentException("La máscara debe ser binaria monocanal de 8 bits.", nameof(binaryMask));
        }
    }
}
