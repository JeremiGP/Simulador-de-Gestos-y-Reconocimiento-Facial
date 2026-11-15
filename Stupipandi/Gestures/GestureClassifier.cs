using System;
using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Catálogo ampliado de gestos reconocibles por el simulador.
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
    Rock
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
/// Evalúa combinaciones de solidez, defectos interdigitales, elongación y relaciones de aspecto.
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
    /// Clasifica una postura de mano basada en descriptores geométricos de contorno.
    /// </summary>
    /// <param name="analysis">Resultado del análisis de contorno (o null si no hay mano visible).</param>
    /// <returns>Tipo de gesto clasificado.</returns>
    public GestureType Classify(ContourAnalysis? analysis)
    {
        if (analysis is null)
        {
            return GestureType.None;
        }

        int defects = analysis.FingerDefectCount;
        double solidity = analysis.Solidity;
        double ar = analysis.AspectRatio; // Alto / Ancho

        // ── 1. Mano abierta (4 o más defectos entre dedos, o 3 con baja solidez y buena dispersión) ──
        if (defects >= 4 || (defects == 3 && solidity < 0.62 && ar < 1.35))
        {
            return GestureType.OpenHand;
        }

        // ── 2. Cuatro dedos ──────────────────────────────────────────────────
        if (defects == 3)
        {
            return GestureType.FourFingers;
        }

        // ── 3. Tres dedos ────────────────────────────────────────────────────
        if (defects == 2)
        {
            // Evaluar si es el signo de Rock / Cuernos (dos dedos exteriores extendidos con valle profundo central)
            if (IsRockSign(analysis))
            {
                return GestureType.Rock;
            }

            return GestureType.ThreeFingers;
        }

        // ── 4. Dos dedos (Paz / Victoria) ────────────────────────────────────
        if (defects == 1)
        {
            // Si el defecto es un valle central profundo entre dedos en V
            var mainDefect = analysis.ValidDefects[0];
            if (mainDefect.AngleDegrees is >= 18.0 and <= 80.0 && solidity < 0.80)
            {
                return GestureType.TwoFingers;
            }

            // Si la solidez es alta y tiene extensión vertical marcada, puede ser pulgar o índice
            if (solidity >= 0.78 && ar >= 1.25 && analysis.HasUpwardExtension())
            {
                return GestureType.ThumbsUp;
            }

            return GestureType.OneFinger;
        }

        // ── 5. Cero defectos (Puño cerrado, Pulgar arriba o 1 Dedo) ──────────
        if (defects == 0)
        {
            // Puño cerrado: Forma compacta, alta solidez, relación de aspecto cercana a 1.0
            if (solidity >= 0.82 && ar is >= 0.70 and <= 1.45)
            {
                return GestureType.Fist;
            }

            // Pulgar hacia arriba: Elongación vertical, protuberancia superior y solidez media-alta
            if (ar >= 1.28 && analysis.HasUpwardExtension() && solidity >= 0.72)
            {
                return GestureType.ThumbsUp;
            }

            // Un dedo apuntando: Delgado y vertical con solidez moderada
            if (ar >= 1.35 && solidity < 0.80)
            {
                return GestureType.OneFinger;
            }

            // Por defecto en cero defectos con alta solidez: Puño
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
    public GestureType Update(ContourAnalysis? analysis)
    {
        GestureType detected = Classify(analysis);

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
    public void Reset() => Update(null);

    /// <summary>
    /// Verifica si los defectos corresponden a la postura "Rock / Cuernos" (índice y meñique arriba, medio y anular doblados).
    /// </summary>
    private static bool IsRockSign(ContourAnalysis analysis)
    {
        if (analysis.ValidDefects.Count < 1) return false;

        // Rock presenta un valle ancho y profundo en el centro
        var deepestDefect = analysis.ValidDefects.OrderByDescending(d => d.DepthPixels).First();
        bool hasWideValley = deepestDefect.AngleDegrees > 45.0 && deepestDefect.DepthPixels > 18.0;

        // Los puntos superiores están en los extremos laterales del contorno
        return hasWideValley && analysis.Solidity < 0.72 && analysis.AspectRatio >= 1.05;
    }
}
