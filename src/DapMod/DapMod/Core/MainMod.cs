using MelonLoader;
using UnityEngine;

namespace DapMod.Core;

public class MainMod : MelonMod
{
    private const float RayDistance = 5f;
    private const float MaxDapStartDistance = 1.0f;

    private bool _dapActive = false;
    private Transform? _currentNpcTarget = null;

    public override void OnInitializeMelon()
    {
        MelonLogger.Msg("DapMod loaded successfully. [Session Build]");
    }

    public override void OnUpdate()
    {
        if (Input.GetKeyDown(KeyCode.G))
        {
            HandleDapInput();
        }

        if (Input.GetKeyDown(KeyCode.H))
        {
            CancelDapSession();
        }
    }

    private void HandleDapInput()
    {
        MelonLogger.Msg("G was detected.");

        if (_dapActive)
        {
            MelonLogger.Msg($"Dap session already active with: {_currentNpcTarget?.name ?? "<unknown>"}");
            return;
        }

        if (!TryGetDappableNpcTarget(out Transform npcRoot, out float hitDistance))
        {
            MelonLogger.Msg("No valid dappable NPC target found.");
            return;
        }

        MelonLogger.Msg($"Valid NPC target found: {npcRoot.name}");

        if (hitDistance > MaxDapStartDistance)
        {
            MelonLogger.Msg(
                $"Target is too far to start dap. Distance: {hitDistance:F2} / Max: {MaxDapStartDistance:F2}");
            return;
        }

        StartDapSession(npcRoot, hitDistance);
    }

    private void StartDapSession(Transform npcRoot, float hitDistance)
    {
        _dapActive = true;
        _currentNpcTarget = npcRoot;

        Transform? playerRoot = FindLocalPlayerRoot();

        MelonLogger.Msg("=== Dap Session Started ===");
        MelonLogger.Msg($"NPC: {npcRoot.name}");
        MelonLogger.Msg($"Start Distance: {hitDistance:F2}");

        if (playerRoot != null)
        {
            float playerToNpcDistance = Vector3.Distance(playerRoot.position, npcRoot.position);
            MelonLogger.Msg($"Player Root: {playerRoot.name}");
            MelonLogger.Msg($"Player->NPC Distance: {playerToNpcDistance:F2}");
            MelonLogger.Msg($"Player Position: {playerRoot.position}");
            MelonLogger.Msg($"NPC Position: {npcRoot.position}");
        }
        else
        {
            MelonLogger.Warning("Could not find local player root.");
        }

        MelonLogger.Msg("Press H to cancel dap session.");
        MelonLogger.Msg("===========================");
    }

    private void CancelDapSession()
    {
        if (!_dapActive)
        {
            return;
        }

        MelonLogger.Msg("=== Dap Session Cancelled ===");
        MelonLogger.Msg($"NPC: {_currentNpcTarget?.name ?? "<unknown>"}");
        MelonLogger.Msg("=============================");

        _dapActive = false;
        _currentNpcTarget = null;
    }

    private Transform? FindLocalPlayerRoot()
    {
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
                return transform;
            }
        }

        return null;
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
            MelonLogger.Msg("Dap probe found no target.");
            return false;
        }

        Transform hitTransform = hit.collider.transform;
        Transform rootTransform = hitTransform.root;

        string rootName = rootTransform.name ?? "<null>";
        int rootLayer = rootTransform.gameObject.layer;

        hitDistance = hit.distance;

        MelonLogger.Msg("=== Dap Filter Probe ===");
        MelonLogger.Msg($"Hit: {hitTransform.name}");
        MelonLogger.Msg($"Root: {rootName}");
        MelonLogger.Msg($"Root Layer: {rootLayer}");
        MelonLogger.Msg($"Hit Distance: {hitDistance:F2}");

        if (rootName.StartsWith("Tripod ("))
        {
            MelonLogger.Msg("Rejected target: local player/self.");
            return false;
        }

        if (rootLayer != 11)
        {
            MelonLogger.Msg("Rejected target: root layer is not NPC layer 11.");
            return false;
        }

        MelonLogger.Msg("========================");

        npcRoot = rootTransform;
        return true;
    }
}