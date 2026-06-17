using System.Collections;
using UnityEngine;

/// <summary>
/// Soft-resets the Alina experience in place so AR tracking is preserved.
/// Restarts plants, fish, audio, and fern blend shapes without reloading the scene.
/// </summary>
[DisallowMultipleComponent]
public class AlinaExperienceReset : MonoBehaviour
{
    [Tooltip("Root used to find experience components. Uses this transform when empty.")]
    [SerializeField] private Transform searchRoot;

    [Header("Optional overrides (auto-resolved under Search Root when empty)")]
    [SerializeField] private RandomOneAtATimeActivator plantActivator;
    [SerializeField] private SplineFishGroupOrchestrator[] fishOrchestrators;
    [SerializeField] private ViewerFishEncounterController encounterController;
    [SerializeField] private ExperienceRiverAmbience riverAmbience;
    [SerializeField] private FrequencyBandVisualizer bandVisualizer;
    [SerializeField] private FrequencyAnalyzer frequencyAnalyzer;
    [SerializeField] private PlantFishReleaseController[] plantFishReleaseControllers;
    [SerializeField] private VertexPathSwarmFollower[] extraSwarms;

    private Coroutine _resetRoutine;

    public void ResetExperience()
    {
        if (_resetRoutine != null)
        {
            StopCoroutine(_resetRoutine);
        }

        _resetRoutine = StartCoroutine(ResetExperienceRoutine());
    }

    private IEnumerator ResetExperienceRoutine()
    {
        ResolveReferences();

        encounterController?.ResetForReplay();
        StopAllSwarms();

        bool activatorDrivesFish = plantActivator != null && plantActivator.DrivesFishOrchestrator;

        riverAmbience?.ResetForReplay();
        frequencyAnalyzer?.RestartPlayback();
        bandVisualizer?.ResetForReplay();

        plantActivator?.ResetAndRestart();

        if (plantFishReleaseControllers != null)
        {
            for (int i = 0; i < plantFishReleaseControllers.Length; i++)
            {
                plantFishReleaseControllers[i]?.ResetForReplay();
            }
        }

        if (fishOrchestrators != null)
        {
            for (int i = 0; i < fishOrchestrators.Length; i++)
            {
                fishOrchestrators[i]?.ResetForReplay(restartIfStartOnPlay: !activatorDrivesFish);
            }
        }

        _resetRoutine = null;
        yield break;
    }

    private void StopAllSwarms()
    {
        if (extraSwarms != null)
        {
            for (int i = 0; i < extraSwarms.Length; i++)
            {
                extraSwarms[i]?.StopSwarmImmediate();
            }
        }

        Transform root = GetSearchRoot();
        if (root == null)
        {
            return;
        }

        VertexPathSwarmFollower[] swarms = root.GetComponentsInChildren<VertexPathSwarmFollower>(true);
        for (int i = 0; i < swarms.Length; i++)
        {
            VertexPathSwarmFollower swarm = swarms[i];
            if (swarm == null)
            {
                continue;
            }

            if (swarm.GetComponent<PlantFishReleaseController>() != null)
            {
                continue;
            }

            swarm.StopSwarmImmediate();
        }
    }

    private void ResolveReferences()
    {
        Transform root = GetSearchRoot();

        if (plantActivator == null && root != null)
        {
            plantActivator = root.GetComponentInChildren<RandomOneAtATimeActivator>(true);
        }

        if (encounterController == null && root != null)
        {
            encounterController = root.GetComponentInChildren<ViewerFishEncounterController>(true);
        }

        if (riverAmbience == null && root != null)
        {
            riverAmbience = root.GetComponentInChildren<ExperienceRiverAmbience>(true);
        }

        if (bandVisualizer == null && root != null)
        {
            bandVisualizer = root.GetComponentInChildren<FrequencyBandVisualizer>(true);
        }

        if (frequencyAnalyzer == null)
        {
            frequencyAnalyzer = FindObjectOfType<FrequencyAnalyzer>();
        }

        if ((fishOrchestrators == null || fishOrchestrators.Length == 0) && root != null)
        {
            fishOrchestrators = root.GetComponentsInChildren<SplineFishGroupOrchestrator>(true);
        }

        if ((plantFishReleaseControllers == null || plantFishReleaseControllers.Length == 0) && root != null)
        {
            plantFishReleaseControllers = root.GetComponentsInChildren<PlantFishReleaseController>(true);
        }

        if ((extraSwarms == null || extraSwarms.Length == 0) && root != null)
        {
            extraSwarms = root.GetComponentsInChildren<VertexPathSwarmFollower>(true);
        }
    }

    private Transform GetSearchRoot()
    {
        return searchRoot != null ? searchRoot : transform;
    }
}
