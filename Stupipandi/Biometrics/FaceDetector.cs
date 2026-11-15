using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Biometrics;

/// <summary>
/// Orientación angular estimada del rostro en el espacio de la imagen.
/// </summary>
public enum FaceOrientation
{
    Frontal,
    LookingLeft,
    LookingRight
}

/// <summary>
/// Detección de rostro que incluye límites geométricos y orientación angular.
/// </summary>
public sealed record OrientedFace(Rect Bounds, FaceOrientation Orientation);

/// <summary>
/// Detector facial multiescala con soporte para rostros frontales y de perfil bilateral.
/// Combina haarcascade_frontalface_default.xml con haarcascade_profileface.xml (en orientación directa
/// y con inversión horizontal en espejo) para mantener el seguimiento ininterrumpido al girar la cabeza.
/// </summary>
public sealed class FaceDetector : IDisposable
{
    private CascadeClassifier? _frontalCascade;
    private CascadeClassifier? _profileCascade;
    private bool _disposed;

    public const string FaceCascadeFile    = "haarcascade_frontalface_default.xml";
    public const string EyeCascadeFile     = "haarcascade_eye.xml";
    public const string SmileCascadeFile   = "haarcascade_smile.xml";
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

        // Si es el detector principal de rostros, cargar también la cascada de perfil
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

    /// <summary>
    /// Detecta rostros con información de orientación (Frontal, Perfil Izquierdo o Perfil Derecho).
    /// </summary>
    public IReadOnlyList<OrientedFace> DetectOrientedFaces(
        Mat grayImage,
        double scaleFactor = 1.1,
        int minNeighbors = 4,
        Size minSize = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (grayImage is null || grayImage.IsDisposed || grayImage.Empty())
        {
            return Array.Empty<OrientedFace>();
        }

        if (minSize == default) minSize = new Size(50, 50);

        List<OrientedFace> candidates = new();

        // 1. Detección frontal estándar
        if (_frontalCascade is not null)
        {
            Rect[] frontals = _frontalCascade.DetectMultiScale(
                grayImage,
                scaleFactor,
                minNeighbors,
                HaarDetectionTypes.ScaleImage,
                minSize);

            foreach (var r in frontals)
            {
                candidates.Add(new OrientedFace(r, FaceOrientation.Frontal));
            }
        }

        // 2. Detección de perfil si el clasificador está disponible
        if (_profileCascade is not null)
        {
            // Perfil directo (convención OpenCV: suele detectar perfil derecho del sujeto, que mira a la derecha de la imagen)
            Rect[] profilesNormal = _profileCascade.DetectMultiScale(
                grayImage,
                scaleFactor,
                minNeighbors,
                HaarDetectionTypes.ScaleImage,
                minSize);

            foreach (var r in profilesNormal)
            {
                candidates.Add(new OrientedFace(r, FaceOrientation.LookingRight));
            }

            // Perfil en espejo (invirtiendo horizontalmente la imagen para detectar el perfil opuesto)
            using var flipped = new Mat();
            Cv2.Flip(grayImage, flipped, FlipMode.Y);

            Rect[] profilesFlipped = _profileCascade.DetectMultiScale(
                flipped,
                scaleFactor,
                minNeighbors,
                HaarDetectionTypes.ScaleImage,
                minSize);

            foreach (var r in profilesFlipped)
            {
                // Re-mapear las coordenadas al espacio original
                int origX = grayImage.Cols - (r.X + r.Width);
                Rect unmirrored = new(origX, r.Y, r.Width, r.Height);
                candidates.Add(new OrientedFace(unmirrored, FaceOrientation.LookingLeft));
            }
        }

        // 3. Supresión de No-Máximos (NMS) para fusionar rectángulos duplicados sobre la misma persona
        return NonMaxSuppression(candidates);
    }

    /// <summary>
    /// Detección clásica de rectángulos compatibles hacia atrás.
    /// </summary>
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

        // Si es una cascada secundaria (ojos, sonrisa) usar llamada directa
        if (_profileCascade is null)
        {
            if (_frontalCascade is null) return Array.Empty<Rect>();
            return _frontalCascade.DetectMultiScale(image, scaleFactor, minNeighbors, flags, minSize, maxSize);
        }

        // Si es el detector de rostros, invocar la detección combinada frontal + perfiles
        var oriented = DetectOrientedFaces(image, scaleFactor, minNeighbors, minSize);
        return oriented.Select(x => x.Bounds).ToArray();
    }

    private static IReadOnlyList<OrientedFace> NonMaxSuppression(List<OrientedFace> faces)
    {
        if (faces.Count <= 1) return faces;

        // Priorizar rostros frontales primero, luego por área descendente
        var sorted = faces
            .OrderByDescending(f => f.Orientation == FaceOrientation.Frontal)
            .ThenByDescending(f => f.Bounds.Width * f.Bounds.Height)
            .ToList();

        List<OrientedFace> merged = new();

        foreach (var face in sorted)
        {
            bool overlaps = false;
            foreach (var existing in merged)
            {
                if (CalculateIoU(face.Bounds, existing.Bounds) > 0.32)
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
            {
                merged.Add(face);
            }
        }

        return merged;
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
