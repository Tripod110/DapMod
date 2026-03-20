using System;
using MelonLoader;
using UnityEngine;

namespace DapMod.Core;

public partial class MainMod
{
    private Transform? GetPlayerReferenceTransform()
    {
        Camera? mainCamera = Camera.main;
        if (mainCamera != null)
        {
            return mainCamera.transform;
        }

        return FindLocalPlayerRoot();
    }

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

        if (rootName.StartsWith("Tripod (", StringComparison.Ordinal))
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
                _ = _cachedPlayerRoot.position;
                return _cachedPlayerRoot;
            }
            catch
            {
                _cachedPlayerRoot = null;
            }
        }

        if (_cachedInteractionManager != null)
        {
            Transform? fromInteraction = ResolvePlayerRootFromTransform(_cachedInteractionManager.transform, allowLooseFallback: true);
            if (fromInteraction != null)
            {
                _cachedPlayerRoot = fromInteraction;
                return _cachedPlayerRoot;
            }
        }

        if (_cachedPlayerComponent == null)
        {
            _cachedPlayerComponent = FindFirstLoadedComponentByTypeName("Il2CppScheduleOne.PlayerScripts.Player") ??
                                     FindFirstLoadedComponentByTypeName("ScheduleOne.PlayerScripts.Player");
        }

        if (_cachedPlayerComponent != null)
        {
            Transform? fromPlayerComponent = ResolvePlayerRootFromTransform(_cachedPlayerComponent.transform, allowLooseFallback: true);
            if (fromPlayerComponent != null)
            {
                _cachedPlayerRoot = fromPlayerComponent;
                return _cachedPlayerRoot;
            }
        }

        Camera? mainCamera = Camera.main;
        if (mainCamera != null)
        {
            Transform? fromCamera = ResolvePlayerRootFromTransform(mainCamera.transform, allowLooseFallback: true);
            if (fromCamera != null)
            {
                _cachedPlayerRoot = fromCamera;
                return _cachedPlayerRoot;
            }
        }

        return null;
    }

    private Transform? ResolvePlayerRootFromTransform(Transform? source, bool allowLooseFallback)
    {
        if (source == null)
        {
            return null;
        }

        Transform walker = source;
        int steps = 0;
        while (walker != null && steps < 12)
        {
            if (IsLikelyPlayerRoot(walker))
            {
                return walker;
            }

            walker = walker.parent;
            steps++;
        }

        if (!allowLooseFallback)
        {
            return null;
        }

        return FindLoosePlayerAncestor(source) ?? source.root ?? source;
    }

    private static Transform? FindLoosePlayerAncestor(Transform source)
    {
        Transform? walker = source;
        int steps = 0;
        while (walker != null && steps < 12)
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
            steps++;
        }

        return null;
    }

    private bool IsLikelyPlayerRoot(Transform transform)
    {
        string name = transform.name ?? string.Empty;
        if (name.StartsWith("Tripod (", StringComparison.Ordinal) ||
            name.Contains("Tripod", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Player", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Component[] components = transform.GetComponents<Component>();
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

    private bool TryGetNpcComponent(Transform npcRoot, out Component npcComponent)
    {
        int key = npcRoot.GetInstanceID();
        if (_npcComponentCache.TryGetValue(key, out npcComponent!))
        {
            return npcComponent != null;
        }

        npcComponent = FindNpcComponent(npcRoot)!;

        if (npcComponent != null)
        {
            _npcComponentCache[key] = npcComponent;
            return true;
        }

        return false;
    }

    private Component? FindNpcComponent(Transform npcRoot)
    {
        Type? npcType = GetNpcRuntimeType();
        if (npcType != null)
        {
            Component[] selfAndChildren = npcRoot.GetComponentsInChildren<Component>(true);
            Component? hierarchyMatch = FindComponentByRuntimeType(selfAndChildren, npcType);
            if (hierarchyMatch != null)
            {
                return hierarchyMatch;
            }

            if (npcRoot.parent != null)
            {
                Component[] parentChain = npcRoot.parent.GetComponentsInChildren<Component>(true);
                hierarchyMatch = FindComponentByRuntimeType(parentChain, npcType);
                if (hierarchyMatch != null)
                {
                    return hierarchyMatch;
                }
            }
        }

        return FindComponentByTypeName(
                   npcRoot.GetComponentsInChildren<Component>(true),
                   "Il2CppScheduleOne.NPCs.NPC") ??
               FindComponentByTypeName(
                   npcRoot.GetComponentsInChildren<Component>(true),
                   "ScheduleOne.NPCs.NPC");
    }

    private Type? GetNpcRuntimeType()
    {
        _cachedNpcType ??= ResolveLoadedType("Il2CppScheduleOne.NPCs.NPC") ??
                           ResolveLoadedType("ScheduleOne.NPCs.NPC");
        return _cachedNpcType;
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
}
