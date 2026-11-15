using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Detalle geométrico de un defecto de convexidad individual.
/// </summary>
public sealed record DefectInfo(
    Point StartPoint,
    Point EndPoint,
    Point FarthestPoint,
    double DepthPixels,
    double AngleDegrees);

/// <summary>
/// Resultado geométrico del análisis del contorno principal de la mano en una máscara de piel.
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

    internal ContourAnalysis(
        Point[] contour,
        Point[] convexHull,
        Vec4i[] convexityDefects,
        double area,
        double hullArea,
        Rect boundingBox,
        Point2f centroid,
        IReadOnlyList<DefectInfo> validDefects)
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
    }

    /// <summary>
    /// Cantidad de defectos válidos entre dedos detectados.
    /// </summary>
    public int FingerDefectCount => ValidDefects.Count;

    /// <summary>
    /// Evalúa si el contorno presenta características de pulgar extendido hacia arriba o lateralmente.
    /// </summary>
    public bool HasUpwardExtension()
    {
        if (Contour.Length == 0) return false;

        // Punto más alto del contorno
        Point topPoint = Contour.OrderBy(p => p.Y).First();

        // Si el punto más alto está significativamente por encima del centroide
        return (Centroid.Y - topPoint.Y) > (BoundingBox.Height * 0.38);
    }
}

/// <summary>
/// Analizador geométrico de contornos y descriptores de envolvente convexa.
/// </summary>
public static class ContourAnalyzer
{
    /// <summary>
    /// Analiza el contorno de piel más prominente que supere el área mínima especificada.
    /// </summary>
    /// <param name="binaryMask">Máscara binaria monocanal (8UC1).</param>
    /// <param name="minimumArea">Área mínima en píxeles cuadrados para descartar ruido.</param>
    /// <param name="minDefectDepthPixels">Profundidad mínima en píxeles para considerar un defecto de convexidad interdigital.</param>
    /// <param name="maxDefectAngleDegrees">Ángulo máximo entre dedos contiguos.</param>
    public static ContourAnalysis? Analyze(
        Mat binaryMask,
        double minimumArea = 5_000,
        double minDefectDepthPixels = 12.0,
        double maxDefectAngleDegrees = 92.0)
    {
        ValidateMask(binaryMask);

        if (minimumArea <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumArea));

        // FindContours puede alterar los datos internos, clonamos la máscara de trabajo
        using var workingMask = binaryMask.Clone();
        Cv2.FindContours(
            workingMask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0)
            return null;

        // Seleccionar el contorno de mayor área que supere el umbral
        Point[]? largestContour = null;
        double maxArea = 0;

        foreach (Point[] c in contours)
        {
            double a = Cv2.ContourArea(c);
            if (a >= minimumArea && a > maxArea)
            {
                maxArea = a;
                largestContour = c;
            }
        }

        if (largestContour is null || largestContour.Length < 4)
            return null;

        // Cálculo de envolvente convexa (puntos e índices)
        Point[] convexHullPoints = Cv2.ConvexHull(largestContour);
        int[] convexHullIndices = Cv2.ConvexHullIndices(largestContour);

        double hullArea = Cv2.ContourArea(convexHullPoints);
        Rect boundingBox = Cv2.BoundingRect(largestContour);

        // Centroide de momentos de masa
        Moments m = Cv2.Moments(largestContour);
        Point2f centroid = m.M00 > 0
            ? new Point2f((float)(m.M10 / m.M00), (float)(m.M01 / m.M00))
            : new Point2f(boundingBox.X + boundingBox.Width / 2f, boundingBox.Y + boundingBox.Height / 2f);

        // Defectos de convexidad
        Vec4i[] convexityDefects = convexHullIndices.Length >= 3
            ? Cv2.ConvexityDefects(largestContour, convexHullIndices) ?? Array.Empty<Vec4i>()
            : Array.Empty<Vec4i>();

        // Filtrar defectos que representen separaciones interdigitales genuinas
        List<DefectInfo> validDefects = new();

        foreach (Vec4i d in convexityDefects)
        {
            // d[0] = startIdx, d[1] = endIdx, d[2] = farthestIdx, d[3] = depth * 256
            Point start = largestContour[d.Item0];
            Point end = largestContour[d.Item1];
            Point farthest = largestContour[d.Item2];
            double depthPixels = d.Item3 / 256.0;

            if (depthPixels < minDefectDepthPixels)
                continue;

            double angle = CalculateAngle(start, farthest, end);
            if (angle <= maxDefectAngleDegrees)
            {
                // Solo defectos ubicados por encima o al nivel del centroide (hacia los dedos)
                if (farthest.Y <= centroid.Y + (boundingBox.Height * 0.15))
                {
                    validDefects.Add(new DefectInfo(start, end, farthest, depthPixels, angle));
                }
            }
        }

        return new ContourAnalysis(
            largestContour,
            convexHullPoints,
            convexityDefects,
            maxArea,
            hullArea,
            boundingBox,
            centroid,
            validDefects);
    }

    private static double CalculateAngle(Point start, Point farthest, Point end)
    {
        double v1X = start.X - farthest.X;
        double v1Y = start.Y - farthest.Y;
        double v2X = end.X - farthest.X;
        double v2Y = end.Y - farthest.Y;

        double dot = (v1X * v2X) + (v1Y * v2Y);
        double mag1 = Math.Sqrt((v1X * v1X) + (v1Y * v1Y));
        double mag2 = Math.Sqrt((v2X * v2X) + (v2Y * v2Y));

        if (mag1 <= double.Epsilon || mag2 <= double.Epsilon)
            return 180.0;

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
            throw new ArgumentException("La máscara debe estar inicializada y tener un único canal.", nameof(binaryMask));
        }
    }
}
