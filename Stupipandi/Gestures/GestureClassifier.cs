using System;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Gestures;

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

public sealed class GestureClassifier
{
    private GestureType _currentGesture = GestureType.None;

    public event EventHandler<GestureChangedEventArgs>? GestureChanged;
    public GestureType CurrentGesture => _currentGesture;

    public GestureType Classify(ContourAnalysis? analysis, Rect? faceBounds = null, Mat? skinMask = null)
    {
        // 1. Manos en la cabeza (Trigger 2)
        if (faceBounds.HasValue && skinMask is not null && !skinMask.Empty())
        {
            if (ContourAnalyzer.CheckHandsOnHead(skinMask, faceBounds.Value))
            {
                return GestureType.HandsOnHead;
            }
        }

        // 2. Saludo militar (Trigger 6)
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
        double ar = analysis.AspectRatio;

        // 3. Palmas en Oración (Trigger 4)
        if (IsPrayingPalmsSilhouette(analysis, faceBounds))
        {
            return GestureType.PrayingPalms;
        }

        // 4. Manos en Ojiva / Hava Nagila (Trigger 5)
        if (IsSteepledFingertips(analysis, faceBounds))
        {
            return GestureType.SteepledFingertips;
        }

        // 5. Pistola con Dedos en "L" (Trigger 3)
        if (IsFingerGun(analysis))
        {
            return GestureType.FingerGun;
        }

        // 6. Mano abierta
        if (defects >= 4 || (defects == 3 && solidity < 0.65 && ar < 1.35))
        {
            return GestureType.OpenHand;
        }

        // 7. Cuatro dedos
        if (defects == 3)
        {
            return GestureType.FourFingers;
        }

        // 8. Tres dedos o Rock
        if (defects == 2)
        {
            if (IsRockSign(analysis))
                return GestureType.Rock;

            return GestureType.ThreeFingers;
        }

        // 9. Dos dedos o 1 Dedo
        if (defects == 1)
        {
            var mainDefect = analysis.ValidDefects[0];
            if (mainDefect.AngleDegrees is >= 18.0 and <= 75.0 && solidity < 0.78)
            {
                return GestureType.TwoFingers;
            }

            return GestureType.OneFinger;
        }

        // 10. Cero defectos
        if (defects == 0)
        {
            if (ar >= 1.45 && solidity < 0.80)
                return GestureType.OneFinger;

            if (solidity >= 0.75)
                return GestureType.Fist;
        }

        return GestureType.None;
    }

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

    public void Reset()
    {
        if (_currentGesture != GestureType.None)
        {
            GestureType previous = _currentGesture;
            _currentGesture = GestureType.None;
            GestureChanged?.Invoke(this, new GestureChangedEventArgs(previous, GestureType.None));
        }
    }

    private static bool IsFingerGun(ContourAnalysis analysis)
    {
        if (!analysis.IndexFingerTip.HasValue || !analysis.ThumbTip.HasValue)
            return false;

        if (analysis.FingerDefectCount is < 1 or > 2)
            return false;

        if (analysis.Solidity is < 0.45 or > 0.77)
            return false;

        Point p1 = analysis.IndexFingerTip.Value;
        Point p2 = analysis.ThumbTip.Value;

        double dx = Math.Abs(p1.X - p2.X);
        double dy = Math.Abs(p1.Y - p2.Y);

        return dx >= analysis.BoundingBox.Width * 0.35 && dy >= analysis.BoundingBox.Height * 0.28;
    }

    private static bool IsPrayingPalmsSilhouette(ContourAnalysis analysis, Rect? faceBounds)
    {
        var bbox = analysis.BoundingBox;
        double ar = analysis.AspectRatio;

        if (ar < 1.50 || analysis.Solidity < 0.78 || analysis.FingerDefectCount > 1)
            return false;

        if (faceBounds.HasValue)
        {
            var face = faceBounds.Value;
            int faceCenterX = face.X + face.Width / 2;
            return Math.Abs(analysis.Centroid.X - faceCenterX) < face.Width * 0.65 &&
                   bbox.Y >= face.Y + (int)(face.Height * 0.55);
        }

        return true;
    }

    /// <summary>
    /// Detecta manos en ojiva / puntas de los dedos juntas (Trigger 5: Hava Nagila).
    /// </summary>
    private static bool IsSteepledFingertips(ContourAnalysis analysis, Rect? faceBounds)
    {
        var bbox = analysis.BoundingBox;
        double ar = analysis.AspectRatio;

        // Relación de aspecto flexible para triángulo de manos frente al pecho
        if (ar is < 0.55 or > 1.50)
            return false;

        if (analysis.Solidity is < 0.52 or > 0.95 || analysis.FingerDefectCount > 2)
            return false;

        // Comparar anchura de la punta superior (top 25%) frente a la base inferior (bottom 25%)
        var topPoints = analysis.ConvexHull.Where(p => p.Y <= bbox.Y + bbox.Height * 0.25).ToList();
        var bottomPoints = analysis.ConvexHull.Where(p => p.Y >= bbox.Bottom - bbox.Height * 0.25).ToList();
        if (topPoints.Count == 0 || bottomPoints.Count == 0)
            return false;

        int topSpan = topPoints.Max(p => p.X) - topPoints.Min(p => p.X);
        int bottomSpan = bottomPoints.Max(p => p.X) - bottomPoints.Min(p => p.X);

        bool isTriangle = topSpan < bbox.Width * 0.55 && bottomSpan > topSpan * 1.25;
        if (!isTriangle)
            return false;

        if (faceBounds.HasValue)
        {
            var face = faceBounds.Value;
            int faceCenterX = face.X + face.Width / 2;
            return Math.Abs(analysis.Centroid.X - faceCenterX) < face.Width * 0.85 &&
                   bbox.Y >= face.Y + (int)(face.Height * 0.50);
        }

        return true;
    }

    private static bool IsRockSign(ContourAnalysis analysis)
    {
        if (analysis.ValidDefects.Count < 1) return false;

        var deepestDefect = analysis.ValidDefects.OrderByDescending(d => d.DepthPixels).First();
        return deepestDefect.AngleDegrees > 45.0 && deepestDefect.DepthPixels > 18.0 && analysis.Solidity < 0.72;
    }
}