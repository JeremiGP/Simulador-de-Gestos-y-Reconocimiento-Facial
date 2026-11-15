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
    EyesClosed
}

/// <summary>
/// Resultado del análisis biométrico de expresiones faciales.
/// </summary>
public sealed record ExpressionResult(
    FacialExpression Expression,
    string Label,
    IReadOnlyList<Rect> EyeBounds,
    bool IsSmiling,
    bool IsMouthOpen,
    bool AreEyesClosed);

/// <summary>
/// Analizador de expresiones faciales y estados oculares/bucales con estabilización temporal anti-flicker.
/// Clasifica en tiempo real: Neutral, Sonriendo (Smiling), Sorprendido (Surprised - boca abierta),
/// y Ojos cerrados/Parpadeo (EyesClosed).
/// </summary>
public sealed class ExpressionAnalyzer : IDisposable
{
    private readonly FaceDetector _eyeDetector;
    private readonly FaceDetector _smileDetector;
    private readonly int _historyWindowSize;
    private readonly Queue<FacialExpression> _expressionHistory = new();

    private FacialExpression _currentStableExpression = FacialExpression.Neutral;
    private bool _disposed;

    /// <summary>
    /// Expresión facial estabilizada actual.
    /// </summary>
    public FacialExpression CurrentExpression => _currentStableExpression;

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="ExpressionAnalyzer"/>.
    /// </summary>
    /// <param name="eyeDetector">Detector Haar de ojos.</param>
    /// <param name="smileDetector">Detector Haar de sonrisa.</param>
    /// <param name="historyWindowSize">Tamaño de la ventana deslizante para confirmación temporal (ej. 5).</param>
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
    /// Analiza la región del rostro proporcionada (escala de grises ecualizada) y extrae la expresión estabilizada.
    /// </summary>
    /// <param name="faceRoi">Matriz en escala de grises correspondiente al área del rostro.</param>
    /// <param name="faceOffset">Coordenadas del rostro en el lienzo global para reproyectar los ojos.</param>
    /// <returns>Resultado con la expresión estable y geometrías de interés.</returns>
    public ExpressionResult Analyze(Mat faceRoi, Rect faceOffset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (faceRoi is null || faceRoi.IsDisposed || faceRoi.Empty())
        {
            return new ExpressionResult(FacialExpression.Neutral, "Neutral", Array.Empty<Rect>(), false, false, false);
        }

        int faceW = faceRoi.Cols;
        int faceH = faceRoi.Rows;

        // ── 1. Análisis Ocular (Banda Superior: 18% a 55% de la altura) ──────
        int eyeTop = (int)(faceH * 0.18);
        int eyeHeight = (int)(faceH * 0.38);
        var eyeSearchRect = new Rect(0, eyeTop, faceW, eyeHeight);
        eyeSearchRect = ClampRect(eyeSearchRect, faceW, faceH);

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

        // Verificación de ojos cerrados: si no hay detección Haar, analizar gradiente/varianza en cuencas
        bool areEyesClosed = false;
        if (!eyesDetected)
        {
            areEyesClosed = CheckEyesClosedHeuristic(faceRoi, eyeSearchRect);
        }

        // ── 2. Análisis Bucal (Banda Inferior: 54% a 92% de la altura) ───────
        int mouthTop = (int)(faceH * 0.54);
        int mouthHeight = (int)(faceH * 0.38);
        int mouthMarginX = (int)(faceW * 0.15);
        int mouthWidth = faceW - (mouthMarginX * 2);

        var mouthSearchRect = new Rect(mouthMarginX, mouthTop, mouthWidth, mouthHeight);
        mouthSearchRect = ClampRect(mouthSearchRect, faceW, faceH);

        bool isSmiling = false;
        bool isMouthOpen = false;

        using (Mat mouthRegion = faceRoi[mouthSearchRect])
        {
            // Verificación de sonrisa con clasificador Haar
            if (_smileDetector.IsLoaded)
            {
                Rect[] smiles = _smileDetector.DetectMultiScale(
                    mouthRegion,
                    scaleFactor: 1.16,
                    minNeighbors: 16,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size((int)(mouthWidth * 0.30), (int)(mouthHeight * 0.22)));

                isSmiling = smiles.Length > 0;
            }

            // Verificación de boca abierta (sorpresa): presencia de cavidad oral oscura profunda
            isMouthOpen = DetectOralCavity(mouthRegion);
        }

        // ── 3. Clasificación de expresión cruda en el fotograma actual ────────
        FacialExpression rawExpression;
        if (areEyesClosed)
        {
            rawExpression = FacialExpression.EyesClosed;
        }
        else if (isMouthOpen && !isSmiling)
        {
            rawExpression = FacialExpression.Surprised;
        }
        else if (isSmiling)
        {
            rawExpression = FacialExpression.Smiling;
        }
        else
        {
            rawExpression = FacialExpression.Neutral;
        }

        // ── 4. Filtro de estabilización temporal (Rolling Majority Window) ────
        _expressionHistory.Enqueue(rawExpression);
        while (_expressionHistory.Count > _historyWindowSize)
        {
            _expressionHistory.Dequeue();
        }

        // Obtener la expresión dominante en la ventana
        var dominantGroup = _expressionHistory
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .First();

        // Requiere al menos el 60% de los fotogramas para cambiar de estado
        if (dominantGroup.Count() >= Math.Ceiling(_historyWindowSize * 0.6))
        {
            _currentStableExpression = dominantGroup.Key;
        }

        string displayLabel = _currentStableExpression switch
        {
            FacialExpression.Smiling => "Sonriendo :-)",
            FacialExpression.Surprised => "Sorprendido :-O",
            FacialExpression.EyesClosed => "Ojos cerrados -.-",
            _ => "Neutral"
        };

        return new ExpressionResult(
            _currentStableExpression,
            displayLabel,
            globalEyes,
            isSmiling,
            isMouthOpen,
            areEyesClosed);
    }

    /// <summary>
    /// Heurística morfológica para detectar cavidad oral abierta (Sorpresa).
    /// </summary>
    private static bool DetectOralCavity(Mat mouthRegion)
    {
        if (mouthRegion.Empty() || mouthRegion.Cols < 10 || mouthRegion.Rows < 10)
            return false;

        // Centrar análisis en el tercio medio horizontal y mitad vertical de la boca
        int roiX = (int)(mouthRegion.Cols * 0.25);
        int roiW = (int)(mouthRegion.Cols * 0.50);
        int roiY = (int)(mouthRegion.Rows * 0.20);
        int roiH = (int)(mouthRegion.Rows * 0.65);

        var subMouthRect = ClampRect(new Rect(roiX, roiY, roiW, roiH), mouthRegion.Cols, mouthRegion.Rows);
        using Mat subMouth = mouthRegion[subMouthRect];

        // Umbral binario estricto para cavidades oscuras interiores (sombra de la boca abierta)
        using var binaryDark = new Mat();
        Cv2.Threshold(subMouth, binaryDark, 55, 255, ThresholdTypes.BinaryInv);

        int darkPixels = Cv2.CountNonZero(binaryDark);
        double darkRatio = (double)darkPixels / (subMouth.Cols * subMouth.Rows);

        // Si más del 22% del centro de la boca es cavidad oscura, la boca está significativamente abierta
        if (darkRatio < 0.20)
        {
            return false;
        }

        // Validar la relación de aspecto vertical de los componentes oscuros
        Cv2.FindContours(
            binaryDark,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return false;

        var largestHole = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        Rect bbox = Cv2.BoundingRect(largestHole);

        // Una boca abierta en sorpresa tiene apertura vertical notable
        double verticalRatio = (double)bbox.Height / subMouth.Rows;
        return verticalRatio >= 0.36 && bbox.Height >= 8;
    }

    /// <summary>
    /// Heurística de varianza en la región ocular cuando el clasificador Haar no detecta ojos abiertos.
    /// </summary>
    private static bool CheckEyesClosedHeuristic(Mat faceRoi, Rect eyeRegionRect)
    {
        using Mat eyeRegion = faceRoi[eyeRegionRect];
        if (eyeRegion.Empty()) return false;

        // Calcular la varianza Laplaciana: los ojos abiertos tienen bordes marcados (pupila, iris, pestañas);
        // los párpados cerrados presentan textura uniforme de baja varianza
        using var laplacian = new Mat();
        Cv2.Laplacian(eyeRegion, laplacian, MatType.CV_64F);

        Cv2.MeanStdDev(laplacian, out _, out Scalar stdDev);
        double variance = stdDev.Val0 * stdDev.Val0;

        // Si la varianza de bordes en la franja ocular es inferior a 45.0, los ojos están cerrados/pestañeando
        return variance < 45.0;
    }

    /// <summary>
    /// Reinicia la ventana de historial temporal.
    /// </summary>
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
