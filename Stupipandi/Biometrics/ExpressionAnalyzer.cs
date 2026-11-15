using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Biometrics;

/// <summary>
/// Expresiones y estados faciales clasificados por el analizador.
/// </summary>
public enum FacialExpression
{
    Neutral,
    Smiling,
    Surprised,
    Roaring,
    EyesClosed
}

/// <summary>
/// Resultado del análisis biométrico de expresiones faciales y dinámicas orales/oculares.
/// </summary>
public sealed record ExpressionResult(
    FacialExpression Expression,
    string Label,
    IReadOnlyList<Rect> EyeBounds,
    bool IsSmiling,
    bool IsMouthOpen,
    bool IsRoaring,
    double MouthOpenRatio,
    bool AreEyesClosed,
    FaceOrientation Orientation);

/// <summary>
/// Analizador avanzado de expresiones faciales, apertura bucal y rugido (Roar)
/// con estabilización temporal anti-flicker.
/// </summary>
public sealed class ExpressionAnalyzer : IDisposable
{
    private readonly FaceDetector _eyeDetector;
    private readonly FaceDetector _smileDetector;
    private readonly int _historyWindowSize;
    private readonly Queue<FacialExpression> _expressionHistory = new();

    private FacialExpression _currentStableExpression = FacialExpression.Neutral;
    private bool _disposed;

    public FacialExpression CurrentExpression => _currentStableExpression;

    public ExpressionAnalyzer(
        FaceDetector eyeDetector,
        FaceDetector smileDetector,
        int historyWindowSize = 5)
    {
        _eyeDetector = eyeDetector ?? throw new ArgumentNullException(nameof(eyeDetector));
        _smileDetector = smileDetector ?? throw new ArgumentNullException(nameof(smileDetector));
        _historyWindowSize = Math.Max(3, historyWindowSize);
    }

    /// <summary>
    /// Analiza el ROI facial (escala de grises ecualizada) y extrae las dinámicas orales, oculares y de rugido.
    /// </summary>
    public ExpressionResult Analyze(Mat faceRoi, Rect faceOffset, FaceOrientation orientation = FaceOrientation.Frontal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (faceRoi is null || faceRoi.IsDisposed || faceRoi.Empty())
        {
            return new ExpressionResult(FacialExpression.Neutral, "Neutral", Array.Empty<Rect>(), false, false, false, 0.0, false, orientation);
        }

        int faceW = faceRoi.Cols;
        int faceH = faceRoi.Rows;

        // ── 1. Análisis Ocular (Franja superior: 18% a 54% de la altura) ──────
        int eyeTop = (int)(faceH * 0.18);
        int eyeHeight = (int)(faceH * 0.36);
        var eyeSearchRect = ClampRect(new Rect(0, eyeTop, faceW, eyeHeight), faceW, faceH);

        List<Rect> globalEyes = new();
        bool eyesDetected = false;

        if (_eyeDetector.IsLoaded)
        {
            using Mat eyeRegion = faceRoi[eyeSearchRect];
            Rect[] localEyes = _eyeDetector.DetectMultiScale(
                eyeRegion,
                scaleFactor: 1.15,
                minNeighbors: 3,
                flags: HaarDetectionTypes.ScaleImage,
                minSize: new Size((int)(faceW * 0.12), (int)(eyeHeight * 0.25)),
                maxSize: new Size((int)(faceW * 0.42), (int)(eyeHeight * 0.85)));

            if (localEyes.Length > 0)
            {
                eyesDetected = true;
                foreach (Rect e in localEyes)
                {
                    globalEyes.Add(new Rect(
                        faceOffset.X + eyeSearchRect.X + e.X,
                        faceOffset.Y + eyeSearchRect.Y + e.Y,
                        e.Width,
                        e.Height));
                }
            }
        }

        bool areEyesClosed = !eyesDetected && CheckEyesClosedHeuristic(faceRoi, eyeSearchRect);

        // ── 2. Análisis Bucal y de Rugido (Franja inferior: 56% a 95% de la altura) ──
        int mouthTop = (int)(faceH * 0.56);
        int mouthHeight = (int)(faceH * 0.38);
        int mouthMarginX = (int)(faceW * 0.14);
        int mouthWidth = faceW - (mouthMarginX * 2);

        var mouthSearchRect = ClampRect(new Rect(mouthMarginX, mouthTop, mouthWidth, mouthHeight), faceW, faceH);

        bool isSmiling = false;
        bool isMouthOpen = false;
        bool isRoaring = false;
        double mouthOpenRatio = 0.0;

        using (Mat mouthRegion = faceRoi[mouthSearchRect])
        {
            if (_smileDetector.IsLoaded)
            {
                Rect[] smiles = _smileDetector.DetectMultiScale(
                    mouthRegion,
                    scaleFactor: 1.16,
                    minNeighbors: 16,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size((int)(mouthWidth * 0.30), (int)(mouthHeight * 0.20)));

                isSmiling = smiles.Length > 0;
            }

            // Detección de cavidad oral oscura y apertura vertical (Open Mouth & Roaring)
            (isMouthOpen, isRoaring, mouthOpenRatio) = AnalyzeOralOpening(mouthRegion);
        }

        // ── 3. Clasificación de expresión cruda ────────────────────────────────
        FacialExpression rawExpression;
        if (isRoaring)
        {
            rawExpression = FacialExpression.Roaring;
        }
        else if (isMouthOpen && !isSmiling)
        {
            rawExpression = FacialExpression.Surprised;
        }
        else if (isSmiling)
        {
            rawExpression = FacialExpression.Smiling;
        }
        else if (areEyesClosed)
        {
            rawExpression = FacialExpression.EyesClosed;
        }
        else
        {
            rawExpression = FacialExpression.Neutral;
        }

        // ── 4. Filtro de estabilización temporal ──────────────────────────────
        _expressionHistory.Enqueue(rawExpression);
        while (_expressionHistory.Count > _historyWindowSize)
        {
            _expressionHistory.Dequeue();
        }

        var dominantGroup = _expressionHistory
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .First();

        if (dominantGroup.Count() >= Math.Ceiling(_historyWindowSize * 0.55))
        {
            _currentStableExpression = dominantGroup.Key;
        }

        string displayLabel = _currentStableExpression switch
        {
            FacialExpression.Roaring => "Rugiendo / Roar! >:O",
            FacialExpression.Surprised => "Boca abierta / Sorpresa :-O",
            FacialExpression.Smiling => "Sonriendo :-)",
            FacialExpression.EyesClosed => "Ojos cerrados -.-",
            _ => "Neutral"
        };

        return new ExpressionResult(
            _currentStableExpression,
            displayLabel,
            globalEyes,
            isSmiling,
            isMouthOpen,
            isRoaring,
            mouthOpenRatio,
            areEyesClosed,
            orientation);
    }

    private static (bool IsOpen, bool IsRoaring, double OpenRatio) AnalyzeOralOpening(Mat mouthRegion)
    {
        if (mouthRegion.Empty() || mouthRegion.Cols < 12 || mouthRegion.Rows < 12)
            return (false, false, 0.0);

        int roiX = (int)(mouthRegion.Cols * 0.20);
        int roiW = (int)(mouthRegion.Cols * 0.60);
        int roiY = (int)(mouthRegion.Rows * 0.15);
        int roiH = (int)(mouthRegion.Rows * 0.72);

        var subMouthRect = ClampRect(new Rect(roiX, roiY, roiW, roiH), mouthRegion.Cols, mouthRegion.Rows);
        using Mat subMouth = mouthRegion[subMouthRect];

        // Umbral binario inverso para aislar la cavidad oral oscura interior
        using var binaryDark = new Mat();
        Cv2.Threshold(subMouth, binaryDark, 58, 255, ThresholdTypes.BinaryInv);

        int darkPixels = Cv2.CountNonZero(binaryDark);
        double darkRatio = (double)darkPixels / (subMouth.Cols * subMouth.Rows);

        Cv2.FindContours(
            binaryDark,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return (false, false, darkRatio);

        var largestHole = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        Rect bbox = Cv2.BoundingRect(largestHole);

        double verticalRatio = (double)bbox.Height / subMouth.Rows;

        // Apertura estándar de boca (Sorpresa)
        bool isOpen = (darkRatio >= 0.18 && verticalRatio >= 0.32);

        // Rugido (Roar): Cavidad oral muy grande y elongada verticalmente
        bool isRoaring = (darkRatio >= 0.28 && verticalRatio >= 0.44 && bbox.Height >= 12);

        return (isOpen, isRoaring, verticalRatio);
    }

    private static bool CheckEyesClosedHeuristic(Mat faceRoi, Rect eyeRegionRect)
    {
        using Mat eyeRegion = faceRoi[eyeRegionRect];
        if (eyeRegion.Empty()) return false;

        using var laplacian = new Mat();
        Cv2.Laplacian(eyeRegion, laplacian, MatType.CV_64F);

        Cv2.MeanStdDev(laplacian, out _, out Scalar stdDev);
        double variance = stdDev.Val0 * stdDev.Val0;

        return variance < 45.0;
    }

    public void Reset()
    {
        _expressionHistory.Clear();
        _currentStableExpression = FacialExpression.Neutral;
    }

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, maxW - 1);
        int y = Math.Clamp(rect.Y, 0, maxH - 1);
        int w = Math.Clamp(rect.Width, 1, maxW - x);
        int h = Math.Clamp(rect.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _expressionHistory.Clear();
    }
}
