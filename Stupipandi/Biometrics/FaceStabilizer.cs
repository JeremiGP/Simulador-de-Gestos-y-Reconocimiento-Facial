using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Biometrics;

/// <summary>
/// Representa un rostro estabilizado espacialmente a lo largo del tiempo.
/// </summary>
public sealed class TrackedFace
{
    public int Id { get; init; }
    public Rect SmoothedRect { get; set; }
    public Rect SmoothedBounds => SmoothedRect;
    public int MissedFrames { get; set; }
    public FaceOrientation Orientation { get; set; }
    public FaceRecognitionResult? LastRecognition { get; set; }
    public ExpressionResult? LastExpression { get; set; }
}

/// <summary>
/// Motor multifacial de estabilización espacial anti-jitter y votación temporal de identidad.
/// Permite rastrear simultáneamente múltiples rostros (requerido para Trigger 1 - Lion Roar)
/// y suaviza las coordenadas $(X, Y, W, H)$ mediante EMA para fijar sombreros AR sin vibración.
/// </summary>
public sealed class FaceStabilizer
{
    private readonly double _spatialAlpha;
    private readonly int _votingWindowSize;
    private readonly int _maxDropoutFrames;
    private readonly double _iouTrackingThreshold;

    private readonly List<TrackedFace> _trackedFaces = new();
    private int _nextFaceId = 1;

    // Ventana temporal de votación de identidad para el usuario primario
    private readonly Queue<(string Label, bool IsRecognized, double Distance)> _identityHistory = new();
    private readonly double _enterThreshold;
    private readonly double _exitThreshold;
    private bool _currentlyRecognizedState;
    private string _currentStableLabel = "Desconocido";

    public IReadOnlyList<TrackedFace> StableFaces => _trackedFaces.Where(f => f.MissedFrames <= _maxDropoutFrames).ToList();
    public Rect? PrimaryFaceRect => StableFaces.Count > 0 ? StableFaces[0].SmoothedRect : null;
    public string StableLabel => _currentStableLabel;
    public bool IsRecognized => _currentlyRecognizedState;

    public FaceStabilizer(
        double spatialAlpha = 0.28,
        int votingWindowSize = 7,
        int maxDropoutFrames = 4,
        double baseThreshold = 52.0)
    {
        _spatialAlpha = Math.Clamp(spatialAlpha, 0.05, 0.95);
        _votingWindowSize = Math.Max(3, votingWindowSize);
        _maxDropoutFrames = Math.Max(1, maxDropoutFrames);
        _iouTrackingThreshold = 0.28;

        _enterThreshold = baseThreshold - 3.5;
        _exitThreshold = baseThreshold + 3.5;
    }

    /// <summary>
    /// Actualiza el seguimiento multifacial en cada fotograma.
    /// </summary>
    public (IReadOnlyList<TrackedFace> Faces, FaceRecognitionResult PrimaryResult) Update(
        IReadOnlyList<OrientedFace> detectedFaces,
        FaceRecognitionResult? rawPrimaryResult,
        int frameWidth,
        int frameHeight)
    {
        // ── 1. Asociación temporal de rostros por IoU ────────────────────────
        HashSet<int> matchedTrackedIds = new();
        List<TrackedFace> activeTracks = new();

        foreach (var det in detectedFaces)
        {
            TrackedFace? bestMatch = null;
            double bestIou = 0;

            foreach (var track in _trackedFaces)
            {
                if (matchedTrackedIds.Contains(track.Id)) continue;

                double iou = CalculateIoU(track.SmoothedRect, det.Bounds);
                if (iou > _iouTrackingThreshold && iou > bestIou)
                {
                    bestIou = iou;
                    bestMatch = track;
                }
            }

            if (bestMatch is not null)
            {
                // Actualizar pista existente con EMA espacial
                matchedTrackedIds.Add(bestMatch.Id);
                bestMatch.MissedFrames = 0;
                bestMatch.Orientation = det.Orientation;

                double sx = _spatialAlpha * det.Bounds.X + (1.0 - _spatialAlpha) * bestMatch.SmoothedRect.X;
                double sy = _spatialAlpha * det.Bounds.Y + (1.0 - _spatialAlpha) * bestMatch.SmoothedRect.Y;
                double sw = _spatialAlpha * det.Bounds.Width + (1.0 - _spatialAlpha) * bestMatch.SmoothedRect.Width;
                double sh = _spatialAlpha * det.Bounds.Height + (1.0 - _spatialAlpha) * bestMatch.SmoothedRect.Height;

                bestMatch.SmoothedRect = ClampToFrame(
                    new Rect((int)Math.Round(sx), (int)Math.Round(sy), (int)Math.Round(sw), (int)Math.Round(sh)),
                    frameWidth,
                    frameHeight);

                activeTracks.Add(bestMatch);
            }
            else
            {
                // Nueva pista facial
                var newTrack = new TrackedFace
                {
                    Id = _nextFaceId++,
                    SmoothedRect = ClampToFrame(det.Bounds, frameWidth, frameHeight),
                    MissedFrames = 0,
                    Orientation = det.Orientation
                };
                activeTracks.Add(newTrack);
            }
        }

        // Pistas que no tuvieron detección en este fotograma (Drop-out recovery)
        foreach (var track in _trackedFaces)
        {
            if (!matchedTrackedIds.Contains(track.Id))
            {
                track.MissedFrames++;
                if (track.MissedFrames <= _maxDropoutFrames)
                {
                    activeTracks.Add(track);
                }
            }
        }

        _trackedFaces.Clear();
        // Ordenar de mayor a menor área para que el rostro principal sea el índice 0
        _trackedFaces.AddRange(activeTracks.OrderByDescending(t => t.SmoothedRect.Width * t.SmoothedRect.Height));

        // ── 2. Votación de identidad para el rostro primario ─────────────────
        FaceRecognitionResult finalPrimaryResult;

        if (_trackedFaces.Count == 0 || rawPrimaryResult is null || !rawPrimaryResult.ModelIsReady)
        {
            _identityHistory.Clear();
            _currentlyRecognizedState = false;
            _currentStableLabel = "Desconocido";

            finalPrimaryResult = new FaceRecognitionResult(
                false,
                rawPrimaryResult?.ModelIsReady == false ? "Modelo sin entrenar" : "Desconocido",
                double.PositiveInfinity,
                rawPrimaryResult?.ModelIsReady ?? false,
                0);
        }
        else
        {
            _identityHistory.Enqueue((rawPrimaryResult.Label, rawPrimaryResult.IsRecognized, rawPrimaryResult.Distance));
            while (_identityHistory.Count > _votingWindowSize)
            {
                _identityHistory.Dequeue();
            }

            double avgDistance = _identityHistory.Average(x => double.IsFinite(x.Distance) ? x.Distance : 100.0);
            var labelVotes = _identityHistory.GroupBy(x => x.Label).OrderByDescending(g => g.Count()).First();
            string dominantLabel = labelVotes.Key;
            int dominantCount = labelVotes.Count();

            // Histéresis dual
            if (_currentlyRecognizedState)
            {
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
                if (avgDistance <= _enterThreshold && dominantLabel != "Desconocido" && dominantCount >= Math.Ceiling(_votingWindowSize * 0.55))
                {
                    _currentlyRecognizedState = true;
                    _currentStableLabel = dominantLabel;
                }
                else
                {
                    _currentStableLabel = "Desconocido";
                }
            }

            double normalizedConfidence = 0.0;
            if (double.IsFinite(avgDistance) && avgDistance > 0)
            {
                double refThreshold = _currentlyRecognizedState ? _exitThreshold : _enterThreshold;
                double score = (1.0 - (avgDistance / refThreshold)) * 100.0;
                normalizedConfidence = Math.Clamp(score + 15.0, 0.0, 100.0);
            }

            finalPrimaryResult = new FaceRecognitionResult(
                _currentlyRecognizedState,
                _currentStableLabel,
                Math.Round(avgDistance, 1),
                true,
                (int)Math.Round(normalizedConfidence));

            _trackedFaces[0].LastRecognition = finalPrimaryResult;
        }

        return (StableFaces, finalPrimaryResult);
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

    public void Reset()
    {
        _trackedFaces.Clear();
        _identityHistory.Clear();
        _currentlyRecognizedState = false;
        _currentStableLabel = "Desconocido";
    }
}
