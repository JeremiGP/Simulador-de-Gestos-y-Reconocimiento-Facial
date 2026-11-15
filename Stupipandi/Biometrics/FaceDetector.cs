using System;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace Stupipandi.Biometrics;

/// <summary>
/// Encapsula de forma segura una instancia de <see cref="CascadeClassifier"/> de OpenCV.
/// Resuelve de forma inteligente la ubicación de los archivos Haar cascade XML tanto
/// en tiempo de desarrollo como en tiempo de ejecución empaquetada.
/// </summary>
public sealed class FaceDetector : IDisposable
{
    private CascadeClassifier? _cascade;
    private bool _disposed;

    public const string FaceCascadeFile    = "haarcascade_frontalface_default.xml";
    public const string EyeCascadeFile     = "haarcascade_eye.xml";
    public const string SmileCascadeFile   = "haarcascade_smile.xml";
    public const string ProfileCascadeFile = "haarcascade_profileface.xml";

    /// <summary>
    /// Nombre del archivo cascade asignado.
    /// </summary>
    public string CascadeFileName { get; }

    /// <summary>
    /// Ruta absoluta resuelta del archivo de cascada si fue encontrado.
    /// </summary>
    public string? ResolvedPath { get; }

    /// <summary>
    /// Indica si el clasificador está listo para realizar detecciones.
    /// </summary>
    public bool IsLoaded => _cascade is not null && !_cascade.IsDisposed;

    /// <summary>
    /// Inicializa un nuevo detector cargando el archivo de cascada especificado.
    /// </summary>
    /// <param name="cascadeFileName">Nombre del archivo XML (por ejemplo, haarcascade_frontalface_default.xml).</param>
    public FaceDetector(string cascadeFileName = FaceCascadeFile)
    {
        CascadeFileName = cascadeFileName;
        ResolvedPath = ResolveCascadePath(cascadeFileName);

        if (ResolvedPath is not null && File.Exists(ResolvedPath))
        {
            var classifier = new CascadeClassifier();
            if (classifier.Load(ResolvedPath) && !classifier.Empty())
            {
                _cascade = classifier;
            }
            else
            {
                classifier.Dispose();
            }
        }

        if (_cascade is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[FaceDetector] Advertencia: No se pudo inicializar la cascada '{cascadeFileName}'.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Ejecuta la detección multiescala sobre la matriz proporcionada.
    /// Devuelve un array vacío si el clasificador no está disponible o la imagen está vacía.
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

        if (_cascade is null || image is null || image.IsDisposed || image.Empty())
        {
            return [];
        }

        return _cascade.DetectMultiScale(
            image,
            scaleFactor,
            minNeighbors,
            flags,
            minSize,
            maxSize);
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
        _cascade?.Dispose();
        _cascade = null;
    }
}
