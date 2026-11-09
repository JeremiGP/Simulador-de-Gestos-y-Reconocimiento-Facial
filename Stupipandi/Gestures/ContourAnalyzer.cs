using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Resultado geométrico del contorno principal encontrado en una máscara.
/// </summary>
public sealed class ContourAnalysis
{
    internal ContourAnalysis(
        Point[] contour,
        Point[] convexHull,
        Vec4i[] convexityDefects,
        double area,
        Rect boundingBox)
    {
        Contour = contour;
        ConvexHull = convexHull;
        ConvexityDefects = convexityDefects;
        Area = area;
        BoundingBox = boundingBox;
    }

    public Point[] Contour { get; }

    public Point[] ConvexHull { get; }

    /// <summary>
    /// Cada defecto contiene: índice inicial, índice final, índice del punto más profundo y profundidad.
    /// La profundidad está expresada en unidades de 1/256 de píxel.
    /// </summary>
    public Vec4i[] ConvexityDefects { get; }

    public double Area { get; }

    public Rect BoundingBox { get; }

    /// <summary>
    /// Devuelve los defectos suficientemente profundos para representar separaciones entre dedos.
    /// </summary>
    public int CountFingerDefects(double minimumDepthPixels = 10, double maximumAngleDegrees = 90)
    {
        if (minimumDepthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDepthPixels));
        }

        if (maximumAngleDegrees is <= 0 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAngleDegrees));
        }

        var validDefects = 0;
        foreach (var defect in ConvexityDefects)
        {
            var start = Contour[defect[0]];
            var end = Contour[defect[1]];
            var farthest = Contour[defect[2]];
            var depthPixels = defect[3] / 256.0;

            if (depthPixels >= minimumDepthPixels &&
                CalculateAngle(start, farthest, end) <= maximumAngleDegrees)
            {
                validDefects++;
            }
        }

        return validDefects;
    }

    private static double CalculateAngle(Point start, Point farthest, Point end)
    {
        var first = new Point2f(start.X - farthest.X, start.Y - farthest.Y);
        var second = new Point2f(end.X - farthest.X, end.Y - farthest.Y);
        var denominator = Math.Sqrt(
            (first.X * first.X + first.Y * first.Y) *
            (second.X * second.X + second.Y * second.Y));

        if (denominator <= double.Epsilon)
        {
            return 180;
        }

        var cosine = (first.X * second.X + first.Y * second.Y) / denominator;
        cosine = Math.Clamp(cosine, -1, 1);
        return Math.Acos(cosine) * 180 / Math.PI;
    }
}

/// <summary>
/// Encuentra y analiza el contorno más grande de una máscara binaria.
/// </summary>
public static class ContourAnalyzer
{
    /// <summary>
    /// Analiza el contorno exterior con mayor área. Devuelve null si no supera el área mínima.
    /// </summary>
    public static ContourAnalysis? Analyze(Mat binaryMask, double minimumArea = 1_000)
    {
        ValidateMask(binaryMask);
        if (minimumArea <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumArea));
        }

        // FindContours puede modificar la imagen recibida, por eso se trabaja sobre una copia.
        using var workingMask = binaryMask.Clone();
        Cv2.FindContours(
            workingMask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var largestContour = contours
            .Where(contour => Cv2.ContourArea(contour) >= minimumArea)
            .OrderByDescending(contour => Cv2.ContourArea(contour))
            .FirstOrDefault();

        if (largestContour is null)
        {
            return null;
        }

        var convexHull = Cv2.ConvexHull(largestContour);
        var convexHullIndices = Cv2.ConvexHullIndices(largestContour);
        var convexityDefects = convexHullIndices.Length >= 3
            ? Cv2.ConvexityDefects(largestContour, convexHullIndices) ?? Array.Empty<Vec4i>()
            : Array.Empty<Vec4i>();

        return new ContourAnalysis(
            largestContour,
            convexHull,
            convexityDefects,
            Cv2.ContourArea(largestContour),
            Cv2.BoundingRect(largestContour));
    }

    private static void ValidateMask(Mat binaryMask)
    {
        ArgumentNullException.ThrowIfNull(binaryMask);
        if (binaryMask.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(binaryMask));
        }

        if (binaryMask.Empty() || binaryMask.Channels() != 1)
        {
            throw new ArgumentException(
                "La máscara debe estar inicializada y tener un único canal.",
                nameof(binaryMask));
        }
    }
}
