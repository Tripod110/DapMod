using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace DapMod.Core;

public partial class MainMod
{
    private void CacheRewardTargetsForSession(Transform npcRoot)
    {
        _currentNpcComponent = null;
        _currentNpcRelationData = null;
        _currentNpcRewardKey = null;

        if (TryGetNpcComponent(npcRoot, out Component npcComponent))
        {
            _currentNpcComponent = npcComponent;
            _currentNpcRelationData = GetObjectMemberValue(npcComponent, "RelationData") ??
                                      GetObjectMemberValue(npcComponent, "relationData") ??
                                      GetObjectMemberValue(npcComponent, "NPCRelationData");
            _currentNpcRewardKey = ResolveNpcRewardKey(npcRoot, npcComponent);
        }
    }

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

        TryPlayDapAudioCue(fileName);
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
        if (TryGetCurrentGameDayIndex(out int cachedDayIndex))
        {
            return cachedDayIndex;
        }

        if (!_attemptedGameDayProviderDiscovery)
        {
            _attemptedGameDayProviderDiscovery = true;
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

        return false;
    }

    private bool TryWarmGameDayProviderCache()
    {
        return TryWarmGameDayProviderCache(out _);
    }

    private bool TryWarmGameDayProviderCache(out int dayIndex)
    {
        dayIndex = -1;
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
        List<Component> candidates = new();

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
            // ignored
        }

        return candidates.ToArray();
    }

    private static bool IsLikelyGameDayProvider(Component component)
    {
        string typeName = component.GetType().FullName ?? component.GetType().Name;
        return typeName.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Day", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("World", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Game", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveGameDayMemberPath(object provider, out string memberPath, out int dayIndex)
    {
        memberPath = string.Empty;
        dayIndex = -1;

        string[] simpleCandidates =
        {
            "Day",
            "CurrentDay",
            "day",
            "currentDay",
            "ElapsedDays",
            "elapsedDays",
            "DayIndex",
            "CurrentDayIndex"
        };

        foreach (string candidate in simpleCandidates)
        {
            if (TryReadGameDayFromMemberPath(provider, candidate, out dayIndex))
            {
                memberPath = candidate;
                return true;
            }
        }

        string[] nestedRoots =
        {
            "Time",
            "time",
            "CurrentTime",
            "currentTime",
            "Data",
            "data",
            "World",
            "world"
        };

        foreach (string root in nestedRoots)
        {
            foreach (string child in simpleCandidates)
            {
                string nestedPath = $"{root}.{child}";
                if (TryReadGameDayFromMemberPath(provider, nestedPath, out dayIndex))
                {
                    memberPath = nestedPath;
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
        string[] parts = memberPath.Split('.');

        foreach (string part in parts)
        {
            if (current == null)
            {
                return false;
            }

            current = GetObjectMemberValue(current, part);
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
            dayIndex = Convert.ToInt32(value);
            return dayIndex >= 0;
        }
        catch
        {
            return false;
        }
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
            foreach (string rawLine in System.IO.File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                string[] parts = rawLine.Split('\t');
                if (parts.Length == 2 && int.TryParse(parts[1], out int dayIndex))
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
        return _npcLastSuccessfulDapDayByKey.TryGetValue(npcKey, out int lastSuccessDay) &&
               lastSuccessDay == currentDayIndex;
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
            foreach (string rawLine in System.IO.File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                string[] parts = rawLine.Split('\t');
                if (parts.Length == 2 && int.TryParse(parts[1], out int dayIndex))
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
            List<string> lines = new(_npcLastSuccessfulDapDayByKey.Count);
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
            List<string> lines = new(_npcLastXpRewardDayByKey.Count);
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
        if (_currentNpcTarget == npcRoot && !string.IsNullOrWhiteSpace(_currentNpcRewardKey))
        {
            return _currentNpcRewardKey!;
        }

        int key = npcRoot.GetInstanceID();
        if (_npcRewardKeyCache.TryGetValue(key, out string? cachedKey) && !string.IsNullOrEmpty(cachedKey))
        {
            return cachedKey;
        }

        if (TryGetNpcComponent(npcRoot, out Component npcComponent))
        {
            string resolved = ResolveNpcRewardKey(npcRoot, npcComponent);
            _npcRewardKeyCache[key] = resolved;
            if (_currentNpcTarget == npcRoot)
            {
                _currentNpcRewardKey = resolved;
            }

            return resolved;
        }

        string fallback = BuildTransformPath(npcRoot);
        _npcRewardKeyCache[key] = fallback;
        return fallback;
    }

    private string ResolveNpcRewardKey(Transform npcRoot, Component npcComponent)
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
            if (value == null)
            {
                continue;
            }

            string text = value.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return BuildTransformPath(npcRoot);
    }

    private bool TryTriggerConversation(Transform npcRoot)
    {
        try
        {
            _allowSyntheticInteraction = true;

            Component? npcComponent = _currentNpcTarget == npcRoot ? _currentNpcComponent : null;
            if (npcComponent == null && TryGetNpcComponent(npcRoot, out Component liveNpcComponent))
            {
                npcComponent = liveNpcComponent;
            }

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
        List<string> segments = new();
        Transform? current = transform;
        while (current != null)
        {
            segments.Add(current.name ?? "<unnamed>");
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments.ToArray());
    }

    private string GetCurrentNpcDisplayName()
    {
        string rawName = _currentNpcTarget?.name ?? "npc";
        int roleIndex = rawName.IndexOf(" (", StringComparison.Ordinal);
        string trimmed = roleIndex > 0 ? rawName[..roleIndex] : rawName;
        return trimmed.Replace('_', ' ').Trim();
    }

    private void ShowDapResultBanner(DapResult result, int xpAwarded, float friendshipAwarded)
    {
        string npcName = GetCurrentNpcDisplayName();
        switch (result)
        {
            case DapResult.Perfect:
                SetDapResultBanner(
                    $"perfect dap with {npcName}",
                    $"+{xpAwarded} xp   +{friendshipAwarded:F2} friendship",
                    new Color(0.60f, 1f, 0.82f, 1f));
                break;

            case DapResult.Good:
                SetDapResultBanner(
                    $"good dap with {npcName}",
                    $"+0 xp   +{friendshipAwarded:F2} friendship",
                    new Color(1f, 0.86f, 0.58f, 1f));
                break;

            default:
                SetDapResultBanner(
                    $"dap with {npcName}",
                    "+0 xp   +0.00 friendship",
                    new Color(0.93f, 0.95f, 0.98f, 1f));
                break;
        }
    }

    private void ShowCancelledDapBanner(string reason)
    {
        SetDapResultBanner(
            $"dap failed with {GetCurrentNpcDisplayName()}",
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

        Component? levelManager = TryCacheLevelManager();
        if (levelManager == null)
        {
            MelonLogger.Warning("Could not find active LevelManager for dap XP reward.");
            return false;
        }

        if (TryInvokeObjectMethod(levelManager, "AddXP", amount) ||
            TryInvokeObjectMethod(levelManager, "AddXPLocal", amount) ||
            TryInvokeObjectMethod(levelManager, "AddExperience", amount) ||
            TryInvokeObjectMethod(levelManager, "GiveXP", amount))
        {
            return true;
        }

        if (TryAdjustNumericMember(levelManager, amount, "XP", "CurrentXP", "Experience"))
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

        if (!TryResolveNpcRewardContext(npcRoot, out _, out object? relationData))
        {
            MelonLogger.Warning("Could not find NPC RelationData for dap friendship reward.");
            return false;
        }

        object liveRelationData = relationData!;

        if (TryInvokeObjectMethod(liveRelationData, "ChangeRelationship", amount, true) ||
            TryInvokeObjectMethod(liveRelationData, "ChangeRelationship", amount) ||
            TryInvokeObjectMethod(liveRelationData, "AddRelationship", amount) ||
            TryInvokeObjectMethod(liveRelationData, "ModifyRelationship", amount) ||
            TryInvokeObjectMethod(liveRelationData, "ChangeFriendship", amount))
        {
            return true;
        }

        if (TryAdjustNumericMember(liveRelationData, amount, "Relationship", "CurrentRelationship", "Friendship"))
        {
            return true;
        }

        MelonLogger.Warning("Could not apply dap friendship reward because no relationship member or method was callable.");
        return false;
    }

    private bool TryResolveNpcRewardContext(Transform npcRoot, out Component? npcComponent, out object? relationData)
    {
        npcComponent = null;
        relationData = null;

        if (_currentNpcTarget == npcRoot)
        {
            npcComponent = _currentNpcComponent;
            relationData = _currentNpcRelationData;
        }

        if (npcComponent == null && TryGetNpcComponent(npcRoot, out Component liveNpcComponent))
        {
            npcComponent = liveNpcComponent;
            if (_currentNpcTarget == npcRoot)
            {
                _currentNpcComponent = liveNpcComponent;
            }
        }

        if (npcComponent == null)
        {
            return false;
        }

        relationData ??= GetObjectMemberValue(npcComponent, "RelationData") ??
                         GetObjectMemberValue(npcComponent, "relationData") ??
                         GetObjectMemberValue(npcComponent, "NPCRelationData");
        if (relationData == null)
        {
            return false;
        }

        if (_currentNpcTarget == npcRoot)
        {
            _currentNpcRelationData = relationData;
        }

        return true;
    }

    private Component? TryCacheLevelManager()
    {
        if (_cachedLevelManager != null)
        {
            try
            {
                _ = _cachedLevelManager.transform;
                return _cachedLevelManager;
            }
            catch
            {
                _cachedLevelManager = null;
            }
        }

        if (Time.time < _nextLevelManagerLookupAllowedTime)
        {
            return null;
        }

        Type? levelManagerType = GetLevelManagerRuntimeType();
        if (levelManagerType == null)
        {
            _nextLevelManagerLookupAllowedTime = Time.time + 5f;
            return null;
        }

        string[] singletonMembers =
        {
            "Instance",
            "instance",
            "Singleton",
            "singleton",
            "LocalInstance",
            "localInstance"
        };

        foreach (string memberName in singletonMembers)
        {
            if (GetStaticObjectMemberValue(levelManagerType, memberName) is Component singletonComponent)
            {
                _cachedLevelManager = singletonComponent;
                return _cachedLevelManager;
            }
        }

        // Avoid a scene-wide scan on the success click. If the singleton path misses,
        // back off and try again later after we have better runtime context.
        _nextLevelManagerLookupAllowedTime = Time.time + 5f;

        return _cachedLevelManager;
    }

    private Type? GetLevelManagerRuntimeType()
    {
        _cachedLevelManagerType ??= ResolveLoadedType("Il2CppScheduleOne.Levelling.LevelManager") ??
                                    ResolveLoadedType("ScheduleOne.Levelling.LevelManager");
        return _cachedLevelManagerType;
    }
}
