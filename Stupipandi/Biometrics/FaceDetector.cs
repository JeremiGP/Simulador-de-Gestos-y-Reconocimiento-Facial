using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Biometrics;

public enum FaceOrientation
{
    Frontal,
    LookingLeft,
    LookingRight
}

public sealed record OrientedFace(Rect Bounds, FaceOrientation Orientation);

public sealed class FaceDetector : IDisposable
{
    private CascadeClassifier? _frontalCascade;
    private CascadeClassifier? _profileCascade;
    private bool _disposed;

    public const string FaceCascadeFile = "haarcascade_frontalface_default.xml";
    public const string EyeCascadeFile = "haarcascade_eye.xml";
    public const string SmileCascadeFile = "haarcascade_smile.xml";
    public const string ProfileCascadeFile = "haarcascade_profileface.xml";

    public bool IsLoaded => (_frontalCascade is not null && !_frontalCascade.IsDisposed) ||
                            (_profileCascade is not null && !_profileCascade.IsDisposed);

    public FaceDetector(string cascadeFileName = FaceCascadeFile)
    {
        string? frontalPath = ResolveCascadePath(cascadeFileName);
        if (frontalPath is not null && File.Exists(frontalPath))
        {
            var c = new CascadeClassifier();
            if (c.Load(frontalPath) && !c.Empty())
            {
                _frontalCascade = c;
            }
            else
            {
                c.Dispose();
            }
        }

        if (cascadeFileName == FaceCascadeFile)
        {
            string? profilePath = ResolveCascadePath(ProfileCascadeFile);
            if (profilePath is not null && File.Exists(profilePath))
            {
                var pc = new CascadeClassifier();
                if (pc.Load(profilePath) && !pc.Empty())
                {
                    _profileCascade = pc;
                }
                else
                {
                    pc.Dispose();
                }
            }
        }
    }

    public IReadOnlyList<OrientedFace> DetectOrientedFaces(
        Mat grayImage,
        double scaleFactor = 1.12,
        int minNeighbors = 6,
        Size minSize = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (grayImage is null || grayImage.IsDisposed || grayImage.Empty())
        {
            return Array.Empty<OrientedFace>();
        }

        // Tamaño mínimo realista para un rostro en webcam (evita falsos positivos en ropa/cuello)
        Size effectiveMinSize = new(Math.Max(95, minSize.Width), Math.Max(95, minSize.Height));
        int effectiveNeighbors = Math.Max(6, minNeighbors);

        List<OrientedFace> candidates = new();

        // 1. Detección frontal
        if (_frontalCascade is not null)
        {
            Rect[] frontals = _frontalCascade.DetectMultiScale(
                grayImage,
                scaleFactor,
                effectiveNeighbors,
                HaarDetectionTypes.ScaleImage,
                effectiveMinSize);

            foreach (var r in frontals)
            {
                candidates.Add(new OrientedFace(r, FaceOrientation.Frontal));
            }
        }

        // 2. Detección de perfil con umbral más estricto (evita pliegues de ropa)
        if (_profileCascade is not null)
        {
            int profileNeighbors = effectiveNeighbors + 2;

            Rect[] profilesNormal = _profileCascade.DetectMultiScale(
                grayImage,
                scaleFactor,
                profileNeighbors,
                HaarDetectionTypes.ScaleImage,
                effectiveMinSize);

            foreach (var r in profilesNormal)
            {
                candidates.Add(new OrientedFace(r, FaceOrientation.LookingRight));
            }

            using var flipped = new Mat();
            Cv2.Flip(grayImage, flipped, FlipMode.Y);

            Rect[] profilesFlipped = _profileCascade.DetectMultiScale(
                flipped,
                scaleFactor,
                profileNeighbors,
                HaarDetectionTypes.ScaleImage,
                effectiveMinSize);

            foreach (var r in profilesFlipped)
            {
                int origX = grayImage.Cols - (r.X + r.Width);
                Rect unmirrored = new(origX, r.Y, r.Width, r.Height);
                candidates.Add(new OrientedFace(unmirrored, FaceOrientation.LookingLeft));
            }
        }

        return NonMaxSuppressionAndBodyFilter(candidates, grayImage.Rows);
    }

    public Rect[] DetectMultiScale(
        Mat image,
        double scaleFactor = 1.1,
        int minNeighbors = 3,
        HaarDetectionTypes flags = HaarDetectionTypes.ScaleImage,
        Size minSize = default,
        Size maxSize = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (image is null || image.IsDisposed || image.Empty())
        {
            return Array.Empty<Rect>();
        }

        if (_profileCascade is null)
        {
            if (_frontalCascade is null) return Array.Empty<Rect>();
            return _frontalCascade.DetectMultiScale(image, scaleFactor, minNeighbors, flags, minSize, maxSize);
        }

        var oriented = DetectOrientedFaces(image, scaleFactor, minNeighbors, minSize);
        return oriented.Select(x => x.Bounds).ToArray();
    }

    private static IReadOnlyList<OrientedFace> NonMaxSuppressionAndBodyFilter(List<OrientedFace> faces, int frameHeight)
    {
        if (faces.Count == 0) return faces;

        var sorted = faces
            .OrderByDescending(f => f.Orientation == FaceOrientation.Frontal)
            .ThenByDescending(f => f.Bounds.Width * f.Bounds.Height)
            .ToList();

        List<OrientedFace> merged = new();

        foreach (var face in sorted)
        {
            // Descartar detecciones en el tercio inferior de la pantalla (pecho/ropa)
            if (face.Bounds.Y > frameHeight * 0.68)
                continue;

            bool reject = false;
            foreach (var existing in merged)
            {
                // 1. Solapamiento directo (misma cara)
                if (CalculateIoU(face.Bounds, existing.Bounds) > 0.25)
                {
                    reject = true;
                    break;
                }

                // 2. Falso positivo en cuello/pecho justo debajo de una cara ya detectada
                int existingCenterX = existing.Bounds.X + existing.Bounds.Width / 2;
                int candidateCenterX = face.Bounds.X + face.Bounds.Width / 2;
                bool alignedVertically = Math.Abs(existingCenterX - candidateCenterX) < existing.Bounds.Width * 0.70;
                bool isBelow = face.Bounds.Y >= existing.Bounds.Y + existing.Bounds.Height * 0.45;

                if (alignedVertically && isBelow)
                {
                    reject = true;
                    break;
                }

                // 3. Una segunda persona real tiene un tamaño comparable (al menos 55% del ancho de la primera)
                if (face.Bounds.Width < existing.Bounds.Width * 0.55)
                {
                    reject = true;
                    break;
                }
            }

            if (!reject)
            {
                merged.Add(face);
            }
        }

        // Máximo 2 rostros simultáneos (evita ruido múltiple)
        return merged.Take(2).ToList();
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

    private static string? ResolveCascadePath(string fileName)
    {
        string[] searchPaths = [
            Path.Combine(AppContext.BaseDirectory, "Assets", "Cascades", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Cascades", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Stupipandi", "Assets", "Cascades", fileName),
            fileName
        ];

        return searchPaths.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frontalCascade?.Dispose();
        _profileCascade?.Dispose();
        _frontalCascade = null;
        _profileCascade = null;
    }
}