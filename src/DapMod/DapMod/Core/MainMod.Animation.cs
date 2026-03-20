using System;
using System.Collections.Generic;
using UnityEngine;

namespace DapMod.Core;

public partial class MainMod
{
    private enum NpcGestureCue
    {
        Start,
        Success,
        Fail
    }

    private static readonly string[] NpcGestureStartCandidates =
    {
        "RightArm_Hold_OpenHand_Lowered",
        "RightArm_Hold_OpenHand",
        "ConversationGesture1"
    };

    private static readonly string[] NpcGestureSuccessCandidates =
    {
        "ConversationGesture1",
        "RightArm_Hold_OpenHand",
        "RightArm_Hold_OpenHand_Lowered"
    };

    private static readonly string[] NpcGestureFailCandidates =
    {
        "DisagreeWave",
        "ConversationGesture1",
        "RightArm_Hold_OpenHand_Lowered"
    };

    private void PrepareNpcGestureProfile(Transform npcRoot)
    {
        _activeNpcGestureAnimator = null;
        _npcStartGestureTrigger = null;
        _npcSuccessGestureTrigger = null;
        _npcFailGestureTrigger = null;

        Animator[] animators = npcRoot.GetComponentsInChildren<Animator>(true);
        int bestScore = -1;

        foreach (Animator animator in animators)
        {
            if (animator == null)
            {
                continue;
            }

            HashSet<string> triggerNames = CollectAnimatorTriggerNames(animator);
            if (triggerNames.Count == 0)
            {
                continue;
            }

            string? startTrigger = FindFirstMatchingTrigger(triggerNames, NpcGestureStartCandidates);
            if (string.IsNullOrEmpty(startTrigger))
            {
                continue;
            }

            string successTrigger = FindFirstMatchingTrigger(triggerNames, NpcGestureSuccessCandidates) ?? startTrigger;
            string failTrigger = FindFirstMatchingTrigger(triggerNames, NpcGestureFailCandidates) ?? successTrigger;
            int score = (startTrigger != null ? 2 : 0) +
                        (successTrigger != null ? 1 : 0) +
                        (failTrigger != null ? 1 : 0);

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            _activeNpcGestureAnimator = animator;
            _npcStartGestureTrigger = startTrigger;
            _npcSuccessGestureTrigger = successTrigger;
            _npcFailGestureTrigger = failTrigger;
        }
    }

    private void PlayNpcGestureCue(NpcGestureCue cue)
    {
        if (_activeNpcGestureAnimator == null)
        {
            return;
        }

        string? triggerName = cue switch
        {
            NpcGestureCue.Start => _npcStartGestureTrigger,
            NpcGestureCue.Success => _npcSuccessGestureTrigger,
            NpcGestureCue.Fail => _npcFailGestureTrigger,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(triggerName))
        {
            return;
        }

        try
        {
            ResetNpcGestureTriggers();
            _activeNpcGestureAnimator.speed = Mathf.Max(1f, _activeNpcGestureAnimator.speed);
            _activeNpcGestureAnimator.SetTrigger(triggerName);
        }
        catch
        {
            // ignored
        }
    }

    private void ResetNpcGestureTriggers()
    {
        if (_activeNpcGestureAnimator == null)
        {
            return;
        }

        ResetAnimatorTriggerIfPresent(_activeNpcGestureAnimator, _npcStartGestureTrigger);
        ResetAnimatorTriggerIfPresent(_activeNpcGestureAnimator, _npcSuccessGestureTrigger);
        ResetAnimatorTriggerIfPresent(_activeNpcGestureAnimator, _npcFailGestureTrigger);
    }

    private static void ResetAnimatorTriggerIfPresent(Animator animator, string? triggerName)
    {
        if (string.IsNullOrWhiteSpace(triggerName))
        {
            return;
        }

        try
        {
            animator.ResetTrigger(triggerName);
        }
        catch
        {
            // ignored
        }
    }

    private static HashSet<string> CollectAnimatorTriggerNames(Animator animator)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter != null && parameter.type == AnimatorControllerParameterType.Trigger)
            {
                names.Add(parameter.name);
            }
        }

        return names;
    }

    private static string? FindFirstMatchingTrigger(HashSet<string> triggerNames, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (triggerNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
