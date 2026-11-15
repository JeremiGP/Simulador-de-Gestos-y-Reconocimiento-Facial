using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Motor de estabilización y reducción de jitter para el seguimiento y clasificación de gestos de mano.
/// Implementa:
/// 1. Suavizado espacial EMA del cuadro delimitador de la mano y de la punta del dedo índice.
/// 2. Filtro de moda estadística sobre ventana deslizante temporal.
/// 3. Debouncing con tiempo mínimo de mantenimiento para prevenir fluctuaciones erráticas.
/// </summary>
public sealed class GestureStabilizer
{
    private readonly double _spatialAlpha;
    private readonly int _historyWindowSize;
    private readonly long _debounceTicks;

    private readonly Queue<GestureType> _gestureHistory = new();
    private Rect? _smoothedHandRect;
    private Point2f? _smoothedIndexTip;
    private GestureType _stableGesture = GestureType.None;
    private GestureType _candidateGesture = GestureType.None;
    private long _candidateFirstSeenTicks;

    /// <summary>
    /// Gesto de mano actualmente estabilizado.
    /// </summary>
    public GestureType StableGesture => _stableGesture;

    /// <summary>
    /// Cuadro delimitador suavizado de la mano.
    /// </summary>
    public Rect? SmoothedHandRect => _smoothedHandRect;

    /// <summary>
    /// Coordenada suavizada de la punta del dedo índice para apuntado y mira técnica.
    /// </summary>
    public Point? SmoothedIndexFingerTip =>
        _smoothedIndexTip.HasValue
            ? new Point((int)Math.Round(_smoothedIndexTip.Value.X), (int)Math.Round(_smoothedIndexTip.Value.Y))
            : null;

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="GestureStabilizer"/>.
    /// </summary>
    /// <param name="spatialAlpha">Coeficiente EMA espacial para el cuadro de la mano (ej. 0.30).</param>
    /// <param name="historyWindowSize">Tamaño de la ventana deslizante para filtro de moda (ej. 7).</param>
    /// <param name="debounceMilliseconds">Tiempo mínimo de sostenimiento antes de confirmar cambio de gesto (ej. 160 ms).</param>
    public GestureStabilizer(
        double spatialAlpha = 0.30,
        int historyWindowSize = 7,
        int debounceMilliseconds = 160)
    {
        _spatialAlpha = Math.Clamp(spatialAlpha, 0.05, 0.95);
        _historyWindowSize = Math.Max(3, historyWindowSize);
        _debounceTicks = (long)(Stopwatch.Frequency * (debounceMilliseconds / 1000.0));
    }

    /// <summary>
    /// Procesa el análisis de contorno y gesto crudo del fotograma actual, devolviendo el gesto y coordenadas estabilizadas.
    /// </summary>
    public (GestureType Gesture, Rect? HandBox, Point? IndexTip) Update(
        ContourAnalysis? rawAnalysis,
        GestureType rawGesture,
        int frameWidth,
        int frameHeight)
    {
        long nowTicks = Stopwatch.GetTimestamp();

        // ── 1. Suavizado espacial de caja de mano y punta de índice ──────────
        if (rawAnalysis is not null)
        {
            Rect newBox = rawAnalysis.BoundingBox;

            if (_smoothedHandRect.HasValue)
            {
                double sx = _spatialAlpha * newBox.X + (1.0 - _spatialAlpha) * _smoothedHandRect.Value.X;
                double sy = _spatialAlpha * newBox.Y + (1.0 - _spatialAlpha) * _smoothedHandRect.Value.Y;
                double sw = _spatialAlpha * newBox.Width + (1.0 - _spatialAlpha) * _smoothedHandRect.Value.Width;
                double sh = _spatialAlpha * newBox.Height + (1.0 - _spatialAlpha) * _smoothedHandRect.Value.Height;

                _smoothedHandRect = ClampToFrame(
                    new Rect((int)Math.Round(sx), (int)Math.Round(sy), (int)Math.Round(sw), (int)Math.Round(sh)),
                    frameWidth,
                    frameHeight);
            }
            else
            {
                _smoothedHandRect = ClampToFrame(newBox, frameWidth, frameHeight);
            }

            // Suavizado EMA de la punta del índice para High Noon
            if (rawAnalysis.IndexFingerTip.HasValue)
            {
                Point rawTip = rawAnalysis.IndexFingerTip.Value;
                if (_smoothedIndexTip.HasValue)
                {
                    float tx = (float)(_spatialAlpha * rawTip.X + (1.0 - _spatialAlpha) * _smoothedIndexTip.Value.X);
                    float ty = (float)(_spatialAlpha * rawTip.Y + (1.0 - _spatialAlpha) * _smoothedIndexTip.Value.Y);
                    _smoothedIndexTip = new Point2f(tx, ty);
                }
                else
                {
                    _smoothedIndexTip = new Point2f(rawTip.X, rawTip.Y);
                }
            }
            else
            {
                _smoothedIndexTip = null;
            }
        }
        else
        {
            _smoothedHandRect = null;
            _smoothedIndexTip = null;
        }

        // ── 2. Ventana temporal de clasificación ─────────────────────────────
        _gestureHistory.Enqueue(rawGesture);
        while (_gestureHistory.Count > _historyWindowSize)
        {
            _gestureHistory.Dequeue();
        }

        // Moda estadística (gesto más frecuente)
        var dominant = _gestureHistory
            .GroupBy(g => g)
            .OrderByDescending(grp => grp.Count())
            .First();

        GestureType dominantGesture = dominant.Key;
        int dominantCount = dominant.Count();

        // ── 3. Lógica de debouncing y persistencia ───────────────────────────
        if (dominantCount >= Math.Ceiling(_historyWindowSize * 0.55))
        {
            if (dominantGesture != _stableGesture)
            {
                if (dominantGesture != _candidateGesture)
                {
                    _candidateGesture = dominantGesture;
                    _candidateFirstSeenTicks = nowTicks;
                }
                else
                {
                    if (nowTicks - _candidateFirstSeenTicks >= _debounceTicks)
                    {
                        _stableGesture = dominantGesture;
                    }
                }
            }
            else
            {
                _candidateGesture = dominantGesture;
            }
        }

        return (_stableGesture, _smoothedHandRect, SmoothedIndexFingerTip);
    }

    /// <summary>
    /// Reinicia todos los filtros del estabilizador de gestos.
    /// </summary>
    public void Reset()
    {
        _gestureHistory.Clear();
        _smoothedHandRect = null;
        _smoothedIndexTip = null;
        _stableGesture = GestureType.None;
        _candidateGesture = GestureType.None;
        _candidateFirstSeenTicks = 0;
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
