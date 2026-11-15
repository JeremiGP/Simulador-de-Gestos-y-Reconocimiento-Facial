using System;
using System.IO;
using OpenCvSharp;

namespace GestureFaceSimulator.Biometrics;

/// <summary>
/// Detecta rostros y ojos usando clasificadores Haar de OpenCV.
/// Las coordenadas devueltas están referidas al frame original.
/// </summary>
public sealed class FaceDetector : IDisposable
{
    private readonly CascadeClassifier _faceCascade;
    private readonly CascadeClassifier _eyeCascade;
    private bool _disposed;

    public FaceDetector(string faceCascadePath, string eyeCascadePath)
    {
        if (string.IsNullOrWhiteSpace(faceCascadePath))
            throw new ArgumentException("Indica la ruta del clasificador de rostros.", nameof(faceCascadePath));
        if (string.IsNullOrWhiteSpace(eyeCascadePath))
            throw new ArgumentException("Indica la ruta del clasificador de ojos.", nameof(eyeCascadePath));
        if (!File.Exists(faceCascadePath))
            throw new FileNotFoundException("No se encontró haarcascade_frontalface_default.xml.", faceCascadePath);
        if (!File.Exists(eyeCascadePath))
            throw new FileNotFoundException("No se encontró haarcascade_eye.xml.", eyeCascadePath);

        CascadeClassifier? face = null;
        CascadeClassifier? eye = null;

        try
        {
            face = new CascadeClassifier(faceCascadePath);
            eye = new CascadeClassifier(eyeCascadePath);

            if (face.Empty())
                throw new InvalidDataException($"El clasificador facial no se pudo cargar: {faceCascadePath}");
            if (eye.Empty())
                throw new InvalidDataException($"El clasificador de ojos no se pudo cargar: {eyeCascadePath}");

            _faceCascade = face!;
            _eyeCascade = eye!;
        }
        catch
        {
            face?.Dispose();
            eye?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Devuelve los rectángulos de los rostros encontrados en el frame.
    /// </summary>
    public Rect[] DetectFaces(Mat frame)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Empty())
            return Array.Empty<Rect>();

        using Mat gray = FaceImageTools.ToGray(frame);
        using var equalized = new Mat();
        Cv2.EqualizeHist(gray, equalized);

        return _faceCascade.DetectMultiScale(
            equalized,
            scaleFactor: 1.1,
            minNeighbors: 5,
            minSize: new Size(60, 60));
    }

    /// <summary>
    /// Busca ojos dentro de un rostro y devuelve sus coordenadas absolutas en el frame.
    /// </summary>
    public Rect[] DetectEyes(Mat frame, Rect faceBounds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Empty())
            return Array.Empty<Rect>();

        Rect safeFace = ClipRectangle(faceBounds, frame.Cols, frame.Rows);
        if (safeFace.Width <= 0 || safeFace.Height <= 0)
            return Array.Empty<Rect>();

        using Mat gray = FaceImageTools.ToGray(frame);
        using Mat faceRegion = gray[safeFace].Clone();
        using var equalized = new Mat();
        Cv2.EqualizeHist(faceRegion, equalized);

        Rect[] localEyes = _eyeCascade.DetectMultiScale(
            equalized,
            scaleFactor: 1.1,
            minNeighbors: 3,
            minSize: new Size(12, 12));

        var absoluteEyes = new Rect[localEyes.Length];
        for (int i = 0; i < localEyes.Length; i++)
        {
            Rect eye = localEyes[i];
            absoluteEyes[i] = new Rect(
                safeFace.X + eye.X,
                safeFace.Y + eye.Y,
                eye.Width,
                eye.Height);
        }

        return absoluteEyes;
    }

    private static Rect ClipRectangle(Rect rectangle, int imageWidth, int imageHeight)
    {
        int x = Math.Clamp(rectangle.X, 0, imageWidth);
        int y = Math.Clamp(rectangle.Y, 0, imageHeight);
        int width = Math.Clamp(rectangle.Width, 0, imageWidth - x);
        int height = Math.Clamp(rectangle.Height, 0, imageHeight - y);
        return new Rect(x, y, width, height);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FaceDetector));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _faceCascade.Dispose();
        _eyeCascade.Dispose();
        _disposed = true;
    }
}
