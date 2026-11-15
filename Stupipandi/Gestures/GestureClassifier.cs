using System;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Catálogo ampliado de gestos y posturas reconocibles por el simulador Stupipandi.
/// </summary>
public enum GestureType
{
    None,
    Fist,
    ThumbsUp,
    OneFinger,
    TwoFingers,
    ThreeFingers,
    FourFingers,
    OpenHand,
    Rock,
    FingerGun,
    PrayingPalms,
    SteepledFingertips,
    MilitarySalute,
    HandsOnHead
}

/// <summary>
/// Argumentos emitidos cuando cambia el gesto estable detectado.
/// </summary>
public sealed class GestureChangedEventArgs : EventArgs
{
    public GestureChangedEventArgs(GestureType previous, GestureType current)
    {
        Previous = previous;
        Current = current;
    }

    public GestureType Previous { get; }
    public GestureType Current { get; }
}

/// <summary>
/// Clasificador geométrico determinista de posturas y gestos de la mano.
/// Evalúa combinaciones de solidez, defectos interdigitales, elongación, relaciones de aspecto
/// y relaciones espaciales con el rostro del usuario.
/// </summary>
public sealed class GestureClassifier
{
    private GestureType _currentGesture = GestureType.None;

    /// <summary>
    /// Evento producido cuando el gesto clasificado cambia.
    /// </summary>
    public event EventHandler<GestureChangedEventArgs>? GestureChanged;

    /// <summary>
    /// Gesto clasificado actual.
    /// </summary>
    public GestureType CurrentGesture => _currentGesture;

    /// <summary>
    /// Clasifica una postura de mano basada en descriptores geométricos de contorno y contexto facial.
    /// </summary>
    /// <param name="analysis">Resultado del análisis de contorno (o null si no hay contorno principal).</param>
    /// <param name="faceBounds">Cuadro delimitador del rostro para evaluar gestos contextuales.</param>
    /// <param name="skinMask">Máscara de piel para evaluar gestos simétricos como Manos en la Cabeza.</param>
    /// <returns>Tipo de gesto clasificado.</returns>
    public GestureType Classify(ContourAnalysis? analysis, Rect? faceBounds = null, Mat? skinMask = null)
    {
        // ── 1. Evaluar gestos contextuales de cuerpo/cabeza primero ───────────
        if (faceBounds.HasValue && skinMask is not null && !skinMask.Empty())
        {
            if (ContourAnalyzer.CheckHandsOnHead(skinMask, faceBounds.Value))
            {
                return GestureType.HandsOnHead;
            }
        }

        if (faceBounds.HasValue && analysis is not null)
        {
            if (ContourAnalyzer.CheckMilitarySalute(analysis, faceBounds.Value))
            {
                return GestureType.MilitarySalute;
            }
        }

        if (analysis is null)
        {
            return GestureType.None;
        }

        int defects = analysis.FingerDefectCount;
        double solidity = analysis.Solidity;
        double ar = analysis.AspectRatio; // Alto / Ancho

        // ── 2. Palmas en Oración (Praying Palms) ──────────────────────────────
        // Contorno vertical centrado, alta solidez, forma ahusada hacia arriba y pocos defectos
        if (solidity >= 0.74 && ar >= 1.38 && defects <= 1)
        {
            if (IsPrayingPalmsSilhouette(analysis))
            {
                return GestureType.PrayingPalms;
            }
        }

        // ── 3. Manos en Ojiva / Dedos Entrelazados (Steepled Fingertips) ───────
        // Silueta en triángulo (Merkel-Raute), ápice superior pronunciado y apertura inferior
        if (IsSteepledFingertips(analysis))
        {
            return GestureType.SteepledFingertips;
        }

        // ── 4. Pistola con Dedos (Finger Gun) ──────────────────────────────────
        // 1 índice extendido horizontal o diagonalmente + pulgar apuntando hacia arriba
        if (IsFingerGun(analysis))
        {
            return GestureType.FingerGun;
        }

        // ── 5. Mano abierta (4 o más defectos entre dedos, o 3 con baja solidez y buena dispersión) ──
        if (defects >= 4 || (defects == 3 && solidity < 0.62 && ar < 1.35))
        {
            return GestureType.OpenHand;
        }

        // ── 6. Cuatro dedos ──────────────────────────────────────────────────
        if (defects == 3)
        {
            return GestureType.FourFingers;
        }

        // ── 7. Tres dedos o Rock ─────────────────────────────────────────────
        if (defects == 2)
        {
            if (IsRockSign(analysis))
            {
                return GestureType.Rock;
            }

            return GestureType.ThreeFingers;
        }

        // ── 8. Dos dedos (Paz / Victoria) ────────────────────────────────────
        if (defects == 1)
        {
            var mainDefect = analysis.ValidDefects[0];
            if (mainDefect.AngleDegrees is >= 18.0 and <= 80.0 && solidity < 0.80)
            {
                return GestureType.TwoFingers;
            }

            if (solidity >= 0.78 && ar >= 1.25 && HasUpwardApex(analysis))
            {
                return GestureType.ThumbsUp;
            }

            return GestureType.OneFinger;
        }

        // ── 9. Cero defectos (Puño cerrado, Pulgar arriba o 1 Dedo) ──────────
        if (defects == 0)
        {
            if (solidity >= 0.82 && ar is >= 0.70 and <= 1.45)
            {
                return GestureType.Fist;
            }

            if (ar >= 1.28 && HasUpwardApex(analysis) && solidity >= 0.72)
            {
                return GestureType.ThumbsUp;
            }

            if (ar >= 1.35 && solidity < 0.80)
            {
                return GestureType.OneFinger;
            }

            if (solidity >= 0.75)
            {
                return GestureType.Fist;
            }
        }

        return GestureType.None;
    }

    /// <summary>
    /// Clasifica y actualiza el gesto actual, disparando el evento si hay cambio.
    /// </summary>
    public GestureType Update(ContourAnalysis? analysis, Rect? faceBounds = null, Mat? skinMask = null)
    {
        GestureType detected = Classify(analysis, faceBounds, skinMask);

        if (detected != _currentGesture)
        {
            GestureType previous = _currentGesture;
            _currentGesture = detected;
            GestureChanged?.Invoke(this, new GestureChangedEventArgs(previous, _currentGesture));
        }

        return _currentGesture;
    }

    /// <summary>
    /// Restablece el clasificador a estado <see cref="GestureType.None"/>.
    /// </summary>
    public void Reset()
    {
        if (_currentGesture != GestureType.None)
        {
            GestureType previous = _currentGesture;
            _currentGesture = GestureType.None;
            GestureChanged?.Invoke(this, new GestureChangedEventArgs(previous, GestureType.None));
        }
    }

    /// <summary>
    /// Evalúa si la geometría corresponde a Finger-Gun (índice extendido + pulgar vertical).
    /// </summary>
    private static bool IsFingerGun(ContourAnalysis analysis)
    {
        if (!analysis.IndexFingerTip.HasValue || !analysis.ThumbTip.HasValue) return false;

        Point index = analysis.IndexFingerTip.Value;
        Point thumb = analysis.ThumbTip.Value;

        // El pulgar debe estar más alto (Y menor) que la base o formar un ángulo pronunciado con el índice
        double dx = Math.Abs(index.X - thumb.X);
        double dy = Math.Abs(index.Y - thumb.Y);

        // Distancia espacial apreciable entre índice y pulgar (> 25% del ancho de la caja)
        double tipDist = Math.Sqrt(dx * dx + dy * dy);
        if (tipDist < analysis.BoundingBox.Width * 0.28) return false;

        // Relación de ángulo: El índice apunta más hacia el lateral (horizontal o diagonal)
        // y el pulgar apunta hacia arriba
        bool thumbIsHigher = thumb.Y <= index.Y + (analysis.BoundingBox.Height * 0.35);
        bool hasGoodSeparation = dx > analysis.BoundingBox.Width * 0.25 || dy > analysis.BoundingBox.Height * 0.25;

        // Solidez intermedia (no puño cerrado ni mano 5 dedos extendida)
        return thumbIsHigher && hasGoodSeparation && analysis.Solidity is >= 0.50 and <= 0.84 && analysis.FingerDefectCount <= 3;
    }

    /// <summary>
    /// Evalúa si la geometría del contorno corresponde a palmas en oración.
    /// </summary>
    private static bool IsPrayingPalmsSilhouette(ContourAnalysis analysis)
    {
        // Un solo bloque vertical con ancho menor arriba que abajo y sin defectos pronunciados
        var bbox = analysis.BoundingBox;
        if (bbox.Height < 100 || bbox.Width < 50) return false;

        // El centroide debe estar en el tercio medio horizontal
        double relCentroidX = (analysis.Centroid.X - bbox.X) / bbox.Width;
        return relCentroidX is >= 0.35 and <= 0.65 && analysis.FingerDefectCount <= 1;
    }

    /// <summary>
    /// Evalúa si la postura corresponde a manos en ojiva (Steepled Fingertips / Merkel-Raute).
    /// </summary>
    private static bool IsSteepledFingertips(ContourAnalysis analysis)
    {
        var bbox = analysis.BoundingBox;
        double ar = (double)bbox.Height / Math.Max(1, bbox.Width);

        // Geometría triangular con relación de aspecto equilibrada (0.75 a 1.45)
        if (ar is < 0.70 or > 1.45) return false;

        // La solidez de manos en ojiva es típicamente moderada (0.55 - 0.78) por el hueco intermedio
        if (analysis.Solidity is < 0.52 or > 0.82) return false;

        // Puntos superiores concentrados en el tercio superior central formando el ápice
        var topPoints = analysis.ConvexHull.Where(p => p.Y <= bbox.Y + bbox.Height * 0.25).ToList();
        if (topPoints.Count == 0) return false;

        double avgTopX = topPoints.Average(p => p.X);
        double relTopX = (avgTopX - bbox.X) / bbox.Width;

        return relTopX is >= 0.30 and <= 0.70;
    }

    /// <summary>
    /// Verifica si los defectos corresponden a la postura "Rock / Cuernos".
    /// </summary>
    private static bool IsRockSign(ContourAnalysis analysis)
    {
        if (analysis.ValidDefects.Count < 1) return false;

        var deepestDefect = analysis.ValidDefects.OrderByDescending(d => d.DepthPixels).First();
        bool hasWideValley = deepestDefect.AngleDegrees > 45.0 && deepestDefect.DepthPixels > 18.0;

        return hasWideValley && analysis.Solidity < 0.72 && analysis.AspectRatio >= 1.05;
    }

    /// <summary>
    /// Verifica si el contorno tiene una protuberancia superior prominente (ej. pulgar o índice levantado).
    /// </summary>
    private static bool HasUpwardApex(ContourAnalysis analysis)
    {
        var bbox = analysis.BoundingBox;
        var topHull = analysis.ConvexHull.Where(p => p.Y <= bbox.Y + bbox.Height * 0.20).ToList();
        return topHull.Count > 0;
    }
}
