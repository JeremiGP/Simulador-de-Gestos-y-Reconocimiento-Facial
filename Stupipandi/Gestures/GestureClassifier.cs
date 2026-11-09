using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Gestos reconocidos por el módulo de análisis de la mano.
/// </summary>
public enum GestureType
{
    Unknown,
    Fist,
    OneFinger,
    TwoFingers,
    ThreeFingers,
    FourFingers,
    OpenHand
}

/// <summary>
/// Información publicada cuando cambia el gesto estable detectado.
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
/// Traduce los defectos de convexidad del contorno a un gesto semántico.
/// </summary>
public sealed class GestureClassifier
{
    private GestureType currentGesture = GestureType.Unknown;

    /// <summary>
    /// Se produce únicamente cuando el gesto clasificado cambia.
    /// </summary>
    public event EventHandler<GestureChangedEventArgs>? GestureChanged;

    public GestureType CurrentGesture => currentGesture;

    /// <summary>
    /// Clasifica un análisis de contorno sin modificar el estado interno.
    /// </summary>
    public GestureType Classify(
        ContourAnalysis? analysis,
        double minimumDefectDepthPixels = 10,
        double maximumDefectAngleDegrees = 90)
    {
        if (analysis is null)
        {
            return GestureType.Unknown;
        }

        var defectCount = analysis.CountFingerDefects(
            minimumDefectDepthPixels,
            maximumDefectAngleDegrees);

        // Un defecto separa dos dedos; por eso N defectos suelen representar N+1 dedos.
        return defectCount switch
        {
            0 => IsClosedFist(analysis)
                ? GestureType.Fist
                : GestureType.OneFinger,
            1 => GestureType.TwoFingers,
            2 => GestureType.ThreeFingers,
            3 => GestureType.FourFingers,
            _ => GestureType.OpenHand
        };
    }

    /// <summary>
    /// Clasifica y publica el nuevo gesto si es diferente del anterior.
    /// </summary>
    public GestureType Update(
        ContourAnalysis? analysis,
        double minimumDefectDepthPixels = 10,
        double maximumDefectAngleDegrees = 90)
    {
        var detected = Classify(
            analysis,
            minimumDefectDepthPixels,
            maximumDefectAngleDegrees);

        if (detected == currentGesture)
        {
            return currentGesture;
        }

        var previous = currentGesture;
        currentGesture = detected;
        GestureChanged?.Invoke(
            this,
            new GestureChangedEventArgs(previous, currentGesture));

        return currentGesture;
    }

    /// <summary>
    /// Restablece el estado a <see cref="GestureType.Unknown"/> y notifica el cambio.
    /// </summary>
    public void Reset() => Update(null);

    private static bool IsClosedFist(ContourAnalysis analysis)
    {
        var hullArea = CalculateHullArea(analysis.ConvexHull);
        if (hullArea <= double.Epsilon)
        {
            return true;
        }

        // Una mano cerrada tiende a ser compacta; el umbral evita confundirla
        // con una silueta abierta sin defectos válidos.
        var solidity = analysis.Area / hullArea;
        return solidity >= 0.88;
    }

    private static double CalculateHullArea(IReadOnlyCollection<Point> hull)
    {
        if (hull.Count < 3)
        {
            return 0;
        }

        return Cv2.ContourArea(hull.ToArray());
    }
}
