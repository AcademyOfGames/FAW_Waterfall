using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ensures plant sway starts for plants that are already active at scene load
/// (e.g. AlinaPrefabParent overrides), without waiting for RandomOneAtATimeActivator's schedule.
/// </summary>
[DefaultExecutionOrder(-50)]
public class PlantSwayInitializer : MonoBehaviour
{
    [SerializeField] private bool initializeOnStart = true;
    [SerializeField] private RandomOneAtATimeActivator plantActivator;
    [SerializeField] private List<GameObject> extraPlantRoots = new List<GameObject>();
    [Tooltip("Root for scanning all plant animators. Uses parent when empty (e.g. AlinaPrefabParent).")]
    [SerializeField] private Transform hierarchySearchRoot;

    private void Awake()
    {
        if (plantActivator == null)
        {
            plantActivator = GetComponent<RandomOneAtATimeActivator>();
        }
    }

    private void Start()
    {
        if (initializeOnStart)
        {
            InitializeActivePlantsNow();
        }
    }

    public void InitializeActivePlantsNow()
    {
        var plantRoots = new HashSet<GameObject>();
        CollectActivatorTargets(plantRoots);
        CollectExtraTargets(plantRoots);
        CollectAnimatorPlantRoots(GetSearchRoot(), plantRoots);

        foreach (GameObject plantRoot in plantRoots)
        {
            if (plantRoot != null && plantRoot.GetComponent<PlantUnderwaterSway>() == null)
            {
                plantRoot.AddComponent<PlantUnderwaterSway>();
            }
        }
    }

    private Transform GetSearchRoot()
    {
        if (hierarchySearchRoot != null)
        {
            return hierarchySearchRoot;
        }

        if (transform.parent != null)
        {
            return transform.parent;
        }

        return transform;
    }

    private void CollectActivatorTargets(HashSet<GameObject> plantRoots)
    {
        if (plantActivator == null || !plantActivator.isActiveAndEnabled)
        {
            return;
        }

        IReadOnlyList<GameObject> targets = plantActivator.PlantTargets;
        for (int i = 0; i < targets.Count; i++)
        {
            GameObject target = targets[i];
            if (target != null)
            {
                plantRoots.Add(target);
            }
        }
    }

    private void CollectExtraTargets(HashSet<GameObject> plantRoots)
    {
        for (int i = 0; i < extraPlantRoots.Count; i++)
        {
            GameObject target = extraPlantRoots[i];
            if (target != null)
            {
                plantRoots.Add(target);
            }
        }
    }

    private static void CollectAnimatorPlantRoots(Transform searchRoot, HashSet<GameObject> plantRoots)
    {
        if (searchRoot == null)
        {
            return;
        }

        Animator[] animators = searchRoot.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            Animator animator = animators[i];
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                continue;
            }

            if (IsFishHierarchy(animator.transform))
            {
                continue;
            }

            plantRoots.Add(animator.gameObject);
        }
    }

    private static bool IsFishHierarchy(Transform transform)
    {
        Transform current = transform;
        while (current != null)
        {
            if (current.name.IndexOf("fish", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }
}
