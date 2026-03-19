using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DapMod.Core;

public class MainMod : MelonMod
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
    private const float DapResultBannerDuration = 2.75f;
    private const double PerformanceLogThresholdMs = 8.0;
    private const int PerfectDapXpReward = 5;
    private const float PerfectDapFriendshipReward = 0.04f;
    private const string AudioCueFolderName = "DapModAudio";
    private const string PerfectAudioFileName = "perfect_dap.wav";
    private const string GoodAudioFileName = "good_dap.wav";
    private const string BadAudioFileName = "bad_dap.wav";

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
    private float _pendingPlayerRestoreTime = -1f;

    private Transform? _cachedPlayerRoot = null;
    private PlayerActionSnapshot? _playerActionSnapshot = null;
    private NpcPauseSnapshot? _npcPauseSnapshot = null;

    // Overlay textures
    private Texture2D? _whiteTex;
    private Texture2D? _blackTex;
    private Texture2D? _cursorTex;
    private Texture2D? _perfectTex;
    private Texture2D? _goodTex;
    private Texture2D? _panelTex;
    private GUIStyle? _resultTitleStyle;
    private GUIStyle? _resultDetailStyle;
    private string? _dapResultTitle;
    private string? _dapResultDetail;
    private float _dapResultDisplayUntil = -1f;
    private Color _dapResultAccent = Color.white;
    private Component? _cachedLevelManager;
    private readonly HashSet<string> _missingAudioCueWarningsShown = new(StringComparer.OrdinalIgnoreCase);

    public override void OnInitializeMelon()
    {
        _instance = this;
        InstallHarmonyPatches();
        EnsureOverlayTextures();
        EnsureAudioPlaceholderDirectory();
        MelonLogger.Msg("DapMod loaded successfully. [Visible Minigame + Action Suppression Build]");
    }

    public override void OnUpdate()
    {
        UpdatePendingPlayerRestore();

        if (Input.GetKeyDown(KeyCode.G))
        {
            HandleDapInput();
        }

        if (Input.GetKeyDown(KeyCode.H))
        {
            CancelDapSession("Manual cancel.");
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
            Transform? playerRoot = FindLocalPlayerRoot();
            if (playerRoot != null)
            {
                FaceNpcTowardPlayer(playerRoot, _currentNpcTarget);

                if (HasBrokenDapRange(playerRoot, _currentNpcTarget))
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
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        EnsureOverlayTextures();

        if (_minigameActive)
        {
            DrawDapOverlay();
        }

        if (HasVisibleDapResultBanner())
        {
            DrawDapResultBanner();
        }
    }

    // -----------------------------
    // Session flow
    // -----------------------------

    private void HandleDapInput()
    {
        if (Time.time < _nextAllowedDapTime)
        {
            float remaining = _nextAllowedDapTime - Time.time;
            MelonLogger.Msg($"Dap on cooldown for {remaining:F2}s");
            return;
        }

        if (_dapActive)
        {
            MelonLogger.Msg($"Dap session already active with: {_currentNpcTarget?.name ?? "<unknown>"}");
            return;
        }

        if (!TryGetDappableNpcTarget(out Transform npcRoot, out float hitDistance))
        {
            if (VerboseLogging)
            {
                MelonLogger.Msg("No valid dappable NPC target found.");
            }

            return;
        }

        Transform? playerRoot = FindLocalPlayerRoot();
        if (playerRoot == null)
        {
            MelonLogger.Warning("Could not find local player root.");
            return;
        }

        float playerToNpcDistance = Vector3.Distance(playerRoot.position, npcRoot.position);
        if (playerToNpcDistance > MaxDapStartDistance)
        {
            MelonLogger.Msg(
                $"Target too far to start dap. Player->NPC: {playerToNpcDistance:F2} / Max: {MaxDapStartDistance:F2} (Ray hit: {hitDistance:F2})");
            _nextAllowedDapTime = Time.time + 0.20f;
            return;
        }

        if (!IsNpcFacingPlayer(playerRoot, npcRoot))
        {
            MelonLogger.Msg("Rejected dap: NPC is not facing the player.");
            _nextAllowedDapTime = Time.time + 0.20f;
            return;
        }

        StartDapSession(playerRoot, npcRoot, hitDistance, playerToNpcDistance);
    }

    private void StartDapSession(Transform playerRoot, Transform npcRoot, float hitDistance, float playerToNpcDistance)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            _dapActive = true;
            _currentNpcTarget = npcRoot;
            _dapStartTime = Time.time;
            _dapOriginPlayerPosition = playerRoot.position;
            _dapOriginNpcPosition = npcRoot.position;

            SuppressPlayerActions(playerRoot);
            PauseNpcBehavior(npcRoot);
            FaceNpcTowardPlayer(playerRoot, npcRoot);
            MaintainNpcLock();
            BeginDapMinigame();
            stopwatch.Stop();

            MelonLogger.Msg(
                $"Dap started with {npcRoot.name} | dist {playerToNpcDistance:F2}m | setup {stopwatch.Elapsed.TotalMilliseconds:F1}ms");

            if (stopwatch.Elapsed.TotalMilliseconds >= PerformanceLogThresholdMs)
            {
                MelonLogger.Warning(
                    $"Dap setup hitch detected: {stopwatch.Elapsed.TotalMilliseconds:F1}ms (player disable {_playerActionSnapshot?.Behaviours.Count ?? 0} / npc pause {_npcPauseSnapshot?.Behaviours.Count ?? 0})");
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

        try
        {
            MelonLogger.Msg($"Dap success with {_currentNpcTarget?.name ?? "<unknown>"} | {result}");

            ApplyDapRewards(result, out xpAwarded, out friendshipAwarded);
            ShowDapResultBanner(result, xpAwarded, friendshipAwarded);
            _nextAllowedDapTime = Time.time + DapRetryCooldown;
            ResetDapState();
            stopwatch.Stop();

            QueueDapAudioPlaceholder(result);

            if (stopwatch.Elapsed.TotalMilliseconds >= PerformanceLogThresholdMs)
            {
                MelonLogger.Warning($"Dap completion hitch detected: {stopwatch.Elapsed.TotalMilliseconds:F1}ms");
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
               (_instance._dapActive || _instance._pendingPlayerRestoreTime >= 0f);
    }

    private void InstallHarmonyPatches()
    {
        HarmonyMethod prefix = new HarmonyMethod(typeof(MainMod).GetMethod(
            nameof(BlockGameplayActionsPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)!);

        foreach (string target in GameplayBlockTargets)
        {
            MethodBase? method = AccessTools.Method(target);
            if (method == null)
            {
                MelonLogger.Warning($"Could not find dap gameplay blocker target: {target}");
                continue;
            }

            HarmonyInstance.Patch(method, prefix: prefix);
        }
    }

    private static bool BlockGameplayActionsPrefix()
    {
        return !ShouldBlockGameplayActions();
    }

    private void ApplyDapRewards(DapResult result, out int xpAwarded, out float friendshipAwarded)
    {
        xpAwarded = 0;
        friendshipAwarded = 0f;

        if (result != DapResult.Perfect)
        {
            return;
        }

        bool xpApplied = AwardPlayerXp(PerfectDapXpReward);
        bool friendshipApplied = _currentNpcTarget != null &&
                                 AwardNpcFriendship(_currentNpcTarget, PerfectDapFriendshipReward);

        if (xpApplied)
        {
            xpAwarded = PerfectDapXpReward;
        }

        if (friendshipApplied)
        {
            friendshipAwarded = PerfectDapFriendshipReward;
        }

        MelonLogger.Msg(
            $"Perfect dap rewards -> XP: {(xpApplied ? PerfectDapXpReward.ToString() : "failed")}, Friendship: {(friendshipApplied ? PerfectDapFriendshipReward.ToString("F2") : "failed")}");
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
                    "+0 xp   +0.00 friendship",
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
            TryInvokeObjectMethod(levelManager, "AddXPLocal", amount))
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

        if (TryInvokeObjectMethod(relationData, "ChangeRelationship", amount, true))
        {
            return true;
        }

        MelonLogger.Warning("Could not apply dap friendship reward because ChangeRelationship was not callable.");
        return false;
    }

    // -----------------------------
    // Targeting / player lookup
    // -----------------------------

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
            return _cachedPlayerRoot;
        }

        Transform[] allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();

        foreach (Transform transform in allTransforms)
        {
            if (transform.parent != null)
            {
                continue;
            }

            string name = transform.name ?? string.Empty;

            if (name.StartsWith("Tripod ("))
            {
                _cachedPlayerRoot = transform;
                MelonLogger.Msg($"Cached player root: {_cachedPlayerRoot.name}");
                return _cachedPlayerRoot;
            }
        }

        return null;
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

            animator.speed = 0f;
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
        float boxSize = OverlayScale;
        float boxX = (Screen.width * 0.5f) - (boxSize * 0.5f);
        float boxY = Screen.height - boxSize - 78f;

        Rect panelRect = new Rect(boxX - 10f, boxY - 30f, boxSize + 20f, boxSize + 58f);
        GUI.DrawTexture(panelRect, _panelTex!);

        Rect areaRect = new Rect(boxX, boxY, boxSize, boxSize);
        DrawRect(areaRect, _blackTex!);
        DrawOutline(areaRect, 1f, _whiteTex!);

        float goodDiameter = GoodZoneRadius * 2f * boxSize;
        Rect goodRect = NormalizedRectToPixels(PerfectZoneCenter, goodDiameter, goodDiameter, areaRect);
        DrawOutline(goodRect, 1f, _goodTex!);

        float perfectDiameter = PerfectZoneRadius * 2f * boxSize;
        Rect perfectRect = NormalizedRectToPixels(PerfectZoneCenter, perfectDiameter, perfectDiameter, areaRect);
        DrawOutline(perfectRect, 1.5f, _perfectTex!);

        Rect centerDotRect = NormalizedRectToPixels(PerfectZoneCenter, CenterDotSize, CenterDotSize, areaRect);
        GUI.DrawTexture(centerDotRect, _whiteTex!);

        Rect cursorRect = NormalizedRectToPixels(_dapCursor, CursorPixelSize, CursorPixelSize, areaRect);
        GUI.DrawTexture(cursorRect, _cursorTex!);

        Vector2 startPx = NormalizedPointToPixels(DapCursorStart, areaRect);
        Vector2 targetPx = NormalizedPointToPixels(PerfectZoneCenter, areaRect);
        DrawLine(startPx, targetPx, 1f, new Color(0.92f, 0.95f, 1f, 0.16f));

        Rect startRect = NormalizedRectToPixels(DapCursorStart, 5f, 5f, areaRect);
        GUI.DrawTexture(startRect, _whiteTex!);

        GUI.Label(new Rect(boxX, boxY - 22f, boxSize + 40f, 20f), "dap");
        GUI.Label(new Rect(boxX, boxY + boxSize + 4f, boxSize + 160f, 20f), "stay close and click on center");
    }

    private void DrawDapResultBanner()
    {
        float width = 320f;
        float height = 68f;
        float x = (Screen.width - width) * 0.5f;
        float y = Screen.height - height - 132f;

        Rect panelRect = new Rect(x, y, width, height);
        GUI.DrawTexture(panelRect, _panelTex!);
        DrawOutline(panelRect, 1f, _whiteTex!);

        Color oldColor = GUI.color;
        GUI.color = _dapResultAccent;
        GUI.DrawTexture(new Rect(x + 10f, y + 10f, 3f, height - 20f), _whiteTex!);
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

    private static GUIStyle MakeLabelStyle(int fontSize, FontStyle fontStyle, Color textColor)
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = fontStyle,
            alignment = TextAnchor.MiddleLeft,
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
}
