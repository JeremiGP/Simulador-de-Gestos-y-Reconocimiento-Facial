using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Biometrics;

/// <summary>
/// Motor de estabilización y reducción de jitter para el seguimiento facial y biométrico.
/// Implementa:
/// 1. Suavizado espacial de cuadros delimitadores mediante promedio móvil exponencial (EMA).
/// 2. Persistencia temporal para evitar parpadeos ante pérdidas breves de fotogramas (Drop-out recovery).
/// 3. Ventana deslizante de votación mayoritaria para la identidad del usuario.
/// 4. Histéresis dual de umbrales para prevenir fluctuaciones erráticas en la frontera de decisión.
/// </summary>
public sealed class FaceStabilizer
{
    private readonly double _spatialAlpha;
    private readonly int _votingWindowSize;
    private readonly int _maxDropoutFrames;
    private readonly double _iouTrackingThreshold;

    // Estado del cuadro delimitador suavizado
    private Rect? _smoothedRect;
    private int _consecutiveMissedFrames;

    // Ventana temporal de votación de identidad (Label, IsRecognized, Confidence)
    private readonly Queue<(string Label, bool IsRecognized, double Distance)> _identityHistory = new();

    // Histéresis para reconocimiento
    private readonly double _enterThreshold;
    private readonly double _exitThreshold;
    private bool _currentlyRecognizedState;
    private string _currentStableLabel = "Desconocido";

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="FaceStabilizer"/>.
    /// </summary>
    /// <param name="spatialAlpha">Coeficiente EMA para coordenadas de caja (0.1 a 0.5; menor = más suave pero más inercia).</param>
    /// <param name="votingWindowSize">Cantidad de fotogramas en la ventana deslizante de votación (ej. 7).</param>
    /// <param name="maxDropoutFrames">Fotogramas máximos tolerados sin detección antes de descartar el rostro (ej. 3).</param>
    /// <param name="baseThreshold">Umbral base de distancia RMS (ej. 52.0).</param>
    public FaceStabilizer(
        double spatialAlpha = 0.28,
        int votingWindowSize = 7,
        int maxDropoutFrames = 4,
        double baseThreshold = 52.0)
    {
        _spatialAlpha = Math.Clamp(spatialAlpha, 0.05, 0.95);
        _votingWindowSize = Math.Max(3, votingWindowSize);
        _maxDropoutFrames = Math.Max(1, maxDropoutFrames);
        _iouTrackingThreshold = 0.30;

        // Histéresis: se requiere menor distancia para entrar a Reconocido,
        // y se permite mayor distancia antes de caer a Desconocido.
        _enterThreshold = baseThreshold - 3.5;
        _exitThreshold = baseThreshold + 3.5;
    }

    /// <summary>
    /// Indica si actualmente hay un rostro estable rastreado por el estabilizador.
    /// </summary>
    public bool HasStableFace => _smoothedRect.HasValue && _consecutiveMissedFrames <= _maxDropoutFrames;

    /// <summary>
    /// Cuadro delimitador actual estabilizado.
    /// </summary>
    public Rect? CurrentSmoothedRect => HasStableFace ? _smoothedRect : null;

    /// <summary>
    /// Identidad consolidada tras el filtrado por ventana de votación e histéresis.
    /// </summary>
    public string StableLabel => _currentStableLabel;

    /// <summary>
    /// Indica si la identidad consolidada corresponde a un usuario reconocido válido.
    /// </summary>
    public bool IsRecognized => _currentlyRecognizedState;

    /// <summary>
    /// Actualiza el estado con la detección y predicción del fotograma actual.
    /// </summary>
    /// <param name="detectedFace">Rostro detectado por OpenCV en este fotograma (o null si no hubo detección).</param>
    /// <param name="rawResult">Resultado crudo arrojado por <see cref="FaceRecognizer"/> (o null).</param>
    /// <param name="frameWidth">Ancho del lienzo para clamp seguro.</param>
    /// <param name="frameHeight">Alto del lienzo para clamp seguro.</param>
    /// <returns>Resultado estabilizado con caja suavizada e identidad consistente.</returns>
    public FaceRecognitionResult Update(
        Rect? detectedFace,
        FaceRecognitionResult? rawResult,
        int frameWidth,
        int frameHeight)
    {
        // ── 1. Estabilización espacial del cuadro delimitador ─────────────────
        if (detectedFace.HasValue)
        {
            Rect newRect = detectedFace.Value;

            if (_smoothedRect.HasValue && CalculateIoU(_smoothedRect.Value, newRect) >= _iouTrackingThreshold)
            {
                // Aplicar filtro EMA espacial para eliminar vibración
                double sx = _spatialAlpha * newRect.X + (1.0 - _spatialAlpha) * _smoothedRect.Value.X;
                double sy = _spatialAlpha * newRect.Y + (1.0 - _spatialAlpha) * _smoothedRect.Value.Y;
                double sw = _spatialAlpha * newRect.Width + (1.0 - _spatialAlpha) * _smoothedRect.Value.Width;
                double sh = _spatialAlpha * newRect.Height + (1.0 - _spatialAlpha) * _smoothedRect.Value.Height;

                _smoothedRect = ClampToFrame(new Rect((int)Math.Round(sx), (int)Math.Round(sy), (int)Math.Round(sw), (int)Math.Round(sh)), frameWidth, frameHeight);
            }
            else
            {
                // Primera detección o salto espacial significativo
                _smoothedRect = ClampToFrame(newRect, frameWidth, frameHeight);
            }

            _consecutiveMissedFrames = 0;
        }
        else
        {
            _consecutiveMissedFrames++;
            if (_consecutiveMissedFrames > _maxDropoutFrames)
            {
                _smoothedRect = null;
                _identityHistory.Clear();
                _currentlyRecognizedState = false;
                _currentStableLabel = "Desconocido";

                return new FaceRecognitionResult(false, "Desconocido", double.PositiveInfinity, rawResult?.ModelIsReady ?? false, 0);
            }
        }

        // Si no hay resultado de reconocimiento en este ciclo, retornar estado persistido
        if (rawResult is null || !rawResult.ModelIsReady)
        {
            return new FaceRecognitionResult(
                false,
                rawResult?.ModelIsReady == false ? "Modelo sin entrenar" : "Desconocido",
                double.PositiveInfinity,
                rawResult?.ModelIsReady ?? false,
                0);
        }

        // ── 2. Ventana de votación temporal ──────────────────────────────────
        _identityHistory.Enqueue((rawResult.Label, rawResult.IsRecognized, rawResult.Distance));
        while (_identityHistory.Count > _votingWindowSize)
        {
            _identityHistory.Dequeue();
        }

        // Calcular promedio de distancia en la ventana
        double avgDistance = _identityHistory.Average(x => double.IsFinite(x.Distance) ? x.Distance : 100.0);

        // Votación por mayoría para la etiqueta de usuario
        var labelVotes = _identityHistory
            .GroupBy(x => x.Label)
            .OrderByDescending(g => g.Count())
            .First();

        string dominantLabel = labelVotes.Key;
        int dominantCount = labelVotes.Count();

        // ── 3. Aplicación de histéresis dual ─────────────────────────────────
        if (_currentlyRecognizedState)
        {
            // Para perder el reconocimiento, la distancia debe superar el umbral de salida
            // o más del 60% de los votos deben indicar que no está reconocido
            if (avgDistance > _exitThreshold || dominantLabel == "Desconocido")
            {
                _currentlyRecognizedState = false;
                _currentStableLabel = "Desconocido";
            }
            else if (dominantCount >= _votingWindowSize / 2)
            {
                _currentStableLabel = dominantLabel;
            }
        }
        else
        {
            // Para ganar el reconocimiento, la distancia debe estar por debajo del umbral de entrada
            // y al menos el 60% de los votos deben concordar en la misma persona
            if (avgDistance <= _enterThreshold && dominantLabel != "Desconocido" && dominantCount >= Math.Ceiling(_votingWindowSize * 0.6))
            {
                _currentlyRecognizedState = true;
                _currentStableLabel = dominantLabel;
            }
            else
            {
                _currentStableLabel = "Desconocido";
            }
        }

        // ── 4. Cálculo de confianza porcentual normalizada (0 - 100%) ─────────
        double normalizedConfidence = 0.0;
        if (double.IsFinite(avgDistance) && avgDistance > 0)
        {
            double refThreshold = _currentlyRecognizedState ? _exitThreshold : _enterThreshold;
            double score = (1.0 - (avgDistance / refThreshold)) * 100.0;
            normalizedConfidence = Math.Clamp(score + 15.0, 0.0, 100.0);
        }

        return new FaceRecognitionResult(
            _currentlyRecognizedState,
            _currentStableLabel,
            Math.Round(avgDistance, 1),
            true,
            (int)Math.Round(normalizedConfidence));
    }

    /// <summary>
    /// Reinicia todos los filtros y buffers del estabilizador.
    /// </summary>
    public void Reset()
    {
        _smoothedRect = null;
        _consecutiveMissedFrames = 0;
        _identityHistory.Clear();
        _currentlyRecognizedState = false;
        _currentStableLabel = "Desconocido";
    }

    private static double CalculateIoU(Rect a, Rect b)
    {
        int x1 = Math.Max(a.Left, b.Left);
        int y1 = Math.Max(a.Top, b.Top);
        int x2 = Math.Min(a.Right, b.Right);
        int y2 = Math.Min(a.Bottom, b.Bottom);

        int intersectionW = Math.Max(0, x2 - x1);
        int intersectionH = Math.Max(0, y2 - y1);
        double intersectionArea = intersectionW * intersectionH;

        double unionArea = (a.Width * a.Height) + (b.Width * b.Height) - intersectionArea;
        if (unionArea <= 0.0) return 0.0;

        return intersectionArea / unionArea;
    }

    private static Rect ClampToFrame(Rect r, int maxW, int maxH)
    {
        int x = Math.Clamp(r.X, 0, maxW - 1);
        int y = Math.Clamp(r.Y, 0, maxH - 1);
        int w = Math.Clamp(r.Width, 1, maxW - x);
        int h = Math.Clamp(r.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }
}
