using System;
using GestureFaceSimulator.Biometrics;

namespace GestureFaceSimulator.State;

public enum ApplicationMode
{
    AwaitingFace,
    GestureInteraction,
    AccessDenied,
    TrainingRequired,
    Exiting
}

public sealed class ApplicationStateChangedEventArgs : EventArgs
{
    public ApplicationStateChangedEventArgs(
        ApplicationMode previousMode,
        ApplicationMode currentMode,
        string? currentUser)
    {
        PreviousMode = previousMode;
        CurrentMode = currentMode;
        CurrentUser = currentUser;
    }

    public ApplicationMode PreviousMode { get; }
    public ApplicationMode CurrentMode { get; }
    public string? CurrentUser { get; }
}

/// <summary>
/// Controla el paso de autenticación facial a interacción por gestos.
/// No depende del enum GestureType de Persona 2: admite cualquier enum o string.
/// </summary>
public sealed class ApplicationStateManager
{
    public ApplicationMode CurrentMode { get; private set; } = ApplicationMode.AwaitingFace;
    public string? CurrentUser { get; private set; }
    public string? LastGesture { get; private set; }

    public event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;
    public event Action<string>? GestureAccepted;

    /// <summary>
    /// Actualiza el estado con el resultado de reconocer el rostro actual.
    /// </summary>
    public void UpdateFaceResult(FaceRecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.ModelIsReady)
        {
            SetState(ApplicationMode.TrainingRequired, null);
            return;
        }

        if (result.IsRecognized)
        {
            SetState(ApplicationMode.GestureInteraction, result.Label);
            return;
        }

        SetState(ApplicationMode.AccessDenied, null);
    }

    /// <summary>
    /// Se llama cuando el detector no encuentra ningún rostro en el frame.
    /// </summary>
    public void OnNoFaceDetected()
    {
        if (CurrentMode != ApplicationMode.Exiting)
            SetState(ApplicationMode.AwaitingFace, null);
    }

    /// <summary>
    /// Acepta el gesto únicamente cuando hay una persona autenticada.
    /// Persona 2 puede pasar su GestureType mediante gesture.ToString().
    /// </summary>
    public bool TryProcessGesture(string? gesture)
    {
        if (CurrentMode != ApplicationMode.GestureInteraction || string.IsNullOrWhiteSpace(gesture))
            return false;

        LastGesture = gesture.Trim();
        GestureAccepted?.Invoke(LastGesture);
        return true;
    }

    /// <summary>
    /// Sobrecarga para usar directamente el enum GestureType de Persona 2.
    /// </summary>
    public bool TryProcessGesture<TGesture>(TGesture gesture)
        where TGesture : struct, Enum
    {
        return TryProcessGesture(gesture.ToString());
    }

    public void Reset()
    {
        LastGesture = null;
        SetState(ApplicationMode.AwaitingFace, null);
    }

    public void Exit()
    {
        SetState(ApplicationMode.Exiting, null);
    }

    private void SetState(ApplicationMode newMode, string? user)
    {
        if (CurrentMode == newMode && string.Equals(CurrentUser, user, StringComparison.Ordinal))
            return;

        ApplicationMode previousMode = CurrentMode;
        CurrentMode = newMode;
        CurrentUser = user;

        StateChanged?.Invoke(
            this,
            new ApplicationStateChangedEventArgs(previousMode, newMode, user));
    }
}
