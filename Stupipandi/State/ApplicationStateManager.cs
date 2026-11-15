using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stupipandi.Biometrics;
using Stupipandi.Gestures;

namespace Stupipandi.State;

public enum ApplicationMode
{
    AwaitingFace,
    GestureInteraction,
    AccessDenied,
    TrainingRequired,
    Exiting
}

public enum ActiveTrigger
{
    None,
    Trigger1_LionRoar,
    Trigger2_HandsOnHead,
    Trigger3_HighNoon,
    Trigger4_PrayingPalms,
    Trigger5_SteepledFingertips,
    Trigger6_MilitarySalute
}

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

public sealed class ApplicationStateManager
{
    private readonly int _faceLossGraceFrames;
    private int _missedFaceCounter;

    private ActiveTrigger _currentTrigger = ActiveTrigger.None;
    private long _triggerEndTicks = 0;
    private readonly Dictionary<ActiveTrigger, long> _cooldowns = new();

    private int _handsOnHeadConsecutiveFrames = 0;
    private int _militarySaluteConsecutiveFrames = 0;

    public ApplicationMode CurrentMode { get; private set; } = ApplicationMode.AwaitingFace;
    public string? CurrentUser { get; private set; }
    public string? LastGesture { get; private set; }
    public int CurrentConfidence { get; private set; }

    public ActiveTrigger CurrentTrigger => _currentTrigger;

    public double TriggerRemainingSeconds
    {
        get
        {
            if (_currentTrigger == ActiveTrigger.None) return 0.0;
            long remaining = _triggerEndTicks - Stopwatch.GetTimestamp();
            return remaining > 0 ? (double)remaining / Stopwatch.Frequency : 0.0;
        }
    }

    public event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;
    public event Action<string>? GestureAccepted;
    public event Action<ActiveTrigger>? TriggerActivated;
    public event Action<ActiveTrigger>? TriggerDeactivated;

    public ApplicationStateManager(int faceLossGraceFrames = 10)
    {
        _faceLossGraceFrames = Math.Max(2, faceLossGraceFrames);
    }

    public ActiveTrigger EvaluateTriggers(
        IReadOnlyList<TrackedFace> trackedFaces,
        GestureType stableGesture,
        ExpressionResult? primaryExpression)
    {
        long nowTicks = Stopwatch.GetTimestamp();

        // 1. Verificar si hay un disparador activo en curso
        if (_currentTrigger != ActiveTrigger.None)
        {
            if (nowTicks >= _triggerEndTicks)
            {
                ActiveTrigger old = _currentTrigger;
                _currentTrigger = ActiveTrigger.None;
                _cooldowns[old] = nowTicks + (long)(Stopwatch.Frequency * 2.0);
                TriggerDeactivated?.Invoke(old);
            }
            else
            {
                if (_currentTrigger == ActiveTrigger.Trigger3_HighNoon && stableGesture == GestureType.FingerGun)
                {
                    _triggerEndTicks = nowTicks + (long)(Stopwatch.Frequency * 1.5);
                }

                return _currentTrigger;
            }
        }

        // 2. Contadores con decaimiento suave (evita perder el conteo por 1 frame de parpadeo)
        if (stableGesture == GestureType.HandsOnHead)
        {
            _handsOnHeadConsecutiveFrames++;
        }
        else
        {
            _handsOnHeadConsecutiveFrames = Math.Max(0, _handsOnHeadConsecutiveFrames - 1);
        }

        if (stableGesture == GestureType.MilitarySalute)
        {
            _militarySaluteConsecutiveFrames++;
        }
        else
        {
            _militarySaluteConsecutiveFrames = Math.Max(0, _militarySaluteConsecutiveFrames - 1);
        }

        // 3. Disparador 1: Lion Roar Meme (2 rostros reales, uno rugiendo hacia el otro)
        if (trackedFaces.Count == 2 && IsCooldownExpired(ActiveTrigger.Trigger1_LionRoar, nowTicks))
        {
            var face1 = trackedFaces[0];
            var face2 = trackedFaces[1];

            bool face1Roars = face1.LastExpression?.IsRoaring == true;
            bool face2Roars = face2.LastExpression?.IsRoaring == true;

            bool face1LookingAt2 = face1.SmoothedBounds.X < face2.SmoothedBounds.X
                ? face1.Orientation == FaceOrientation.LookingRight || face1.Orientation == FaceOrientation.Frontal
                : face1.Orientation == FaceOrientation.LookingLeft || face1.Orientation == FaceOrientation.Frontal;

            bool face2LookingAt1 = face2.SmoothedBounds.X < face1.SmoothedBounds.X
                ? face2.Orientation == FaceOrientation.LookingRight || face2.Orientation == FaceOrientation.Frontal
                : face2.Orientation == FaceOrientation.LookingLeft || face2.Orientation == FaceOrientation.Frontal;

            if ((face1Roars && face1LookingAt2) || (face2Roars && face2LookingAt1))
            {
                ActivateTrigger(ActiveTrigger.Trigger1_LionRoar, durationSeconds: 5.0, nowTicks);
                return _currentTrigger;
            }
        }

        // 4. Disparador 2: Manos en la cabeza (>= 4 fotogramas estabilizados)
        if (_handsOnHeadConsecutiveFrames >= 4 && IsCooldownExpired(ActiveTrigger.Trigger2_HandsOnHead, nowTicks))
        {
            _handsOnHeadConsecutiveFrames = 0;
            ActivateTrigger(ActiveTrigger.Trigger2_HandsOnHead, durationSeconds: 4.0, nowTicks);
            return _currentTrigger;
        }

        // 5. Disparador 6: Saludo militar (>= 5 fotogramas estabilizados)
        if (_militarySaluteConsecutiveFrames >= 5 && IsCooldownExpired(ActiveTrigger.Trigger6_MilitarySalute, nowTicks))
        {
            _militarySaluteConsecutiveFrames = 0;
            ActivateTrigger(ActiveTrigger.Trigger6_MilitarySalute, durationSeconds: 8.3, nowTicks);
            return _currentTrigger;
        }

        // 6. Disparador 3: High Noon
        if (stableGesture == GestureType.FingerGun && IsCooldownExpired(ActiveTrigger.Trigger3_HighNoon, nowTicks))
        {
            ActivateTrigger(ActiveTrigger.Trigger3_HighNoon, durationSeconds: 2.0, nowTicks);
            return _currentTrigger;
        }

        // 7. Disparador 4: Praying Palms
        if (stableGesture == GestureType.PrayingPalms && IsCooldownExpired(ActiveTrigger.Trigger4_PrayingPalms, nowTicks))
        {
            ActivateTrigger(ActiveTrigger.Trigger4_PrayingPalms, durationSeconds: 5.0, nowTicks);
            return _currentTrigger;
        }

        // 8. Disparador 5: Steepled Fingertips (Hava Nagila)
        if (stableGesture == GestureType.SteepledFingertips && IsCooldownExpired(ActiveTrigger.Trigger5_SteepledFingertips, nowTicks))
        {
            ActivateTrigger(ActiveTrigger.Trigger5_SteepledFingertips, durationSeconds: 6.0, nowTicks);
            return _currentTrigger;
        }

        return ActiveTrigger.None;
    }

    public void ActivateTrigger(ActiveTrigger trigger, double durationSeconds, long nowTicks = 0)
    {
        if (nowTicks == 0) nowTicks = Stopwatch.GetTimestamp();

        _currentTrigger = trigger;
        _triggerEndTicks = nowTicks + (long)(Stopwatch.Frequency * Math.Max(0.5, durationSeconds));
        TriggerActivated?.Invoke(trigger);
    }

    public void CancelCurrentTrigger()
    {
        if (_currentTrigger != ActiveTrigger.None)
        {
            ActiveTrigger old = _currentTrigger;
            _currentTrigger = ActiveTrigger.None;
            _triggerEndTicks = 0;
            TriggerDeactivated?.Invoke(old);
        }
    }

    private bool IsCooldownExpired(ActiveTrigger trigger, long nowTicks)
    {
        if (!_cooldowns.TryGetValue(trigger, out long expireTicks))
            return true;

        return nowTicks >= expireTicks;
    }

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

    public void OnNoFaceDetected()
    {
        if (CurrentMode == ApplicationMode.Exiting) return;

        _missedFaceCounter++;
        if (_missedFaceCounter >= _faceLossGraceFrames)
        {
            SetState(ApplicationMode.AwaitingFace, null, 0);
            LastGesture = null;
        }
    }

    public bool TryProcessGesture(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture) || gesture == "None")
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

    public bool TryProcessGesture<TGesture>(TGesture gesture)
        where TGesture : struct, Enum
    {
        return TryProcessGesture(gesture.ToString());
    }

    public void Reset()
    {
        _missedFaceCounter = 0;
        LastGesture = null;
        CurrentConfidence = 0;
        _handsOnHeadConsecutiveFrames = 0;
        _militarySaluteConsecutiveFrames = 0;
        CancelCurrentTrigger();
        _cooldowns.Clear();
        SetState(ApplicationMode.AwaitingFace, null, 0);
    }

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