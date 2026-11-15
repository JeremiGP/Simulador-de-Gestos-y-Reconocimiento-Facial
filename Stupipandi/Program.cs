using System;
using System.Collections.Generic;
using System.IO;
using OpenCvSharp;
using Stupipandi.Biometrics;
using Stupipandi.Gestures;
using Stupipandi.Infrastructure;
using Stupipandi.State;

namespace Stupipandi;

/// <summary>
/// Punto de entrada del Simulador de Gestos y Reconocimiento Facial.
///
/// Arquitectura del flujo de procesamiento:
///   VideoLoopEngine ──(OnFrameReceived)──► ProcessFrame()
///                                              ├── FramePreprocessor (Grayscale + EqualizeHist)
///                                              ├── FaceDetector (Detección de rostro)
///                                              │     ├── FaceRecognizer (Predicción biométrica)
///                                              │     ├── EyeDetector (Detección de ojos en ROI facial)
///                                              │     └── SmileDetector (Detección de sonrisa)
///                                              ├── SkinSegmentation (Máscara de piel HSV / YCrCb)
///                                              │     └── Supresión de región facial
///                                              ├── ContourAnalyzer (Contorno mayor y defectos de convexidad)
///                                              ├── GestureClassifier (Clasificación de dedos / puño)
///                                              ├── ApplicationStateManager (Máquina de estados de acceso)
///                                              └── OverlayRenderer (HUD, badges y bounding boxes)
///   VideoLoopEngine ──(OnKeyPressed)─────► HandleKey()
/// </summary>
internal sealed class Program : IDisposable
{
    // ── Módulos de visión y estado ───────────────────────────────────────────
    private readonly CameraManager           _cameraManager     = new(cameraIndex: 0);
    private readonly VideoLoopEngine         _loopEngine;
    private readonly FaceDetector            _faceDetector      = new(FaceDetector.FaceCascadeFile);
    private readonly FaceDetector            _eyeDetector       = new(FaceDetector.EyeCascadeFile);
    private readonly FaceDetector            _smileDetector     = new(FaceDetector.SmileCascadeFile);
    private readonly FaceRecognizer          _faceRecognizer    = new(threshold: 52.0);
    private readonly GestureClassifier       _gestureClassifier = new();
    private readonly OverlayRenderer         _overlayRenderer   = new();
    private readonly ApplicationStateManager _stateManager      = new();
    private readonly FpsCounter              _fpsCounter        = new(alpha: 0.08);

    // ── Configuración en tiempo de ejecución ─────────────────────────────────
    private SkinSegmentation.ColorSpace _activeColorSpace = SkinSegmentation.ColorSpace.Hsv;
    private volatile string?            _activePrompt     = null;
    private volatile int                _enrolmentTargetSamples = 0;
    private string?                     _enrolmentName    = null;
    private bool                        _disposed;

    // ── Ruta persistente del modelo ──────────────────────────────────────────
    private static string FaceModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "face_model.xml");

    // ── Constructor ─────────────────────────────────────────────────────────
    private Program()
    {
        _loopEngine = new VideoLoopEngine(_cameraManager, "Stupipandi - Simulador de Gestos y Reconocimiento Facial");

        // Suscripción a eventos del motor de vídeo
        _loopEngine.OnFrameReceived += ProcessFrame;
        _loopEngine.OnKeyPressed    += HandleKey;
        _loopEngine.OnLoopExited    += () => Console.WriteLine("\n[INFO] Ciclo de video finalizado correctamente.");

        // Notificaciones del hardware de captura
        _cameraManager.OnDeviceError += msg =>
            Console.Error.WriteLine($"[HARDWARE] {msg}");

        // Registro de cambios de gestos
        _gestureClassifier.GestureChanged += (_, e) =>
        {
            if (e.Current != GestureType.Unknown)
            {
                string nombreGesto = OverlayRenderer.FormatGestureName(e.Current);
                Console.WriteLine($"[GESTO] Detectado: {nombreGesto}");
            }
            _stateManager.TryProcessGesture(e.Current);
        };

        // Registro de transiciones de estado
        _stateManager.StateChanged += (_, e) =>
        {
            string usuarioInfo = e.CurrentUser is null ? string.Empty : $" | Usuario: {e.CurrentUser}";
            Console.WriteLine($"[ESTADO] {e.PreviousMode} -> {e.CurrentMode}{usuarioInfo}");
        };

        // Notificación de gestos aceptados en modo interactivo
        _stateManager.GestureAccepted += gesto =>
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[INTERACCION] Gesto aceptado para usuario {_stateManager.CurrentUser}: {gesto}");
            Console.ResetColor();
        };
    }

    // ── Método Principal ─────────────────────────────────────────────────────
    private static int Main()
    {
        Console.Title = "Stupipandi - Simulador de Gestos y Reconocimiento Facial";
        PrintBanner();

        using var app = new Program();

        try
        {
            app.Startup();
            app._loopEngine.Run(); // Bloquea hasta que el usuario presione ESC o Q
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[ERROR CRITICO] {ex.Message}");
            Console.ResetColor();
            return 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[EXCEPCION INESPERADA] {ex}");
            Console.ResetColor();
            return 2;
        }
        finally
        {
            Console.WriteLine("\nPresione cualquier tecla para cerrar...");
            try { Console.ReadKey(intercept: true); } catch { /* sin consola interactiva */ }
        }
    }

    // ── Inicialización del sistema ───────────────────────────────────────────
    private void Startup()
    {
        // 1. Cargar cascadas de Haar
        LogCascadeStatus("Rostro frontal", _faceDetector);
        LogCascadeStatus("Ojos",           _eyeDetector);
        LogCascadeStatus("Sonrisa",        _smileDetector);

        // 2. Cargar modelo biométrico entrenado previamente si existe
        try
        {
            if (_faceRecognizer.Load(FaceModelPath))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[BIOMETRIA] Modelo facial cargado con exito ({_faceRecognizer.SampleCount} muestras registradas).");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[BIOMETRIA] Sin modelo previo. Presione 'T' en la ventana para registrar su rostro.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BIOMETRIA] Advertencia al leer archivo de modelo: {ex.Message}");
        }

        // 3. Iniciar hardware de cámara
        _cameraManager.Start(requestedWidth: 1280, requestedHeight: 720);
        Console.WriteLine($"[CAMARA] Dispositivo listo: {_cameraManager.FrameWidth}x{_cameraManager.FrameHeight}");
        Console.WriteLine("[CONTROL] Controles: ESC/Q=Salir | T=Enrolar rostro | S=Guardar modelo | R=Reiniciar | C=Color");
    }

    private static void LogCascadeStatus(string name, FaceDetector detector)
    {
        if (detector.IsLoaded)
        {
            Console.WriteLine($"[CASCADA] {name}: OK ({Path.GetFileName(detector.ResolvedPath)})");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CASCADA] {name}: NO DISPONIBLE (la funcionalidad operara degradada)");
            Console.ResetColor();
        }
    }

    // ── Procesamiento por fotograma ──────────────────────────────────────────
    private void ProcessFrame(Mat frame)
    {
        _fpsCounter.Tick();
        double currentFps = _fpsCounter.Value;

        // 1. Preprocesamiento optimizado (escala de grises + ecualización de histograma)
        using Mat processed = FramePreprocessor.ProcessPipeline(frame);

        // 2. Detección multiescala de rostros
        Rect[] faces = _faceDetector.DetectMultiScale(
            processed,
            scaleFactor:  1.1,
            minNeighbors: 5,
            flags:        HaarDetectionTypes.ScaleImage,
            minSize:      new Size(60, 60));

        Rect? primaryFace = null;

        if (faces.Length == 0)
        {
            _stateManager.OnNoFaceDetected();
            _gestureClassifier.Reset();
        }
        else
        {
            primaryFace = faces[0];
            Rect clampedFace = ClampRect(primaryFace.Value, frame.Cols, frame.Rows);
            using Mat faceRoi = processed[clampedFace];

            // 3. Reconocimiento biométrico del rostro
            FaceRecognitionResult recResult = _faceRecognizer.Predict(faceRoi);
            _stateManager.UpdateFaceResult(recResult);

            // 4. Muestreo de enrolamiento activo (si el usuario presiono 'T')
            CheckEnrolmentSample(faceRoi);

            // 5. Detección de ojos (en la región superior del rostro)
            List<Rect>? eyeRects = null;
            if (_eyeDetector.IsLoaded)
            {
                int eyeRoiH = (int)(clampedFace.Height * 0.58);
                using Mat eyeRegion = faceRoi[new Rect(0, 0, clampedFace.Width, eyeRoiH)];
                Rect[] eyes = _eyeDetector.DetectMultiScale(
                    eyeRegion,
                    scaleFactor:  1.15,
                    minNeighbors: 3,
                    flags:        HaarDetectionTypes.ScaleImage,
                    minSize:      new Size(16, 16),
                    maxSize:      new Size((int)(clampedFace.Width * 0.42), (int)(eyeRoiH * 0.8)));

                if (eyes.Length > 0)
                {
                    eyeRects = new List<Rect>(eyes.Length);
                    foreach (Rect e in eyes)
                    {
                        eyeRects.Add(new Rect(primaryFace.Value.X + e.X, primaryFace.Value.Y + e.Y, e.Width, e.Height));
                    }
                }
            }

            // 6. Detección de sonrisa (en la región inferior del rostro)
            bool isSmiling = false;
            if (_smileDetector.IsLoaded)
            {
                int smileY = (int)(clampedFace.Height * 0.52);
                int smileH = clampedFace.Height - smileY;
                using Mat smileRegion = faceRoi[new Rect(0, smileY, clampedFace.Width, smileH)];
                Rect[] smiles = _smileDetector.DetectMultiScale(
                    smileRegion,
                    scaleFactor:  1.18,
                    minNeighbors: 18,
                    flags:        HaarDetectionTypes.ScaleImage,
                    minSize:      new Size(28, 16));
                isSmiling = smiles.Length > 0;
            }

            // 7. Renderizado de capas faciales sobre el frame
            foreach (Rect face in faces)
            {
                _overlayRenderer.DrawFace(frame, face, recResult, eyeRects, isSmiling);
            }
        }

        // 8. Detección de gestos mediante segmentación de piel
        using (Mat skinMask = SkinSegmentation.CreateMask(frame, _activeColorSpace))
        {
            // Supresión de región facial para evitar falsos positivos con la cabeza
            foreach (Rect face in faces)
            {
                Rect faceSuppression = ClampRect(
                    new Rect(face.X - 12, face.Y - 12, face.Width + 24, face.Height + 24),
                    skinMask.Cols,
                    skinMask.Rows);
                Cv2.Rectangle(skinMask, faceSuppression, Scalar.Black, -1);
            }

            ContourAnalysis? contour = ContourAnalyzer.Analyze(skinMask, minimumArea: 6_000);
            GestureType currentGesture = _gestureClassifier.Update(contour);

            if (contour is not null && currentGesture != GestureType.Unknown)
            {
                _overlayRenderer.DrawGesture(frame, contour.BoundingBox, currentGesture);
            }
        }

        // 9. Renderizado del HUD informativo y atajos
        _overlayRenderer.DrawHud(
            frame,
            _stateManager.CurrentMode,
            currentFps,
            _stateManager.CurrentUser,
            _stateManager.LastGesture,
            _activeColorSpace.ToString().ToUpperInvariant(),
            _activePrompt);
    }

    private void CheckEnrolmentSample(Mat faceRoi)
    {
        if (_enrolmentTargetSamples > 0 && !string.IsNullOrWhiteSpace(_enrolmentName))
        {
            try
            {
                _faceRecognizer.AddSample(faceRoi, _enrolmentName);
                _enrolmentTargetSamples--;

                if (_enrolmentTargetSamples <= 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[ENROLAMIENTO] Completado con exito para '{_enrolmentName}'. Total de muestras en memoria: {_faceRecognizer.SampleCount}.");
                    Console.WriteLine("[ENROLAMIENTO] Presione 'S' para persistir el modelo a disco.");
                    Console.ResetColor();

                    _activePrompt = $"Enrolado con exito: {_enrolmentName}";
                    _enrolmentName = null;

                    // Despejar el prompt tras 3 segundos
                    System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ => _activePrompt = null);
                }
                else
                {
                    _activePrompt = $"Capturando muestras ({5 - _enrolmentTargetSamples}/5)... Mire a la camara";
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ENROLAMIENTO] Error al procesar muestra: {ex.Message}");
                _enrolmentTargetSamples = 0;
                _activePrompt = null;
            }
        }
    }

    // ── Manejo interactivo de teclado ────────────────────────────────────────
    private void HandleKey(char key)
    {
        switch (char.ToUpperInvariant(key))
        {
            case 'T':
                BeginEnrolment();
                break;

            case 'S':
                SaveModel();
                break;

            case 'R':
                _stateManager.Reset();
                _gestureClassifier.Reset();
                _fpsCounter.Reset();
                _activePrompt = "Sistema reiniciado";
                System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => _activePrompt = null);
                Console.WriteLine("[CONTROL] Estado del sistema reiniciado.");
                break;

            case 'C':
                ToggleColorSpace();
                break;
        }
    }

    private void BeginEnrolment()
    {
        Console.WriteLine("\n──────────────────────────────────────────────────────────");
        Console.Write("[ENROLAMIENTO] Ingrese el nombre de la persona a registrar: ");
        string? name = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[ENROLAMIENTO] Operacion cancelada: nombre vacio.");
            Console.ResetColor();
            return;
        }

        _enrolmentName = name;
        _enrolmentTargetSamples = 5; // Adquiere 5 fotogramas consecutivos de rostro
        _activePrompt = $"Registrando a '{name}'... Mantenga la mirada al frente";
        Console.WriteLine($"[ENROLAMIENTO] Adquiriendo 5 muestras de '{name}'. Mire a la camara...");
    }

    private void SaveModel()
    {
        try
        {
            _faceRecognizer.Save(FaceModelPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[MODELO] Modelo guardado exitosamente en: {FaceModelPath}");
            Console.ResetColor();

            _activePrompt = "Modelo guardado correctamente";
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => _activePrompt = null);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[MODELO] Error al guardar el archivo: {ex.Message}");
            Console.ResetColor();
        }
    }

    private void ToggleColorSpace()
    {
        _activeColorSpace = _activeColorSpace == SkinSegmentation.ColorSpace.Hsv
            ? SkinSegmentation.ColorSpace.YCrCb
            : SkinSegmentation.ColorSpace.Hsv;

        string csName = _activeColorSpace.ToString().ToUpperInvariant();
        Console.WriteLine($"[COLOR] Espacio de color cambiado a: {csName}");

        _activePrompt = $"Espacio de color piel: {csName}";
        System.Threading.Tasks.Task.Delay(1800).ContinueWith(_ => _activePrompt = null);
    }

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, maxW - 1);
        int y = Math.Clamp(rect.Y, 0, maxH - 1);
        int w = Math.Clamp(rect.Width,  1, maxW - x);
        int h = Math.Clamp(rect.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }

    // ── Liberación de recursos ───────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loopEngine.OnFrameReceived -= ProcessFrame;
        _loopEngine.OnKeyPressed    -= HandleKey;

        _loopEngine.Dispose();
        _cameraManager.Dispose();
        _faceDetector.Dispose();
        _eyeDetector.Dispose();
        _smileDetector.Dispose();
    }

    // ── Banner visual en consola ─────────────────────────────────────────────
    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  ==============================================================
    STUPIPANDI - SIMULADOR DE GESTOS Y RECONOCIMIENTO FACIAL
    Plataforma: .NET 10 | OpenCvSharp4 4.13 | Vision por Computador
  ==============================================================");
        Console.ResetColor();
    }
}
