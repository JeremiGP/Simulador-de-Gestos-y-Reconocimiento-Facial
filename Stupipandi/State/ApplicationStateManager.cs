using System;
using Stupipandi.Biometrics;

namespace Stupipandi.State;

/// <summary>
/// Modos de operación y seguridad del sistema.
/// </summary>
public enum ApplicationMode
{
    AwaitingFace,
    GestureInteraction,
    AccessDenied,
    TrainingRequired,
    Exiting
}

/// <summary>
/// Argumentos de evento para cambios de estado en la máquina de acceso.
/// </summary>
public sealed class ApplicationStateChangedEventArgs : EventArgs
{
    public ApplicationMode PreviousMode { get; }
    public ApplicationMode CurrentMode { get; }
    public string? CurrentUser { get; }
    public int ConfidencePercent { get; }

    public ApplicationStateChangedEventArgs(
        ApplicationMode previousMode,
        ApplicationMode currentMode,
        string? currentUser,
        int confidencePercent = 0)
    {
        PreviousMode = previousMode;
        CurrentMode = currentMode;
        CurrentUser = currentUser;
        ConfidencePercent = confidencePercent;
    }
}

/// <summary>
/// Gestor de estado de acceso y autenticación biométrica con filtro anti-flicker y gracia de pérdida temporal de rostro.
/// </summary>
public sealed class ApplicationStateManager
{
    private readonly int _faceLossGraceFrames;
    private int _missedFaceCounter;

    public ApplicationMode CurrentMode { get; private set; } = ApplicationMode.AwaitingFace;
    public string? CurrentUser { get; private set; }
    public string? LastGesture { get; private set; }
    public int CurrentConfidence { get; private set; }

    public event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;
    public event Action<string>? GestureAccepted;

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="ApplicationStateManager"/>.
    /// </summary>
    /// <param name="faceLossGraceFrames">Cantidad de fotogramas de gracia antes de salir de modo interactivo si se pierde el rostro (ej. 10).</param>
    public ApplicationStateManager(int faceLossGraceFrames = 10)
    {
        _faceLossGraceFrames = Math.Max(2, faceLossGraceFrames);
    }

    /// <summary>
    /// Actualiza el estado del sistema con el resultado biométrico estabilizado.
    /// </summary>
    public void UpdateFaceResult(FaceRecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _missedFaceCounter = 0;
        CurrentConfidence = result.ConfidencePercent;

        if (!result.ModelIsReady)
        {
            SetState(ApplicationMode.TrainingRequired, null, 0);
            return;
        }

        if (result.IsRecognized)
        {
            SetState(ApplicationMode.GestureInteraction, result.Label, result.ConfidencePercent);
            return;
        }

        SetState(ApplicationMode.AccessDenied, null, 0);
    }

    /// <summary>
    /// Notifica que no se detectó ningún rostro en el fotograma actual.
    /// Aplica una ventana de gracia antes de revertir a <see cref="ApplicationMode.AwaitingFace"/>
    /// para evitar parpadeos causados por oclusiones momentáneas.
    /// </summary>
    public void OnNoFaceDetected()
    {
        if (CurrentMode == ApplicationMode.Exiting) return;

        _missedFaceCounter++;

        // Solo revertir a Esperando Rostro si se superan los fotogramas de gracia
        if (_missedFaceCounter >= _faceLossGraceFrames)
        {
            SetState(ApplicationMode.AwaitingFace, null, 0);
            LastGesture = null;
        }
    }

    /// <summary>
    /// Acepta y procesa un gesto únicamente cuando el sistema se encuentra en modo de interacción autorizado.
    /// </summary>
    public bool TryProcessGesture(string? gesture)
    {
        if (CurrentMode != ApplicationMode.GestureInteraction || string.IsNullOrWhiteSpace(gesture) || gesture == "None")
        {
            return false;
        }

        string cleanGesture = gesture.Trim();
        if (LastGesture != cleanGesture)
        {
            LastGesture = cleanGesture;
            GestureAccepted?.Invoke(LastGesture);
        }

        return true;
    }

    /// <summary>
    /// Sobrecarga para procesar directamente enumeraciones de gestos.
    /// </summary>
    public bool TryProcessGesture<TGesture>(TGesture gesture)
        where TGesture : struct, Enum
    {
        return TryProcessGesture(gesture.ToString());
    }

    /// <summary>
    /// Reinicia la máquina de estados.
    /// </summary>
    public void Reset()
    {
        _missedFaceCounter = 0;
        LastGesture = null;
        CurrentConfidence = 0;
        SetState(ApplicationMode.AwaitingFace, null, 0);
    }

    /// <summary>
    /// Marca el sistema para terminación.
    /// </summary>
    public void Exit() => SetState(ApplicationMode.Exiting, null, 0);

    private void SetState(ApplicationMode newMode, string? user, int confidence)
    {
        if (CurrentMode == newMode && string.Equals(CurrentUser, user, StringComparison.Ordinal))
        {
            CurrentConfidence = confidence;
            return;
        }

        ApplicationMode previousMode = CurrentMode;
        CurrentMode = newMode;
        CurrentUser = user;
        CurrentConfidence = confidence;

        StateChanged?.Invoke(this, new ApplicationStateChangedEventArgs(previousMode, newMode, user, confidence));
    }
}
