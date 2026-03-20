using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DapMod.Core;

public partial class MainMod : MelonMod
{
    private static MainMod? _instance;
    private static readonly string[] GameplayBlockTargets =
    {
        "Il2CppScheduleOne.Combat.PunchController:UpdateInput",
        "Il2CppScheduleOne.Combat.PunchController:Release",
        "Il2CppScheduleOne.Combat.PunchController:Punch",
        "Il2CppScheduleOne.PlayerScripts.Player:SendPunch",
        "Il2CppScheduleOne.PlayerScripts.Player:Punch",
        "Il2CppScheduleOne.Interaction.InteractionManager:CheckInteraction",
        "Il2CppScheduleOne.Interaction.InteractionManager:CheckRightClick"
    };

    // -----------------------------
    // Tuning
    // -----------------------------

    private const float RayDistance = 5f;
    private const float MaxDapStartDistance = 2.10f;   // true player <-> NPC distance
    private const float MinNpcFacingDot = 0.15f;
    private const float DapSessionTimeout = 8f;
    private const float DapRetryCooldown = 0.75f;
    private const float DapMaxPlayerDriftDistance = 0.85f;
    private const float DapMaxNpcDriftDistance = 0.60f;
    private const float PostDapInputBlockDuration = 0.15f;
    private const float PostDapConversationDelay = 0.18f;
    private const float DeferredSaveDelay = 0.20f;
    private const float DapResultBannerDuration = 2.75f;
    private const double PerformanceLogThresholdMs = 8.0;
    private const int PerfectDapXpReward = 5;
    private const float PerfectDapFriendshipReward = 0.04f;
    private const string AudioCueFolderName = "DapModAudio";
    private const string DailyXpStateFileName = "DapModDailyXp.txt";
    private const string DailySuccessfulDapStateFileName = "DapModDailyDaps.txt";
    private const string PerfectAudioFileName = "perfect_dap.wav";
    private const string GoodAudioFileName = "good_dap.wav";
    private const string BadAudioFileName = "bad_dap.wav";
    private const int AnimationProbeMaxItems = 12;
    private const float OverlayIntroDuration = 0.16f;
    private const float AudioWarmupDelay = 1.00f;
    private static readonly bool EnableRuntimeAudio = false;

    // Virtual dap cursor / minigame
    private const float DapCursorSpeed = 1.65f;
    private const float PerfectZoneRadius = 0.05f;
    private const float GoodZoneRadius = 0.11f;
    private const float CursorAssistRadius = 0.18f;
    private const float CursorAssistStrength = 1.25f;

    // Screen-space locations
    private static readonly Vector2 DapCursorStart = new(0.85f, 0.50f);
    private static readonly Vector2 PerfectZoneCenter = new(0.55f, 0.50f);

    // Lightweight overlay sizing
    private const float OverlayScale = 208f;
    private const float CursorPixelSize = 10f;
    private const float CenterDotSize = 6f;

    private static readonly bool VerboseLogging = false;

    // -----------------------------
    // Types
    // -----------------------------

    private enum DapResult
    {
        Perfect,
        Good,
        Miss
    }

    private sealed class BehaviourState
    {
        public Behaviour Behaviour = null!;
        public bool Enabled;
    }

    private sealed class AnimatorState
    {
        public Animator Animator = null!;
        public float Speed;
    }

    private sealed class RigidbodyState
    {
        public Rigidbody Rigidbody = null!;
        public RigidbodyConstraints Constraints;
        public bool IsKinematic;
        public bool UseGravity;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
    }

    private sealed class ReflectionPropertyState
    {
        public PropertyInfo Property = null!;
        public object? Value;
    }

    private sealed class ReflectionMethodCallState
    {
        public MethodInfo Method = null!;
        public object?[] Arguments = Array.Empty<object?>();
    }

    private sealed class ReflectionComponentState
    {
        public Component Component = null!;
        public readonly List<ReflectionPropertyState> PropertyStates = new();
        public readonly List<ReflectionMethodCallState> RestoreCalls = new();
    }

    private sealed class PlayerActionSnapshot
    {
        public Transform PlayerRoot = null!;
        public readonly List<BehaviourState> Behaviours = new();
        public readonly List<ReflectionComponentState> ReflectionStates = new();
    }

    private sealed class NpcPauseSnapshot
    {
        public Transform NpcRoot = null!;
        public Vector3 LockedPosition;
        public Quaternion LockedRotation;
        public readonly List<BehaviourState> Behaviours = new();
        public readonly List<AnimatorState> Animators = new();
        public readonly List<RigidbodyState> Rigidbodies = new();
        public readonly List<ReflectionComponentState> ReflectionStates = new();
    }

    // -----------------------------
    // State
    // -----------------------------

    private bool _dapActive = false;
    private Transform? _currentNpcTarget = null;
    private float _dapStartTime = -1f;
    private float _nextAllowedDapTime = 0f;
    private Vector3 _dapOriginPlayerPosition = Vector3.zero;
    private Vector3 _dapOriginNpcPosition = Vector3.zero;

    private bool _minigameActive = false;
    private Vector2 _dapCursor = Vector2.zero;
    private float _dapOverlayStartTime = -1f;
    private float _pendingPlayerRestoreTime = -1f;

    private Transform? _cachedPlayerRoot = null;
    private Component? _cachedPlayerComponent;
    private Component? _currentNpcComponent;
    private object? _currentNpcRelationData;
    private string? _currentNpcRewardKey;
    private PlayerActionSnapshot? _playerActionSnapshot = null;
    private NpcPauseSnapshot? _npcPauseSnapshot = null;
    private readonly Dictionary<int, Component> _npcComponentCache = new();
    private readonly Dictionary<int, string> _npcRewardKeyCache = new();
    private Type? _cachedNpcType;
    private Type? _cachedLevelManagerType;

    // Overlay textures
    private Texture2D? _whiteTex;
    private Texture2D? _blackTex;
    private Texture2D? _cursorTex;
    private Texture2D? _perfectTex;
    private Texture2D? _goodTex;
    private Texture2D? _panelTex;
    private GUIStyle? _overlayPanelStyle;
    private GUIStyle? _overlayAreaStyle;
    private GUIStyle? _overlayOutlineStyle;
    private GUIStyle? _overlayTitleStyle;
    private GUIStyle? _overlayHintStyle;
    private GUIStyle? _overlayMicroStyle;
    private GUIStyle? _overlayTagStyle;
    private GUIStyle? _overlayCursorStyle;
    private GUIStyle? _overlayTargetStyle;
    private GUIStyle? _resultTitleStyle;
    private GUIStyle? _resultDetailStyle;
    private string? _dapResultTitle;
    private string? _dapResultDetail;
    private float _dapResultDisplayUntil = -1f;
    private Color _dapResultAccent = Color.white;
    private GameObject? _audioHostObject;
    private AudioSource? _audioSource;
    private bool _audioWarmupComplete;
    private readonly Dictionary<string, AudioClip> _audioClipCache = new(StringComparer.OrdinalIgnoreCase);
    private Component? _cachedLevelManager;
    private float _nextLevelManagerLookupAllowedTime;
    private Component? _cachedGameDayProvider;
    private Component? _cachedInteractionManager;
    private string? _cachedGameDayMemberName;
    private bool _attemptedGameDayProviderDiscovery;
    private bool _warnedAboutDayFallback;
    private bool _consumeInteractionUntilRelease;
    private bool _allowSyntheticInteraction;
    private Transform? _pendingDapNpc;
    private float _pendingDapHitDistance = -1f;
    private Transform? _pendingConversationNpc;
    private float _pendingConversationStartTime = -1f;
    private bool _pendingSaveSuccessfulDapState;
    private bool _pendingSaveDailyXpState;
    private float _pendingSaveNotBeforeTime = -1f;
    private readonly HashSet<string> _missingAudioCueWarningsShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedAnimationProbeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _npcLastXpRewardDayByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _npcLastSuccessfulDapDayByKey = new(StringComparer.OrdinalIgnoreCase);
    private Animator? _activeNpcGestureAnimator;
    private string? _npcStartGestureTrigger;
    private string? _npcSuccessGestureTrigger;
    private string? _npcFailGestureTrigger;

    public override void OnInitializeMelon()
    {
        _instance = this;
        InstallHarmonyPatches();
        EnsureAudioPlaceholderDirectory();
        LoadDailyXpRewardState();
        LoadSuccessfulDapState();
        MelonLogger.Msg("DapMod loaded successfully. [Visible Minigame + Action Suppression Build]");
    }

    public override void OnUpdate()
    {
        UpdatePendingPlayerRestore();
        UpdateConsumedInteractionState();
        UpdateQueuedDapStart();
        FlushPendingStateSaves();
        UpdateDeferredConversation();
        WarmAudioCacheIfNeeded();

        if (Input.GetKeyDown(KeyCode.H))
        {
            CancelDapSession("Manual cancel.");
        }

        if (Input.GetKeyDown(KeyCode.K))
        {
            HandleAnimationProbeInput();
        }

        if (_minigameActive && Input.GetKeyDown(KeyCode.J))
        {
            float distanceToPerfect = Vector2.Distance(_dapCursor, PerfectZoneCenter);
            MelonLogger.Msg(
                $"Cursor Debug -> X: {_dapCursor.x:F3}, Y: {_dapCursor.y:F3}, DistToPerfect: {distanceToPerfect:F3}");
        }

        if (_dapActive && Time.time - _dapStartTime >= DapSessionTimeout)
        {
            CancelDapSession("Session timed out.");
        }

        if (_dapActive && _currentNpcTarget != null)
        {
            Transform? playerReference = GetPlayerReferenceTransform();
            if (playerReference != null)
            {
                FaceNpcTowardPlayer(playerReference, _currentNpcTarget);

                if (HasBrokenDapRange(playerReference, _currentNpcTarget))
                {
                    CancelDapSession("Moved too far away from the dap.");
                    return;
                }
            }

            MaintainNpcLock();
        }

        UpdateDapMinigame();
    }

    public override void OnGUI()
    {
        if (Event.current == null)
        {
            return;
        }

        EnsureOverlayTextures();
        GUI.depth = -1000;

        if (_minigameActive)
        {
            DrawDapOverlay();
        }

        if (HasVisibleDapResultBanner())
        {
            DrawDapResultBanner();
        }
    }

    private void UpdateConsumedInteractionState()
    {
        if (_consumeInteractionUntilRelease && !Input.GetKey(KeyCode.E))
        {
            _consumeInteractionUntilRelease = false;
        }
    }

    private void UpdateQueuedDapStart()
    {
        if (_pendingDapNpc == null || _dapActive)
        {
            return;
        }

        Transform pendingNpc = _pendingDapNpc;
        float pendingHitDistance = _pendingDapHitDistance;
        _pendingDapNpc = null;
        _pendingDapHitDistance = -1f;

        Transform? playerReference = GetPlayerReferenceTransform();
        if (playerReference == null)
        {
            MelonLogger.Warning("Could not find player reference for queued dap start.");
            return;
        }

        if (pendingNpc == null)
        {
            return;
        }

        float playerToNpcDistance = Vector3.Distance(playerReference.position, pendingNpc.position);
        if (playerToNpcDistance > MaxDapStartDistance)
        {
            return;
        }

        StartDapSession(playerReference, pendingNpc, pendingHitDistance, playerToNpcDistance);
    }

    private void UpdateDeferredConversation()
    {
        if (_pendingConversationNpc == null)
        {
            return;
        }

        if (_pendingConversationStartTime >= 0f && Time.time < _pendingConversationStartTime)
        {
            return;
        }

        if (_dapActive || _pendingPlayerRestoreTime >= 0f || _consumeInteractionUntilRelease)
        {
            return;
        }

        Transform pendingNpc = _pendingConversationNpc;
        _pendingConversationNpc = null;
        _pendingConversationStartTime = -1f;

        if (!TryTriggerConversation(pendingNpc))
        {
            MelonLogger.Warning($"Could not auto-start conversation after dap with {pendingNpc.name}. Press E to talk normally.");
        }
    }

    private void FlushPendingStateSaves()
    {
        if (!_pendingSaveSuccessfulDapState && !_pendingSaveDailyXpState)
        {
            return;
        }

        if (_pendingSaveNotBeforeTime >= 0f && Time.time < _pendingSaveNotBeforeTime)
        {
            return;
        }

        if (_pendingSaveSuccessfulDapState)
        {
            SaveSuccessfulDapState();
            _pendingSaveSuccessfulDapState = false;
        }

        if (_pendingSaveDailyXpState)
        {
            SaveDailyXpRewardState();
            _pendingSaveDailyXpState = false;
        }

        _pendingSaveNotBeforeTime = -1f;
    }

    // -----------------------------
    // Session flow
    // -----------------------------

    private bool HandleInteractionCheck(object? interactionManagerInstance)
    {
        if (interactionManagerInstance is Component interactionManagerComponent)
        {
            _cachedInteractionManager = interactionManagerComponent;

            Transform? interactionDerivedRoot = ResolvePlayerRootFromTransform(interactionManagerComponent.transform, allowLooseFallback: true);
            if (interactionDerivedRoot != null)
            {
                _cachedPlayerRoot = interactionDerivedRoot;
            }
        }

        if (_allowSyntheticInteraction)
        {
            return true;
        }

        if (_consumeInteractionUntilRelease)
        {
            return false;
        }

        if (!Input.GetKeyDown(KeyCode.E))
        {
            return true;
        }

        if (Time.time < _nextAllowedDapTime)
        {
            return false;
        }

        if (_dapActive)
        {
            return false;
        }

        if (!TryGetDappableNpcTarget(out Transform npcRoot, out float hitDistance))
        {
            return true;
        }

        Transform? playerReference = GetPlayerReferenceTransform();
        if (playerReference == null)
        {
            MelonLogger.Warning("Could not find player reference.");
            return true;
        }

        float playerToNpcDistance = Vector3.Distance(playerReference.position, npcRoot.position);
        if (playerToNpcDistance > MaxDapStartDistance)
        {
            return true;
        }

        if (!IsNpcFacingPlayer(playerReference, npcRoot))
        {
            return true;
        }

        _consumeInteractionUntilRelease = true;
        _pendingDapNpc = npcRoot;
        _pendingDapHitDistance = hitDistance;
        return false;
    }

    private void HandleAnimationProbeInput()
    {
        Transform? playerReference = GetPlayerReferenceTransform();
        if (playerReference == null)
        {
            MelonLogger.Warning("Animation probe failed: could not find player reference.");
            return;
        }

        if (TryGetDappableNpcTarget(out Transform npcRoot, out _))
        {
            ProbeAnimationOptions(playerReference, npcRoot, true);
            return;
        }

        MelonLogger.Msg("Animation probe: aim at a valid NPC and press K.");
    }

    private void StartDapSession(Transform playerReference, Transform npcRoot, float hitDistance, float playerToNpcDistance)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        double cacheMs = 0d;
        double playerSuppressMs = 0d;
        double npcPauseMs = 0d;

        try
        {
            _dapActive = true;
            _currentNpcTarget = npcRoot;
            _dapStartTime = Time.time;
            _dapOriginPlayerPosition = playerReference.position;
            _dapOriginNpcPosition = npcRoot.position;
            CacheRewardTargetsForSession(npcRoot);
            PrepareNpcGestureProfile(npcRoot);
            cacheMs = stopwatch.Elapsed.TotalMilliseconds;

            Transform playerActionRoot = FindLocalPlayerRoot() ?? playerReference;
            SuppressPlayerActions(playerActionRoot);
            playerSuppressMs = stopwatch.Elapsed.TotalMilliseconds - cacheMs;
            PauseNpcBehavior(npcRoot);
            npcPauseMs = stopwatch.Elapsed.TotalMilliseconds - cacheMs - playerSuppressMs;
            FaceNpcTowardPlayer(playerReference, npcRoot);
            MaintainNpcLock();
            PlayNpcGestureCue(NpcGestureCue.Start);
            BeginDapMinigame();
            stopwatch.Stop();

            MelonLogger.Msg(
                $"Dap started with {npcRoot.name} | dist {playerToNpcDistance:F2}m | setup {stopwatch.Elapsed.TotalMilliseconds:F1}ms");

            if (stopwatch.Elapsed.TotalMilliseconds >= PerformanceLogThresholdMs)
            {
                MelonLogger.Warning(
                    $"Dap setup hitch detected: {stopwatch.Elapsed.TotalMilliseconds:F1}ms (cache {cacheMs:F1}ms / player {playerSuppressMs:F1}ms / npc {npcPauseMs:F1}ms)");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            EmergencyResetDapState("Dap setup failed.", ex);
        }
    }

    private void BeginDapMinigame()
    {
        _minigameActive = true;
        _dapCursor = DapCursorStart;
        _dapOverlayStartTime = Time.time;
        _dapResultDisplayUntil = -1f;
        _dapResultTitle = null;
        _dapResultDetail = null;

        if (VerboseLogging)
        {
            MelonLogger.Msg(
                $"Dap minigame active | cursor {_dapCursor.x:F2},{_dapCursor.y:F2} | perfect radius {PerfectZoneRadius:F2} | good radius {GoodZoneRadius:F2}");
        }
    }

    private void UpdateDapMinigame()
    {
        if (!_minigameActive)
        {
            return;
        }

        float mouseX = Input.GetAxis("Mouse X");
        float mouseY = Input.GetAxis("Mouse Y");

        Vector2 delta = new Vector2(mouseX, -mouseY) * DapCursorSpeed * Time.deltaTime;
        _dapCursor += delta;

        float distanceToCenter = Vector2.Distance(_dapCursor, PerfectZoneCenter);
        if (distanceToCenter <= CursorAssistRadius && distanceToCenter > 0.0001f)
        {
            Vector2 towardCenter = (PerfectZoneCenter - _dapCursor).normalized;
            float assistFactor = 1f - (distanceToCenter / CursorAssistRadius);
            _dapCursor += towardCenter * (CursorAssistStrength * assistFactor * Time.deltaTime);
        }

        _dapCursor.x = Mathf.Clamp01(_dapCursor.x);
        _dapCursor.y = Mathf.Clamp01(_dapCursor.y);

        if (Input.GetMouseButtonDown(0))
        {
            TryCompleteDap();
        }
    }

    private void TryCompleteDap()
    {
        if (!_dapActive || !_minigameActive)
        {
            return;
        }

        float distanceToPerfect = Vector2.Distance(_dapCursor, PerfectZoneCenter);
        DapResult result = EvaluateDapResult(distanceToPerfect);

        if (VerboseLogging)
        {
            MelonLogger.Msg(
                $"Dap attempt | cursor {_dapCursor.x:F3},{_dapCursor.y:F3} | dist {distanceToPerfect:F3} | result {result}");
        }

        switch (result)
        {
            case DapResult.Perfect:
            case DapResult.Good:
                CompleteDapSuccess(result);
                break;

            default:
                CancelDapSession("Missed the dap timing.");
                break;
        }
    }

    private DapResult EvaluateDapResult(float distanceToPerfect)
    {
        if (distanceToPerfect <= PerfectZoneRadius)
        {
            return DapResult.Perfect;
        }

        if (distanceToPerfect <= GoodZoneRadius)
        {
            return DapResult.Good;
        }

        return DapResult.Miss;
    }

    private void CompleteDapSuccess(DapResult result)
    {
        int xpAwarded = 0;
        float friendshipAwarded = 0f;
        Stopwatch stopwatch = Stopwatch.StartNew();
        double rewardMs = 0d;
        double resetMs = 0d;

        try
        {
            MelonLogger.Msg($"Dap success with {_currentNpcTarget?.name ?? "<unknown>"} | {result}");

            Transform? successfulNpc = _currentNpcTarget;
            ApplyDapRewards(result, out xpAwarded, out friendshipAwarded);
            rewardMs = stopwatch.Elapsed.TotalMilliseconds;
            PlayNpcGestureCue(NpcGestureCue.Success);
            ShowDapResultBanner(result, xpAwarded, friendshipAwarded);
            if (successfulNpc != null)
            {
                _pendingConversationNpc = successfulNpc;
                _pendingConversationStartTime = Time.time + PostDapConversationDelay;
            }

            _nextAllowedDapTime = Time.time + DapRetryCooldown;
            ResetDapState();
            resetMs = stopwatch.Elapsed.TotalMilliseconds - rewardMs;
            stopwatch.Stop();

            QueueDapAudioPlaceholder(result);

            if (stopwatch.Elapsed.TotalMilliseconds >= PerformanceLogThresholdMs)
            {
                MelonLogger.Warning(
                    $"Dap completion hitch detected: {stopwatch.Elapsed.TotalMilliseconds:F1}ms (rewards {rewardMs:F1}ms / reset {resetMs:F1}ms)");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            EmergencyResetDapState("Dap completion failed.", ex);
        }
    }

    private void CancelDapSession(string reason)
    {
        if (!_dapActive)
        {
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            MelonLogger.Msg($"Dap cancelled with {_currentNpcTarget?.name ?? "<unknown>"} | {reason}");

            PlayNpcGestureCue(NpcGestureCue.Fail);
            ShowCancelledDapBanner(reason);
            _nextAllowedDapTime = Time.time + DapRetryCooldown;
            ResetDapState();
            stopwatch.Stop();

            QueueDapAudioPlaceholder(DapResult.Miss);

            if (stopwatch.Elapsed.TotalMilliseconds >= PerformanceLogThresholdMs)
            {
                MelonLogger.Warning($"Dap cancel hitch detected: {stopwatch.Elapsed.TotalMilliseconds:F1}ms");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            EmergencyResetDapState("Dap cancellation failed.", ex);
        }
    }

    private void ResetDapState()
    {
        SchedulePlayerActionRestore();
        RestoreNpcBehavior();
        ClearDapSessionState();
    }

    private void ClearDapSessionState()
    {
        _dapActive = false;
        _currentNpcTarget = null;
        _dapStartTime = -1f;
        _dapOriginPlayerPosition = Vector3.zero;
        _dapOriginNpcPosition = Vector3.zero;
        _minigameActive = false;
        _dapCursor = Vector2.zero;
        _dapOverlayStartTime = -1f;
        _currentNpcComponent = null;
        _currentNpcRelationData = null;
        _currentNpcRewardKey = null;
        _activeNpcGestureAnimator = null;
        _npcStartGestureTrigger = null;
        _npcSuccessGestureTrigger = null;
        _npcFailGestureTrigger = null;
    }

    private void EmergencyResetDapState(string reason, Exception ex)
    {
        MelonLogger.Error($"{reason} {ex}");

        try
        {
            RestorePlayerActions();
        }
        catch
        {
            // ignored
        }

        try
        {
            RestoreNpcBehavior();
        }
        catch
        {
            // ignored
        }

        ClearDapSessionState();
        _nextAllowedDapTime = Time.time + DapRetryCooldown;
        SetDapResultBanner("dap error", $"{reason} +0 xp   +0.00 friendship", new Color(1f, 0.42f, 0.42f, 1f));
        QueueDapAudioPlaceholder(DapResult.Miss);
    }

    private static bool ShouldBlockGameplayActions()
    {
        return _instance != null &&
               !_instance._allowSyntheticInteraction &&
               (_instance._dapActive ||
                _instance._pendingPlayerRestoreTime >= 0f ||
                _instance._consumeInteractionUntilRelease);
    }

    private void InstallHarmonyPatches()
    {
        HarmonyMethod prefix = new HarmonyMethod(typeof(MainMod).GetMethod(
            nameof(BlockGameplayActionsPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)!);

        foreach (string target in GameplayBlockTargets)
        {
            MethodBase? method = ResolveGameplayBlockTargetMethod(target);
            if (method == null)
            {
                MelonLogger.Warning($"Could not find dap gameplay blocker target: {target}");
                continue;
            }

            HarmonyInstance.Patch(method, prefix: prefix);
        }
    }

    private static bool BlockGameplayActionsPrefix(MethodBase __originalMethod, object? __instance)
    {
        if (_instance != null &&
            __originalMethod.Name.Equals("CheckInteraction", StringComparison.Ordinal))
        {
            return _instance.HandleInteractionCheck(__instance);
        }

        return !ShouldBlockGameplayActions();
    }

    private static MethodBase? ResolveGameplayBlockTargetMethod(string target)
    {
        int separatorIndex = target.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= target.Length - 1)
        {
            return null;
        }

        string typeName = target.Substring(0, separatorIndex);
        string methodName = target[(separatorIndex + 1)..];

        Type? type = ResolveLoadedType(typeName);
        if (type == null && typeName.StartsWith("Il2Cpp", StringComparison.Ordinal))
        {
            type = ResolveLoadedType(typeName.Substring("Il2Cpp".Length));
        }

        if (type == null)
        {
            return null;
        }

        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        foreach (MethodInfo method in methods)
        {
            if (method.Name.Equals(methodName, StringComparison.Ordinal))
            {
                return method;
            }
        }

        return null;
    }

    private static Type? ResolveLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    #if false
    private void ApplyDapRewards(DapResult result, out int xpAwarded, out float friendshipAwarded)
    {
        xpAwarded = 0;
        friendshipAwarded = 0f;

        if (_currentNpcTarget == null)
        {
            return;
        }

        if (result != DapResult.Perfect && result != DapResult.Good)
        {
            return;
        }

        bool friendshipApplied = AwardNpcFriendship(_currentNpcTarget, PerfectDapFriendshipReward);
        bool xpApplied = result == DapResult.Perfect &&
                         TryAwardDailyNpcXp(_currentNpcTarget, PerfectDapXpReward);

        if (result == DapResult.Perfect && xpApplied)
        {
            xpAwarded = PerfectDapXpReward;
        }

        if (friendshipApplied)
        {
            friendshipAwarded = PerfectDapFriendshipReward;
        }

        MelonLogger.Msg(
            $"Dap rewards -> Result: {result}, XP: {(xpApplied ? PerfectDapXpReward.ToString() : "none")}, Friendship: {(friendshipApplied ? PerfectDapFriendshipReward.ToString("F2") : "failed")}");
    }

    private void QueueDapAudioPlaceholder(DapResult result)
    {
        string fileName = result switch
        {
            DapResult.Perfect => PerfectAudioFileName,
            DapResult.Good => GoodAudioFileName,
            _ => BadAudioFileName
        };

        string expectedPath = GetAudioCuePath(fileName);

        try
        {
            if (System.IO.File.Exists(expectedPath))
            {
                if (VerboseLogging)
                {
                    MelonLogger.Msg($"Dap audio placeholder ready: {expectedPath}");
                }

                return;
            }

            if (_missingAudioCueWarningsShown.Add(expectedPath))
            {
                MelonLogger.Warning($"Missing dap audio cue placeholder: {expectedPath}");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not inspect dap audio cue placeholder '{expectedPath}': {ex.Message}");
        }
    }

    private void EnsureAudioPlaceholderDirectory()
    {
        string audioDirectory = GetAudioPlaceholderDirectory();

        try
        {
            System.IO.Directory.CreateDirectory(audioDirectory);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not create dap audio placeholder directory: {ex.Message}");
        }
    }

    private static string GetAudioPlaceholderDirectory()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", AudioCueFolderName);
    }

    private static string GetAudioCuePath(string fileName)
    {
        return System.IO.Path.Combine(GetAudioPlaceholderDirectory(), fileName);
    }

    private bool TryAwardDailyNpcXp(Transform npcRoot, int amount)
    {
        if (amount <= 0)
        {
            return false;
        }

        string npcKey = GetNpcRewardKey(npcRoot);
        int currentDayIndex = GetCurrentRewardDayIndex();

        if (_npcLastXpRewardDayByKey.TryGetValue(npcKey, out int lastRewardDay) &&
            lastRewardDay == currentDayIndex)
        {
            MelonLogger.Msg($"Daily dap XP already claimed for {npcRoot.name} on day {currentDayIndex}.");
            return false;
        }

        if (!AwardPlayerXp(amount))
        {
            return false;
        }

        _npcLastXpRewardDayByKey[npcKey] = currentDayIndex;
        _pendingSaveDailyXpState = true;
        _pendingSaveNotBeforeTime = Time.time + DeferredSaveDelay;
        return true;
    }

    private int GetCurrentRewardDayIndex()
    {
        if (TryGetCurrentGameDayIndex(out int gameDayIndex))
        {
            return gameDayIndex;
        }

        if (!_warnedAboutDayFallback)
        {
            _warnedAboutDayFallback = true;
            MelonLogger.Warning("Could not resolve the in-game day. Falling back to real-world date for daily dap XP limits.");
        }

        return int.Parse(DateTime.UtcNow.ToString("yyyyMMdd"));
    }

    private bool TryGetCurrentGameDayIndex(out int dayIndex)
    {
        dayIndex = -1;

        if (_cachedGameDayProvider != null &&
            !string.IsNullOrEmpty(_cachedGameDayMemberName) &&
            TryReadGameDayFromMemberPath(_cachedGameDayProvider, _cachedGameDayMemberName!, out dayIndex))
        {
            return true;
        }

        if (_attemptedGameDayProviderDiscovery)
        {
            return false;
        }

        _attemptedGameDayProviderDiscovery = true;

        foreach (Component component in GetLikelyGameDayProviderCandidates())
        {
            if (component == null)
            {
                continue;
            }

            if (TryResolveGameDayMemberPath(component, out string memberPath, out dayIndex))
            {
                _cachedGameDayProvider = component;
                _cachedGameDayMemberName = memberPath;
                MelonLogger.Msg($"Resolved game day provider: {GetComponentTypeName(component)} -> {memberPath}");
                return true;
            }
        }

        return false;
    }

    private Component[] GetLikelyGameDayProviderCandidates()
    {
        List<Component> candidates = new List<Component>();

        try
        {
            foreach (Component component in UnityEngine.Object.FindObjectsOfType<Component>(true))
            {
                if (component != null && IsLikelyGameDayProvider(component))
                {
                    candidates.Add(component);
                }
            }
        }
        catch
        {
            try
            {
                foreach (Component component in Resources.FindObjectsOfTypeAll<Component>())
                {
                    if (component != null && IsLikelyGameDayProvider(component))
                    {
                        candidates.Add(component);
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        return candidates.ToArray();
    }

    private static bool IsLikelyGameDayProvider(Component component)
    {
        string typeName = GetComponentTypeName(component);
        return typeName.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Day", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Clock", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Session", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("World", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveGameDayMemberPath(object provider, out string memberPath, out int dayIndex)
    {
        memberPath = string.Empty;
        dayIndex = -1;

        string[] directMembers =
        {
            "CurrentDay",
            "Day",
            "DayNumber",
            "CurrentDayIndex",
            "ElapsedDays",
            "DaysPassed",
            "TotalDays"
        };

        foreach (string memberName in directMembers)
        {
            if (TryReadGameDayFromMemberPath(provider, memberName, out dayIndex))
            {
                memberPath = memberName;
                return true;
            }
        }

        string[] nestedMembers =
        {
            "Time",
            "CurrentTime",
            "Clock",
            "Date",
            "WorldTime",
            "Session",
            "SaveData"
        };

        foreach (string outerMember in nestedMembers)
        {
            object? nested = GetObjectMemberValue(provider, outerMember);
            if (nested == null)
            {
                continue;
            }

            foreach (string innerMember in directMembers)
            {
                string candidatePath = $"{outerMember}.{innerMember}";
                if (TryReadGameDayFromMemberPath(provider, candidatePath, out dayIndex))
                {
                    memberPath = candidatePath;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryReadGameDayFromMemberPath(object provider, string memberPath, out int dayIndex)
    {
        dayIndex = -1;
        object? current = provider;

        string[] segments = memberPath.Split('.');
        foreach (string segment in segments)
        {
            if (current == null)
            {
                return false;
            }

            current = GetObjectMemberValue(current, segment);
        }

        return TryConvertToDayIndex(current, out dayIndex);
    }

    private static bool TryConvertToDayIndex(object? value, out int dayIndex)
    {
        dayIndex = -1;
        if (value == null)
        {
            return false;
        }

        try
        {
            switch (value)
            {
                case int i:
                    dayIndex = i;
                    return true;
                case long l when l >= int.MinValue && l <= int.MaxValue:
                    dayIndex = (int)l;
                    return true;
                case short s:
                    dayIndex = s;
                    return true;
                case byte b:
                    dayIndex = b;
                    return true;
                case float f:
                    dayIndex = Mathf.FloorToInt(f);
                    return true;
                case double d:
                    dayIndex = (int)Math.Floor(d);
                    return true;
                case decimal m:
                    dayIndex = (int)Math.Floor((double)m);
                    return true;
                case string text when int.TryParse(text, out int parsed):
                    dayIndex = parsed;
                    return true;
            }

            if (value.GetType().IsEnum)
            {
                dayIndex = Convert.ToInt32(value);
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private void LoadDailyXpRewardState()
    {
        _npcLastXpRewardDayByKey.Clear();

        string path = GetDailyXpRewardStatePath();
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            string[] lines = System.IO.File.ReadAllLines(path);
            foreach (string rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                string[] parts = rawLine.Split('\t');
                if (parts.Length != 2)
                {
                    continue;
                }

                if (int.TryParse(parts[1], out int dayIndex))
                {
                    _npcLastXpRewardDayByKey[parts[0]] = dayIndex;
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not load dap daily XP state: {ex.Message}");
        }
    }

    private bool HasSuccessfulDapToday(Transform npcRoot)
    {
        string npcKey = GetNpcRewardKey(npcRoot);
        int currentDayIndex = GetCurrentRewardDayIndex();
        return _npcLastSuccessfulDapDayByKey.TryGetValue(npcKey, out int lastSuccessfulDay) &&
               lastSuccessfulDay == currentDayIndex;
    }

    private void MarkSuccessfulDapToday(Transform npcRoot)
    {
        string npcKey = GetNpcRewardKey(npcRoot);
        _npcLastSuccessfulDapDayByKey[npcKey] = GetCurrentRewardDayIndex();
        _pendingSaveSuccessfulDapState = true;
        _pendingSaveNotBeforeTime = Time.time + DeferredSaveDelay;
    }

    private void LoadSuccessfulDapState()
    {
        _npcLastSuccessfulDapDayByKey.Clear();

        string path = GetSuccessfulDapStatePath();
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            string[] lines = System.IO.File.ReadAllLines(path);
            foreach (string rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                string[] parts = rawLine.Split('\t');
                if (parts.Length != 2)
                {
                    continue;
                }

                if (int.TryParse(parts[1], out int dayIndex))
                {
                    _npcLastSuccessfulDapDayByKey[parts[0]] = dayIndex;
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not load dap daily success state: {ex.Message}");
        }
    }

    private void SaveSuccessfulDapState()
    {
        try
        {
            List<string> lines = new List<string>(_npcLastSuccessfulDapDayByKey.Count);
            foreach (KeyValuePair<string, int> kvp in _npcLastSuccessfulDapDayByKey)
            {
                lines.Add($"{kvp.Key}\t{kvp.Value}");
            }

            System.IO.File.WriteAllLines(GetSuccessfulDapStatePath(), lines);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not save dap daily success state: {ex.Message}");
        }
    }

    private void SaveDailyXpRewardState()
    {
        try
        {
            List<string> lines = new List<string>(_npcLastXpRewardDayByKey.Count);
            foreach (KeyValuePair<string, int> kvp in _npcLastXpRewardDayByKey)
            {
                lines.Add($"{kvp.Key}\t{kvp.Value}");
            }

            System.IO.File.WriteAllLines(GetDailyXpRewardStatePath(), lines);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not save dap daily XP state: {ex.Message}");
        }
    }

    private static string GetDailyXpRewardStatePath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", DailyXpStateFileName);
    }

    private static string GetSuccessfulDapStatePath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", DailySuccessfulDapStateFileName);
    }

    private string GetNpcRewardKey(Transform npcRoot)
    {
        Component? npcComponent = FindComponentByTypeName(
            npcRoot.GetComponentsInChildren<Component>(true),
            "Il2CppScheduleOne.NPCs.NPC");

        if (npcComponent != null)
        {
            string[] memberCandidates =
            {
                "ID",
                "NPCID",
                "Guid",
                "GUID",
                "FullName",
                "FirstName",
                "Name"
            };

            foreach (string memberName in memberCandidates)
            {
                object? value = GetObjectMemberValue(npcComponent, memberName);
                if (value != null)
                {
                    string text = value.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
        }

        return BuildTransformPath(npcRoot);
    }

    private bool TryTriggerConversation(Transform npcRoot)
    {
        try
        {
            _allowSyntheticInteraction = true;

            Component? npcComponent = FindComponentByTypeName(
                npcRoot.GetComponentsInChildren<Component>(true),
                "Il2CppScheduleOne.NPCs.NPC");

            if (npcComponent != null &&
                TryInvokeObjectMethodByNames(npcComponent,
                    "Interact",
                    "StartConversation",
                    "OpenConversation",
                    "Talk"))
            {
                return true;
            }

            Component? interactionManager = FindInteractionManager();
            if (interactionManager != null)
            {
                object? interactableTarget = FindInteractionTarget(interactionManager);

                if (TryInvokeObjectMethodByNames(interactionManager,
                        "Interact",
                        "StartInteraction",
                        "AttemptInteract",
                        "TryInteract",
                        "TriggerInteraction",
                        "CheckInteraction"))
                {
                    return true;
                }

                if (interactableTarget != null &&
                    TryInvokeObjectMethodByNames(interactableTarget,
                        "Interact",
                        "StartInteraction",
                        "StartConversation",
                        "OpenConversation",
                        "Talk"))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _allowSyntheticInteraction = false;
        }
    }

    private Component? FindInteractionManager()
    {
        if (_cachedInteractionManager != null)
        {
            return _cachedInteractionManager;
        }

        _cachedInteractionManager = FindFirstLoadedComponentByTypeName("Il2CppScheduleOne.Interaction.InteractionManager") ??
                                    FindFirstLoadedComponentByTypeName("ScheduleOne.Interaction.InteractionManager");
        return _cachedInteractionManager;
    }

    private static object? FindInteractionTarget(object interactionManager)
    {
        string[] memberCandidates =
        {
            "CurrentInteractable",
            "currentInteractable",
            "HoveredInteractable",
            "hoveredInteractable",
            "CurrentTarget",
            "currentTarget",
            "Target"
        };

        foreach (string memberName in memberCandidates)
        {
            object? target = GetObjectMemberValue(interactionManager, memberName);
            if (target != null)
            {
                return target;
            }
        }

        return null;
    }

    private static string BuildTransformPath(Transform transform)
    {
        List<string> segments = new List<string>();
        Transform? current = transform;
        while (current != null)
        {
            segments.Add(current.name ?? "<unnamed>");
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments.ToArray());
    }

    #endif
    private void ProbeAnimationOptions(Transform playerRoot, Transform npcRoot, bool forceLog)
    {
        string probeKey = $"{playerRoot.name}->{npcRoot.name}";
        if (!forceLog && !_loggedAnimationProbeKeys.Add(probeKey))
        {
            return;
        }

        MelonLogger.Msg("=== Dap Animation Probe ===");
        LogAnimatorSummary("Player", playerRoot);
        LogAnimatorSummary("NPC", npcRoot);
        MelonLogger.Msg("Press K while aiming at an NPC to rescan animation options.");
        MelonLogger.Msg("===========================");
    }

    private void LogAnimatorSummary(string label, Transform root)
    {
        Animator[] animators = root.GetComponentsInChildren<Animator>(true);
        MelonLogger.Msg($"{label} animator count: {animators.Length}");

        for (int i = 0; i < animators.Length; i++)
        {
            Animator animator = animators[i];
            if (animator == null)
            {
                continue;
            }

            List<string> parameterNames = CollectAnimatorParameterHints(animator);
            List<string> clipNames = CollectAnimatorClipHints(animator);

            string parameterText = parameterNames.Count > 0 ? string.Join(", ", parameterNames.ToArray()) : "<none>";
            string clipText = clipNames.Count > 0 ? string.Join(", ", clipNames.ToArray()) : "<none>";

            MelonLogger.Msg($"{label} Animator[{i}] {animator.name} params: {parameterText}");
            MelonLogger.Msg($"{label} Animator[{i}] {animator.name} clips: {clipText}");
        }
    }

    private List<string> CollectAnimatorParameterHints(Animator animator)
    {
        List<string> likely = new List<string>();
        List<string> fallback = new List<string>();

        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            string descriptor = $"{parameter.name}:{parameter.type}";
            if (IsLikelyDapAnimationName(parameter.name))
            {
                AddUniqueLimited(likely, descriptor, AnimationProbeMaxItems);
            }
            else
            {
                AddUniqueLimited(fallback, descriptor, AnimationProbeMaxItems);
            }
        }

        return likely.Count > 0 ? likely : fallback;
    }

    private List<string> CollectAnimatorClipHints(Animator animator)
    {
        List<string> likely = new List<string>();
        List<string> fallback = new List<string>();

        RuntimeAnimatorController? controller = animator.runtimeAnimatorController;
        AnimationClip[] clips = controller != null ? controller.animationClips : Array.Empty<AnimationClip>();
        foreach (AnimationClip clip in clips)
        {
            if (clip == null)
            {
                continue;
            }

            if (IsLikelyDapAnimationName(clip.name))
            {
                AddUniqueLimited(likely, clip.name, AnimationProbeMaxItems);
            }
            else
            {
                AddUniqueLimited(fallback, clip.name, AnimationProbeMaxItems);
            }
        }

        return likely.Count > 0 ? likely : fallback;
    }

    private static bool IsLikelyDapAnimationName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("greet", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("wave", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("shake", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("hand", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("hello", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("interact", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("talk", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("gesture", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("emote", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("dap", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUniqueLimited(List<string> values, string value, int maxItems)
    {
        if (values.Count >= maxItems || values.Contains(value))
        {
            return;
        }

        values.Add(value);
    }

    #if false
    private void ShowDapResultBanner(DapResult result, int xpAwarded, float friendshipAwarded)
    {
        switch (result)
        {
            case DapResult.Perfect:
                SetDapResultBanner(
                    "perfect dap",
                    $"+{xpAwarded} xp   +{friendshipAwarded:F2} friendship",
                    new Color(0.60f, 1f, 0.82f, 1f));
                break;

            case DapResult.Good:
                SetDapResultBanner(
                    "good dap",
                    $"+0 xp   +{friendshipAwarded:F2} friendship",
                    new Color(1f, 0.86f, 0.58f, 1f));
                break;

            default:
                SetDapResultBanner(
                    "dap",
                    "+0 xp   +0.00 friendship",
                    new Color(0.93f, 0.95f, 0.98f, 1f));
                break;
        }
    }

    private void ShowCancelledDapBanner(string reason)
    {
        SetDapResultBanner(
            "dap failed",
            $"{reason}  +0 xp   +0.00 friendship",
            new Color(1f, 0.58f, 0.58f, 1f));
    }

    private void SetDapResultBanner(string title, string detail, Color accent)
    {
        _dapResultTitle = title;
        _dapResultDetail = detail;
        _dapResultAccent = accent;
        _dapResultDisplayUntil = Time.time + DapResultBannerDuration;
    }

    private bool HasVisibleDapResultBanner()
    {
        return !string.IsNullOrEmpty(_dapResultTitle) &&
               !string.IsNullOrEmpty(_dapResultDetail) &&
               _dapResultDisplayUntil >= 0f &&
               Time.time <= _dapResultDisplayUntil;
    }

    private bool AwardPlayerXp(int amount)
    {
        if (amount <= 0)
        {
            return false;
        }

        Component? levelManager = _cachedLevelManager;
        if (levelManager == null)
        {
            levelManager = FindFirstLoadedComponentByTypeName("Il2CppScheduleOne.Levelling.LevelManager");
            _cachedLevelManager = levelManager;
        }

        if (levelManager == null)
        {
            MelonLogger.Warning("Could not find active LevelManager for dap XP reward.");
            return false;
        }

        if (TryInvokeObjectMethod(levelManager, "AddXP", amount) ||
            TryInvokeObjectMethod(levelManager, "AddXPLocal", amount) ||
            TryInvokeObjectMethod(levelManager, "AddExperience", amount) ||
            TryInvokeObjectMethod(levelManager, "GiveXP", amount) ||
            TryInvokeObjectMethod(levelManager, "GiveExperience", amount) ||
            TryInvokeObjectMethod(levelManager, "RewardXP", amount))
        {
            return true;
        }

        if (TryAdjustNumericMember(levelManager, amount,
                "XP",
                "CurrentXP",
                "Experience",
                "CurrentExperience",
                "TotalXP",
                "TotalExperience"))
        {
            return true;
        }

        MelonLogger.Warning("Could not apply dap XP reward because LevelManager XP methods were not callable.");
        return false;
    }

    private bool AwardNpcFriendship(Transform npcRoot, float amount)
    {
        if (amount <= 0f)
        {
            return false;
        }

        Component? npcComponent = FindComponentByTypeName(
            npcRoot.GetComponentsInChildren<Component>(true),
            "Il2CppScheduleOne.NPCs.NPC");

        if (npcComponent == null)
        {
            MelonLogger.Warning("Could not find NPC component for dap friendship reward.");
            return false;
        }

        object? relationData = GetObjectMemberValue(npcComponent, "RelationData");
        if (relationData == null)
        {
            MelonLogger.Warning("Could not find NPC RelationData for dap friendship reward.");
            return false;
        }

        if (TryInvokeObjectMethod(relationData, "ChangeRelationship", amount, true) ||
            TryInvokeObjectMethod(relationData, "ChangeRelationship", amount) ||
            TryInvokeObjectMethod(relationData, "AddRelationship", amount) ||
            TryInvokeObjectMethod(relationData, "ModifyRelationship", amount) ||
            TryInvokeObjectMethod(relationData, "AddFriendship", amount) ||
            TryInvokeObjectMethod(relationData, "ChangeFriendship", amount))
        {
            return true;
        }

        if (TryAdjustNumericMember(relationData, amount,
                "Relationship",
                "CurrentRelationship",
                "Friendship",
                "CurrentFriendship",
                "FriendshipLevel",
                "Value"))
        {
            return true;
        }

        MelonLogger.Warning("Could not apply dap friendship reward because no relationship member or method was callable.");
        return false;
    }

    #endif
    // -----------------------------
    // Targeting / player lookup
    // -----------------------------

    #if false
    private bool TryGetDappableNpcTarget(out Transform npcRoot, out float hitDistance)
    {
        npcRoot = null!;
        hitDistance = -1f;

        Camera? camera = Camera.main;
        if (camera == null)
        {
            MelonLogger.Warning("No main camera found.");
            return false;
        }

        Ray ray = new Ray(camera.transform.position, camera.transform.forward);

        if (!Physics.Raycast(ray, out RaycastHit hit, RayDistance))
        {
            return false;
        }

        Transform hitTransform = hit.collider.transform;
        Transform rootTransform = hitTransform.root;

        string rootName = rootTransform.name ?? "<null>";
        int rootLayer = rootTransform.gameObject.layer;

        hitDistance = hit.distance;

        if (VerboseLogging)
        {
            MelonLogger.Msg("=== Dap Filter Probe ===");
            MelonLogger.Msg($"Hit: {hitTransform.name}");
            MelonLogger.Msg($"Root: {rootName}");
            MelonLogger.Msg($"Root Layer: {rootLayer}");
            MelonLogger.Msg($"Hit Distance: {hitDistance:F2}");
            MelonLogger.Msg("========================");
        }

        if (rootName.StartsWith("Tripod ("))
        {
            return false;
        }

        if (rootLayer != 11)
        {
            return false;
        }

        npcRoot = rootTransform;
        return true;
    }

    private Transform? FindLocalPlayerRoot()
    {
        if (_cachedPlayerRoot != null)
        {
            try
            {
                if (_cachedPlayerRoot.gameObject != null)
                {
                    return _cachedPlayerRoot;
                }
            }
            catch
            {
                _cachedPlayerRoot = null;
            }
        }

        Camera? mainCamera = Camera.main;
        if (mainCamera != null)
        {
            Transform? cameraDerivedRoot = ResolvePlayerRootFromTransform(mainCamera.transform, allowLooseFallback: true);
            if (cameraDerivedRoot != null)
            {
                _cachedPlayerRoot = cameraDerivedRoot;
                MelonLogger.Msg($"Cached player root from camera: {_cachedPlayerRoot.name}");
                return _cachedPlayerRoot;
            }
        }

        Component? playerComponent = FindFirstLoadedComponentByTypeName("Il2CppScheduleOne.PlayerScripts.Player") ??
                                     FindFirstLoadedComponentByTypeName("ScheduleOne.PlayerScripts.Player");
        if (playerComponent != null)
        {
            Transform? playerComponentRoot = ResolvePlayerRootFromTransform(playerComponent.transform, allowLooseFallback: true);
            if (playerComponentRoot != null)
            {
                _cachedPlayerRoot = playerComponentRoot;
                MelonLogger.Msg($"Cached player root from player component: {_cachedPlayerRoot.name}");
                return _cachedPlayerRoot;
            }
        }

        Transform[] allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (Transform transform in allTransforms)
        {
            Transform? resolvedRoot = ResolvePlayerRootFromTransform(transform, allowLooseFallback: false);
            if (resolvedRoot == null)
            {
                continue;
            }

            _cachedPlayerRoot = resolvedRoot;
            MelonLogger.Msg($"Cached player root from transform scan: {_cachedPlayerRoot.name}");
            return _cachedPlayerRoot;
        }

        return null;
    }

    private Transform? ResolvePlayerRootFromTransform(Transform? source, bool allowLooseFallback)
    {
        if (source == null)
        {
            return null;
        }

        Transform current = source;
        while (current.parent != null)
        {
            current = current.parent;
        }

        if (IsLikelyPlayerRoot(current))
        {
            return current;
        }

        Transform root = source.root;
        if (root != null && IsLikelyPlayerRoot(root))
        {
            return root;
        }

        Transform walker = source;
        while (walker != null)
        {
            if (IsLikelyPlayerRoot(walker))
            {
                return walker;
            }

            walker = walker.parent;
        }

        if (allowLooseFallback)
        {
            Transform? looseFallback = FindLoosePlayerAncestor(source);
            if (looseFallback != null)
            {
                return looseFallback;
            }

            return root != null ? root : source;
        }

        return null;
    }

    private static Transform? FindLoosePlayerAncestor(Transform source)
    {
        Transform? walker = source;
        while (walker != null)
        {
            string name = walker.name ?? string.Empty;
            if (name.Contains("Tripod", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CameraContainer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("BodyContainer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Player", StringComparison.OrdinalIgnoreCase))
            {
                return walker;
            }

            walker = walker.parent;
        }

        return null;
    }

    private bool IsLikelyPlayerRoot(Transform transform)
    {
        string name = transform.name ?? string.Empty;
        if (name.StartsWith("Tripod (", StringComparison.Ordinal))
        {
            return true;
        }

        Component[] components = transform.GetComponentsInChildren<Component>(true);
        foreach (Component component in components)
        {
            if (component == null)
            {
                continue;
            }

            string fullName = component.GetType().FullName ?? string.Empty;
            if (fullName.Equals("Il2CppScheduleOne.PlayerScripts.Player", StringComparison.Ordinal) ||
                fullName.Equals("ScheduleOne.PlayerScripts.Player", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsNpcFacingPlayer(Transform playerRoot, Transform npcRoot)
    {
        Vector3 npcToPlayer = playerRoot.position - npcRoot.position;
        npcToPlayer.y = 0f;

        if (npcToPlayer.sqrMagnitude <= 0.0001f)
        {
            return true;
        }

        npcToPlayer.Normalize();

        Vector3 npcForward = npcRoot.forward;
        npcForward.y = 0f;

        if (npcForward.sqrMagnitude <= 0.0001f)
        {
            return false;
        }

        npcForward.Normalize();

        float dot = Vector3.Dot(npcForward, npcToPlayer);

        if (VerboseLogging)
        {
            MelonLogger.Msg($"NPC Facing Dot: {dot:F2}");
        }

        return dot >= MinNpcFacingDot;
    }

    private void FaceNpcTowardPlayer(Transform playerRoot, Transform npcRoot)
    {
        Vector3 npcToPlayer = playerRoot.position - npcRoot.position;
        npcToPlayer.y = 0f;

        if (npcToPlayer.sqrMagnitude > 0.0001f)
        {
            Quaternion npcRotation = Quaternion.LookRotation(npcToPlayer.normalized);
            npcRoot.rotation = npcRotation;

            if (_npcPauseSnapshot != null && _npcPauseSnapshot.NpcRoot == npcRoot)
            {
                _npcPauseSnapshot.LockedRotation = npcRotation;
            }
        }
    }

    private bool HasBrokenDapRange(Transform playerRoot, Transform npcRoot)
    {
        float playerDrift = Vector3.Distance(playerRoot.position, _dapOriginPlayerPosition);
        if (playerDrift > DapMaxPlayerDriftDistance)
        {
            if (VerboseLogging)
            {
                MelonLogger.Msg($"Dap failed: player drifted {playerDrift:F2}m from origin.");
            }

            return true;
        }

        float npcDrift = Vector3.Distance(npcRoot.position, _dapOriginNpcPosition);
        if (npcDrift > DapMaxNpcDriftDistance)
        {
            if (VerboseLogging)
            {
                MelonLogger.Msg($"Dap failed: NPC drifted {npcDrift:F2}m from origin.");
            }

            return true;
        }

        float currentDistance = Vector3.Distance(playerRoot.position, npcRoot.position);
        return currentDistance > MaxDapStartDistance;
    }

    #endif
    // -----------------------------
    // Player action suppression
    // -----------------------------

    private void SuppressPlayerActions(Transform playerRoot)
    {
        RestorePlayerActions();
        _pendingPlayerRestoreTime = -1f;

        PlayerActionSnapshot snapshot = new PlayerActionSnapshot
        {
            PlayerRoot = playerRoot
        };

        Behaviour[] behaviours = GetPlayerActionBehaviours(playerRoot);
        foreach (Behaviour behaviour in behaviours)
        {
            if (behaviour == null)
            {
                continue;
            }

            if (behaviour.enabled && behaviour is not Animator)
            {
                snapshot.Behaviours.Add(new BehaviourState
                {
                    Behaviour = behaviour,
                    Enabled = behaviour.enabled
                });

                behaviour.enabled = false;
            }

            ReflectionComponentState? reflectionState = CapturePlayerActionComponentState(behaviour);
            if (HasReflectionChanges(reflectionState))
            {
                snapshot.ReflectionStates.Add(reflectionState!);
            }
        }

        _playerActionSnapshot = snapshot;
    }

    private void SchedulePlayerActionRestore()
    {
        if (_playerActionSnapshot == null)
        {
            _pendingPlayerRestoreTime = -1f;
            return;
        }

        _pendingPlayerRestoreTime = Time.time + PostDapInputBlockDuration;
    }

    private void UpdatePendingPlayerRestore()
    {
        if (_pendingPlayerRestoreTime < 0f)
        {
            return;
        }

        if (Time.time < _pendingPlayerRestoreTime)
        {
            return;
        }

        if (Input.GetMouseButton(0))
        {
            return;
        }

        RestorePlayerActions();
    }

    private void RestorePlayerActions()
    {
        if (_playerActionSnapshot == null)
        {
            _pendingPlayerRestoreTime = -1f;
            return;
        }

        RestoreReflectionProperties(_playerActionSnapshot.ReflectionStates);
        RestoreBehaviourStates(_playerActionSnapshot.Behaviours);
        InvokeRestoreMethods(_playerActionSnapshot.ReflectionStates);

        _playerActionSnapshot = null;
        _pendingPlayerRestoreTime = -1f;
    }

    private Behaviour[] GetPlayerActionBehaviours(Transform playerRoot)
    {
        List<Behaviour> filtered = new List<Behaviour>();
        Behaviour[] behaviours = playerRoot.GetComponentsInChildren<Behaviour>(true);
        foreach (Behaviour behaviour in behaviours)
        {
            if (behaviour != null && IsPlayerActionComponent(behaviour))
            {
                filtered.Add(behaviour);
            }
        }

        return filtered.ToArray();
    }

    private ReflectionComponentState? CapturePlayerActionComponentState(Component component)
    {
        ReflectionComponentState? state = null;

        TrySnapshotProperty(component, ref state, "PunchingEnabled", false);
        TrySnapshotProperty(component, ref state, "CanDestroy", false);
        TrySnapshotProperty(component, ref state, "CanInteractWhenEquipped", false);
        TrySnapshotProperty(component, ref state, "CanPickUpWhenEquipped", false);

        return state;
    }

    private static bool IsPlayerActionComponent(Component component)
    {
        string fullName = GetComponentTypeName(component);

        return fullName.StartsWith("Il2CppScheduleOne.Combat.", StringComparison.Ordinal) ||
               fullName.StartsWith("Il2CppScheduleOne.Equipping.", StringComparison.Ordinal) ||
               fullName.StartsWith("Il2CppScheduleOne.Interaction.", StringComparison.Ordinal) ||
               fullName.StartsWith("Il2CppScheduleOne.AvatarFramework.Equipping.", StringComparison.Ordinal);
    }

    // -----------------------------
    // NPC pause / restore
    // -----------------------------

    private void PauseNpcBehavior(Transform npcRoot)
    {
        RestoreNpcBehavior();

        NpcPauseSnapshot snapshot = new NpcPauseSnapshot
        {
            NpcRoot = npcRoot,
            LockedPosition = npcRoot.position,
            LockedRotation = npcRoot.rotation
        };

        Animator[] animators = npcRoot.GetComponentsInChildren<Animator>(true);
        foreach (Animator animator in animators)
        {
            snapshot.Animators.Add(new AnimatorState
            {
                Animator = animator,
                Speed = animator.speed
            });

            if (animator != _activeNpcGestureAnimator)
            {
                animator.speed = 0f;
            }
        }

        Rigidbody[] rigidbodies = npcRoot.GetComponentsInChildren<Rigidbody>(true);
        foreach (Rigidbody rb in rigidbodies)
        {
            snapshot.Rigidbodies.Add(new RigidbodyState
            {
                Rigidbody = rb,
                Constraints = rb.constraints,
                IsKinematic = rb.isKinematic,
                UseGravity = rb.useGravity,
                Velocity = rb.velocity,
                AngularVelocity = rb.angularVelocity
            });

            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        Behaviour[] behaviours = npcRoot.GetComponentsInChildren<Behaviour>(true);
        foreach (Behaviour behaviour in behaviours)
        {
            if (behaviour == null || behaviour is Animator)
            {
                continue;
            }

            if (!IsNpcControlComponent(behaviour))
            {
                continue;
            }

            ReflectionComponentState? reflectionState = CaptureNpcControlComponentState(behaviour);

            if (behaviour.enabled)
            {
                snapshot.Behaviours.Add(new BehaviourState
                {
                    Behaviour = behaviour,
                    Enabled = behaviour.enabled
                });

                behaviour.enabled = false;
            }

            if (HasReflectionChanges(reflectionState))
            {
                snapshot.ReflectionStates.Add(reflectionState!);
            }
        }

        _npcPauseSnapshot = snapshot;
    }

    private void RestoreNpcBehavior()
    {
        if (_npcPauseSnapshot == null)
        {
            return;
        }

        RestoreReflectionProperties(_npcPauseSnapshot.ReflectionStates);
        RestoreBehaviourStates(_npcPauseSnapshot.Behaviours);
        InvokeRestoreMethods(_npcPauseSnapshot.ReflectionStates);

        foreach (AnimatorState state in _npcPauseSnapshot.Animators)
        {
            if (state.Animator != null)
            {
                state.Animator.speed = state.Speed;
            }
        }

        foreach (RigidbodyState state in _npcPauseSnapshot.Rigidbodies)
        {
            if (state.Rigidbody != null)
            {
                state.Rigidbody.constraints = state.Constraints;
                state.Rigidbody.isKinematic = state.IsKinematic;
                state.Rigidbody.useGravity = state.UseGravity;
                state.Rigidbody.velocity = state.Velocity;
                state.Rigidbody.angularVelocity = state.AngularVelocity;
            }
        }

        _npcPauseSnapshot = null;
    }

    private void MaintainNpcLock()
    {
        if (_npcPauseSnapshot == null || _npcPauseSnapshot.NpcRoot == null)
        {
            return;
        }

        _npcPauseSnapshot.NpcRoot.position = _npcPauseSnapshot.LockedPosition;
        _npcPauseSnapshot.NpcRoot.rotation = _npcPauseSnapshot.LockedRotation;

        foreach (RigidbodyState state in _npcPauseSnapshot.Rigidbodies)
        {
            if (state.Rigidbody == null)
            {
                continue;
            }

            state.Rigidbody.velocity = Vector3.zero;
            state.Rigidbody.angularVelocity = Vector3.zero;
        }
    }

    private ReflectionComponentState? CaptureNpcControlComponentState(Component component)
    {
        ReflectionComponentState? state = null;
        string fullName = GetComponentTypeName(component);

        if (fullName.Equals("Il2CppScheduleOne.NPCs.NPCMovement", StringComparison.Ordinal))
        {
            TrySnapshotProperty(component, ref state, "IsPaused", true);
            TrySnapshotProperty(component, ref state, "ObstacleAvoidanceEnabled", false);
            TrySnapshotProperty(component, ref state, "MoveSpeedMultiplier", 0f);
            TrySnapshotProperty(component, ref state, "MovementSpeedScale", 0f);

            TryInvokeMethod(component, "Stop");

            if (TryInvokeMethod(component, "PauseMovement"))
            {
                QueueRestoreMethod(ref state, component, "ResumeMovement");
            }

            if (TryInvokeMethod(component, "SetAgentEnabled", false))
            {
                QueueRestoreMethod(ref state, component, "SetAgentEnabled", true);
            }
        }

        if (fullName.Equals("Il2CppScheduleOne.NPCs.NPCScheduleManager", StringComparison.Ordinal))
        {
            TrySnapshotProperty(component, ref state, "ScheduleEnabled", false);

            if (TryInvokeMethod(component, "DisableSchedule"))
            {
                QueueRestoreMethod(ref state, component, "EnableSchedule");
            }
        }

        if (fullName.StartsWith("Il2CppScheduleOne.NPCs.Behaviour.", StringComparison.Ordinal))
        {
            if (TryInvokeMethod(component, "Pause"))
            {
                QueueRestoreMethod(ref state, component, "Resume");
            }

            if (TryInvokeMethod(component, "Deactivate"))
            {
                QueueRestoreMethod(ref state, component, "Activate");
            }

            TryInvokeMethod(component, "Disable");
        }

        if (fullName.Contains("NavMeshAgent", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("Agent", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("Path", StringComparison.OrdinalIgnoreCase))
        {
            TrySnapshotProperty(component, ref state, "isStopped", true);
            TrySnapshotProperty(component, ref state, "speed", 0f);
            TrySnapshotProperty(component, ref state, "velocity", Vector3.zero);
            TrySnapshotProperty(component, ref state, "angularVelocity", Vector3.zero);
        }

        return state;
    }

    private static bool IsNpcControlComponent(Component component)
    {
        string fullName = GetComponentTypeName(component);

        if (fullName.StartsWith("Il2CppScheduleOne.NPCs.Behaviour.", StringComparison.Ordinal) ||
            fullName.StartsWith("Il2CppScheduleOne.NPCs.Schedules.", StringComparison.Ordinal) ||
            fullName.StartsWith("Il2CppScheduleOne.Tools.NPCWalkTo", StringComparison.Ordinal))
        {
            return true;
        }

        if (fullName.StartsWith("Il2CppScheduleOne.NPCs.", StringComparison.Ordinal))
        {
            return fullName.Contains("Movement", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Schedule", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Action", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Behaviour", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Awareness", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Patrol", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Walk", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Wander", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("Locomotion", StringComparison.OrdinalIgnoreCase) ||
                   fullName.Contains("SpeedController", StringComparison.OrdinalIgnoreCase);
        }

        return fullName.Contains("NavMeshAgent", StringComparison.OrdinalIgnoreCase) ||
               fullName.Contains("Agent", StringComparison.OrdinalIgnoreCase) ||
               fullName.Contains("Mover", StringComparison.OrdinalIgnoreCase) ||
               fullName.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
               fullName.Contains("Locomotion", StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------
    // Reflection helpers
    // -----------------------------

    #if false
    private static string GetComponentTypeName(Component component)
    {
        Type type = component.GetType();
        return type.FullName ?? type.Name;
    }

    private static bool HasReflectionChanges(ReflectionComponentState? state)
    {
        return state != null && (state.PropertyStates.Count > 0 || state.RestoreCalls.Count > 0);
    }

    private static void RestoreBehaviourStates(List<BehaviourState> states)
    {
        foreach (BehaviourState state in states)
        {
            if (state.Behaviour != null)
            {
                state.Behaviour.enabled = state.Enabled;
            }
        }
    }

    private static void RestoreReflectionProperties(List<ReflectionComponentState> states)
    {
        foreach (ReflectionComponentState state in states)
        {
            foreach (ReflectionPropertyState propertyState in state.PropertyStates)
            {
                try
                {
                    MethodInfo? setter = propertyState.Property.GetSetMethod(true);
                    setter?.Invoke(state.Component, new[] { propertyState.Value });
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static void InvokeRestoreMethods(List<ReflectionComponentState> states)
    {
        foreach (ReflectionComponentState state in states)
        {
            foreach (ReflectionMethodCallState restoreCall in state.RestoreCalls)
            {
                try
                {
                    restoreCall.Method.Invoke(state.Component, restoreCall.Arguments);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static bool TrySnapshotProperty(Component component, ref ReflectionComponentState? state, string propertyName, object? replacementValue)
    {
        PropertyInfo? property = component.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        MethodInfo? getter = property?.GetGetMethod(true);
        MethodInfo? setter = property?.GetSetMethod(true);

        if (property == null || getter == null || setter == null)
        {
            return false;
        }

        try
        {
            object? currentValue = getter.Invoke(component, Array.Empty<object>());

            state ??= new ReflectionComponentState
            {
                Component = component
            };

            state.PropertyStates.Add(new ReflectionPropertyState
            {
                Property = property,
                Value = currentValue
            });

            setter.Invoke(component, new[] { replacementValue });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool QueueRestoreMethod(ref ReflectionComponentState? state, Component component, string methodName, params object?[] args)
    {
        MethodInfo? method = FindCompatibleMethod(component.GetType(), methodName, args);
        if (method == null)
        {
            return false;
        }

        state ??= new ReflectionComponentState
        {
            Component = component
        };

        state.RestoreCalls.Add(new ReflectionMethodCallState
        {
            Method = method,
            Arguments = (object?[])args.Clone()
        });

        return true;
    }

    private static bool TryInvokeMethod(Component component, string methodName, params object?[] args)
    {
        MethodInfo? method = FindCompatibleMethod(component.GetType(), methodName, args);
        if (method == null)
        {
            return false;
        }

        try
        {
            method.Invoke(component, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? FindCompatibleMethod(Type type, string methodName, object?[] args)
    {
        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (MethodInfo method in methods)
        {
            if (!method.Name.Equals(methodName, StringComparison.Ordinal))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != args.Length)
            {
                continue;
            }

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                object? arg = args[i];
                Type parameterType = parameters[i].ParameterType;

                if (arg == null)
                {
                    if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                    {
                        matches = false;
                        break;
                    }

                    continue;
                }

                Type argType = arg.GetType();
                if (!parameterType.IsAssignableFrom(argType))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return method;
            }
        }

        return null;
    }

    private static Component? FindFirstLoadedComponentByTypeName(string fullTypeName)
    {
        try
        {
            Component? behaviourMatch = FindComponentByTypeName(UnityEngine.Object.FindObjectsOfType<Behaviour>(true), fullTypeName);
            if (behaviourMatch != null)
            {
                return behaviourMatch;
            }

            return FindComponentByTypeName(UnityEngine.Object.FindObjectsOfType<Component>(true), fullTypeName);
        }
        catch
        {
            try
            {
                Component? behaviourMatch = FindComponentByTypeName(Resources.FindObjectsOfTypeAll<Behaviour>(), fullTypeName);
                if (behaviourMatch != null)
                {
                    return behaviourMatch;
                }

                return FindComponentByTypeName(Resources.FindObjectsOfTypeAll<Component>(), fullTypeName);
            }
            catch
            {
                return null;
            }
        }
    }

    private static Component? FindComponentByTypeName(Component[] components, string fullTypeName)
    {
        foreach (Component component in components)
        {
            if (component == null)
            {
                continue;
            }

            Type type = component.GetType();
            if (string.Equals(type.FullName, fullTypeName, StringComparison.Ordinal))
            {
                return component;
            }
        }

        return null;
    }

    private static object? GetObjectMemberValue(object instance, string memberName)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        Type type = instance.GetType();
        PropertyInfo? property = type.GetProperty(memberName, Flags);
        MethodInfo? getter = property?.GetGetMethod(true);
        if (getter != null)
        {
            try
            {
                return getter.Invoke(instance, Array.Empty<object>());
            }
            catch
            {
                // ignored
            }
        }

        FieldInfo? field = type.GetField(memberName, Flags);
        if (field != null)
        {
            try
            {
                return field.GetValue(instance);
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static bool TryAdjustNumericMember(object instance, float delta, params string[] memberNames)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        Type type = instance.GetType();
        foreach (string memberName in memberNames)
        {
            PropertyInfo? property = type.GetProperty(memberName, Flags);
            MethodInfo? getter = property?.GetGetMethod(true);
            MethodInfo? setter = property?.GetSetMethod(true);
            if (property != null && getter != null && setter != null &&
                TryConvertAdjustedValue(getter.Invoke(instance, Array.Empty<object>()), delta, property.PropertyType, out object? adjustedValue))
            {
                try
                {
                    setter.Invoke(instance, new[] { adjustedValue });
                    return true;
                }
                catch
                {
                    // ignored
                }
            }

            FieldInfo? field = type.GetField(memberName, Flags);
            if (field != null &&
                TryConvertAdjustedValue(field.GetValue(instance), delta, field.FieldType, out adjustedValue))
            {
                try
                {
                    field.SetValue(instance, adjustedValue);
                    return true;
                }
                catch
                {
                    // ignored
                }
            }
        }

        return false;
    }

    private static bool TryConvertAdjustedValue(object? currentValue, float delta, Type targetType, out object? adjustedValue)
    {
        adjustedValue = null;
        if (currentValue == null)
        {
            return false;
        }

        try
        {
            if (targetType == typeof(float))
            {
                adjustedValue = Convert.ToSingle(currentValue) + delta;
                return true;
            }

            if (targetType == typeof(double))
            {
                adjustedValue = Convert.ToDouble(currentValue) + delta;
                return true;
            }

            if (targetType == typeof(int))
            {
                adjustedValue = Mathf.RoundToInt(Convert.ToSingle(currentValue) + delta);
                return true;
            }

            if (targetType == typeof(long))
            {
                adjustedValue = Convert.ToInt64(Mathf.RoundToInt(Convert.ToSingle(currentValue) + delta));
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static bool TryInvokeObjectMethod(object instance, string methodName, params object?[] args)
    {
        MethodInfo? method = FindCompatibleMethod(instance.GetType(), methodName, args);
        if (method == null)
        {
            return false;
        }

        try
        {
            method.Invoke(instance, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeObjectMethodByNames(object instance, params string[] methodNames)
    {
        foreach (string methodName in methodNames)
        {
            if (TryInvokeObjectMethod(instance, methodName))
            {
                return true;
            }
        }

        return false;
    }

    // -----------------------------
    // Overlay rendering
    // -----------------------------

    private void EnsureOverlayTextures()
    {
        _whiteTex ??= MakeSolidTex(new Color(0.93f, 0.95f, 0.98f, 1f));
        _blackTex ??= MakeSolidTex(new Color(0.05f, 0.07f, 0.10f, 0.78f));
        _cursorTex ??= MakeSolidTex(new Color(0.70f, 0.93f, 1f, 1f));
        _perfectTex ??= MakeSolidTex(new Color(0.60f, 1f, 0.82f, 0.92f));
        _goodTex ??= MakeSolidTex(new Color(1f, 0.86f, 0.58f, 0.80f));
        _panelTex ??= MakeSolidTex(new Color(0.03f, 0.04f, 0.06f, 0.30f));
        _overlayPanelStyle ??= MakePanelStyle(new Color(0.05f, 0.07f, 0.10f, 0.92f));
        _overlayAreaStyle ??= MakePanelStyle(new Color(0.03f, 0.05f, 0.08f, 0.98f));
        _overlayOutlineStyle ??= MakePanelStyle(new Color(0.90f, 0.93f, 0.97f, 0.95f));
        _overlayTitleStyle ??= MakeLabelStyle(18, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        _overlayHintStyle ??= MakeLabelStyle(13, FontStyle.Normal, new Color(0.92f, 0.95f, 0.98f, 0.98f), TextAnchor.MiddleCenter);
        _overlayMicroStyle ??= MakeLabelStyle(11, FontStyle.Normal, new Color(0.74f, 0.80f, 0.88f, 1f), TextAnchor.MiddleCenter);
        _overlayTagStyle ??= MakeLabelStyle(11, FontStyle.Bold, new Color(0.14f, 0.16f, 0.19f, 1f), TextAnchor.MiddleCenter);
        _overlayCursorStyle ??= MakeLabelStyle(26, FontStyle.Bold, new Color(0.70f, 0.93f, 1f, 1f), TextAnchor.MiddleCenter);
        _overlayTargetStyle ??= MakeLabelStyle(28, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        _resultTitleStyle ??= MakeLabelStyle(15, FontStyle.Bold, Color.white);
        _resultDetailStyle ??= MakeLabelStyle(12, FontStyle.Normal, new Color(0.90f, 0.93f, 0.97f, 0.96f));
    }

    private Texture2D MakeSolidTex(Color color)
    {
        Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }

    private void DrawDapOverlay()
    {
        float boxSize = Mathf.Max(252f, OverlayScale + 56f);
        float boxX = (Screen.width * 0.5f) - (boxSize * 0.5f) + 128f;
        float boxY = (Screen.height * 0.5f) - (boxSize * 0.5f) + 10f;

        Rect panelRect = new Rect(boxX - 32f, boxY - 86f, boxSize + 64f, boxSize + 174f);
        Rect areaRect = new Rect(boxX, boxY, boxSize, boxSize);
        Vector2 perfectPoint = NormalizedPointToPixels(PerfectZoneCenter, areaRect);
        Vector2 startPoint = NormalizedPointToPixels(DapCursorStart, areaRect);
        Vector2 cursorPoint = NormalizedPointToPixels(_dapCursor, areaRect);

        DrawOutlinedLabel(new Rect(panelRect.x, panelRect.y + 8f, panelRect.width, 28f), "DAP UP", _overlayTitleStyle!, Color.white);
        DrawOutlinedLabel(new Rect(panelRect.x, panelRect.y + 38f, panelRect.width, 20f), "move the O with your mouse and click on the +", _overlayHintStyle!, Color.white);
        DrawOutlinedLabel(new Rect(panelRect.x, areaRect.yMax + 8f, panelRect.width, 22f), "left click while your cursor is on center", _overlayHintStyle!, Color.white);
        DrawOutlinedLabel(new Rect(panelRect.x, areaRect.yMax + 30f, panelRect.width, 20f), "a clean dap rolls straight into conversation", _overlayMicroStyle!, new Color(0.86f, 0.91f, 0.98f, 1f));

        for (int i = 1; i <= 7; i++)
        {
            float t = i / 8f;
            Vector2 dotPoint = Vector2.Lerp(startPoint, perfectPoint, t);
            DrawOutlinedLabel(new Rect(dotPoint.x - 8f, dotPoint.y - 8f, 16f, 16f), ".", _overlayMicroStyle!, new Color(1f, 1f, 1f, 0.78f));
        }

        float goodDiameter = Mathf.Max(88f, GoodZoneRadius * 2f * boxSize);
        Rect goodRect = NormalizedRectToPixels(PerfectZoneCenter, goodDiameter, goodDiameter, areaRect);
        DrawOutlinedLabel(new Rect(goodRect.x - 14f, goodRect.y - 26f, goodRect.width + 28f, 18f), "GOOD", _overlayMicroStyle!, new Color(1f, 0.90f, 0.45f, 1f));
        DrawOutlinedLabel(new Rect(perfectPoint.x - 54f, perfectPoint.y + 20f, 108f, 18f), "PERFECT", _overlayMicroStyle!, new Color(0.68f, 1f, 0.84f, 1f));
        DrawOutlinedLabel(new Rect(perfectPoint.x - 24f, perfectPoint.y - 26f, 48f, 52f), "+", _overlayTargetStyle!, new Color(0.68f, 1f, 0.84f, 1f));
        DrawOutlinedLabel(new Rect(startPoint.x + 12f, startPoint.y - 10f, 70f, 20f), "START", _overlayMicroStyle!, Color.white);
        DrawOutlinedLabel(new Rect(cursorPoint.x - 18f, cursorPoint.y - 20f, 36f, 36f), "O", _overlayCursorStyle!, new Color(0.70f, 0.93f, 1f, 1f));
    }

    private void DrawDapResultBanner()
    {
        float width = 360f;
        float height = 78f;
        float x = (Screen.width - width) * 0.5f;
        float y = Screen.height - height - 96f;

        Rect panelRect = new Rect(x, y, width, height);
        GUI.DrawTexture(panelRect, _blackTex!);
        DrawOutline(panelRect, 1.5f, _whiteTex!);

        Color oldColor = GUI.color;
        GUI.color = _dapResultAccent;
        GUI.DrawTexture(new Rect(x + 12f, y + 12f, 4f, height - 24f), _whiteTex!);
        GUI.color = oldColor;

        GUI.Label(
            new Rect(x + 24f, y + 10f, width - 36f, 24f),
            _dapResultTitle ?? string.Empty,
            WithTextColor(_resultTitleStyle!, _dapResultAccent));

        GUI.Label(
            new Rect(x + 24f, y + 34f, width - 36f, 22f),
            _dapResultDetail ?? string.Empty,
            _resultDetailStyle!);
    }

    private void DrawRect(Rect rect, Texture2D texture)
    {
        GUI.DrawTexture(rect, texture);
    }

    private void DrawOutline(Rect rect, float thickness, Texture2D texture)
    {
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, thickness), texture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), texture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, thickness, rect.height), texture);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), texture);
    }

    private Rect NormalizedRectToPixels(Vector2 normalizedPoint, float width, float height, Rect areaRect)
    {
        Vector2 p = NormalizedPointToPixels(normalizedPoint, areaRect);
        return new Rect(p.x - (width * 0.5f), p.y - (height * 0.5f), width, height);
    }

    private Vector2 NormalizedPointToPixels(Vector2 normalizedPoint, Rect areaRect)
    {
        float x = areaRect.x + (normalizedPoint.x * areaRect.width);
        float y = areaRect.y + ((1f - normalizedPoint.y) * areaRect.height);
        return new Vector2(x, y);
    }

    private void DrawLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        Matrix4x4 matrix = GUI.matrix;
        Color oldColor = GUI.color;

        Vector2 delta = b - a;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        float length = delta.magnitude;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new Rect(a.x, a.y - (thickness * 0.5f), length, thickness), _whiteTex!);
        GUI.matrix = matrix;
        GUI.color = oldColor;
    }

    private static GUIStyle MakePanelStyle(Color backgroundColor)
    {
        Texture2D background = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        background.SetPixel(0, 0, backgroundColor);
        background.Apply();

        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.normal.background = background;
        style.border = new RectOffset(0, 0, 0, 0);
        style.padding = new RectOffset(0, 0, 0, 0);
        style.margin = new RectOffset(0, 0, 0, 0);
        return style;
    }

    private static GUIStyle MakeLabelStyle(int fontSize, FontStyle fontStyle, Color textColor, TextAnchor alignment = TextAnchor.MiddleLeft)
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = fontStyle,
            alignment = alignment,
            richText = false
        };

        style.normal.textColor = textColor;
        return style;
    }

    private static GUIStyle WithTextColor(GUIStyle style, Color textColor)
    {
        style.normal.textColor = textColor;
        return style;
    }

    private static void DrawOutlinedLabel(Rect rect, string text, GUIStyle style, Color textColor)
    {
        Color oldColor = style.normal.textColor;
        style.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x - 1f, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), text, style);

        style.normal.textColor = textColor;
        GUI.Label(rect, text, style);
        style.normal.textColor = oldColor;
    }
    #endif
}
